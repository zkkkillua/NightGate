using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NightGate.Core;

namespace NightGate.Service;

public sealed class ConfigurationConfiguredRuleProvider(IConfiguration configuration) :
    IConfiguredRuleProvider
{
    public const string ConfigurationKey = "NightGate:AppRules";
    public const int MaximumConfigurationCharacters = 65_536;
    private const int MaximumRules = 128;
    private const int MaximumHelpersPerRule = 64;

    public ConfiguredRuleProviderResult GetRules()
    {
        try
        {
            string? json = configuration[ConfigurationKey];
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumConfigurationCharacters)
            {
                return Invalid();
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaximumRules)
            {
                return Invalid();
            }

            ImmutableArray<AppRule>.Builder rules = ImmutableArray.CreateBuilder<AppRule>();
            HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> executablePaths = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement element in root.EnumerateArray())
            {
                if (!TryReadRule(element, out AppRule? rule)
                    || !ids.Add(rule!.Id)
                    || !roots.Add(rule.RootExecutablePath!)
                    || !executablePaths.Add(rule.RootExecutablePath!)
                    || rule.HelperExecutablePaths.Any(path => !executablePaths.Add(path)))
                {
                    return Invalid();
                }

                rules.Add(rule);
            }

            return ConfiguredRuleProviderResult.Success(rules);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Invalid();
        }
    }

    private static bool TryReadRule(JsonElement element, out AppRule? rule)
    {
        rule = null;
        if (element.ValueKind != JsonValueKind.Object
            || !HasOnlyUniqueProperties(
                element,
                "id",
                "rootExecutablePath",
                "helperExecutablePaths",
                "category",
                "sessionMinutes")
            || !TryGetRequiredString(element, "id", out string? id)
            || !TryGetRequiredString(element, "rootExecutablePath", out string? rootPath)
            || !TryGetRequiredString(element, "category", out string? categoryText))
        {
            return false;
        }

        AppRuleCategory category = categoryText switch
        {
            "game" => AppRuleCategory.Game,
            "voice" => AppRuleCategory.Voice,
            _ => (AppRuleCategory)(-1),
        };
        if (category is not AppRuleCategory.Game and not AppRuleCategory.Voice)
        {
            return false;
        }

        int sessionMinutes = 35;
        if (element.TryGetProperty("sessionMinutes", out JsonElement durationElement)
            && (durationElement.ValueKind != JsonValueKind.Number
                || !durationElement.TryGetInt32(out sessionMinutes)))
        {
            return false;
        }

        ImmutableArray<string> helpers = [];
        if (element.TryGetProperty("helperExecutablePaths", out JsonElement helpersElement))
        {
            if (helpersElement.ValueKind != JsonValueKind.Array
                || helpersElement.GetArrayLength() > MaximumHelpersPerRule)
            {
                return false;
            }

            ImmutableArray<string>.Builder helperBuilder = ImmutableArray.CreateBuilder<string>();
            foreach (JsonElement helperElement in helpersElement.EnumerateArray())
            {
                if (helperElement.ValueKind != JsonValueKind.String
                    || helperElement.GetString() is not { } helper)
                {
                    return false;
                }

                helperBuilder.Add(helper);
            }

            helpers = helperBuilder.ToImmutable();
        }

        rule = new(id!, rootPath!, helpers, category, sessionMinutes);
        return true;
    }

    private static bool HasOnlyUniqueProperties(JsonElement element, params string[] allowedNames)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowedNames.Contains(property.Name, StringComparer.Ordinal)
                || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetRequiredString(
        JsonElement element,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static ConfiguredRuleProviderResult Invalid() =>
        ConfiguredRuleProviderResult.Degraded("configured-rules-invalid");
}

public sealed class ConfiguredRuleIdSnapshotProvider(IConfiguredRuleProvider ruleProvider) :
    IAllowedProcessSnapshotProvider
{
    public ImmutableArray<string> GetSnapshot() => GetSnapshotResult().Identifiers;

    public AllowedProcessSnapshotResult GetSnapshotResult()
    {
        try
        {
            ConfiguredRuleProviderResult result = ruleProvider.GetRules();
            if (result.IsDegraded || !ConfiguredRuleSetValidator.IsValid(result.Rules))
            {
                return AllowedProcessSnapshotResult.Unavailable(
                    result.DegradationCode ?? "configured-rules-invalid");
            }

            return AllowedProcessSnapshotResult.Available(
                result.Rules.Select(rule => rule.Id));
        }
        catch (Exception)
        {
            return AllowedProcessSnapshotResult.Unavailable("configured-rules-unavailable");
        }
    }
}

internal static class ConfiguredRuleSetValidator
{
    public static bool IsValid(ImmutableArray<AppRule> rules)
    {
        if (rules.IsDefault)
        {
            return false;
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
                return false;
            }
        }

        return true;
    }
}
