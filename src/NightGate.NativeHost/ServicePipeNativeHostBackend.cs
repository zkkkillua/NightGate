using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using NightGate.Protocol;

namespace NightGate.NativeHost;

internal interface IServicePipeExchange
{
    ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> requestUtf8,
        CancellationToken cancellationToken = default);
}

internal interface INativeHostServiceRequestIdSource
{
    string Next();
}

internal interface INativeHostClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class NativeHostServiceRequestIdSource :
    INativeHostServiceRequestIdSource
{
    public string Next() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
}

internal sealed class NativeHostClock : INativeHostClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal sealed class ServicePipeNativeHostBackend(
    IServicePipeExchange exchange,
    INativeHostServiceRequestIdSource requestIds,
    INativeHostClock clock) : INativeHostBackend
{
    private const int MaximumPolicyRules = 100;
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web);

    public async ValueTask<ChromePolicyPayload> GetPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        string requestId = NextRequestId();
        byte[] request = SerializeRequest("getPolicy", requestId, new { });
        ReadOnlyMemory<byte> response = await exchange
            .ExchangeAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return ParsePolicyResponse(response, requestId);
    }

    public async ValueTask<bool> HeartbeatAsync(
        NativeHeartbeatObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ChromePolicyPayload current = await GetPolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasAppliedRevision = observation.PolicyRevision >= 0;
        bool revisionMatches = hasAppliedRevision
            && current.Revision == observation.PolicyRevision;
        long recordedRevision = hasAppliedRevision
            ? observation.PolicyRevision
            : current.Revision;
        string healthRequestId = NextRequestId();
        byte[] healthRequest = SerializeRequest(
            "recordChromeHealth",
            healthRequestId,
            new
            {
                extensionId = observation.ExtensionId,
                extensionVersion = observation.ExtensionVersion,
                profileTokenSha256 = observation.ProfileTokenSha256,
                policyRevision = recordedRevision,
                incognitoAllowed = observation.IncognitoAllowed,
                protectionReady = observation.ProtectionReady && revisionMatches,
            });
        ReadOnlyMemory<byte> healthResponse = await exchange
            .ExchangeAsync(healthRequest, cancellationToken)
            .ConfigureAwait(false);
        if (!ParseHealthResponse(healthResponse, healthRequestId))
        {
            return false;
        }
        return !hasAppliedRevision || revisionMatches;
    }

    public async ValueTask<bool> RecordEventAsync(
        BrowserPrivacyEvent privacyEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privacyEvent);
        string requestId = NextRequestId();
        byte[] request = SerializeRequest(
            "recordBrowserEvent",
            requestId,
            new
            {
                timestamp = UtcTimestamp(privacyEvent.TimestampUtc),
                eventType = EventTypeToken(privacyEvent.EventType),
                category = CategoryToken(privacyEvent.Category),
            });
        ReadOnlyMemory<byte> response = await exchange
            .ExchangeAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return ParseEventResponse(response, requestId);
    }

    private string NextRequestId()
    {
        string requestId = requestIds.Next();
        if (!NightGateProtocol.IsValidRequestId(requestId))
        {
            throw new InvalidDataException("Native-host service request ID is invalid.");
        }
        return requestId;
    }

    private static byte[] SerializeRequest(string type, string requestId, object payload)
    {
        byte[] result = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                version = NightGateProtocol.Version,
                type,
                requestId,
                payload,
            },
            SerializerOptions);
        if (result.Length > NightGateProtocol.MaximumBodyBytes)
        {
            throw new InvalidDataException("Service request exceeds the allowed size.");
        }
        return result;
    }

    private ChromePolicyPayload ParsePolicyResponse(
        ReadOnlyMemory<byte> response,
        string requestId)
    {
        using JsonDocument document = ParseResponse(
            response,
            "getPolicyResult",
            requestId,
            out string status,
            out JsonElement data);
        if (!HasExactProperties(
                data,
                "enforcementEnabled",
                "isDegraded",
                "degradationCode",
                "policy")
            || !TryReadBoolean(data, "enforcementEnabled", out bool enforcementEnabled)
            || !TryReadBoolean(data, "isDegraded", out bool isDegraded)
            || !TryReadOptionalBoundedString(data, "degradationCode", 128, out _)
            || (status == "degraded") != isDegraded)
        {
            throw new InvalidDataException("Service policy status is malformed.");
        }

        JsonElement policy = data.GetProperty("policy");
        if (isDegraded || !enforcementEnabled)
        {
            if (policy.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
            {
                throw new InvalidDataException("Degraded service policy is malformed.");
            }
            return FailOpenPolicy();
        }

        if (policy.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Service policy is missing.");
        }
        ServicePolicyProjectionInput input = ParseProjectionInput(
            policy,
            enforcementEnabled,
            isDegraded);
        return ChromePolicyProjector.Project(input);
    }

    private static bool ParseEventResponse(
        ReadOnlyMemory<byte> response,
        string requestId)
    {
        using JsonDocument document = ParseResponse(
            response,
            "recordBrowserEventResult",
            requestId,
            out string status,
            out JsonElement data);
        if (!HasExactProperties(data, "status")
            || !TryReadString(data, "status", out string? dataStatus)
            || dataStatus is not ("recorded" or "degraded")
            || (status == "success") != (dataStatus == "recorded"))
        {
            throw new InvalidDataException("Browser-event response is malformed.");
        }
        return dataStatus == "recorded";
    }

    private static bool ParseHealthResponse(
        ReadOnlyMemory<byte> response,
        string requestId)
    {
        using JsonDocument document = ParseResponse(
            response,
            "recordChromeHealthResult",
            requestId,
            out string status,
            out JsonElement data);
        if (!HasExactProperties(data, "status")
            || !TryReadString(data, "status", out string? dataStatus)
            || dataStatus is not ("recorded" or "degraded")
            || (status == "success") != (dataStatus == "recorded"))
        {
            throw new InvalidDataException("Chrome-health response is malformed.");
        }
        return dataStatus == "recorded";
    }

    private static JsonDocument ParseResponse(
        ReadOnlyMemory<byte> response,
        string expectedType,
        string expectedRequestId,
        out string status,
        out JsonElement data)
    {
        status = string.Empty;
        data = default;
        if (response.IsEmpty || response.Length > NightGateProtocol.MaximumBodyBytes)
        {
            throw new InvalidDataException("Service response size is invalid.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                response,
                new JsonDocumentOptions { MaxDepth = 24 });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Service response is not valid JSON.", exception);
        }

        JsonElement root = document.RootElement;
        if (!HasUniquePropertiesRecursively(root)
            || !HasExactProperties(root, "version", "type", "requestId", "payload")
            || !root.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int protocolVersion)
            || protocolVersion != NightGateProtocol.Version
            || !TryReadString(root, "type", out string? type)
            || !string.Equals(type, expectedType, StringComparison.Ordinal)
            || !TryReadString(root, "requestId", out string? responseRequestId)
            || !string.Equals(responseRequestId, expectedRequestId, StringComparison.Ordinal)
            || !root.TryGetProperty("payload", out JsonElement payload)
            || !HasExactProperties(payload, "status", "data")
            || !TryReadString(payload, "status", out string? parsedStatus)
            || parsedStatus is not ("success" or "degraded"))
        {
            document.Dispose();
            throw new InvalidDataException("Service response envelope is malformed.");
        }

        status = parsedStatus;
        data = payload.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("Service response data is malformed.");
        }
        return document;
    }

    private static ServicePolicyProjectionInput ParseProjectionInput(
        JsonElement policy,
        bool outerEnforcementEnabled,
        bool outerIsDegraded)
    {
        if (!HasExactProperties(
                policy,
                "revision",
                "evaluatedAt",
                "phase",
                "window",
                "appRules",
                "siteRules",
                "enforcementEnabled",
                "isDegraded",
                "activeOverride")
            || !TryReadInt64(policy, "revision", out long revision)
            || !TryReadTimestamp(policy, "evaluatedAt", out DateTimeOffset evaluatedAt)
            || !TryReadString(policy, "phase", out string? phase)
            || !IsPhase(phase)
            || !TryReadBoolean(policy, "enforcementEnabled", out bool policyEnforcementEnabled)
            || !TryReadBoolean(policy, "isDegraded", out bool policyIsDegraded)
            || policyEnforcementEnabled != outerEnforcementEnabled
            || policyIsDegraded != outerIsDegraded
            || !policy.TryGetProperty("window", out JsonElement window)
            || !policy.TryGetProperty("appRules", out JsonElement appRules)
            || !ValidateAppRules(appRules)
            || !policy.TryGetProperty("siteRules", out JsonElement siteRules))
        {
            throw new InvalidDataException("Service policy payload is malformed.");
        }

        ParseWindow(
            window,
            out DateOnly nightDate,
            out DateTimeOffset lastStart,
            out DateTimeOffset lockAt,
            out DateTimeOffset wakeAt);
        IReadOnlyList<ServiceSiteRuleProjectionInput> projectedRules =
            ParseSiteRules(siteRules);
        string? overrideKind = ParseActiveOverride(policy.GetProperty("activeOverride"));

        return new(
            policyEnforcementEnabled,
            policyIsDegraded,
            revision,
            evaluatedAt,
            phase!,
            nightDate,
            lastStart,
            lockAt,
            wakeAt,
            overrideKind,
            projectedRules);
    }

    private static void ParseWindow(
        JsonElement window,
        out DateOnly nightDate,
        out DateTimeOffset lastStart,
        out DateTimeOffset lockAt,
        out DateTimeOffset wakeAt)
    {
        nightDate = default;
        lastStart = default;
        lockAt = default;
        wakeAt = default;
        if (!HasExactProperties(
                window,
                "nightDate",
                "protectedStart",
                "lastStart",
                "lock",
                "lightsOut",
                "wake")
            || !TryReadDate(window, "nightDate", out nightDate)
            || !TryReadTimestamp(window, "protectedStart", out DateTimeOffset protectedStart)
            || !TryReadTimestamp(window, "lastStart", out lastStart)
            || !TryReadTimestamp(window, "lock", out lockAt)
            || !TryReadTimestamp(window, "lightsOut", out DateTimeOffset lightsOut)
            || !TryReadTimestamp(window, "wake", out wakeAt)
            || protectedStart > lastStart
            || lastStart >= lockAt
            || lockAt >= lightsOut
            || lightsOut >= wakeAt)
        {
            throw new InvalidDataException("Service night window is malformed.");
        }
    }

    private static IReadOnlyList<ServiceSiteRuleProjectionInput> ParseSiteRules(
        JsonElement siteRules)
    {
        if (siteRules.ValueKind != JsonValueKind.Array
            || siteRules.GetArrayLength() > MaximumPolicyRules)
        {
            throw new InvalidDataException("Service site rules are malformed.");
        }

        List<ServiceSiteRuleProjectionInput> result = [];
        foreach (JsonElement rule in siteRules.EnumerateArray())
        {
            if (!HasExactProperties(rule, "domain")
                || !TryReadString(rule, "domain", out string? domain)
                || domain is not { Length: >= 1 and <= 253 })
            {
                throw new InvalidDataException("Service site rule is malformed.");
            }
            result.Add(new(domain, Classify(domain)));
        }
        return result;
    }

    private static bool ValidateAppRules(JsonElement appRules)
    {
        if (appRules.ValueKind != JsonValueKind.Array
            || appRules.GetArrayLength() > MaximumPolicyRules)
        {
            return false;
        }
        foreach (JsonElement rule in appRules.EnumerateArray())
        {
            if (!HasExactProperties(
                    rule,
                    "id",
                    "rootExecutablePath",
                    "helperExecutablePaths",
                    "category",
                    "sessionMinutes",
                    "isConfigured"))
            {
                return false;
            }
        }
        return true;
    }

    private static string? ParseActiveOverride(JsonElement activeOverride)
    {
        if (activeOverride.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (!HasExactProperties(
                activeOverride,
                "kind",
                "requestedAtUtc",
                "startsAtUtc",
                "endsAtUtc",
                "allowedProcessIdentifiers")
            || !TryReadString(activeOverride, "kind", out string? kind)
            || kind is not ("teamRescue" or "emergency" or "entertainment")
            || !TryReadTimestamp(activeOverride, "requestedAtUtc", out DateTimeOffset requestedAt)
            || !TryReadTimestamp(activeOverride, "startsAtUtc", out DateTimeOffset startsAt)
            || !TryReadTimestamp(activeOverride, "endsAtUtc", out DateTimeOffset endsAt)
            || requestedAt > startsAt
            || startsAt >= endsAt
            || !activeOverride.TryGetProperty(
                "allowedProcessIdentifiers",
                out JsonElement allowedProcesses)
            || allowedProcesses.ValueKind != JsonValueKind.Array
            || allowedProcesses.GetArrayLength() > MaximumPolicyRules)
        {
            throw new InvalidDataException("Service active override is malformed.");
        }
        foreach (JsonElement process in allowedProcesses.EnumerateArray())
        {
            if (process.ValueKind != JsonValueKind.String
                || process.GetString() is not { Length: >= 1 and <= 260 })
            {
                throw new InvalidDataException("Service override process is malformed.");
            }
        }
        return kind;
    }

    private ChromePolicyPayload FailOpenPolicy()
    {
        DateTimeOffset now = clock.UtcNow.ToUniversalTime();
        return ChromePolicyProjector.Project(new(
            false,
            true,
            0,
            now,
            "free",
            DateOnly.FromDateTime(now.UtcDateTime),
            now.AddMinutes(-1),
            now.AddMinutes(1),
            now.AddHours(8),
            null,
            Array.Empty<ServiceSiteRuleProjectionInput>()));
    }

    private static BrowserSiteCategory Classify(string domain)
    {
        string normalized = domain.TrimEnd('.').ToLowerInvariant();
        if (IsDomainOrSubdomainOfAny(
                normalized,
                "youtube.com",
                "bilibili.com",
                "iqiyi.com",
                "netflix.com",
                "v.qq.com"))
        {
            return BrowserSiteCategory.Video;
        }
        if (IsDomainOrSubdomainOfAny(
                normalized,
                "steampowered.com",
                "steamcommunity.com",
                "epicgames.com"))
        {
            return BrowserSiteCategory.Gaming;
        }
        if (IsDomainOrSubdomainOfAny(
                normalized,
                "x.com",
                "twitter.com",
                "reddit.com",
                "weibo.com",
                "zhihu.com",
                "facebook.com",
                "instagram.com",
                "douyin.com",
                "tiktok.com"))
        {
            return BrowserSiteCategory.Social;
        }
        return BrowserSiteCategory.Other;
    }

    private static bool IsDomainOrSubdomainOfAny(string value, params string[] roots) =>
        roots.Any(root => string.Equals(value, root, StringComparison.Ordinal)
            || value.EndsWith('.' + root, StringComparison.Ordinal));

    private static bool IsPhase(string? phase) => phase is
        "free" or
        "lastStart" or
        "grace" or
        "landingLocked" or
        "coolingOff" or
        "overrideActive" or
        "morning";

    private static bool HasExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        HashSet<string> expected = new(names, StringComparer.Ordinal);
        int count = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            count++;
            if (!expected.Remove(property.Name))
            {
                return false;
            }
        }
        return count == names.Length && expected.Count == 0;
    }

    private static bool HasUniquePropertiesRecursively(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)
                    || !HasUniquePropertiesRecursively(property.Value))
                {
                    return false;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                if (!HasUniquePropertiesRecursively(child))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool TryReadString(
        JsonElement element,
        string name,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString();
        return value is not null;
    }

    private static bool TryReadBoolean(
        JsonElement element,
        string name,
        out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out JsonElement property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadInt64(
        JsonElement element,
        string name,
        out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value is >= 0 and <= 9_007_199_254_740_991;
    }

    private static bool TryReadOptionalBoundedString(
        JsonElement element,
        string name,
        int maximumLength,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out JsonElement property))
        {
            return false;
        }
        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString();
        return value is { Length: >= 1 } && value.Length <= maximumLength;
    }

    private static bool TryReadTimestamp(
        JsonElement element,
        string name,
        out DateTimeOffset value)
    {
        value = default;
        return TryReadString(element, name, out string? token)
            && token is { Length: >= 20 and <= 64 }
            && DateTimeOffset.TryParse(
                token,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value);
    }

    private static bool TryReadDate(
        JsonElement element,
        string name,
        out DateOnly value)
    {
        value = default;
        return TryReadString(element, name, out string? token)
            && token is { Length: 10 }
            && DateOnly.TryParseExact(
                token,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value);
    }

    private static string EventTypeToken(BrowserPrivacyEventType eventType) =>
        eventType switch
        {
            BrowserPrivacyEventType.MediaPlaying => "mediaPlaying",
            BrowserPrivacyEventType.MediaPaused => "mediaPaused",
            BrowserPrivacyEventType.MediaEnded => "mediaEnded",
            BrowserPrivacyEventType.NavigationBlocked => "navigationBlocked",
            _ => throw new InvalidDataException("Browser event type is invalid."),
        };

    private static string CategoryToken(BrowserSiteCategory category) => category switch
    {
        BrowserSiteCategory.Gaming => "gaming",
        BrowserSiteCategory.Video => "video",
        BrowserSiteCategory.Social => "social",
        BrowserSiteCategory.Other => "other",
        _ => throw new InvalidDataException("Browser site category is invalid."),
    };

    private static string UtcTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);
}
