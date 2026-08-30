using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class ConfiguredSiteRuleProviderTests
{
    [Fact]
    public void MissingConfiguration_IsSuccessfulEmptySelection()
    {
        ConfigurationManager configuration = new();

        ConfiguredSiteRuleProviderResult result =
            new ConfigurationConfiguredSiteRuleProvider(configuration).GetRules();

        Assert.False(result.IsDegraded);
        Assert.True(result.EnforcementEnabled);
        Assert.Empty(result.Rules);
        Assert.Null(result.DegradationCode);
    }

    [Fact]
    public void ConfiguredDomains_AreCanonicalAndPublishedInOrdinalOrder()
    {
        ConfigurationManager configuration = new()
        {
            [ConfigurationConfiguredSiteRuleProvider.ConfigurationKey] =
                """
                [
                  { "domain": "Z.Example.COM" },
                  { "domain": "例子.测试" },
                  { "domain": "EXAMPLE.COM." }
                ]
                """,
        };

        ConfiguredSiteRuleProviderResult result =
            new ConfigurationConfiguredSiteRuleProvider(configuration).GetRules();

        Assert.False(result.IsDegraded);
        Assert.Equal(
            ["example.com", "xn--fsqu00a.xn--0zwm56d", "z.example.com"],
            result.Rules.Select(rule => rule.Domain).ToArray());
    }

    [Theory]
    [InlineData("fa\u00DF.de", "xn--fa-hia.de")]
    [InlineData("\u03BF\u03B4\u03CC\u03C2.gr", "xn--pxavk3b.gr")]
    [InlineData("\u03BF\u03B4\u03CC\u03C3.gr", "xn--pxavn9a.gr")]
    public void UnicodeDomains_UseTheSameNonTransitionalIdnMappingAsChrome(
        string domain,
        string expected)
    {
        string json = JsonSerializer.Serialize(new[] { new { domain } });

        ConfiguredSiteRuleProviderResult result = Provider(json).GetRules();

        Assert.False(result.IsDegraded);
        Assert.Equal(expected, Assert.Single(result.Rules).Domain);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[{\"domain\":1}]")]
    [InlineData("[{\"domain\":\"example.com\",\"extra\":true}]")]
    [InlineData("[{\"domain\":\"example.com\",\"domain\":\"other.com\"}]")]
    [InlineData("[{\"Domain\":\"example.com\"}]")]
    public void NonExactJsonShape_IsDegradedWithoutPartialRules(string json)
    {
        ConfiguredSiteRuleProviderResult result = Provider(json).GetRules();

        AssertDegraded(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" example.com")]
    [InlineData("example.com ")]
    [InlineData("https://example.com")]
    [InlineData("example.com:443")]
    [InlineData("example.com/path")]
    [InlineData("user@example.com")]
    [InlineData("*.example.com")]
    [InlineData("localhost")]
    [InlineData("intranet")]
    [InlineData(".example.com")]
    [InlineData("example..com")]
    [InlineData("example.com..")]
    [InlineData("-example.com")]
    [InlineData("example-.com")]
    [InlineData("127.0.0.1")]
    [InlineData("127.1")]
    [InlineData("127.0.1")]
    [InlineData("[::1]")]
    [InlineData("exa_mple.com")]
    [InlineData("xn--.com")]
    [InlineData("xn--a.com")]
    [InlineData("example.123")]
    [InlineData("999.999")]
    [InlineData("example.0x10")]
    [InlineData("999.0X10")]
    [InlineData("example.0x")]
    [InlineData("999.0X")]
    public void ForbiddenDomainForm_IsDegradedWithoutPartialRules(string domain)
    {
        string json = JsonSerializer.Serialize(new[] { new { domain } });

        ConfiguredSiteRuleProviderResult result = Provider(json).GetRules();

        AssertDegraded(result);
    }

    [Fact]
    public void OversizedLabel_IsDegradedWithoutPartialRules()
    {
        string json = JsonSerializer.Serialize(new[]
        {
            new { domain = $"{new string('a', 64)}.com" },
        });

        ConfiguredSiteRuleProviderResult result = Provider(json).GetRules();

        AssertDegraded(result);
    }

    [Fact]
    public void DuplicateLogicalDomains_AreDegradedWithoutPartialRules()
    {
        string json = JsonSerializer.Serialize(new[]
        {
            new { domain = "EXAMPLE.COM." },
            new { domain = "example.com" },
        });

        ConfiguredSiteRuleProviderResult result = Provider(json).GetRules();

        AssertDegraded(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("null")]
    public void MalformedConfiguration_IsDegraded(string json)
    {
        ConfiguredSiteRuleProviderResult result = Provider(json).GetRules();

        AssertDegraded(result);
    }

    [Fact]
    public void RuleCount_IsBoundedAtOneHundred()
    {
        string hundred = JsonSerializer.Serialize(
            Enumerable.Range(0, 100).Select(index => new
            {
                domain = $"site-{index:D3}.example.com",
            }));
        string hundredAndOne = JsonSerializer.Serialize(
            Enumerable.Range(0, 101).Select(index => new
            {
                domain = $"site-{index:D3}.example.com",
            }));

        ConfiguredSiteRuleProviderResult accepted = Provider(hundred).GetRules();
        ConfiguredSiteRuleProviderResult rejected = Provider(hundredAndOne).GetRules();

        Assert.False(accepted.IsDegraded);
        Assert.Equal(100, accepted.Rules.Length);
        AssertDegraded(rejected);
    }

    [Fact]
    public void OversizedConfiguration_IsDegradedBeforeParsing()
    {
        string json = "[" + new string(
            ' ',
            ConfigurationConfiguredSiteRuleProvider.MaximumConfigurationCharacters) + "]";

        ConfiguredSiteRuleProviderResult result = Provider(json).GetRules();

        AssertDegraded(result);
    }

    [Fact]
    public void MaximumConfigurationBoundary_IsMeasuredBeforeParsing()
    {
        string json = "[" + new string(
            ' ',
            ConfigurationConfiguredSiteRuleProvider.MaximumConfigurationCharacters - 2) + "]";

        ConfiguredSiteRuleProviderResult result = Provider(json).GetRules();

        Assert.False(result.IsDegraded);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public void ConfigurationAccessFailure_IsDegraded()
    {
        ConfiguredSiteRuleProviderResult result =
            new ConfigurationConfiguredSiteRuleProvider(new ThrowingConfiguration())
                .GetRules();

        AssertDegraded(result);
    }

    private static ConfigurationConfiguredSiteRuleProvider Provider(string json)
    {
        ConfigurationManager configuration = new()
        {
            [ConfigurationConfiguredSiteRuleProvider.ConfigurationKey] = json,
        };
        return new(configuration);
    }

    private static void AssertDegraded(ConfiguredSiteRuleProviderResult result)
    {
        Assert.True(result.IsDegraded);
        Assert.False(result.EnforcementEnabled);
        Assert.Empty(result.Rules);
        Assert.Equal("configured-site-rules-invalid", result.DegradationCode);
    }

    private sealed class ThrowingConfiguration : IConfiguration
    {
        public string? this[string key]
        {
            get => throw new IOException("configuration-unavailable");
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() =>
            new CancellationChangeToken(CancellationToken.None);

        public IConfigurationSection GetSection(string key) =>
            throw new NotSupportedException();
    }
}
