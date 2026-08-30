using System.Globalization;
using System.Text.Json;

namespace NightGate.NativeHost;

internal enum NativeHostRequestKind
{
    GetPolicy,
    Heartbeat,
    MediaState,
    NavigationAttempt,
}

internal enum BrowserPrivacyEventType
{
    MediaPlaying,
    MediaPaused,
    MediaEnded,
    NavigationBlocked,
}

internal enum BrowserSiteCategory
{
    Gaming,
    Video,
    Social,
    Other,
}

internal sealed record BrowserPrivacyEvent(
    DateTimeOffset TimestampUtc,
    BrowserPrivacyEventType EventType,
    string RuleId,
    BrowserSiteCategory Category);

internal sealed record NativeHeartbeatPayload(
    long Revision,
    string ExtensionVersion,
    bool IncognitoAllowed,
    bool ProtectionReady);

internal sealed record NativeHostRequest(
    NativeHostRequestKind Kind,
    string RequestId,
    string ProfileToken,
    NativeHeartbeatPayload? Heartbeat = null,
    BrowserPrivacyEvent? PrivacyEvent = null);

internal static class NativeHostMessageCodec
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly string[] EnvelopeProperties =
        ["version", "type", "requestId", "profileToken", "payload"];
    private static readonly string[] EventProperties =
        ["timestamp", "eventType", "ruleId", "category"];
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web);

    public static bool TryDecode(
        ReadOnlyMemory<byte> bodyUtf8,
        out NativeHostRequest? request)
    {
        request = null;
        if (bodyUtf8.IsEmpty
            || bodyUtf8.Length > ChromeNativeMessageFraming.MaximumBodyBytes)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bodyUtf8,
                new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (!HasExactProperties(root, EnvelopeProperties)
                || !HasUniquePropertiesRecursively(root)
                || !root.TryGetProperty("version", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int protocolVersion)
                || protocolVersion != 1
                || !TryReadString(root, "type", out string? type)
                || !TryReadString(root, "requestId", out string? requestId)
                || !IsPrintableIdentifier(requestId)
                || !TryReadString(root, "profileToken", out string? profileToken)
                || !IsProfileToken(profileToken)
                || !root.TryGetProperty("payload", out JsonElement payload)
                || payload.ValueKind != JsonValueKind.Object
                || !TryParseKind(type, out NativeHostRequestKind kind))
            {
                return false;
            }

            NativeHeartbeatPayload? heartbeat = null;
            BrowserPrivacyEvent? privacyEvent = null;
            switch (kind)
            {
                case NativeHostRequestKind.GetPolicy:
                    if (!HasExactProperties(payload, []))
                    {
                        return false;
                    }
                    break;
                case NativeHostRequestKind.Heartbeat:
                    bool hasProtectionReady = payload.TryGetProperty(
                        "protectionReady",
                        out JsonElement protectionReadyElement);
                    bool protectionReady = false;
                    string[] heartbeatProperties = hasProtectionReady
                        ? ["revision", "extensionVersion", "incognitoAllowed", "protectionReady"]
                        : ["revision", "extensionVersion", "incognitoAllowed"];
                    if (!HasExactProperties(payload, heartbeatProperties)
                        || !payload.TryGetProperty("revision", out JsonElement revision)
                        || revision.ValueKind != JsonValueKind.Number
                        || !revision.TryGetInt64(out long parsedRevision)
                        || parsedRevision is < -1 or > MaximumSafeInteger
                        || !TryReadString(
                            payload,
                            "extensionVersion",
                            out string? extensionVersion)
                        || !IsExtensionVersion(extensionVersion)
                        || !TryReadBoolean(
                            payload,
                            "incognitoAllowed",
                            out bool incognitoAllowed)
                        || (hasProtectionReady
                            && !TryReadBoolean(
                                payload,
                                "protectionReady",
                                out protectionReady)))
                    {
                        return false;
                    }
                    heartbeat = new(
                        parsedRevision,
                        extensionVersion!,
                        incognitoAllowed,
                        protectionReady);
                    break;
                case NativeHostRequestKind.MediaState:
                case NativeHostRequestKind.NavigationAttempt:
                    if (!TryParsePrivacyEvent(kind, payload, out privacyEvent))
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }

            request = new(
                kind,
                requestId!,
                profileToken!,
                heartbeat,
                privacyEvent);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static byte[] EncodeAcknowledgement(
        NativeHostRequest request,
        bool accepted)
    {
        ArgumentNullException.ThrowIfNull(request);
        byte[] response = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                version = 1,
                type = $"{KindToken(request.Kind)}Result",
                requestId = request.RequestId,
                profileToken = request.ProfileToken,
                payload = new { accepted },
            },
            SerializerOptions);
        if (response.Length > ChromeNativeMessageFraming.MaximumBodyBytes)
        {
            throw new InvalidDataException("Native response exceeds the allowed size.");
        }
        return response;
    }

    public static byte[] EncodePolicy(
        NativeHostRequest request,
        ChromePolicyPayload policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        if (request.Kind != NativeHostRequestKind.GetPolicy)
        {
            throw new ArgumentException("Only getPolicy accepts a policy response.", nameof(request));
        }
        byte[] response = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                version = 1,
                type = "getPolicyResult",
                requestId = request.RequestId,
                profileToken = request.ProfileToken,
                payload = policy,
            },
            SerializerOptions);
        if (response.Length > ChromeNativeMessageFraming.MaximumBodyBytes)
        {
            throw new InvalidDataException("Native response exceeds the allowed size.");
        }
        return response;
    }

    private static bool TryParsePrivacyEvent(
        NativeHostRequestKind kind,
        JsonElement payload,
        out BrowserPrivacyEvent? privacyEvent)
    {
        privacyEvent = null;
        if (!HasExactProperties(payload, EventProperties)
            || !TryReadString(payload, "timestamp", out string? timestamp)
            || !TryParseCanonicalUtc(timestamp, out DateTimeOffset timestampUtc)
            || !TryReadString(payload, "eventType", out string? eventTypeToken)
            || !TryParseEventType(eventTypeToken, out BrowserPrivacyEventType eventType)
            || (kind == NativeHostRequestKind.NavigationAttempt
                ? eventType != BrowserPrivacyEventType.NavigationBlocked
                : eventType == BrowserPrivacyEventType.NavigationBlocked)
            || !TryReadString(payload, "ruleId", out string? ruleId)
            || !IsPrintableIdentifier(ruleId)
            || !TryReadString(payload, "category", out string? categoryToken)
            || !TryParseCategory(categoryToken, out BrowserSiteCategory category))
        {
            return false;
        }

        privacyEvent = new(timestampUtc, eventType, ruleId!, category);
        return true;
    }

    private static bool TryParseCanonicalUtc(
        string? token,
        out DateTimeOffset value)
    {
        value = default;
        if (token is null)
        {
            return false;
        }

        string format = token.Length switch
        {
            20 => "yyyy-MM-dd'T'HH:mm:ss'Z'",
            24 => "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            _ => string.Empty,
        };
        return format.Length != 0
            && DateTimeOffset.TryParseExact(
                token,
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value);
    }

    private static bool HasExactProperties(JsonElement element, string[] names)
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
        JsonElement value,
        string name,
        out string? token)
    {
        token = null;
        if (!value.TryGetProperty(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        token = element.GetString();
        return token is not null;
    }

    private static bool IsPrintableIdentifier(string? token) =>
        token is { Length: >= 1 and <= 64 }
        && token.All(character => character is >= '\x20' and <= '\x7e');

    private static bool IsProfileToken(string? token) =>
        token is { Length: 43 }
        && token.All(character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_' or '-');

    private static bool IsExtensionVersion(string? token)
    {
        if (token is not { Length: >= 1 and <= 32 })
        {
            return false;
        }

        string[] segments = token.Split('.');
        return segments is { Length: >= 1 and <= 4 }
            && segments.All(segment =>
                segment.Length > 0
                && segment.All(char.IsAsciiDigit)
                && ushort.TryParse(
                    segment,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _));
    }

    private static bool TryReadBoolean(
        JsonElement value,
        string name,
        out bool result)
    {
        result = false;
        if (!value.TryGetProperty(name, out JsonElement element)
            || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }
        result = element.GetBoolean();
        return true;
    }

    private static bool TryParseKind(
        string? token,
        out NativeHostRequestKind kind)
    {
        switch (token)
        {
            case "getPolicy": kind = NativeHostRequestKind.GetPolicy; return true;
            case "heartbeat": kind = NativeHostRequestKind.Heartbeat; return true;
            case "mediaState": kind = NativeHostRequestKind.MediaState; return true;
            case "navigationAttempt": kind = NativeHostRequestKind.NavigationAttempt; return true;
            default: kind = default; return false;
        }
    }

    private static bool TryParseEventType(
        string? token,
        out BrowserPrivacyEventType eventType)
    {
        switch (token)
        {
            case "mediaPlaying": eventType = BrowserPrivacyEventType.MediaPlaying; return true;
            case "mediaPaused": eventType = BrowserPrivacyEventType.MediaPaused; return true;
            case "mediaEnded": eventType = BrowserPrivacyEventType.MediaEnded; return true;
            case "navigationBlocked": eventType = BrowserPrivacyEventType.NavigationBlocked; return true;
            default: eventType = default; return false;
        }
    }

    private static bool TryParseCategory(
        string? token,
        out BrowserSiteCategory category)
    {
        switch (token)
        {
            case "gaming": category = BrowserSiteCategory.Gaming; return true;
            case "video": category = BrowserSiteCategory.Video; return true;
            case "social": category = BrowserSiteCategory.Social; return true;
            case "other": category = BrowserSiteCategory.Other; return true;
            default: category = default; return false;
        }
    }

    internal static string KindToken(NativeHostRequestKind kind) => kind switch
    {
        NativeHostRequestKind.GetPolicy => "getPolicy",
        NativeHostRequestKind.Heartbeat => "heartbeat",
        NativeHostRequestKind.MediaState => "mediaState",
        NativeHostRequestKind.NavigationAttempt => "navigationAttempt",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
