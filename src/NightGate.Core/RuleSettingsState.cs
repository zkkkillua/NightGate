using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace NightGate.Core;

public sealed record RuleSettingsState
{
    public const int MaximumRulesPerSet = 100;
    public const int MaximumUtf8PayloadBytes = 512 * 1024;
    private static readonly JsonSerializerOptions PersistenceJsonOptions =
        new(JsonSerializerDefaults.Web);

    public RuleSettingsState(
        ImmutableArray<AppRule> ActiveAppRules = default,
        ImmutableArray<SiteRule> ActiveSiteRules = default,
        ImmutableArray<AppRule>? PendingAppRules = null,
        ImmutableArray<SiteRule>? PendingSiteRules = null,
        DateOnly? PendingEffectiveNightDate = null,
        DateTimeOffset? PendingSavedAtUtc = null)
    {
        ImmutableArray<AppRule> activeApps = ActiveAppRules.IsDefault ? [] : ActiveAppRules;
        ImmutableArray<SiteRule> activeSites = ActiveSiteRules.IsDefault ? [] : ActiveSiteRules;
        ValidateAppRules(activeApps, nameof(ActiveAppRules));
        ValidateSiteRules(activeSites, nameof(ActiveSiteRules));

        bool hasPendingApps = PendingAppRules is not null;
        bool hasPendingSites = PendingSiteRules is not null;
        bool hasPendingDate = PendingEffectiveNightDate is not null;
        bool hasPendingSavedAt = PendingSavedAtUtc is not null;
        if (hasPendingApps != hasPendingSites
            || hasPendingApps != hasPendingDate
            || hasPendingApps != hasPendingSavedAt)
        {
            throw new ArgumentException("Pending rule settings must be all null or all present.");
        }

        if (PendingAppRules is { } pendingApps)
        {
            if (pendingApps.IsDefault || PendingSiteRules!.Value.IsDefault)
            {
                throw new ArgumentException("Pending rule arrays must be initialized.");
            }

            ValidateAppRules(pendingApps, nameof(PendingAppRules));
            ValidateSiteRules(PendingSiteRules.Value, nameof(PendingSiteRules));
            if (PendingEffectiveNightDate is not { } effectiveNight
                || effectiveNight == default)
            {
                throw new ArgumentOutOfRangeException(nameof(PendingEffectiveNightDate));
            }

            if (PendingSavedAtUtc is not { } savedAt
                || savedAt == default
                || savedAt.Offset != TimeSpan.Zero)
            {
                throw new ArgumentException(
                    "Pending rule settings require a nondefault UTC saved time.",
                    nameof(PendingSavedAtUtc));
            }
        }

        this.ActiveAppRules = activeApps;
        this.ActiveSiteRules = activeSites;
        this.PendingAppRules = PendingAppRules;
        this.PendingSiteRules = PendingSiteRules;
        this.PendingEffectiveNightDate = PendingEffectiveNightDate;
        this.PendingSavedAtUtc = PendingSavedAtUtc;

        if (JsonSerializer.SerializeToUtf8Bytes(this, PersistenceJsonOptions).Length
            > MaximumUtf8PayloadBytes)
        {
            throw new ArgumentException(
                $"Rule settings cannot exceed {MaximumUtf8PayloadBytes} UTF-8 bytes.");
        }
    }

    public static RuleSettingsState Initial { get; } = new();

    public ImmutableArray<AppRule> ActiveAppRules { get; }

    public ImmutableArray<SiteRule> ActiveSiteRules { get; }

    public ImmutableArray<AppRule>? PendingAppRules { get; }

    public ImmutableArray<SiteRule>? PendingSiteRules { get; }

    public DateOnly? PendingEffectiveNightDate { get; }

    public DateTimeOffset? PendingSavedAtUtc { get; }

    private static void ValidateAppRules(ImmutableArray<AppRule> rules, string parameterName)
    {
        if (rules.IsDefault || rules.Length > MaximumRulesPerSet)
        {
            throw new ArgumentException("The app-rule set is invalid or too large.", parameterName);
        }

        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> executablePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (AppRule? rule in rules)
        {
            if (rule is null
                || !rule.IsConfigured
                || rule.RootExecutablePath is null
                || !ids.Add(rule.Id)
                || !roots.Add(rule.RootExecutablePath)
                || !executablePaths.Add(rule.RootExecutablePath)
                || rule.HelperExecutablePaths.Any(path => !executablePaths.Add(path)))
            {
                throw new ArgumentException("The app-rule set is not canonical.", parameterName);
            }
        }
    }

    private static void ValidateSiteRules(ImmutableArray<SiteRule> rules, string parameterName)
    {
        if (rules.IsDefault || rules.Length > MaximumRulesPerSet)
        {
            throw new ArgumentException("The site-rule set is invalid or too large.", parameterName);
        }

        string? previous = null;
        foreach (SiteRule? rule in rules)
        {
            if (rule is null
                || !SiteRuleDomainNormalizer.TryNormalize(rule.Domain, out string normalized)
                || !string.Equals(rule.Domain, normalized, StringComparison.Ordinal)
                || previous is not null && string.CompareOrdinal(previous, normalized) >= 0)
            {
                throw new ArgumentException("The site-rule set is not canonical.", parameterName);
            }

            previous = normalized;
        }
    }
}

public static class SiteRuleDomainNormalizer
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value)
            || !string.Equals(value.Trim(), value, StringComparison.Ordinal)
            || value.IndexOfAny(['/', '@', ':', '*', '?', '#', '[', ']', '\\']) >= 0)
        {
            return false;
        }

        string withoutTrailingDot = value.EndsWith(".", StringComparison.Ordinal)
            ? value[..^1]
            : value;
        if (withoutTrailingDot.Length == 0
            || withoutTrailingDot.StartsWith(".", StringComparison.Ordinal)
            || withoutTrailingDot.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        string ascii;
        try
        {
            ascii = new IdnMapping { UseStd3AsciiRules = true }
                .GetAscii(withoutTrailingDot)
                .ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        string[] labels = ascii.Split('.');
        if (ascii.Length > 253
            || labels.Length < 2
            || EndsInWhatwgNumber(labels[^1])
            || string.Equals(ascii, "localhost", StringComparison.Ordinal)
            || IPAddress.TryParse(ascii, out _)
            || labels.Any(label => !IsAsciiDomainLabel(label)))
        {
            return false;
        }

        normalized = ascii;
        return true;
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
}
