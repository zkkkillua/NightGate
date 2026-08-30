using System.Collections.Immutable;

namespace NightGate.Core;

public sealed record PolicySnapshot(
    DateTimeOffset EvaluatedAt,
    NightPhase Phase,
    NightWindow Window,
    ImmutableArray<AppRule> AppRules,
    ImmutableArray<SiteRule> SiteRules,
    bool EnforcementEnabled = true,
    bool IsDegraded = false,
    ActiveOverride? ActiveOverride = null)
{
    public long Revision { get; init; } = EvaluatedAt.ToUnixTimeMilliseconds();

    public bool HasEquivalentEnforcementTo(PolicySnapshot? other) =>
        other is not null
        && Phase == other.Phase
        && Window == other.Window
        && AppRules.Length == other.AppRules.Length
        && AppRules.Zip(other.AppRules).All(pair => AppRuleEquals(pair.First, pair.Second))
        && SiteRules.SequenceEqual(other.SiteRules)
        && EnforcementEnabled == other.EnforcementEnabled
        && IsDegraded == other.IsDegraded
        && ActiveOverrideEquals(ActiveOverride, other.ActiveOverride);

    private static bool AppRuleEquals(AppRule left, AppRule right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && string.Equals(
            left.RootExecutablePath,
            right.RootExecutablePath,
            StringComparison.OrdinalIgnoreCase)
        && left.HelperExecutablePaths.SequenceEqual(
            right.HelperExecutablePaths,
            StringComparer.OrdinalIgnoreCase)
        && left.Category == right.Category
        && left.SessionMinutes == right.SessionMinutes;

    private static bool ActiveOverrideEquals(ActiveOverride? left, ActiveOverride? right) =>
        ReferenceEquals(left, right)
        || left is not null
            && right is not null
            && left.Kind == right.Kind
            && left.RequestedAtUtc == right.RequestedAtUtc
            && left.StartsAtUtc == right.StartsAtUtc
            && left.EndsAtUtc == right.EndsAtUtc
            && left.AllowedProcessIdentifiers.SequenceEqual(
                right.AllowedProcessIdentifiers,
                StringComparer.Ordinal);
}
