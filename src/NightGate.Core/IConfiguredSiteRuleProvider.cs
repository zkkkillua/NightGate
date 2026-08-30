using System.Collections.Immutable;

namespace NightGate.Core;

public interface IConfiguredSiteRuleProvider
{
    ConfiguredSiteRuleProviderResult GetRules();
}

public sealed record ConfiguredSiteRuleProviderResult
{
    private ConfiguredSiteRuleProviderResult(
        StorageMode mode,
        ImmutableArray<SiteRule> rules,
        string? degradationCode)
    {
        Mode = mode;
        Rules = rules;
        DegradationCode = degradationCode;
    }

    public StorageMode Mode { get; }

    public ImmutableArray<SiteRule> Rules { get; }

    public string? DegradationCode { get; }

    public bool IsDegraded => Mode == StorageMode.Degraded;

    public bool EnforcementEnabled => Mode == StorageMode.Success;

    public static ConfiguredSiteRuleProviderResult Success(IEnumerable<SiteRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new(StorageMode.Success, [.. rules], null);
    }

    public static ConfiguredSiteRuleProviderResult Degraded(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new(StorageMode.Degraded, [], code);
    }
}
