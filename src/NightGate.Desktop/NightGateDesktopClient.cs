using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightGate.Core;
using NightGate.Protocol;

namespace NightGate.Desktop;

public interface INightGatePipeTransport
{
    ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> requestUtf8,
        CancellationToken cancellationToken = default);
}

public interface IProtocolRequestIdSource
{
    string NextRequestId();
}

public sealed class GuidProtocolRequestIdSource : IProtocolRequestIdSource
{
    public string NextRequestId() => Guid.NewGuid().ToString("N");
}

public sealed partial class NightGateDesktopClient : IDesktopPolicyClient
{
    private const string FailOpenCode = "service-unavailable";
    private static readonly TimeSpan TeamRescueDuration = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan EmergencyDuration = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan EntertainmentCoolingOff = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EntertainmentDuration = TimeSpan.FromMinutes(20);
    private readonly INightGatePipeTransport _transport;
    private readonly IProtocolRequestIdSource _requestIdSource;
    private readonly string? _desktopSessionId;
    private readonly SemaphoreSlim _endDesktopSessionGate = new(1, 1);
    private readonly object _stateSync = new();
    private DesktopPolicyResult _currentPolicy = DesktopPolicyResult.FailOpen("service-not-contacted");
    private DesktopEndSessionResult? _completedEndSession;
    private long _latestStartedOperation;
    private long _currentPolicyOperation;

    public NightGateDesktopClient(
        INightGatePipeTransport transport,
        IProtocolRequestIdSource? requestIdSource = null,
        string? desktopSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (desktopSessionId is not null && !IsCanonicalDesktopSessionId(desktopSessionId))
        {
            throw new ArgumentException(
                "Desktop session ID must be 32 lowercase hexadecimal characters.",
                nameof(desktopSessionId));
        }

        _transport = transport;
        _requestIdSource = requestIdSource ?? new GuidProtocolRequestIdSource();
        _desktopSessionId = desktopSessionId;
    }

    public DesktopPolicyResult CurrentPolicy
    {
        get
        {
            lock (_stateSync)
            {
                return _currentPolicy;
            }
        }
    }

    public async ValueTask<DesktopPolicyResult> GetPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        long operationId = Interlocked.Increment(ref _latestStartedOperation);
        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> request = _desktopSessionId is null
                ? CreateRequest("getPolicy", requestId, new { })
                : CreateRequest(
                    "getDesktopPolicy",
                    requestId,
                    new DesktopSessionPayload(_desktopSessionId));
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(request, cancellationToken)
                .ConfigureAwait(false);
            ResponseWrapper<DesktopServiceRuntimeStatusDto> wrapper = DecodeResponse<DesktopServiceRuntimeStatusDto>(
                response,
                _desktopSessionId is null ? "getPolicyResult" : "getDesktopPolicyResult",
                requestId);
            DesktopServiceRuntimeStatusDto status = wrapper.Data;
            bool canEnforce = wrapper.Status == "success"
                && status.EnforcementEnabled
                && !status.IsDegraded
                && status.Policy is
                {
                    EnforcementEnabled: true,
                    IsDegraded: false,
                } policy
                && IsUsablePolicy(policy);
            DesktopPolicyResult result = canEnforce
                ? new(true, false, null, status)
                : new(
                    false,
                    true,
                    status.DegradationCode ?? (wrapper.Status == "degraded"
                        ? "service-degraded"
                        : "policy-disabled"),
                    status);
            return SetCurrent(result, operationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetCurrent(DesktopPolicyResult.FailOpen(FailOpenCode), operationId);
            throw;
        }
        catch (Exception)
        {
            return SetCurrent(DesktopPolicyResult.FailOpen(FailOpenCode), operationId);
        }
    }

    public async ValueTask<DesktopOverrideResult> RequestOverrideAsync(
        DesktopOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateOverrideRequest(request);
        long operationId = Interlocked.Increment(ref _latestStartedOperation);
        try
        {
            string requestId = NextRequestId();
            OverrideRequestPayload requestPayload = new(
                OverrideKindToken(request.Kind),
                request.EmergencyReason is { } reason
                    ? EmergencyReasonToken(reason)
                    : null);
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(
                    CreateRequest("requestOverride", requestId, requestPayload),
                    cancellationToken)
                .ConfigureAwait(false);
            ResponseWrapper<OverrideResponseDto> wrapper = DecodeResponse<OverrideResponseDto>(
                response,
                "requestOverrideResult",
                requestId);
            if (wrapper.Status != "success")
            {
                DesktopPolicyResult failed = SetCurrent(
                    DesktopPolicyResult.FailOpen("service-degraded"),
                    operationId);
                return new(false, "service-degraded", null, failed);
            }

            OverrideResponseDto data = wrapper.Data;
            if (!data.Accepted)
            {
                if (string.IsNullOrWhiteSpace(data.Error)
                    || data.Kind is not null
                    || data.StartsAtUtc is not null
                    || data.EndsAtUtc is not null)
                {
                    throw new JsonException("Rejected override response is malformed.");
                }

                return new(false, data.Error, null, CurrentPolicy);
            }

            if (data.Error is not null
                || data.Kind is not { } kind
                || kind != request.Kind
                || data.StartsAtUtc is not { } startsAtUtc
                || data.EndsAtUtc is not { } endsAtUtc
                || endsAtUtc <= startsAtUtc
                || endsAtUtc - startsAtUtc != OverrideWindowDuration(request.Kind))
            {
                throw new JsonException("Accepted override response is malformed.");
            }

            DesktopOverrideWindowDto activeWindow = new(kind, startsAtUtc, endsAtUtc);
            DesktopPolicyResult refreshed = await GetPolicyAsync(cancellationToken).ConfigureAwait(false);
            return new(true, null, activeWindow, refreshed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetCurrent(DesktopPolicyResult.FailOpen(FailOpenCode), operationId);
            throw;
        }
        catch (Exception)
        {
            DesktopPolicyResult failed = SetCurrent(
                DesktopPolicyResult.FailOpen(FailOpenCode),
                operationId);
            return new(false, FailOpenCode, null, failed);
        }
    }

    public async ValueTask<DesktopRecordEventResult> RecordEventAsync(
        PrivacySafeEventKind kind,
        CancellationToken cancellationToken = default)
    {
        string kindToken = EventKindToken(kind);
        long operationId = Interlocked.Increment(ref _latestStartedOperation);
        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> response = await _transport
                .ExchangeAsync(
                    CreateRequest("recordEvent", requestId, new { kind = kindToken }),
                    cancellationToken)
                .ConfigureAwait(false);
            ResponseWrapper<RecordEventResponseDto> wrapper = DecodeResponse<RecordEventResponseDto>(
                response,
                "recordEventResult",
                requestId);
            if (wrapper.Status != "success")
            {
                SetCurrent(DesktopPolicyResult.FailOpen("service-degraded"), operationId);
                return new(false, "service-degraded");
            }

            RecordEventResponseDto data = wrapper.Data;
            if (data.Recorded && data.Error is not null)
            {
                throw new JsonException("Recorded event response cannot contain an error.");
            }

            if (!data.Recorded && string.IsNullOrWhiteSpace(data.Error))
            {
                throw new JsonException("Rejected event response must contain an error.");
            }

            return new(data.Recorded, data.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetCurrent(DesktopPolicyResult.FailOpen(FailOpenCode), operationId);
            throw;
        }
        catch (Exception)
        {
            SetCurrent(DesktopPolicyResult.FailOpen(FailOpenCode), operationId);
            return new(false, FailOpenCode);
        }
    }

    public async ValueTask<DesktopEndSessionResult> EndDesktopSessionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_desktopSessionId is null)
        {
            return new(false, "desktop-session-not-enabled");
        }

        await _endDesktopSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_completedEndSession is { } completed)
            {
                return completed;
            }

            long operationId = Interlocked.Increment(ref _latestStartedOperation);
            try
            {
                string requestId = NextRequestId();
                ReadOnlyMemory<byte> response = await _transport
                    .ExchangeAsync(
                        CreateRequest(
                            "endDesktopSession",
                            requestId,
                            new DesktopSessionPayload(_desktopSessionId)),
                        cancellationToken)
                    .ConfigureAwait(false);
                ResponseWrapper<EndDesktopSessionResponseDto> wrapper =
                    DecodeResponse<EndDesktopSessionResponseDto>(
                        response,
                        "endDesktopSessionResult",
                        requestId);
                if (wrapper.Status != "success")
                {
                    throw new JsonException("Desktop session end response is degraded.");
                }

                EndDesktopSessionResponseDto data = wrapper.Data;
                if (data.Accepted)
                {
                    if (data.Error is not null)
                    {
                        throw new JsonException(
                            "Accepted desktop session end response cannot contain an error.");
                    }

                    DesktopEndSessionResult accepted = new(true, null);
                    _completedEndSession = accepted;
                    SetCurrent(
                        DesktopPolicyResult.FailOpen("desktop-session-ended"),
                        operationId);
                    return accepted;
                }

                if (string.IsNullOrWhiteSpace(data.Error))
                {
                    throw new JsonException(
                        "Rejected desktop session end response must contain an error.");
                }

                SetCurrent(DesktopPolicyResult.FailOpen(data.Error), operationId);
                return new(false, data.Error);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetCurrent(
                    DesktopPolicyResult.FailOpen(FailOpenCode),
                    operationId);
                throw;
            }
            catch (Exception)
            {
                SetCurrent(
                    DesktopPolicyResult.FailOpen(FailOpenCode),
                    operationId);
                return new(false, FailOpenCode);
            }
        }
        finally
        {
            _endDesktopSessionGate.Release();
        }
    }

    internal static bool IsUsablePolicy(DesktopPolicySnapshotDto policy)
    {
        if (policy.Window is null
            || policy.AppRules is null
            || policy.SiteRules is null
            || policy.Revision is < 0 or > 9_007_199_254_740_991
            || !Enum.IsDefined(policy.Phase)
            || !IsUsableWindow(policy.Window))
        {
            return false;
        }

        HashSet<string> appRuleIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> rootExecutablePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopAppRuleDto? rule in policy.AppRules)
        {
            if (rule is null
                || !IsFullyConfiguredRule(rule)
                || !appRuleIds.Add(rule.Id)
                || !rootExecutablePaths.Add(rule.RootExecutablePath!))
            {
                return false;
            }
        }

        HashSet<string> siteDomains = new(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopSiteRuleDto? rule in policy.SiteRules)
        {
            if (rule is null
                || string.IsNullOrWhiteSpace(rule.Domain)
                || !string.Equals(rule.Domain, rule.Domain.Trim(), StringComparison.Ordinal)
                || !siteDomains.Add(rule.Domain))
            {
                return false;
            }
        }

        if (policy.ActiveOverride is not { } activeOverride)
        {
            return policy.Phase is not DesktopNightPhase.CoolingOff
                and not DesktopNightPhase.OverrideActive;
        }

        return IsUsableOverride(activeOverride, appRuleIds)
            && IsUsableOverridePhase(policy, activeOverride);
    }

    private static bool IsFullyConfiguredRule(DesktopAppRuleDto rule)
    {
        if (!rule.IsConfigured
            || rule.RootExecutablePath is not { } rootExecutablePath
            || rule.HelperExecutablePaths is null
            || rule.Category is not { } category)
        {
            return false;
        }

        AppRuleCategory coreCategory = category switch
        {
            DesktopAppRuleCategory.Game => AppRuleCategory.Game,
            DesktopAppRuleCategory.Voice => AppRuleCategory.Voice,
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };

        try
        {
            string[] helpers = rule.HelperExecutablePaths.ToArray();
            AppRule configured = new(
                rule.Id,
                rootExecutablePath,
                helpers,
                coreCategory,
                rule.SessionMinutes);
            return configured.IsConfigured
                && string.Equals(configured.Id, rule.Id, StringComparison.Ordinal)
                && string.Equals(
                    configured.RootExecutablePath,
                    rootExecutablePath,
                    StringComparison.OrdinalIgnoreCase)
                && configured.HelperExecutablePaths.SequenceEqual(
                    helpers,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsUsableWindow(DesktopNightWindowDto window)
    {
        if (!(window.ProtectedStart < window.LastStart
            && window.LastStart < window.Lock
            && window.Lock < window.LightsOut
            && window.LightsOut < window.Wake))
        {
            return false;
        }

        DateOnly nextDate;
        try
        {
            nextDate = window.NightDate.AddDays(1);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        DateOnly protectedDate = LocalDate(window.ProtectedStart);
        DateOnly lastStartDate = LocalDate(window.LastStart);
        DateOnly lockDate = LocalDate(window.Lock);
        DateOnly lightsOutDate = LocalDate(window.LightsOut);
        DateOnly wakeDate = LocalDate(window.Wake);
        return protectedDate == window.NightDate
            && IsNightOrNextDate(lastStartDate, window.NightDate, nextDate)
            && IsNightOrNextDate(lockDate, window.NightDate, nextDate)
            && IsNightOrNextDate(lightsOutDate, window.NightDate, nextDate)
            && wakeDate == nextDate;
    }

    private static bool IsUsableOverride(
        DesktopActiveOverrideDto activeOverride,
        IReadOnlySet<string> configuredRuleIds)
    {
        if (activeOverride.AllowedProcessIdentifiers is null
            || activeOverride.RequestedAtUtc > activeOverride.StartsAtUtc
            || activeOverride.StartsAtUtc >= activeOverride.EndsAtUtc)
        {
            return false;
        }

        HashSet<string> allowedIdentifiers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? identifier in activeOverride.AllowedProcessIdentifiers)
        {
            if (string.IsNullOrWhiteSpace(identifier)
                || !string.Equals(identifier, identifier.Trim(), StringComparison.Ordinal)
                || !allowedIdentifiers.Add(identifier))
            {
                return false;
            }
        }

        TimeSpan startDelay = activeOverride.StartsAtUtc - activeOverride.RequestedAtUtc;
        TimeSpan duration = activeOverride.EndsAtUtc - activeOverride.StartsAtUtc;
        return activeOverride.Kind switch
        {
            DesktopOverrideKind.TeamRescue =>
                startDelay == TimeSpan.Zero
                && duration == TeamRescueDuration
                && allowedIdentifiers.All(configuredRuleIds.Contains),
            DesktopOverrideKind.Emergency =>
                startDelay == TimeSpan.Zero
                && duration == EmergencyDuration
                && allowedIdentifiers.Count == 0,
            DesktopOverrideKind.Entertainment =>
                startDelay == EntertainmentCoolingOff
                && duration == EntertainmentDuration
                && allowedIdentifiers.Count == 0,
            _ => false,
        };
    }

    private static bool IsUsableOverridePhase(
        DesktopPolicySnapshotDto policy,
        DesktopActiveOverrideDto activeOverride)
    {
        if (policy.EvaluatedAt < activeOverride.RequestedAtUtc
            || policy.EvaluatedAt >= activeOverride.EndsAtUtc)
        {
            return false;
        }

        if (policy.EvaluatedAt < activeOverride.StartsAtUtc)
        {
            return activeOverride.Kind == DesktopOverrideKind.Entertainment
                && policy.Phase == DesktopNightPhase.CoolingOff;
        }

        return policy.Phase == DesktopNightPhase.OverrideActive;
    }

    private static TimeSpan OverrideWindowDuration(DesktopOverrideKind kind) => kind switch
    {
        DesktopOverrideKind.TeamRescue => TeamRescueDuration,
        DesktopOverrideKind.Emergency => EmergencyDuration,
        DesktopOverrideKind.Entertainment => EntertainmentDuration,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static DateOnly LocalDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(value.DateTime);

    private static bool IsNightOrNextDate(
        DateOnly candidate,
        DateOnly nightDate,
        DateOnly nextDate) => candidate == nightDate || candidate == nextDate;

    private static void ValidateOverrideRequest(DesktopOverrideRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Kind)
            || request.EmergencyReason is { } reason && !Enum.IsDefined(reason)
            || request.Kind == DesktopOverrideKind.Emergency && request.EmergencyReason is null
            || request.Kind != DesktopOverrideKind.Emergency && request.EmergencyReason is not null)
        {
            throw new ArgumentException("Override request is invalid.", nameof(request));
        }
    }

    private string NextRequestId()
    {
        string requestId = _requestIdSource.NextRequestId();
        if (!NightGateProtocol.IsValidRequestId(requestId))
        {
            throw new InvalidDataException("Request ID source returned an invalid identifier.");
        }

        return requestId;
    }

    private DesktopPolicyResult SetCurrent(DesktopPolicyResult policy, long operationId)
    {
        lock (_stateSync)
        {
            if (operationId >= _currentPolicyOperation)
            {
                _currentPolicyOperation = operationId;
                _currentPolicy = policy;
            }

            return _currentPolicy;
        }
    }

    private static ReadOnlyMemory<byte> CreateRequest<T>(
        string type,
        string requestId,
        T payload)
    {
        JsonElement payloadElement = JsonSerializer.SerializeToElement(payload, DesktopJson.Options);
        return JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                version = NightGateProtocol.Version,
                type,
                requestId,
                payload = payloadElement,
            },
            DesktopJson.Options);
    }

    private static ResponseWrapper<T> DecodeResponse<T>(
        ReadOnlyMemory<byte> utf8Json,
        string expectedType,
        string expectedRequestId)
    {
        if (utf8Json.Length > NightGateProtocol.MaximumBodyBytes)
        {
            throw new InvalidDataException("Response exceeds the protocol limit.");
        }

        using JsonDocument document = JsonDocument.Parse(utf8Json);
        JsonElement root = document.RootElement;
        EnsureExactObject(root, "version", "type", "requestId", "payload");
        if (!root.TryGetProperty("version", out JsonElement versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out int version)
            || version != NightGateProtocol.Version
            || !root.TryGetProperty("type", out JsonElement typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || typeElement.GetString() != expectedType
            || !root.TryGetProperty("requestId", out JsonElement requestIdElement)
            || requestIdElement.ValueKind != JsonValueKind.String
            || requestIdElement.GetString() != expectedRequestId
            || !NightGateProtocol.IsValidRequestId(requestIdElement.GetString())
            || !root.TryGetProperty("payload", out JsonElement payload))
        {
            throw new JsonException("Response envelope does not match the request.");
        }

        EnsureExactObject(payload, "status", "data");
        if (!payload.TryGetProperty("status", out JsonElement statusElement)
            || statusElement.ValueKind != JsonValueKind.String
            || statusElement.GetString() is not ("success" or "degraded")
            || !payload.TryGetProperty("data", out JsonElement dataElement))
        {
            throw new JsonException("Response wrapper is malformed.");
        }

        EnsureNoDuplicateObjectProperties(dataElement);
        T? data = dataElement.Deserialize<T>(DesktopJson.Options);
        if (data is null)
        {
            throw new JsonException("Response data is null.");
        }

        return new(statusElement.GetString()!, data);
    }

    private static void EnsureExactObject(JsonElement element, params string[] expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected an object.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!expectedNames.Contains(property.Name, StringComparer.Ordinal)
                || !seen.Add(property.Name))
            {
                throw new JsonException("Object contains an unknown or duplicate property.");
            }
        }

        if (seen.Count != expectedNames.Length)
        {
            throw new JsonException("Object is missing a required property.");
        }
    }

    private static void EnsureNoDuplicateObjectProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw new JsonException("Object contains a duplicate property.");
                }

                EnsureNoDuplicateObjectProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                EnsureNoDuplicateObjectProperties(item);
            }
        }
    }

    private static string OverrideKindToken(DesktopOverrideKind kind) => kind switch
    {
        DesktopOverrideKind.TeamRescue => "teamRescue",
        DesktopOverrideKind.Emergency => "emergency",
        DesktopOverrideKind.Entertainment => "entertainment",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string EmergencyReasonToken(DesktopEmergencyReason reason) => reason switch
    {
        DesktopEmergencyReason.Health => "health",
        DesktopEmergencyReason.Safety => "safety",
        DesktopEmergencyReason.UrgentWork => "urgentWork",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static string EventKindToken(PrivacySafeEventKind kind) => kind switch
    {
        PrivacySafeEventKind.MissedLock => "missedLock",
        PrivacySafeEventKind.WorkstationLocked => "workstationLocked",
        PrivacySafeEventKind.LateNewEntertainment => "lateNewEntertainment",
        PrivacySafeEventKind.DeliberateBypass => "deliberateBypass",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed record ResponseWrapper<T>(string Status, T Data);

    private sealed record OverrideRequestPayload(
        string Kind,
        string? EmergencyReason);

    private sealed record OverrideResponseDto(
        bool Accepted,
        string? Error = null,
        DesktopOverrideKind? Kind = null,
        DateTimeOffset? StartsAtUtc = null,
        DateTimeOffset? EndsAtUtc = null);

    private sealed record RecordEventResponseDto(bool Recorded, string? Error = null);

    private sealed record DesktopSessionPayload(string SessionId);

    private sealed record EndDesktopSessionResponseDto(bool Accepted, string? Error = null);

    private static bool IsCanonicalDesktopSessionId(string value) =>
        value.Length == 32
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static class DesktopJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new StrictCamelCaseEnumConverter<DesktopNightPhase>());
        options.Converters.Add(new StrictCamelCaseEnumConverter<DesktopOverrideKind>());
        options.Converters.Add(new StrictCamelCaseEnumConverter<DesktopEmergencyReason>());
        options.Converters.Add(new StrictCamelCaseEnumConverter<DesktopAppRuleCategory>());
        options.Converters.Add(new StrictCamelCaseEnumConverter<PrivacySafeEventKind>());
        options.Converters.Add(new StrictCamelCaseEnumConverter<DesktopNightNoticeKind>());
        return options;
    }

    private sealed class StrictCamelCaseEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private static readonly IReadOnlyDictionary<string, TEnum> ValuesByToken =
            Enum.GetValues<TEnum>().ToDictionary(
                value => JsonNamingPolicy.CamelCase.ConvertName(value.ToString()),
                StringComparer.Ordinal);

        private static readonly IReadOnlyDictionary<TEnum, string> TokensByValue =
            ValuesByToken.ToDictionary(pair => pair.Value, pair => pair.Key);

        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || reader.GetString() is not { } token
                || !ValuesByToken.TryGetValue(token, out TEnum value))
            {
                throw new JsonException($"Invalid {typeof(TEnum).Name} token.");
            }

            return value;
        }

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options)
        {
            if (!TokensByValue.TryGetValue(value, out string? token))
            {
                throw new JsonException($"Undefined {typeof(TEnum).Name} value.");
            }

            writer.WriteStringValue(token);
        }
    }
}
