using System.Collections.Immutable;

namespace NightGate.Core;

public interface IConfiguredRuleProvider
{
    ConfiguredRuleProviderResult GetRules();
}

public sealed record ConfiguredRuleProviderResult
{
    private ConfiguredRuleProviderResult(
        StorageMode mode,
        ImmutableArray<AppRule> rules,
        string? degradationCode)
    {
        Mode = mode;
        Rules = rules;
        DegradationCode = degradationCode;
    }

    public StorageMode Mode { get; }

    public ImmutableArray<AppRule> Rules { get; }

    public string? DegradationCode { get; }

    public bool IsDegraded => Mode == StorageMode.Degraded;

    public bool EnforcementEnabled => Mode == StorageMode.Success;

    public static ConfiguredRuleProviderResult Success(IEnumerable<AppRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return new(StorageMode.Success, [.. rules], null);
    }

    public static ConfiguredRuleProviderResult Degraded(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new(StorageMode.Degraded, [], code);
    }
}
