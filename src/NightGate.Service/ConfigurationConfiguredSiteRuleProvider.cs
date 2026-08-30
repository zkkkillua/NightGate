using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NightGate.Core;

namespace NightGate.Service;

public sealed class ConfigurationConfiguredSiteRuleProvider(IConfiguration configuration) :
    IConfiguredSiteRuleProvider
{
    public const string ConfigurationKey = "NightGate:SiteRules";
    public const int MaximumConfigurationCharacters = 65_536;
    private const int MaximumRules = 100;

    public ConfiguredSiteRuleProviderResult GetRules()
    {
        try
        {
            string? json = configuration[ConfigurationKey];
            if (json is null)
            {
                return ConfiguredSiteRuleProviderResult.Success([]);
            }

            if (json.Length > MaximumConfigurationCharacters)
            {
                return Invalid();
            }

            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() > MaximumRules)
            {
                return Invalid();
            }

            List<SiteRule> rules = [];
            HashSet<string> domains = new(StringComparer.Ordinal);
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (!TryReadExactDomain(element, out string? domain)
                    || !ConfiguredSiteRuleDomainNormalizer.TryNormalize(
                        domain,
                        out string normalized)
                    || !domains.Add(normalized))
                {
                    return Invalid();
                }

                rules.Add(new(normalized));
            }

            return ConfiguredSiteRuleProviderResult.Success(
                rules.OrderBy(rule => rule.Domain, StringComparer.Ordinal));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Invalid();
        }
    }

    private static bool TryReadExactDomain(JsonElement element, out string? domain)
    {
        domain = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        int propertyCount = 0;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            propertyCount++;
            if (propertyCount != 1
                || !string.Equals(property.Name, "domain", StringComparison.Ordinal)
                || property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            domain = property.Value.GetString();
        }

        return propertyCount == 1 && domain is not null;
    }

    private static ConfiguredSiteRuleProviderResult Invalid() =>
        ConfiguredSiteRuleProviderResult.Degraded("configured-site-rules-invalid");
}

internal static class ConfiguredSiteRuleDomainNormalizer
{
    public static bool TryNormalize(string? value, out string normalized) =>
        SiteRuleDomainNormalizer.TryNormalize(value, out normalized);
}

internal static class ConfiguredSiteRuleSetValidator
{
    public static bool IsValid(System.Collections.Immutable.ImmutableArray<SiteRule> rules)
    {
        if (rules.IsDefault || rules.Length > 100)
        {
            return false;
        }

        string? previous = null;
        foreach (SiteRule? rule in rules)
        {
            if (rule is null
                || !ConfiguredSiteRuleDomainNormalizer.TryNormalize(
                    rule.Domain,
                    out string normalized)
                || !string.Equals(rule.Domain, normalized, StringComparison.Ordinal)
                || previous is not null
                    && string.CompareOrdinal(previous, normalized) >= 0)
            {
                return false;
            }

            previous = normalized;
        }

        return true;
    }
}
