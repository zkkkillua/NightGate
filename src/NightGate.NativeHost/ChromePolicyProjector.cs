using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NightGate.NativeHost;

internal sealed record ServiceSiteRuleProjectionInput(
    string Domain,
    BrowserSiteCategory Category);

internal sealed record ServicePolicyProjectionInput(
    bool EnforcementEnabled,
    bool IsDegraded,
    long Revision,
    DateTimeOffset EvaluatedAtUtc,
    string Phase,
    DateOnly NightDate,
    DateTimeOffset LastStartAtUtc,
    DateTimeOffset LockAtUtc,
    DateTimeOffset WakeAtUtc,
    string? OverrideKind,
    IReadOnlyList<ServiceSiteRuleProjectionInput> SiteRules);

internal sealed record ChromeSiteRulePayload(
    string RuleId,
    string Category,
    string Domain);

internal sealed record ChromePolicyPayload(
    long Revision,
    string GateId,
    string EvaluatedAtUtc,
    string LastStartAtUtc,
    int TtlMs,
    string Mode,
    string LockAtUtc,
    string WakeAtUtc,
    string? OverrideKind,
    IReadOnlyList<ChromeSiteRulePayload> SiteRules);

internal static class ChromePolicyProjector
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private const int PolicyTtlMilliseconds = 45_000;

    public static ChromePolicyPayload Project(ServicePolicyProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.SiteRules);
        long revision = input.Revision;

        DateTimeOffset lastStart = input.LastStartAtUtc.ToUniversalTime();
        DateTimeOffset lockAt = input.LockAtUtc.ToUniversalTime();
        DateTimeOffset wakeAt = input.WakeAtUtc.ToUniversalTime();
        if (revision is < 0 or > MaximumSafeInteger
            || lastStart >= lockAt
            || lockAt >= wakeAt)
        {
            throw new InvalidDataException("Policy time boundaries are invalid.");
        }

        bool failOpen = !input.EnforcementEnabled || input.IsDegraded;
        string mode = failOpen
            ? "failOpen"
            : EffectiveMode(input.Phase, input.OverrideKind);
        IReadOnlyList<ChromeSiteRulePayload> rules = failOpen
            ? Array.Empty<ChromeSiteRulePayload>()
            : ProjectSiteRules(input.SiteRules);
        string? overrideKind = failOpen ? null : input.OverrideKind;
        string gateId = string.Create(
            CultureInfo.InvariantCulture,
            $"night-{input.NightDate:yyyy-MM-dd}-{lockAt.ToUnixTimeSeconds()}");

        return new(
            revision,
            gateId,
            UtcTimestamp(input.EvaluatedAtUtc),
            UtcTimestamp(lastStart),
            PolicyTtlMilliseconds,
            mode,
            UtcTimestamp(lockAt),
            UtcTimestamp(wakeAt),
            overrideKind,
            rules);
    }

    private static string EffectiveMode(string phase, string? overrideKind)
    {
        if (phase == "coolingOff")
        {
            return overrideKind == "entertainment"
                ? "blocked"
                : throw new InvalidDataException("Cooling-off policy is inconsistent.");
        }

        if (phase == "overrideActive")
        {
            return overrideKind switch
            {
                "emergency" or "entertainment" => "fullOverride",
                "teamRescue" => "blocked",
                _ => throw new InvalidDataException("Active override policy is inconsistent."),
            };
        }

        if (overrideKind is not null)
        {
            throw new InvalidDataException("Base phase unexpectedly contains an override.");
        }

        return phase switch
        {
            "free" or "morning" => "unrestricted",
            "lastStart" or "grace" => "grandfatherOneMedia",
            "landingLocked" => "blocked",
            _ => throw new InvalidDataException("Unknown policy phase."),
        };
    }

    private static IReadOnlyList<ChromeSiteRulePayload> ProjectSiteRules(
        IReadOnlyList<ServiceSiteRuleProjectionInput> rules)
    {
        if (rules.Count > 100)
        {
            throw new InvalidDataException("Too many site rules.");
        }

        List<ChromeSiteRulePayload> projected = new(rules.Count);
        string? previousDomain = null;
        foreach (ServiceSiteRuleProjectionInput rule in rules)
        {
            if (rule is null)
            {
                throw new InvalidDataException("Site rule is missing.");
            }
            string domain = ValidateCanonicalDomain(rule.Domain);
            if (previousDomain is not null
                && string.CompareOrdinal(previousDomain, domain) >= 0)
            {
                throw new InvalidDataException(
                    "Site rule domains must be unique and ordinally ordered.");
            }
            projected.Add(new(
                RuleId(domain),
                CategoryToken(rule.Category),
                domain));
            previousDomain = domain;
        }
        return projected.ToArray();
    }

    private static string ValidateCanonicalDomain(string value)
    {
        if (string.IsNullOrEmpty(value)
            || !string.Equals(value.Trim(), value, StringComparison.Ordinal)
            || value.IndexOfAny(['/', '@', ':', '*', '?', '#', '[', ']', '\\']) >= 0)
        {
            throw new InvalidDataException("Site rule is not a domain.");
        }

        string withoutTrailingDot = value.EndsWith(".", StringComparison.Ordinal)
            ? value[..^1]
            : value;
        if (withoutTrailingDot.Length == 0
            || withoutTrailingDot.StartsWith(".", StringComparison.Ordinal)
            || withoutTrailingDot.EndsWith(".", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Site rule dot placement is invalid.");
        }

        string ascii;
        try
        {
            ascii = new IdnMapping { UseStd3AsciiRules = true }
                .GetAscii(withoutTrailingDot)
                .ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Site rule IDN is invalid.", exception);
        }

        string[] labels = ascii.Split('.');
        if (!string.Equals(value, ascii, StringComparison.Ordinal)
            || ascii.Length > 253
            || labels.Length < 2
            || EndsInWhatwgNumber(labels[^1])
            || string.Equals(ascii, "localhost", StringComparison.Ordinal)
            || IPAddress.TryParse(ascii, out _)
            || labels.Any(label => !IsAsciiDomainLabel(label)))
        {
            throw new InvalidDataException(
                "Site rule must be a canonical service-published domain.");
        }
        return value;
    }

    private static bool EndsInWhatwgNumber(string label) =>
        label.All(char.IsAsciiDigit)
        || label.Length >= 2
            && label.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && label[2..].All(char.IsAsciiHexDigit);

    private static bool IsAsciiDomainLabel(string label) =>
        label is { Length: >= 1 and <= 63 }
        && char.IsAsciiLetterOrDigit(label[0])
        && char.IsAsciiLetterOrDigit(label[^1])
        && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static string RuleId(string domain)
    {
        byte[] digest = SHA256.HashData(Encoding.ASCII.GetBytes(domain));
        return $"site-{Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant()}";
    }

    private static string CategoryToken(BrowserSiteCategory category) => category switch
    {
        BrowserSiteCategory.Gaming => "gaming",
        BrowserSiteCategory.Video => "video",
        BrowserSiteCategory.Social => "social",
        BrowserSiteCategory.Other => "other",
        _ => throw new InvalidDataException("Unknown site category."),
    };

    private static string UtcTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture);
}
