using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NightGate.Desktop;

public sealed record LegacyShutdownTaskCandidate(
    string TaskPath,
    string ActionFingerprint,
    bool WasEnabled);

public sealed record LegacyShutdownTaskScanResult(
    bool Available,
    string? Error,
    IReadOnlyList<LegacyShutdownTaskCandidate> Candidates)
{
    public static LegacyShutdownTaskScanResult Unavailable(string error) => new(
        false,
        error,
        Array.Empty<LegacyShutdownTaskCandidate>());
}

public enum LegacyTaskMutationStatus
{
    Disabled,
    Restored,
    Unchanged,
    Changed,
    Missing,
    Unavailable,
    Invalid,
}

public sealed record LegacyTaskMutationResult(
    string TaskPath,
    string ActionFingerprint,
    LegacyTaskMutationStatus Status);

public enum LegacyTaskObservationStatus
{
    MatchingEnabled,
    MatchingDisabled,
    Changed,
    Missing,
    Unavailable,
    Invalid,
}

public sealed record LegacyTaskObservationResult(
    string TaskPath,
    string ActionFingerprint,
    LegacyTaskObservationStatus Status);

internal enum LegacyScheduledTaskActionKind
{
    Execute,
    Other,
}

internal sealed record LegacyScheduledTaskActionPropertySnapshot(
    string Name,
    string? Value);

internal sealed record LegacyScheduledTaskActionSnapshot(
    LegacyScheduledTaskActionKind Kind,
    string? ExecutablePath,
    string? Arguments,
    string? WorkingDirectory = null,
    int NativeType = 0,
    string? ActionId = null,
    IReadOnlyList<LegacyScheduledTaskActionPropertySnapshot>? Properties = null);

internal sealed record LegacyScheduledTaskSnapshot(
    string TaskPath,
    bool Enabled,
    IReadOnlyList<LegacyScheduledTaskActionSnapshot> Actions,
    string DefinitionFingerprint,
    DateTimeOffset? LastRunTimeUtc = null,
    int? LastTaskResult = null);

internal sealed record LegacyScheduledTaskEnumerationResult(
    bool Complete,
    IReadOnlyList<LegacyScheduledTaskSnapshot> Tasks)
{
    public static LegacyScheduledTaskEnumerationResult Unavailable { get; } =
        new(false, Array.Empty<LegacyScheduledTaskSnapshot>());
}

internal enum LegacyScheduledTaskReadStatus
{
    Found,
    Missing,
    Unavailable,
}

internal sealed record LegacyScheduledTaskReadResult(
    LegacyScheduledTaskReadStatus Status,
    LegacyScheduledTaskSnapshot? Task)
{
    public static LegacyScheduledTaskReadResult Found(
        LegacyScheduledTaskSnapshot task) => new(
        LegacyScheduledTaskReadStatus.Found,
        task);

    public static LegacyScheduledTaskReadResult Missing { get; } = new(
        LegacyScheduledTaskReadStatus.Missing,
        null);

    public static LegacyScheduledTaskReadResult Unavailable { get; } = new(
        LegacyScheduledTaskReadStatus.Unavailable,
        null);
}

internal enum LegacyScheduledTaskSetEnabledStatus
{
    Updated,
    Unchanged,
    Changed,
    Missing,
    Unavailable,
}

internal interface ILegacyScheduledTaskPlatform
{
    LegacyScheduledTaskEnumerationResult Enumerate(
        CancellationToken cancellationToken = default);

    LegacyScheduledTaskReadResult Read(
        string taskPath,
        CancellationToken cancellationToken = default);

    LegacyScheduledTaskSetEnabledStatus TrySetEnabled(
        LegacyScheduledTaskSnapshot expectedTask,
        bool enabled,
        CancellationToken cancellationToken = default);
}

internal static class LegacyScheduledTaskSnapshotComparer
{
    public static bool EqualsExact(
        LegacyScheduledTaskSnapshot? left,
        LegacyScheduledTaskSnapshot? right)
    {
        if (left is null
            || right is null
            || !string.Equals(left.TaskPath, right.TaskPath, StringComparison.Ordinal)
            || left.Enabled != right.Enabled
            || !string.Equals(
                left.DefinitionFingerprint,
                right.DefinitionFingerprint,
                StringComparison.Ordinal)
            || left.Actions is null
            || right.Actions is null
            || left.Actions.Count != right.Actions.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Actions.Count; index++)
        {
            LegacyScheduledTaskActionSnapshot? leftAction = left.Actions[index];
            LegacyScheduledTaskActionSnapshot? rightAction = right.Actions[index];
            if (leftAction is null
                || rightAction is null
                || leftAction.Kind != rightAction.Kind
                || !string.Equals(
                    leftAction.ExecutablePath,
                    rightAction.ExecutablePath,
                    StringComparison.Ordinal)
                || !string.Equals(
                    leftAction.Arguments,
                    rightAction.Arguments,
                    StringComparison.Ordinal)
                || !string.Equals(
                    leftAction.WorkingDirectory,
                    rightAction.WorkingDirectory,
                    StringComparison.Ordinal)
                || leftAction.NativeType != rightAction.NativeType
                || !string.Equals(
                    leftAction.ActionId,
                    rightAction.ActionId,
                    StringComparison.Ordinal)
                || !PropertiesEqual(
                    leftAction.Properties,
                    rightAction.Properties))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PropertiesEqual(
        IReadOnlyList<LegacyScheduledTaskActionPropertySnapshot>? left,
        IReadOnlyList<LegacyScheduledTaskActionPropertySnapshot>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            LegacyScheduledTaskActionPropertySnapshot? leftProperty = left[index];
            LegacyScheduledTaskActionPropertySnapshot? rightProperty = right[index];
            if (leftProperty is null
                || rightProperty is null
                || !string.Equals(
                    leftProperty.Name,
                    rightProperty.Name,
                    StringComparison.Ordinal)
                || !string.Equals(
                    leftProperty.Value,
                    rightProperty.Value,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

public interface ILegacyShutdownTaskAdapter
{
    IReadOnlyList<LegacyShutdownTaskCandidate> Scan(
        CancellationToken cancellationToken = default);

    LegacyShutdownTaskScanResult ScanWithStatus(
        CancellationToken cancellationToken = default);

    IReadOnlyList<LegacyTaskMutationResult> DisableSelected(
        IEnumerable<LegacyShutdownTaskCandidate>? selectedCandidates,
        CancellationToken cancellationToken = default);

    IReadOnlyList<LegacyTaskMutationResult> Restore(
        IEnumerable<LegacyShutdownTaskCandidate>? persistedCandidates,
        CancellationToken cancellationToken = default);

    IReadOnlyList<LegacyTaskMutationResult> ReconcilePrepared(
        IEnumerable<LegacyShutdownTaskCandidate>? persistedCandidates,
        CancellationToken cancellationToken = default);

    IReadOnlyList<LegacyTaskObservationResult> Observe(
        IEnumerable<LegacyShutdownTaskCandidate>? persistedCandidates,
        CancellationToken cancellationToken = default);
}

public sealed record LegacyShutdownTaskRuntimeEvidence(
    string TaskPath,
    string ActionFingerprint,
    LegacyTaskObservationStatus Status,
    bool? Enabled,
    DateTimeOffset? LastRunTimeUtc,
    int? LastTaskResult);

public interface ILegacyShutdownTaskEvidenceReader
{
    IReadOnlyList<LegacyShutdownTaskRuntimeEvidence> ReadRuntimeEvidence(
        IEnumerable<LegacyShutdownTaskCandidate>? persistedCandidates,
        CancellationToken cancellationToken = default);
}

public sealed class LegacyShutdownTaskAdapter :
    ILegacyShutdownTaskAdapter,
    ILegacyShutdownTaskEvidenceReader
{
    private const uint MaximumShutdownTimeoutSeconds = 315_360_000;
    private readonly ILegacyScheduledTaskPlatform _platform;
    private readonly Func<string, string?> _expandEnvironment;
    private readonly object _scanLock = new();
    private readonly object _legacyDefinitionLock = new();
    private readonly Dictionary<string, string> _trustedLegacyDefinitions = new(
        StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, LegacyShutdownTaskCandidate> _latestScan =
        new Dictionary<string, LegacyShutdownTaskCandidate>(
            StringComparer.OrdinalIgnoreCase);

    public LegacyShutdownTaskAdapter()
        : this(
            new WindowsTaskSchedulerPlatform(),
            Environment.ExpandEnvironmentVariables)
    {
    }

    internal LegacyShutdownTaskAdapter(
        ILegacyScheduledTaskPlatform platform,
        Func<string, string?> expandEnvironment)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(expandEnvironment);
        _platform = platform;
        _expandEnvironment = expandEnvironment;
    }

    public IReadOnlyList<LegacyShutdownTaskCandidate> Scan(
        CancellationToken cancellationToken = default) =>
        ScanWithStatus(cancellationToken).Candidates;

    public LegacyShutdownTaskScanResult ScanWithStatus(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LegacyScheduledTaskEnumerationResult enumeration;
        try
        {
            enumeration = _platform.Enumerate(cancellationToken);
            if (enumeration is null || enumeration.Tasks is null)
            {
                StoreLatestScan([]);
                return LegacyShutdownTaskScanResult.Unavailable(
                    "scheduler-unavailable");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            StoreLatestScan([]);
            return LegacyShutdownTaskScanResult.Unavailable(
                "scheduler-unavailable");
        }

        IReadOnlyList<LegacyScheduledTaskSnapshot> tasks = enumeration.Tasks;
        List<LegacyShutdownTaskCandidate> candidates = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        bool scanComplete = enumeration.Complete;
        try
        {
            foreach (LegacyScheduledTaskSnapshot? task in tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (task is not null
                        && paths.Add(task.TaskPath)
                        && TryClassify(task, out string fingerprint))
                    {
                        candidates.Add(new(task.TaskPath, fingerprint, task.Enabled));
                    }
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    // One inaccessible or malformed task cannot hide other candidates.
                    scanComplete = false;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Preserve candidates read before an unavailable collection failed.
            scanComplete = false;
        }

        LegacyShutdownTaskCandidate[] result = candidates.ToArray();
        StoreLatestScan(scanComplete ? result : []);
        return scanComplete
            ? new(true, null, result)
            : new(false, "scheduler-incomplete", result);
    }

    public IReadOnlyList<LegacyTaskMutationResult> DisableSelected(
        IEnumerable<LegacyShutdownTaskCandidate>? selectedCandidates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, LegacyShutdownTaskCandidate> latest;
        lock (_scanLock)
        {
            latest = _latestScan;
        }

        return ProcessRecords(
            selectedCandidates,
            candidate =>
            {
                if (!latest.TryGetValue(candidate.TaskPath, out var scanned)
                    || !EqualsExact(scanned, candidate))
                {
                    return Result(candidate, LegacyTaskMutationStatus.Invalid);
                }

                return DisableOne(candidate, cancellationToken);
            },
            cancellationToken);
    }

    public IReadOnlyList<LegacyTaskMutationResult> Restore(
        IEnumerable<LegacyShutdownTaskCandidate>? persistedCandidates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ProcessRecords(
            persistedCandidates,
            candidate => RestoreOne(candidate, cancellationToken),
            cancellationToken);
    }

    public IReadOnlyList<LegacyTaskMutationResult> ReconcilePrepared(
        IEnumerable<LegacyShutdownTaskCandidate>? persistedCandidates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ProcessRecords(
            persistedCandidates,
            candidate => DisableOne(candidate, cancellationToken),
            cancellationToken);
    }

    public IReadOnlyList<LegacyTaskObservationResult> Observe(
        IEnumerable<LegacyShutdownTaskCandidate>? persistedCandidates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (persistedCandidates is null)
        {
            return [];
        }

        List<LegacyTaskObservationResult> results = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (LegacyShutdownTaskCandidate? candidate in persistedCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate is null
                    || !IsValidRecord(candidate)
                    || !paths.Add(candidate.TaskPath))
                {
                    results.Add(new(
                        candidate?.TaskPath ?? string.Empty,
                        candidate?.ActionFingerprint ?? string.Empty,
                        LegacyTaskObservationStatus.Invalid));
                    continue;
                }

                try
                {
                    results.Add(ObserveOne(candidate, cancellationToken));
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    results.Add(Observation(
                        candidate,
                        LegacyTaskObservationStatus.Unavailable));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // A hostile or failing enumerable cannot escape this read-only boundary.
        }

        return results.ToArray();
    }

    public IReadOnlyList<LegacyShutdownTaskRuntimeEvidence> ReadRuntimeEvidence(
        IEnumerable<LegacyShutdownTaskCandidate>? persistedCandidates,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (persistedCandidates is null)
        {
            return [];
        }

        List<LegacyShutdownTaskRuntimeEvidence> results = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (LegacyShutdownTaskCandidate? candidate in persistedCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate is null
                    || !IsValidRecord(candidate)
                    || !paths.Add(candidate.TaskPath))
                {
                    results.Add(RuntimeEvidence(
                        candidate,
                        LegacyTaskObservationStatus.Invalid,
                        task: null));
                    continue;
                }

                try
                {
                    results.Add(ReadRuntimeEvidenceOne(candidate, cancellationToken));
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    results.Add(RuntimeEvidence(
                        candidate,
                        LegacyTaskObservationStatus.Unavailable,
                        task: null));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // A failing enumerable cannot turn diagnostic evidence into a task effect.
        }

        return results.ToArray();
    }

    private static IReadOnlyList<LegacyTaskMutationResult> ProcessRecords(
        IEnumerable<LegacyShutdownTaskCandidate>? records,
        Func<LegacyShutdownTaskCandidate, LegacyTaskMutationResult> process,
        CancellationToken cancellationToken)
    {
        if (records is null)
        {
            return [];
        }

        List<LegacyTaskMutationResult> results = [];
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (LegacyShutdownTaskCandidate? candidate in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate is null
                    || !IsValidRecord(candidate)
                    || !paths.Add(candidate.TaskPath))
                {
                    results.Add(new(
                        candidate?.TaskPath ?? string.Empty,
                        candidate?.ActionFingerprint ?? string.Empty,
                        LegacyTaskMutationStatus.Invalid));
                    continue;
                }

                try
                {
                    results.Add(process(candidate));
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    results.Add(Result(
                        candidate,
                        LegacyTaskMutationStatus.Unavailable));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // A hostile or failing enumerable cannot escape the onboarding boundary.
        }

        return results.ToArray();
    }

    private LegacyTaskMutationResult DisableOne(
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken)
    {
        LegacyTaskMutationResult? failure = ReadExact(
            candidate,
            cancellationToken,
            LegacyReadPurpose.Disable,
            out LegacyScheduledTaskSnapshot? task,
            out bool legacyMatch);
        if (failure is not null)
        {
            return failure;
        }

        if (task!.Enabled != candidate.WasEnabled)
        {
            return Result(
                candidate,
                task.Enabled
                    ? LegacyTaskMutationStatus.Changed
                    : LegacyTaskMutationStatus.Unchanged);
        }

        if (!candidate.WasEnabled)
        {
            return Result(candidate, LegacyTaskMutationStatus.Unchanged);
        }

        LegacyTaskMutationResult result = SetEnabled(
            candidate,
            task,
            enabled: false,
            LegacyTaskMutationStatus.Disabled,
            cancellationToken);
        if (legacyMatch && result.Status == LegacyTaskMutationStatus.Disabled)
        {
            RememberLegacyDefinition(candidate, task.DefinitionFingerprint);
        }

        return result;
    }

    private LegacyTaskObservationResult ObserveOne(
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken)
    {
        LegacyTaskMutationResult? failure = ReadExact(
            candidate,
            cancellationToken,
            LegacyReadPurpose.Observe,
            out LegacyScheduledTaskSnapshot? task,
            out _);
        if (failure is not null)
        {
            return Observation(
                candidate,
                failure.Status switch
                {
                    LegacyTaskMutationStatus.Changed =>
                        LegacyTaskObservationStatus.Changed,
                    LegacyTaskMutationStatus.Missing =>
                        LegacyTaskObservationStatus.Missing,
                    LegacyTaskMutationStatus.Unavailable =>
                        LegacyTaskObservationStatus.Unavailable,
                    _ => LegacyTaskObservationStatus.Invalid,
                });
        }

        return Observation(
            candidate,
            task!.Enabled
                ? LegacyTaskObservationStatus.MatchingEnabled
                : LegacyTaskObservationStatus.MatchingDisabled);
    }

    private LegacyShutdownTaskRuntimeEvidence ReadRuntimeEvidenceOne(
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken)
    {
        LegacyTaskMutationResult? failure = ReadExact(
            candidate,
            cancellationToken,
            LegacyReadPurpose.Observe,
            out LegacyScheduledTaskSnapshot? task,
            out _);
        LegacyTaskObservationStatus status = failure is null
            ? task!.Enabled
                ? LegacyTaskObservationStatus.MatchingEnabled
                : LegacyTaskObservationStatus.MatchingDisabled
            : failure.Status switch
            {
                LegacyTaskMutationStatus.Changed =>
                    LegacyTaskObservationStatus.Changed,
                LegacyTaskMutationStatus.Missing =>
                    LegacyTaskObservationStatus.Missing,
                LegacyTaskMutationStatus.Unavailable =>
                    LegacyTaskObservationStatus.Unavailable,
                _ => LegacyTaskObservationStatus.Invalid,
            };
        return RuntimeEvidence(candidate, status, task);
    }

    private LegacyTaskMutationResult RestoreOne(
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken)
    {
        LegacyTaskMutationResult? failure = ReadExact(
            candidate,
            cancellationToken,
            LegacyReadPurpose.Restore,
            out LegacyScheduledTaskSnapshot? task,
            out bool legacyMatch);
        if (failure is not null)
        {
            return failure;
        }

        if (!candidate.WasEnabled || task!.Enabled)
        {
            return Result(candidate, LegacyTaskMutationStatus.Unchanged);
        }

        LegacyTaskMutationResult result = SetEnabled(
            candidate,
            task,
            enabled: true,
            LegacyTaskMutationStatus.Restored,
            cancellationToken);
        if (legacyMatch && result.Status == LegacyTaskMutationStatus.Restored)
        {
            ForgetLegacyDefinition(candidate);
        }

        return result;
    }

    private LegacyTaskMutationResult? ReadExact(
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken,
        LegacyReadPurpose purpose,
        out LegacyScheduledTaskSnapshot? task,
        out bool legacyMatch)
    {
        task = null;
        legacyMatch = false;
        LegacyScheduledTaskReadResult read;
        try
        {
            read = _platform.Read(candidate.TaskPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Result(candidate, LegacyTaskMutationStatus.Unavailable);
        }

        if (read is null
            || read.Status == LegacyScheduledTaskReadStatus.Unavailable)
        {
            return Result(candidate, LegacyTaskMutationStatus.Unavailable);
        }

        if (read.Status == LegacyScheduledTaskReadStatus.Missing)
        {
            return Result(candidate, LegacyTaskMutationStatus.Missing);
        }

        task = read.Task;
        if (read.Status != LegacyScheduledTaskReadStatus.Found
            || task is null
            || !string.Equals(
                task.TaskPath,
                candidate.TaskPath,
                StringComparison.Ordinal)
            || !TryClassify(task, out string currentFingerprint))
        {
            return Result(candidate, LegacyTaskMutationStatus.Changed);
        }

        if (string.Equals(
                currentFingerprint,
                candidate.ActionFingerprint,
                StringComparison.Ordinal))
        {
            return null;
        }

        legacyMatch = (TryClassifyActions(task, out string actionFingerprint)
                && string.Equals(
                    actionFingerprint,
                    candidate.ActionFingerprint,
                    StringComparison.Ordinal))
            || MatchesVersion033SimpleFingerprint(
                task,
                candidate.ActionFingerprint);
        bool mayCaptureBeforeDisable = purpose == LegacyReadPurpose.Disable
            && task.Enabled;
        bool hasTrustedDefinition = IsTrustedLegacyDefinition(
            candidate,
            task.DefinitionFingerprint);
        if (!legacyMatch || !mayCaptureBeforeDisable && !hasTrustedDefinition)
        {
            legacyMatch = false;
            return Result(candidate, LegacyTaskMutationStatus.Changed);
        }

        return null;
    }

    private void RememberLegacyDefinition(
        LegacyShutdownTaskCandidate candidate,
        string definitionFingerprint)
    {
        lock (_legacyDefinitionLock)
        {
            _trustedLegacyDefinitions[LegacyDefinitionKey(candidate)] =
                definitionFingerprint;
        }
    }

    private bool IsTrustedLegacyDefinition(
        LegacyShutdownTaskCandidate candidate,
        string definitionFingerprint)
    {
        lock (_legacyDefinitionLock)
        {
            return _trustedLegacyDefinitions.TryGetValue(
                    LegacyDefinitionKey(candidate),
                    out string? trusted)
                && string.Equals(
                    trusted,
                    definitionFingerprint,
                    StringComparison.Ordinal);
        }
    }

    private void ForgetLegacyDefinition(LegacyShutdownTaskCandidate candidate)
    {
        lock (_legacyDefinitionLock)
        {
            _trustedLegacyDefinitions.Remove(LegacyDefinitionKey(candidate));
        }
    }

    private static string LegacyDefinitionKey(
        LegacyShutdownTaskCandidate candidate) =>
        candidate.TaskPath + "\0" + candidate.ActionFingerprint;

    private bool MatchesVersion033SimpleFingerprint(
        LegacyScheduledTaskSnapshot task,
        string expectedFingerprint)
    {
        // Version 0.3.3 persisted only normalized executable and arguments.
        // Its hash is safe to honor only when every action property omitted by
        // that format is now provably default and there is exactly one action.
        if (task.Actions is not { Count: 1 }
            || task.Actions[0] is not { } action
            || action.Kind != LegacyScheduledTaskActionKind.Execute
            || action.NativeType != 0
            || !string.IsNullOrEmpty(action.WorkingDirectory)
            || !string.IsNullOrEmpty(action.ActionId)
            || action.Properties is not null
            || !TryNormalizeVersion033Executable(
                action.ExecutablePath,
                out string executable)
            || !TryNormalizeArguments(action.Arguments, out string arguments))
        {
            return false;
        }

        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(executable + "\n" + arguments)));
        return string.Equals(
            fingerprint,
            expectedFingerprint,
            StringComparison.Ordinal);
    }

    private bool TryNormalizeVersion033Executable(
        string? value,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim();
        bool beginsQuoted = candidate.StartsWith('"');
        bool endsQuoted = candidate.EndsWith('"');
        if (beginsQuoted != endsQuoted)
        {
            return false;
        }

        if (beginsQuoted)
        {
            candidate = candidate[1..^1];
            if (candidate.Length == 0 || candidate.Contains('"'))
            {
                return false;
            }
        }

        string? expanded = _expandEnvironment(candidate);
        if (string.IsNullOrWhiteSpace(expanded)
            || expanded.Contains('%')
            || expanded.Contains('"')
            || !Path.IsPathFullyQualified(expanded)
            || !TryNormalizeExecutable(
                value: value,
                workingDirectory: null,
                normalized: out normalized))
        {
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    private LegacyTaskMutationResult SetEnabled(
        LegacyShutdownTaskCandidate candidate,
        LegacyScheduledTaskSnapshot expectedTask,
        bool enabled,
        LegacyTaskMutationStatus success,
        CancellationToken cancellationToken)
    {
        LegacyScheduledTaskSetEnabledStatus status;
        try
        {
            status = _platform.TrySetEnabled(
                expectedTask,
                enabled,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return Result(candidate, LegacyTaskMutationStatus.Unavailable);
        }

        return Result(
            candidate,
            status switch
            {
                LegacyScheduledTaskSetEnabledStatus.Updated => success,
                LegacyScheduledTaskSetEnabledStatus.Unchanged =>
                    LegacyTaskMutationStatus.Unchanged,
                LegacyScheduledTaskSetEnabledStatus.Changed =>
                    LegacyTaskMutationStatus.Changed,
                LegacyScheduledTaskSetEnabledStatus.Missing =>
                    LegacyTaskMutationStatus.Missing,
                _ => LegacyTaskMutationStatus.Unavailable,
            });
    }

    private static bool IsValidRecord(LegacyShutdownTaskCandidate candidate) =>
        candidate.TaskPath is { Length: >= 1 and <= 1_024 }
        && candidate.TaskPath.StartsWith("\\", StringComparison.Ordinal)
        && !candidate.TaskPath.Contains('\0')
        && candidate.ActionFingerprint is { Length: 64 }
        && candidate.ActionFingerprint.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool EqualsExact(
        LegacyShutdownTaskCandidate left,
        LegacyShutdownTaskCandidate right) =>
        string.Equals(left.TaskPath, right.TaskPath, StringComparison.Ordinal)
        && string.Equals(
            left.ActionFingerprint,
            right.ActionFingerprint,
            StringComparison.Ordinal)
        && left.WasEnabled == right.WasEnabled;

    private static LegacyTaskMutationResult Result(
        LegacyShutdownTaskCandidate candidate,
        LegacyTaskMutationStatus status) => new(
        candidate.TaskPath,
        candidate.ActionFingerprint,
        status);

    private static LegacyTaskObservationResult Observation(
        LegacyShutdownTaskCandidate candidate,
        LegacyTaskObservationStatus status) => new(
        candidate.TaskPath,
        candidate.ActionFingerprint,
        status);

    private static LegacyShutdownTaskRuntimeEvidence RuntimeEvidence(
        LegacyShutdownTaskCandidate? candidate,
        LegacyTaskObservationStatus status,
        LegacyScheduledTaskSnapshot? task) => new(
        candidate?.TaskPath ?? string.Empty,
        candidate?.ActionFingerprint ?? string.Empty,
        status,
        task?.Enabled,
        task?.LastRunTimeUtc,
        task?.LastTaskResult);

    private void StoreLatestScan(
        IReadOnlyList<LegacyShutdownTaskCandidate> candidates)
    {
        Dictionary<string, LegacyShutdownTaskCandidate> latest =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (LegacyShutdownTaskCandidate candidate in candidates)
        {
            latest[candidate.TaskPath] = candidate;
        }

        lock (_scanLock)
        {
            _latestScan = latest;
        }
    }

    private bool TryClassify(
        LegacyScheduledTaskSnapshot task,
        out string fingerprint)
    {
        fingerprint = string.Empty;
        if (!IsValidFingerprint(task.DefinitionFingerprint)
            || !TryClassifyActions(task, out string actionFingerprint))
        {
            return false;
        }

        StringBuilder canonical = new();
        AppendFingerprintField(canonical, "definition-v1");
        AppendFingerprintField(canonical, task.DefinitionFingerprint);
        AppendFingerprintField(canonical, "actions-v1");
        AppendFingerprintField(canonical, actionFingerprint);
        fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return true;
    }

    private bool TryClassifyActions(
        LegacyScheduledTaskSnapshot task,
        out string fingerprint)
    {
        fingerprint = string.Empty;
        if (string.IsNullOrWhiteSpace(task.TaskPath)
            || !task.TaskPath.StartsWith("\\", StringComparison.Ordinal)
            || task.Actions is null
            || task.Actions.Count == 0)
        {
            return false;
        }

        bool invokesShutdown = false;
        StringBuilder canonical = new();
        for (int index = 0; index < task.Actions.Count; index++)
        {
            LegacyScheduledTaskActionSnapshot? action = task.Actions[index];
            if (action is null)
            {
                return false;
            }

            AppendFingerprintField(canonical, index.ToString(CultureInfo.InvariantCulture));
            AppendFingerprintField(canonical, ((int)action.Kind).ToString(CultureInfo.InvariantCulture));
            AppendFingerprintField(canonical, action.NativeType.ToString(CultureInfo.InvariantCulture));
            AppendFingerprintField(canonical, action.WorkingDirectory ?? string.Empty);
            AppendNullableFingerprintField(canonical, action.ActionId);
            if (action.Properties is null)
            {
                AppendFingerprintField(canonical, "no-properties");
            }
            else
            {
                AppendFingerprintField(
                    canonical,
                    action.Properties.Count.ToString(CultureInfo.InvariantCulture));
                foreach (LegacyScheduledTaskActionPropertySnapshot? property in
                         action.Properties)
                {
                    if (property is null || string.IsNullOrEmpty(property.Name))
                    {
                        return false;
                    }

                    AppendFingerprintField(canonical, property.Name);
                    AppendNullableFingerprintField(canonical, property.Value);
                }
            }

            if (action.Kind == LegacyScheduledTaskActionKind.Execute
                && TryNormalizeShutdownAction(
                    action.ExecutablePath,
                    action.Arguments,
                    action.WorkingDirectory,
                    out string executable,
                    out string arguments))
            {
                invokesShutdown = true;
                AppendFingerprintField(canonical, "shutdown");
                AppendFingerprintField(canonical, executable);
                AppendFingerprintField(canonical, arguments);
            }
            else
            {
                AppendFingerprintField(canonical, "other");
                AppendFingerprintField(canonical, action.ExecutablePath ?? string.Empty);
                AppendFingerprintField(canonical, action.Arguments ?? string.Empty);
            }
        }

        if (!invokesShutdown)
        {
            return false;
        }

        fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return true;
    }

    private static bool IsValidFingerprint(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void AppendNullableFingerprintField(
        StringBuilder destination,
        string? value)
    {
        AppendFingerprintField(destination, value is null ? "null" : "value");
        if (value is not null)
        {
            AppendFingerprintField(destination, value);
        }
    }

    private static void AppendFingerprintField(StringBuilder destination, string value)
    {
        destination.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        destination.Append(':');
        destination.Append(value);
        destination.Append(';');
    }

    private bool TryNormalizeShutdownAction(
        string? executablePath,
        string? arguments,
        string? workingDirectory,
        out string executable,
        out string normalizedArguments)
    {
        executable = string.Empty;
        normalizedArguments = string.Empty;
        if (TryNormalizeExecutable(
                executablePath,
                workingDirectory,
                out executable)
            && TryNormalizeArguments(arguments, out normalizedArguments))
        {
            return true;
        }

        if (TryNormalizeCmdExecutable(
                executablePath,
                workingDirectory,
                out executable)
            && TryNormalizeCmdArguments(arguments, out normalizedArguments))
        {
            return true;
        }

        if (TryNormalizePowerShellExecutable(
                executablePath,
                workingDirectory,
                out executable)
            && TryNormalizePowerShellArguments(arguments, out normalizedArguments))
        {
            return true;
        }

        executable = string.Empty;
        normalizedArguments = string.Empty;
        return false;
    }

    private bool TryNormalizeExecutable(
        string? value,
        string? workingDirectory,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim();
        bool beginsQuoted = candidate.StartsWith('"');
        bool endsQuoted = candidate.EndsWith('"');
        if (beginsQuoted != endsQuoted)
        {
            return false;
        }

        if (beginsQuoted)
        {
            candidate = candidate[1..^1];
            if (candidate.Length == 0 || candidate.Contains('"'))
            {
                return false;
            }
        }

        if (!TryGetWindowsSystemBinaryPaths(
                "shutdown.exe",
                relativeSubdirectory: null,
                out string system32Path,
                out string sysWow64Path,
                out string sysNativePath))
        {
            return false;
        }

        string? expanded = _expandEnvironment(candidate);
        if (string.IsNullOrWhiteSpace(expanded)
            || expanded.Contains('%')
            || expanded.Contains('"'))
        {
            return false;
        }

        string resolvedPath;
        if (Path.IsPathFullyQualified(expanded))
        {
            resolvedPath = NormalizeFullPath(expanded);
        }
        else
        {
            if (!(string.Equals(
                    candidate,
                    "shutdown",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    candidate,
                    "shutdown.exe",
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                resolvedPath = system32Path;
            }
            else
            {
                if (!TryExpandAbsolutePath(
                        workingDirectory,
                        out string expandedWorkingDirectory))
                {
                    return false;
                }

                resolvedPath = Path.Combine(expandedWorkingDirectory, "shutdown.exe");
            }
        }

        if (!TryMatchAllowedPath(
                resolvedPath,
                [system32Path, sysWow64Path, sysNativePath],
                out string matchedPath))
        {
            return false;
        }

        normalized = matchedPath.ToUpperInvariant();
        return true;
    }

    private bool TryNormalizeCmdExecutable(
        string? value,
        string? workingDirectory,
        out string normalized)
    {
        normalized = string.Empty;
        if (!TryGetWindowsSystemBinaryPaths(
                "cmd.exe",
                relativeSubdirectory: null,
                out string system32Path,
                out string sysWow64Path,
                out string sysNativePath)
            || !TryNormalizeKnownExecutable(
                value,
                workingDirectory,
                ["cmd", "cmd.exe"],
                [system32Path, sysWow64Path, sysNativePath],
                system32Path,
                out string path))
        {
            return false;
        }

        normalized = "CMD:" + path.ToUpperInvariant();
        return true;
    }

    private bool TryNormalizePowerShellExecutable(
        string? value,
        string? workingDirectory,
        out string normalized)
    {
        normalized = string.Empty;
        if (TryGetWindowsSystemBinaryPaths(
                "powershell.exe",
                @"WindowsPowerShell\v1.0",
                out string system32Path,
                out string sysWow64Path,
                out string sysNativePath)
            && TryNormalizeKnownExecutable(
                value,
                workingDirectory,
                ["powershell", "powershell.exe"],
                [system32Path, sysWow64Path, sysNativePath],
                system32Path,
                out string windowsPowerShellPath))
        {
            normalized = "POWERSHELL:" + windowsPowerShellPath.ToUpperInvariant();
            return true;
        }

        if (!TryUnquoteExecutable(value, out string candidate)
            || !(string.Equals(candidate, "pwsh", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate, "pwsh.exe", StringComparison.OrdinalIgnoreCase)
                || Path.IsPathFullyQualified(
                    _expandEnvironment(candidate) ?? string.Empty)))
        {
            return false;
        }

        string? expanded = _expandEnvironment(candidate);
        if (string.IsNullOrWhiteSpace(expanded)
            || expanded.Contains('%')
            || expanded.Contains('"'))
        {
            return false;
        }

        if (!Path.IsPathFullyQualified(expanded))
        {
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                return false;
            }

            normalized = "PWSH:SEARCHPATH";
            return true;
        }

        string fullPath = NormalizeFullPath(expanded);
        if (!string.Equals(
                Path.GetFileName(fullPath),
                "pwsh.exe",
                StringComparison.OrdinalIgnoreCase)
            || !TryExpandAbsolutePath(
                @"%ProgramFiles%\PowerShell",
                out string powerShellRoot)
            || !IsPathBelow(fullPath, powerShellRoot))
        {
            return false;
        }

        normalized = "PWSH:" + fullPath.ToUpperInvariant();
        return true;
    }

    private bool TryNormalizeKnownExecutable(
        string? value,
        string? workingDirectory,
        IReadOnlyList<string> bareNames,
        IReadOnlyList<string> allowedPaths,
        string defaultPath,
        out string normalized)
    {
        normalized = string.Empty;
        if (!TryUnquoteExecutable(value, out string candidate))
        {
            return false;
        }

        string? expanded = _expandEnvironment(candidate);
        if (string.IsNullOrWhiteSpace(expanded)
            || expanded.Contains('%')
            || expanded.Contains('"'))
        {
            return false;
        }

        string resolvedPath;
        if (Path.IsPathFullyQualified(expanded))
        {
            resolvedPath = NormalizeFullPath(expanded);
        }
        else
        {
            if (!bareNames.Any(name => string.Equals(
                    candidate,
                    name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                resolvedPath = defaultPath;
            }
            else
            {
                if (!TryExpandAbsolutePath(
                        workingDirectory,
                        out string expandedWorkingDirectory))
                {
                    return false;
                }

                resolvedPath = Path.Combine(
                    expandedWorkingDirectory,
                    Path.GetFileName(defaultPath));
            }
        }

        return TryMatchAllowedPath(
            resolvedPath,
            allowedPaths,
            out normalized);
    }

    private bool TryGetWindowsSystemBinaryPaths(
        string fileName,
        string? relativeSubdirectory,
        out string system32Path,
        out string sysWow64Path,
        out string sysNativePath)
    {
        system32Path = string.Empty;
        sysWow64Path = string.Empty;
        sysNativePath = string.Empty;
        if (!TryExpandAbsolutePath(@"%SystemRoot%", out string windowsRoot))
        {
            return false;
        }

        system32Path = BuildSystemBinaryPath(
            windowsRoot,
            "System32",
            relativeSubdirectory,
            fileName);
        sysWow64Path = BuildSystemBinaryPath(
            windowsRoot,
            "SysWOW64",
            relativeSubdirectory,
            fileName);
        sysNativePath = BuildSystemBinaryPath(
            windowsRoot,
            "Sysnative",
            relativeSubdirectory,
            fileName);
        return true;
    }

    private static string BuildSystemBinaryPath(
        string windowsRoot,
        string systemDirectory,
        string? relativeSubdirectory,
        string fileName)
    {
        string directory = Path.Combine(windowsRoot, systemDirectory);
        if (!string.IsNullOrEmpty(relativeSubdirectory))
        {
            directory = Path.Combine(directory, relativeSubdirectory);
        }

        return NormalizeFullPath(Path.Combine(directory, fileName));
    }

    private static bool TryMatchAllowedPath(
        string candidate,
        IReadOnlyList<string> allowedPaths,
        out string matchedPath)
    {
        matchedPath = allowedPaths.FirstOrDefault(path => string.Equals(
                NormalizeFullPath(candidate),
                path,
                StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
        return matchedPath.Length > 0;
    }

    private static bool TryUnquoteExecutable(
        string? value,
        out string candidate)
    {
        candidate = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        candidate = value.Trim();
        bool beginsQuoted = candidate.StartsWith('"');
        bool endsQuoted = candidate.EndsWith('"');
        if (beginsQuoted != endsQuoted)
        {
            return false;
        }

        if (beginsQuoted)
        {
            candidate = candidate[1..^1];
            if (candidate.Length == 0 || candidate.Contains('"'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPathBelow(string path, string root)
    {
        string normalizedRoot = NormalizeFullPath(root)
            + Path.DirectorySeparatorChar;
        return NormalizeFullPath(path).StartsWith(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    private bool TryExpandAbsolutePath(
        string? value,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string? expanded = _expandEnvironment(value.Trim());
        if (string.IsNullOrWhiteSpace(expanded)
            || expanded.Contains('%')
            || expanded.Contains('"')
            || !Path.IsPathFullyQualified(expanded))
        {
            return false;
        }

        normalized = NormalizeFullPath(expanded);
        return true;
    }

    private static string NormalizeFullPath(string value) =>
        Path.GetFullPath(value)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    private bool TryNormalizeCmdArguments(
        string? value,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int index = 0;
        bool useS = false;
        bool disableAutoRun = false;
        while (TryReadWindowsToken(value, ref index, out string option))
        {
            if (string.Equals(option, "/c", StringComparison.OrdinalIgnoreCase))
            {
                string command = value[index..].Trim();
                if (!TryNormalizeEmbeddedShutdownCommand(
                        command,
                        ShellKind.Cmd,
                        out string inner))
                {
                    return false;
                }

                normalized = $"cmd;s={useS};d={disableAutoRun};{inner}";
                return true;
            }

            if (string.Equals(option, "/s", StringComparison.OrdinalIgnoreCase)
                && !useS)
            {
                useS = true;
                continue;
            }

            if (string.Equals(option, "/d", StringComparison.OrdinalIgnoreCase)
                && !disableAutoRun)
            {
                disableAutoRun = true;
                continue;
            }

            return false;
        }

        return false;
    }

    private bool TryNormalizePowerShellArguments(
        string? value,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int index = 0;
        bool noProfile = false;
        bool nonInteractive = false;
        bool noLogo = false;
        string? executionPolicy = null;
        string? windowStyle = null;
        while (TryReadWindowsToken(value, ref index, out string option))
        {
            if (!option.StartsWith("-", StringComparison.Ordinal))
            {
                return TryNormalizeEmbeddedShutdownCommand(
                    value,
                    ShellKind.PowerShell,
                    out normalized);
            }

            if (string.Equals(option, "-Command", StringComparison.OrdinalIgnoreCase)
                || string.Equals(option, "-c", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryNormalizeEmbeddedShutdownCommand(
                        value[index..].Trim(),
                        ShellKind.PowerShell,
                        out string inner))
                {
                    return false;
                }

                normalized = $"powershell;noProfile={noProfile};nonInteractive={nonInteractive};noLogo={noLogo};executionPolicy={executionPolicy ?? "default"};windowStyle={windowStyle ?? "default"};{inner}";
                return true;
            }

            if (string.Equals(option, "-NoProfile", StringComparison.OrdinalIgnoreCase)
                && !noProfile)
            {
                noProfile = true;
                continue;
            }

            if (string.Equals(
                    option,
                    "-NonInteractive",
                    StringComparison.OrdinalIgnoreCase)
                && !nonInteractive)
            {
                nonInteractive = true;
                continue;
            }

            if (string.Equals(option, "-NoLogo", StringComparison.OrdinalIgnoreCase)
                && !noLogo)
            {
                noLogo = true;
                continue;
            }

            if (string.Equals(
                    option,
                    "-ExecutionPolicy",
                    StringComparison.OrdinalIgnoreCase)
                && executionPolicy is null
                && TryReadWindowsToken(value, ref index, out string policy)
                && IsKnownExecutionPolicy(policy))
            {
                executionPolicy = policy.ToLowerInvariant();
                continue;
            }

            if (string.Equals(
                    option,
                    "-WindowStyle",
                    StringComparison.OrdinalIgnoreCase)
                && windowStyle is null
                && TryReadWindowsToken(value, ref index, out string style)
                && IsKnownWindowStyle(style))
            {
                windowStyle = style.ToLowerInvariant();
                continue;
            }

            return false;
        }

        return false;
    }

    private bool TryNormalizeEmbeddedShutdownCommand(
        string commandText,
        ShellKind shell,
        out string normalized)
    {
        normalized = string.Empty;
        if (!TryStripWholeCommandQuotes(commandText, out string command)
            || ContainsUnsafeShellSyntax(command, shell)
            || !TryTokenizeWindowsCommandLine(command, out string[] tokens)
            || tokens.Length == 0)
        {
            return false;
        }

        if (shell == ShellKind.PowerShell
            && string.Equals(
                tokens[0],
                "Stop-Computer",
                StringComparison.OrdinalIgnoreCase))
        {
            return TryNormalizeStopComputer(tokens, out normalized);
        }

        if (!TryNormalizeExecutable(tokens[0], null, out string executable)
            || !TryNormalizeArguments(tokens[1..], out string arguments))
        {
            return false;
        }

        normalized = $"embedded={executable};{arguments}";
        return true;
    }

    private static bool TryNormalizeStopComputer(
        IReadOnlyList<string> tokens,
        out string normalized)
    {
        normalized = string.Empty;
        bool force = false;
        bool computerName = false;
        for (int index = 1; index < tokens.Count; index++)
        {
            string token = tokens[index];
            if (string.Equals(token, "-Force", StringComparison.OrdinalIgnoreCase)
                && !force)
            {
                force = true;
                continue;
            }

            if (string.Equals(
                    token,
                    "-ComputerName",
                    StringComparison.OrdinalIgnoreCase)
                && !computerName
                && index + 1 < tokens.Count
                && IsLocalComputerName(tokens[++index]))
            {
                computerName = true;
                continue;
            }

            return false;
        }

        normalized = $"stop-computer;force={force};target=local";
        return true;
    }

    private static bool IsLocalComputerName(string value) =>
        string.Equals(value, ".", StringComparison.Ordinal)
        || string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownExecutionPolicy(string value) =>
        value.Equals("Bypass", StringComparison.OrdinalIgnoreCase)
        || value.Equals("RemoteSigned", StringComparison.OrdinalIgnoreCase)
        || value.Equals("AllSigned", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Restricted", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Unrestricted", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Default", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownWindowStyle(string value) =>
        value.Equals("Normal", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Hidden", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Minimized", StringComparison.OrdinalIgnoreCase)
        || value.Equals("Maximized", StringComparison.OrdinalIgnoreCase);

    private static bool TryStripWholeCommandQuotes(
        string value,
        out string command)
    {
        command = value.Trim();
        if (command.Length == 0)
        {
            return false;
        }

        if (command[0] == '"' && command[^1] == '"')
        {
            command = command[1..^1].Trim();
        }

        return command.Length > 0;
    }

    private static bool ContainsUnsafeShellSyntax(string command, ShellKind shell)
    {
        ReadOnlySpan<char> unsafeCharacters = shell == ShellKind.Cmd
            ? "&|<>^\r\n()%!".AsSpan()
            : ";|&<>\r\n`$(){}[],".AsSpan();
        return command.AsSpan().IndexOfAny(unsafeCharacters) >= 0;
    }

    private static bool TryTokenizeWindowsCommandLine(
        string value,
        out string[] tokens)
    {
        List<string> result = [];
        int index = 0;
        while (index < value.Length)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                index++;
            }

            if (index >= value.Length)
            {
                break;
            }

            if (!TryReadWindowsToken(value, ref index, out string token))
            {
                tokens = [];
                return false;
            }

            result.Add(token);
        }

        tokens = result.ToArray();
        return true;
    }

    private static bool TryReadWindowsToken(
        string value,
        ref int index,
        out string token)
    {
        token = string.Empty;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        if (index >= value.Length)
        {
            return false;
        }

        StringBuilder builder = new();
        bool inQuotes = false;
        while (index < value.Length)
        {
            char character = value[index];
            if (char.IsWhiteSpace(character) && !inQuotes)
            {
                break;
            }

            if (character == '\\')
            {
                int slashStart = index;
                while (index < value.Length && value[index] == '\\')
                {
                    index++;
                }

                int slashCount = index - slashStart;
                if (index < value.Length && value[index] == '"')
                {
                    builder.Append('\\', slashCount / 2);
                    if (slashCount % 2 == 0)
                    {
                        inQuotes = !inQuotes;
                    }
                    else
                    {
                        builder.Append('"');
                    }

                    index++;
                    continue;
                }

                builder.Append('\\', slashCount);
                continue;
            }

            if (character == '"')
            {
                inQuotes = !inQuotes;
                index++;
                continue;
            }

            builder.Append(character);
            index++;
        }

        if (inQuotes)
        {
            return false;
        }

        token = builder.ToString();
        return token.Length > 0;
    }

    private static bool TryNormalizeArguments(
        string? value,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !TryTokenizeWindowsCommandLine(value, out string[] tokens))
        {
            return false;
        }

        return TryNormalizeArguments(tokens, out normalized);
    }

    private static bool TryNormalizeArguments(
        IReadOnlyList<string> tokens,
        out string normalized)
    {
        normalized = string.Empty;
        bool shutdown = false;
        bool powerOff = false;
        bool force = false;
        bool hybrid = false;
        bool soft = false;
        uint? timeout = null;
        string? reason = null;
        string? comment = null;
        for (int index = 0; index < tokens.Count; index++)
        {
            string token = tokens[index];
            if (token.Length < 2 || token[0] is not ('/' or '-'))
            {
                return false;
            }

            string option = token[1..].ToLowerInvariant();
            switch (option)
            {
                case "s" when !shutdown && !powerOff:
                    shutdown = true;
                    break;
                case "p" when !shutdown && !powerOff:
                    powerOff = true;
                    break;
                case "f" when !force:
                    force = true;
                    break;
                case "hybrid" when !hybrid:
                    hybrid = true;
                    break;
                case "soft" when !soft:
                    soft = true;
                    break;
                case "t" when timeout is null && index + 1 < tokens.Count:
                    if (!TryParseTimeout(tokens[++index], out uint parsedTimeout))
                    {
                        return false;
                    }

                    timeout = parsedTimeout;
                    break;
                case "d" when reason is null && index + 1 < tokens.Count:
                    if (!TryNormalizeReason(tokens[++index], out reason))
                    {
                        return false;
                    }

                    break;
                case "c" when comment is null && index + 1 < tokens.Count:
                    comment = tokens[++index];
                    if (comment.Length is 0 or > 512 || comment.Contains('\0'))
                    {
                        return false;
                    }

                    break;
                default:
                    if (option.StartsWith("t:", StringComparison.Ordinal)
                        && timeout is null
                        && TryParseTimeout(option[2..], out uint inlineTimeout))
                    {
                        timeout = inlineTimeout;
                        break;
                    }

                    return false;
            }
        }

        if ((!shutdown && !powerOff)
            || powerOff && (force || hybrid || soft || timeout is not null)
            || (hybrid || soft) && !shutdown)
        {
            return false;
        }

        string compatible = string.Create(
            CultureInfo.InvariantCulture,
            $"mode={(shutdown ? "shutdown" : "poweroff")};force={force};hybrid={hybrid};timeout={(timeout?.ToString(CultureInfo.InvariantCulture) ?? "default")}");
        normalized = !soft && reason is null && comment is null
            ? compatible
            : $"{compatible};soft={soft};reason={reason ?? "default"};comment={comment ?? "default"}";
        return true;
    }

    private static bool TryNormalizeReason(string value, out string normalized)
    {
        normalized = string.Empty;
        string candidate = value.ToLowerInvariant();
        string prefix = string.Empty;
        if (candidate.StartsWith("p:", StringComparison.Ordinal)
            || candidate.StartsWith("u:", StringComparison.Ordinal))
        {
            prefix = candidate[..2];
            candidate = candidate[2..];
        }

        string[] parts = candidate.Split(':');
        if (parts.Length != 2
            || !ushort.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ushort major)
            || !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out ushort minor))
        {
            return false;
        }

        normalized = $"{prefix}{major}:{minor}";
        return true;
    }

    private static bool TryParseTimeout(string value, out uint timeout) =>
        uint.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out timeout)
        && timeout <= MaximumShutdownTimeoutSeconds;

    private enum ShellKind
    {
        Cmd,
        PowerShell,
    }

    private enum LegacyReadPurpose
    {
        Disable,
        Restore,
        Observe,
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;
}
