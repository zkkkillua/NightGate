using System.Text;
using System.Text.Json;
using NightGate.NativeHost;

namespace NightGate.NativeHost.Tests;

public sealed class ChromePolicyProjectorTests
{
    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset EvaluatedAt =
        DateTimeOffset.Parse("2026-07-14T15:30:00+00:00");

    [Theory]
    [InlineData("free", null, "unrestricted")]
    [InlineData("lastStart", null, "grandfatherOneMedia")]
    [InlineData("grace", null, "grandfatherOneMedia")]
    [InlineData("landingLocked", null, "blocked")]
    [InlineData("coolingOff", "entertainment", "blocked")]
    [InlineData("overrideActive", "emergency", "fullOverride")]
    [InlineData("overrideActive", "entertainment", "fullOverride")]
    [InlineData("overrideActive", "teamRescue", "blocked")]
    [InlineData("morning", null, "unrestricted")]
    public void Project_MapsNightPhaseAndOverrideToEffectiveBrowserMode(
        string phase,
        string? overrideKind,
        string expectedMode)
    {
        ChromePolicyPayload result = ChromePolicyProjector.Project(
            Input(phase: phase, overrideKind: overrideKind));

        Assert.Equal(expectedMode, result.Mode);
        Assert.Equal(overrideKind, result.OverrideKind);
    }

    [Fact]
    public void Project_UsesStableCorrelationSafeRevisionGateAndUtcDeadlines()
    {
        ChromePolicyPayload result = ChromePolicyProjector.Project(Input());

        Assert.Equal(EvaluatedAt.ToUnixTimeMilliseconds(), result.Revision);
        Assert.Equal("night-2026-07-14-1784047200", result.GateId);
        Assert.Equal("2026-07-14T15:30:00.000Z", result.EvaluatedAtUtc);
        Assert.Equal("2026-07-14T16:05:00.000Z", result.LastStartAtUtc);
        Assert.Equal("2026-07-14T16:40:00.000Z", result.LockAtUtc);
        Assert.Equal("2026-07-15T01:00:00.000Z", result.WakeAtUtc);
        Assert.Equal(45_000, result.TtlMs);
    }

    [Fact]
    public void Project_RenewsEvaluationTimeWithoutChangingExplicitSemanticRevision()
    {
        DateTimeOffset renewedAt = EvaluatedAt.AddSeconds(30);

        ChromePolicyPayload result = ChromePolicyProjector.Project(Input(
            revision: 42,
            evaluatedAt: renewedAt));

        Assert.Equal(42, result.Revision);
        Assert.Equal("2026-07-14T15:30:30.000Z", result.EvaluatedAtUtc);
    }

    [Fact]
    public void Project_AcceptsCanonicalAsciiIdnDomainsWithoutRewritingAndDerivesStableRuleIds()
    {
        ServicePolicyProjectionInput input = Input(siteRules:
        [
            new("example.com", BrowserSiteCategory.Video),
            new("xn--fa-hia.de", BrowserSiteCategory.Other),
            new("xn--pxavk3b.gr", BrowserSiteCategory.Social),
            new("xn--pxavn9a.gr", BrowserSiteCategory.Gaming),
        ]);

        ChromePolicyPayload first = ChromePolicyProjector.Project(input);
        ChromePolicyPayload second = ChromePolicyProjector.Project(input);

        Assert.Equal(first.SiteRules, second.SiteRules);
        Assert.Collection(
            first.SiteRules,
            rule =>
            {
                Assert.Equal("example.com", rule.Domain);
                Assert.Equal("video", rule.Category);
                Assert.Matches("^site-[0-9a-f]{16}$", rule.RuleId);
            },
            rule =>
            {
                Assert.Equal("xn--fa-hia.de", rule.Domain);
                Assert.Equal("other", rule.Category);
                Assert.Matches("^site-[0-9a-f]{16}$", rule.RuleId);
            },
            rule =>
            {
                Assert.Equal("xn--pxavk3b.gr", rule.Domain);
                Assert.Equal("social", rule.Category);
                Assert.Matches("^site-[0-9a-f]{16}$", rule.RuleId);
            },
            rule =>
            {
                Assert.Equal("xn--pxavn9a.gr", rule.Domain);
                Assert.Equal("gaming", rule.Category);
                Assert.Matches("^site-[0-9a-f]{16}$", rule.RuleId);
            });
        Assert.DoesNotContain("/", JsonSerializer.Serialize(first.SiteRules), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fa\u00df.de")]
    [InlineData("\u03bf\u03b4\u03cc\u03c2.gr")]
    [InlineData("EXAMPLE.com")]
    [InlineData("example.com.")]
    public void Project_RejectsNonCanonicalServiceDomainsInsteadOfRewritingThem(string domain)
    {
        Assert.Throws<InvalidDataException>(() => ChromePolicyProjector.Project(
            Input(siteRules: [new(domain, BrowserSiteCategory.Other)])));
    }

    [Theory]
    [InlineData("xn--a.com")]
    [InlineData("127.1")]
    [InlineData("127.0.1")]
    [InlineData("example.123")]
    [InlineData("999.999")]
    [InlineData("example.0x10")]
    [InlineData("999.0X10")]
    [InlineData("example.0x")]
    [InlineData("999.0X")]
    [InlineData("intranet")]
    [InlineData("localhost")]
    public void Project_RejectsHostsThatServiceCannotPublish(string domain)
    {
        Assert.Throws<InvalidDataException>(() => ChromePolicyProjector.Project(
            Input(siteRules: [new(domain, BrowserSiteCategory.Other)])));
    }

    [Fact]
    public void Project_RequiresServiceDomainsInStrictOrdinalOrder()
    {
        Assert.Throws<InvalidDataException>(() => ChromePolicyProjector.Project(
            Input(siteRules:
            [
                new("z.example.com", BrowserSiteCategory.Other),
                new("a.example.com", BrowserSiteCategory.Other),
            ])));
    }

    [Fact]
    public void Project_DegradedOrDisabledPolicyIsExplicitFailOpenAndHasNoRules()
    {
        ChromePolicyPayload degraded = ChromePolicyProjector.Project(
            Input(enforcementEnabled: false, isDegraded: true));

        Assert.Equal("failOpen", degraded.Mode);
        Assert.Empty(degraded.SiteRules);
        Assert.Null(degraded.OverrideKind);
    }

    [Theory]
    [InlineData("unknown", null)]
    [InlineData("overrideActive", null)]
    [InlineData("free", "emergency")]
    public void Project_RejectsInconsistentPhaseOrOverride(string phase, string? overrideKind)
    {
        Assert.Throws<InvalidDataException>(() =>
            ChromePolicyProjector.Project(Input(phase: phase, overrideKind: overrideKind)));
    }

    [Fact]
    public void Project_RejectsInvalidOrderingDuplicateOrNonDomainRules()
    {
        Assert.Throws<InvalidDataException>(() => ChromePolicyProjector.Project(
            Input(lockAt: DateTimeOffset.Parse("2026-07-14T16:00:00Z"))));
        Assert.Throws<InvalidDataException>(() => ChromePolicyProjector.Project(
            Input(siteRules: [new("video.example", BrowserSiteCategory.Video), new("video.example", BrowserSiteCategory.Video)])));
        Assert.Throws<InvalidDataException>(() => ChromePolicyProjector.Project(
            Input(siteRules: [new("https://video.example/path", BrowserSiteCategory.Video)])));
    }

    [Fact]
    public void EncodePolicy_ProducesTheExactExtensionSchemaAndEchoesCorrelation()
    {
        string requestJson =
            "{\"version\":1,\"type\":\"getPolicy\",\"requestId\":\"policy-1\","
            + $"\"profileToken\":\"{Token}\",\"payload\":{{}}}}";
        Assert.True(NativeHostMessageCodec.TryDecode(
            Encoding.UTF8.GetBytes(requestJson),
            out NativeHostRequest? request));

        byte[] encoded = NativeHostMessageCodec.EncodePolicy(
            request!,
            ChromePolicyProjector.Project(Input()));
        using JsonDocument document = JsonDocument.Parse(encoded);
        JsonElement root = document.RootElement;

        Assert.Equal(5, root.EnumerateObject().Count());
        Assert.Equal("getPolicyResult", root.GetProperty("type").GetString());
        Assert.Equal("policy-1", root.GetProperty("requestId").GetString());
        Assert.Equal(Token, root.GetProperty("profileToken").GetString());
        JsonElement payload = root.GetProperty("payload");
        Assert.Equal(10, payload.EnumerateObject().Count());
        Assert.Equal("grandfatherOneMedia", payload.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("overrideKind").ValueKind);
    }

    private static ServicePolicyProjectionInput Input(
        string phase = "grace",
        string? overrideKind = null,
        bool enforcementEnabled = true,
        bool isDegraded = false,
        long? revision = null,
        DateTimeOffset? evaluatedAt = null,
        DateTimeOffset? lockAt = null,
        IReadOnlyList<ServiceSiteRuleProjectionInput>? siteRules = null) => new(
            enforcementEnabled,
            isDegraded,
            revision ?? EvaluatedAt.ToUnixTimeMilliseconds(),
            evaluatedAt ?? EvaluatedAt,
            phase,
            new DateOnly(2026, 7, 14),
            DateTimeOffset.Parse("2026-07-14T16:05:00Z"),
            lockAt ?? DateTimeOffset.Parse("2026-07-14T16:40:00Z"),
            DateTimeOffset.Parse("2026-07-15T01:00:00Z"),
            overrideKind,
            siteRules ?? [new("video.example", BrowserSiteCategory.Video)]);
}
