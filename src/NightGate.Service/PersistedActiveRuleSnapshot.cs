using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using NightGate.Core;

namespace NightGate.Service;

public interface IActiveRuleSnapshotPublisher
{
    void Publish(ImmutableArray<AppRule> activeAppRules);
}

public interface IActiveProcessSnapshotPublisher
{
    void PublishProcessSnapshot(ProcessPersistenceRecord record);

    void InvalidateProcessSnapshot();
}

public sealed class PersistedActiveRuleSnapshot :
    IAllowedProcessSnapshotProvider,
    IActiveRuleSnapshotPublisher,
    IActiveProcessSnapshotPublisher
{
    private const int MaximumPersistedInstances = 2_048;
    private static readonly TimeSpan MaximumProcessSnapshotAge =
        TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _generationGate = new(1, 1);
    private readonly IClock? _clock;
    private readonly string? _configuredUserSid;
    private readonly ICurrentProcessWitnessProvider _currentProcessWitnessProvider;
    private readonly Action? _processSnapshotRulesCaptured;
    private ImmutableArray<AppRule> _activeRules = [];
    private long _activeRulesGeneration;
    private long _snapshotGeneration;
    private AllowedProcessSnapshotResult _current =
        AllowedProcessSnapshotResult.Unavailable(
            "process-snapshot-unavailable",
            generation: 0);
    private ClockObservation? _processSnapshotObservedAt;
    private ImmutableArray<CurrentProcessWitness> _currentGameWitnesses = [];

    public PersistedActiveRuleSnapshot(IClock? clock = null)
        : this(
            clock,
            configuredUserSid: null,
            UnavailableCurrentProcessWitnessProvider.Instance,
            processSnapshotRulesCaptured: null)
    {
    }

    public PersistedActiveRuleSnapshot(
        IClock clock,
        string configuredUserSid,
        ICurrentProcessWitnessProvider? currentProcessWitnessProvider = null)
        : this(
            clock ?? throw new ArgumentNullException(nameof(clock)),
            ValidateConfiguredUserSid(configuredUserSid),
            currentProcessWitnessProvider
                ?? UnavailableCurrentProcessWitnessProvider.Instance,
            processSnapshotRulesCaptured: null)
    {
    }

    internal PersistedActiveRuleSnapshot(
        IClock? clock,
        Action? processSnapshotRulesCaptured)
        : this(
            clock,
            configuredUserSid: null,
            UnavailableCurrentProcessWitnessProvider.Instance,
            processSnapshotRulesCaptured)
    {
    }

    private PersistedActiveRuleSnapshot(
        IClock? clock,
        string? configuredUserSid,
        ICurrentProcessWitnessProvider currentProcessWitnessProvider,
        Action? processSnapshotRulesCaptured)
    {
        _clock = clock;
        _configuredUserSid = configuredUserSid;
        _currentProcessWitnessProvider = currentProcessWitnessProvider;
        _processSnapshotRulesCaptured = processSnapshotRulesCaptured;
    }

    public ImmutableArray<string> GetSnapshot() => GetSnapshotResult().Identifiers;

    public AllowedProcessSnapshotResult GetSnapshotResult()
    {
        lock (_sync)
        {
            return CurrentResultUnderLock();
        }
    }

    public IDisposable? TryAcquireValidationLease(long? expectedGeneration)
    {
        if (expectedGeneration is null)
        {
            return null;
        }

        _generationGate.Wait();
        try
        {
            lock (_sync)
            {
                AllowedProcessSnapshotResult current = CurrentResultUnderLock();
                if (expectedGeneration != _snapshotGeneration
                    || !current.IsAvailable)
                {
                    _generationGate.Release();
                    return null;
                }
            }

            return new GenerationValidationLease(_generationGate);
        }
        catch (Exception)
        {
            _generationGate.Release();
            return null;
        }
    }

    public void Publish(ImmutableArray<AppRule> activeAppRules)
    {
        _ = new RuleSettingsState(ActiveAppRules: activeAppRules);
        _generationGate.Wait();
        try
        {
            lock (_sync)
            {
                _activeRules = activeAppRules;
                _activeRulesGeneration++;
                _snapshotGeneration++;
                SetUnavailableUnderLock("process-snapshot-unavailable");
            }
        }
        finally
        {
            _generationGate.Release();
        }
    }

    public void PublishProcessSnapshot(ProcessPersistenceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ImmutableArray<AppRule> activeRules;
        long activeRulesGeneration;
        lock (_sync)
        {
            activeRules = _activeRules;
            activeRulesGeneration = _activeRulesGeneration;
        }

        _processSnapshotRulesCaptured?.Invoke();
        SnapshotBuildResult replacement = BuildCurrentSnapshot(record, activeRules);

        _generationGate.Wait();
        try
        {
            lock (_sync)
            {
                if (activeRulesGeneration != _activeRulesGeneration)
                {
                    return;
                }

                _snapshotGeneration++;
                ClockObservation? observedAt = null;
                if (replacement.Result.IsAvailable)
                {
                    try
                    {
                        observedAt = Observe();
                    }
                    catch (Exception)
                    {
                        replacement = SnapshotBuildResult.Unavailable(
                            "process-snapshot-stale");
                    }
                }

                _current = Stamp(replacement.Result, _snapshotGeneration);
                _processSnapshotObservedAt = replacement.Result.IsAvailable
                    ? observedAt
                    : null;
                _currentGameWitnesses = replacement.Result.IsAvailable
                    ? replacement.GameWitnesses
                    : [];
            }
        }
        finally
        {
            _generationGate.Release();
        }
    }

    public void InvalidateProcessSnapshot()
    {
        _generationGate.Wait();
        try
        {
            lock (_sync)
            {
                _snapshotGeneration++;
                SetUnavailableUnderLock("process-snapshot-unavailable");
            }
        }
        finally
        {
            _generationGate.Release();
        }
    }

    private AllowedProcessSnapshotResult CurrentResultUnderLock()
    {
        if (!_current.IsAvailable)
        {
            return _current;
        }

        ClockObservation now;
        try
        {
            now = Observe();
        }
        catch (Exception)
        {
            return AllowedProcessSnapshotResult.Unavailable(
                "process-snapshot-stale",
                _snapshotGeneration);
        }

        if (!IsFresh(_processSnapshotObservedAt, now))
        {
            return AllowedProcessSnapshotResult.Unavailable(
                "process-snapshot-stale",
                _snapshotGeneration);
        }

        string? liveEvidenceFailure = ValidateCurrentGameWitnesses();
        return liveEvidenceFailure is null
            ? _current
            : AllowedProcessSnapshotResult.Unavailable(
                liveEvidenceFailure,
                _snapshotGeneration);
    }

    private string? ValidateCurrentGameWitnesses()
    {
        if (_currentGameWitnesses.IsDefaultOrEmpty)
        {
            return "no-current-restricted-game";
        }

        foreach (CurrentProcessWitness expected in _currentGameWitnesses)
        {
            if (!_currentProcessWitnessProvider.TryRead(
                    expected.ProcessId,
                    out CurrentProcessWitness current))
            {
                return "no-current-restricted-game";
            }

            if (!WitnessesMatch(expected, current))
            {
                return "process-snapshot-identity-untrusted";
            }
        }

        return null;
    }

    private void SetUnavailableUnderLock(string degradationCode)
    {
        _current = AllowedProcessSnapshotResult.Unavailable(
            degradationCode,
            _snapshotGeneration);
        _processSnapshotObservedAt = null;
        _currentGameWitnesses = [];
    }

    private ClockObservation Observe() => _clock?.Observe()
        ?? new(DateTimeOffset.UtcNow);

    private static bool IsFresh(
        ClockObservation? observed,
        ClockObservation current)
    {
        if (observed is not
            {
                Uptime: { } priorUptime,
                BootSessionId: { } priorBoot,
            }
            || current is not
            {
                Uptime: { } currentUptime,
                BootSessionId: { } currentBoot,
            })
        {
            return false;
        }

        return priorBoot != Guid.Empty
            && priorBoot == currentBoot
            && priorUptime >= TimeSpan.Zero
            && currentUptime >= priorUptime
            && currentUptime - priorUptime <= MaximumProcessSnapshotAge;
    }

    private SnapshotBuildResult BuildCurrentSnapshot(
        ProcessPersistenceRecord record,
        ImmutableArray<AppRule> activeRules)
    {
        if (record.Slot != ProcessPersistenceSlot.ProcessGateEnvelope
            || record.SchemaVersion != ProcessPersistenceLimits.CurrentSchemaVersion
            || record.Version < 1
            || !ProcessPersistenceLimits.IsValidPayload(
                record.PayloadJson,
                record.SchemaVersion))
        {
            return SnapshotBuildResult.Unavailable(
                "process-snapshot-unavailable");
        }

        if (_configuredUserSid is null)
        {
            return SnapshotBuildResult.Unavailable(
                "process-snapshot-identity-untrusted");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(record.PayloadJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("envelope", out JsonElement envelope)
                || envelope.ValueKind != JsonValueKind.Object
                || !envelope.TryGetProperty("revision", out JsonElement revision)
                || revision.ValueKind != JsonValueKind.Number
                || !revision.TryGetInt64(out long revisionValue)
                || revisionValue < 1
                || !envelope.TryGetProperty("reducerState", out JsonElement state)
                || state.ValueKind != JsonValueKind.Object
                || !ReadBoolean(state, "creationTimelineTrusted")
                || ReadBoolean(state, "morningReleased")
                || !state.TryGetProperty(
                    "observerContinuityEpoch",
                    out JsonElement epoch)
                || epoch.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(epoch.GetString())
                || !HasTrustedContinuity(
                    envelope,
                    epoch.GetString()!))
            {
                return SnapshotBuildResult.Unavailable(
                    "process-snapshot-continuity-untrusted");
            }

            if (!state.TryGetProperty("knownInstances", out JsonElement known)
                || known.ValueKind != JsonValueKind.Array
                || known.GetArrayLength() > MaximumPersistedInstances
                || !state.TryGetProperty(
                    "eligibleInstances",
                    out JsonElement eligible)
                || eligible.ValueKind != JsonValueKind.Array
                || eligible.GetArrayLength() > MaximumPersistedInstances
                || !state.TryGetProperty(
                    "taintedInstances",
                    out JsonElement tainted)
                || tainted.ValueKind != JsonValueKind.Array
                || tainted.GetArrayLength() > MaximumPersistedInstances)
            {
                return SnapshotBuildResult.Unavailable(
                    "process-snapshot-unavailable");
            }

            HashSet<ProcessKey> taintedKeys = [];
            foreach (JsonElement item in tainted.EnumerateArray())
            {
                if (!TryReadKey(item, out ProcessKey key)
                    || !taintedKeys.Add(key))
                {
                    return SnapshotBuildResult.Unavailable(
                        "process-snapshot-unavailable");
                }
            }

            Dictionary<ProcessKey, PersistedProcessIdentity> knownIdentities = [];
            foreach (JsonElement item in known.EnumerateArray())
            {
                if (!TryReadIdentity(
                        item,
                        out ProcessKey key,
                        out PersistedProcessIdentity identity)
                    || !knownIdentities.TryAdd(key, identity))
                {
                    return SnapshotBuildResult.Unavailable(
                        "process-snapshot-unavailable");
                }
            }

            Dictionary<ProcessKey, string> eligibleRules = [];
            foreach (JsonElement item in eligible.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("key", out JsonElement keyElement)
                    || !TryReadKey(keyElement, out ProcessKey key)
                    || !item.TryGetProperty("ruleId", out JsonElement ruleId)
                    || ruleId.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(ruleId.GetString())
                    || !knownIdentities.ContainsKey(key)
                    || !eligibleRules.TryAdd(key, ruleId.GetString()!))
                {
                    return SnapshotBuildResult.Unavailable(
                        "process-snapshot-unavailable");
                }
            }

            ImmutableArray<string>.Builder currentGameIds =
                ImmutableArray.CreateBuilder<string>();
            ImmutableArray<CurrentProcessWitness>.Builder currentWitnesses =
                ImmutableArray.CreateBuilder<CurrentProcessWitness>();
            foreach (AppRule rule in activeRules.Where(
                         rule => rule.Category == AppRuleCategory.Game
                             && rule.RootExecutablePath is not null))
            {
                CurrentProcessWitness? exactWitness = null;
                foreach ((ProcessKey key, string eligibleRuleId) in eligibleRules)
                {
                    if (taintedKeys.Contains(key)
                        || !string.Equals(
                            eligibleRuleId,
                            rule.Id,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    PersistedProcessIdentity identity = knownIdentities[key];
                    if (!string.Equals(
                            identity.UserSid,
                            _configuredUserSid,
                            StringComparison.Ordinal)
                        || !string.Equals(
                            identity.ExecutablePath,
                            rule.RootExecutablePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return SnapshotBuildResult.Unavailable(
                            "process-snapshot-identity-untrusted");
                    }

                    if (!_currentProcessWitnessProvider.TryRead(
                            key.Pid,
                            out CurrentProcessWitness current))
                    {
                        continue;
                    }

                    CurrentProcessWitness expected = new(
                        key.Pid,
                        key.CreationUtcTicks,
                        identity.ExecutablePath,
                        identity.SessionId);
                    if (!WitnessesMatch(expected, current))
                    {
                        return SnapshotBuildResult.Unavailable(
                            "process-snapshot-identity-untrusted");
                    }

                    exactWitness = expected;
                    break;
                }

                if (exactWitness is { } witness)
                {
                    currentGameIds.Add(rule.Id);
                    currentWitnesses.Add(witness);
                }
            }

            if (currentGameIds.Count == 0)
            {
                return SnapshotBuildResult.Unavailable(
                    "no-current-restricted-game");
            }

            return new(
                AllowedProcessSnapshotResult.Available(
                    currentGameIds.Concat(
                        activeRules
                            .Where(rule => rule.Category == AppRuleCategory.Voice)
                            .Select(rule => rule.Id))),
                currentWitnesses.ToImmutable());
        }
        catch (Exception exception) when (exception is JsonException
            or InvalidOperationException
            or ArgumentException
            or OverflowException)
        {
            return SnapshotBuildResult.Unavailable(
                "process-snapshot-unavailable");
        }
    }

    private static bool HasTrustedContinuity(
        JsonElement envelope,
        string observerEpoch)
    {
        if (!envelope.TryGetProperty(
                "observationContinuity",
                out JsonElement continuity)
            || continuity.ValueKind != JsonValueKind.Object
            || !TryReadBoolean(continuity, "isLost", out bool isLost)
            || isLost
            || !TryReadBoolean(
                continuity,
                "trustSeverPersisted",
                out bool trustSeverPersisted)
            || trustSeverPersisted
            || !continuity.TryGetProperty(
                "lastTrustedEpoch",
                out JsonElement lastTrustedEpoch)
            || lastTrustedEpoch.ValueKind != JsonValueKind.String
            || !string.Equals(
                lastTrustedEpoch.GetString(),
                observerEpoch,
                StringComparison.Ordinal)
            || !continuity.TryGetProperty("lossEpoch", out JsonElement lossEpoch)
            || lossEpoch.ValueKind != JsonValueKind.Null
            || !continuity.TryGetProperty("clockEpoch", out JsonElement clockEpoch)
            || clockEpoch.ValueKind != JsonValueKind.String
            || !string.Equals(
                clockEpoch.GetString(),
                observerEpoch,
                StringComparison.Ordinal)
            || !continuity.TryGetProperty(
                "sampleUtcHighWater",
                out JsonElement utcHighWater)
            || utcHighWater.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                utcHighWater.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsedUtc)
            || parsedUtc.Offset != TimeSpan.Zero
            || !continuity.TryGetProperty(
                "sampleMonotonicHighWater",
                out JsonElement monotonicHighWater)
            || monotonicHighWater.ValueKind != JsonValueKind.String
            || !TimeSpan.TryParse(
                monotonicHighWater.GetString(),
                CultureInfo.InvariantCulture,
                out TimeSpan parsedMonotonic)
            || parsedMonotonic < TimeSpan.Zero)
        {
            return false;
        }

        return true;
    }

    private static bool TryReadIdentity(
        JsonElement item,
        out ProcessKey key,
        out PersistedProcessIdentity identity)
    {
        key = default;
        identity = default;
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("key", out JsonElement keyElement)
            || !TryReadKey(keyElement, out key)
            || !item.TryGetProperty("value", out JsonElement value)
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("identity", out JsonElement identityElement)
            || identityElement.ValueKind != JsonValueKind.Object
            || !identityElement.TryGetProperty(
                "key",
                out JsonElement identityKeyElement)
            || !TryReadKey(identityKeyElement, out ProcessKey identityKey)
            || identityKey != key
            || !identityElement.TryGetProperty(
                "creationInstantUtc",
                out JsonElement created)
            || created.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                created.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset createdAt)
            || createdAt.Offset != TimeSpan.Zero
            || createdAt.UtcTicks != key.CreationUtcTicks
            || !identityElement.TryGetProperty(
                "executablePath",
                out JsonElement path)
            || path.ValueKind != JsonValueKind.String
            || !Win32ExecutablePathCanonicalizer.TryCanonicalize(
                path.GetString(),
                out string canonicalPath)
            || !string.Equals(
                path.GetString(),
                canonicalPath,
                StringComparison.OrdinalIgnoreCase)
            || !identityElement.TryGetProperty("userSid", out JsonElement sid)
            || sid.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(sid.GetString())
            || !sid.GetString()!.StartsWith("S-", StringComparison.Ordinal)
            || !identityElement.TryGetProperty(
                "sessionId",
                out JsonElement session)
            || session.ValueKind != JsonValueKind.Number
            || !session.TryGetInt32(out int sessionId)
            || sessionId < 0)
        {
            return false;
        }

        identity = new(canonicalPath, sid.GetString()!, sessionId);
        return true;
    }

    private static bool ReadBoolean(JsonElement value, string propertyName) =>
        TryReadBoolean(value, propertyName, out bool result) && result;

    private static bool TryReadBoolean(
        JsonElement value,
        string propertyName,
        out bool result)
    {
        result = false;
        if (!value.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        result = property.GetBoolean();
        return true;
    }

    private static bool TryReadKey(JsonElement value, out ProcessKey key)
    {
        key = default;
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("pid", out JsonElement pid)
            || pid.ValueKind != JsonValueKind.Number
            || !pid.TryGetInt32(out int processId)
            || processId <= 0
            || !value.TryGetProperty(
                "creationUtcTicks",
                out JsonElement ticks)
            || ticks.ValueKind != JsonValueKind.Number
            || !ticks.TryGetInt64(out long creationUtcTicks)
            || creationUtcTicks < DateTime.MinValue.Ticks
            || creationUtcTicks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        key = new(processId, creationUtcTicks);
        return true;
    }

    private static bool WitnessesMatch(
        CurrentProcessWitness expected,
        CurrentProcessWitness current) =>
        expected.ProcessId == current.ProcessId
        && expected.CreationUtcTicks == current.CreationUtcTicks
        && expected.SessionId == current.SessionId
        && string.Equals(
            expected.ExecutablePath,
            current.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);

    private static AllowedProcessSnapshotResult Stamp(
        AllowedProcessSnapshotResult result,
        long generation) => result.IsAvailable
            ? AllowedProcessSnapshotResult.Available(
                result.Identifiers,
                generation)
            : AllowedProcessSnapshotResult.Unavailable(
                result.DegradationCode ?? "process-snapshot-unavailable",
                generation);

    private static string ValidateConfiguredUserSid(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !value.StartsWith("S-", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The configured user SID must be canonical.",
                nameof(value));
        }

        return value;
    }

    private readonly record struct ProcessKey(int Pid, long CreationUtcTicks);

    private readonly record struct PersistedProcessIdentity(
        string ExecutablePath,
        string UserSid,
        int SessionId);

    private sealed record SnapshotBuildResult(
        AllowedProcessSnapshotResult Result,
        ImmutableArray<CurrentProcessWitness> GameWitnesses)
    {
        public static SnapshotBuildResult Unavailable(string degradationCode) =>
            new(
                AllowedProcessSnapshotResult.Unavailable(degradationCode),
                []);
    }

    private sealed class GenerationValidationLease(SemaphoreSlim gate) :
        IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
