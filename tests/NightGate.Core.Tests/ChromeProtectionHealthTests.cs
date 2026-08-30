using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class ChromeProtectionHealthTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 15, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Observation_AcceptsOnlyBoundedPrivacyPreservingFacts()
    {
        ChromeProtectionHealth health = Valid();

        Assert.Equal(ChromeProtectionHealth.ExpectedExtensionId, health.ExtensionId);
        Assert.Equal(new string('a', 64), health.ProfileTokenSha256);
        Assert.True(health.IsExpectedExtension);
        Assert.True(health.ProtectionReady);
        Assert.True(health.IsFreshAt(ObservedAt.AddSeconds(90), TimeSpan.FromSeconds(90)));
        Assert.False(health.IsFreshAt(ObservedAt.AddSeconds(91), TimeSpan.FromSeconds(90)));
        Assert.False(health.IsFreshAt(ObservedAt.AddSeconds(-1), TimeSpan.FromSeconds(90)));
    }

    [Theory]
    [InlineData("", "1.0.0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("eefgemhlhbdodhlgjmicnoifhclhdgmz", "1.0.0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("eefgemhlhbdodhlgjmicnoifhclhdgmm", "", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("eefgemhlhbdodhlgjmicnoifhclhdgmm", "1.beta", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("eefgemhlhbdodhlgjmicnoifhclhdgmm", "1.0.0", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("eefgemhlhbdodhlgjmicnoifhclhdgmm", "1.0.0", "not-a-hash")]
    public void Observation_RejectsInvalidIdentityVersionOrHash(
        string extensionId,
        string extensionVersion,
        string hash)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ChromeProtectionHealth(
            extensionId,
            extensionVersion,
            hash,
            1,
            true,
            ObservedAt));
    }

    [Fact]
    public void Observation_RejectsNegativeRevisionNonUtcTimeAndInvalidFreshnessInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Valid(policyRevision: -1));
        Assert.Throws<ArgumentException>(() => Valid(
            observedAt: ObservedAt.ToOffset(TimeSpan.FromHours(8))));
        Assert.Throws<ArgumentException>(() => Valid().IsFreshAt(
            ObservedAt.ToOffset(TimeSpan.FromHours(8)),
            TimeSpan.FromSeconds(90)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Valid().IsFreshAt(
            ObservedAt,
            TimeSpan.Zero));
    }

    [Theory]
    [InlineData("0.1.1", false)]
    [InlineData("0.1.2", false)]
    [InlineData("0.1.3", false)]
    [InlineData("0.1.3.65535", false)]
    [InlineData("0.1.4", true)]
    [InlineData("0.1.4.0", true)]
    [InlineData("0.1.5", true)]
    [InlineData("0.2.0", true)]
    public void ExpectedExtensionRequiresTheCurrentHealthProtocol(
        string extensionVersion,
        bool expected)
    {
        ChromeProtectionHealth health = new(
            ChromeProtectionHealth.ExpectedExtensionId,
            extensionVersion,
            new string('a', 64),
            1,
            true,
            ObservedAt,
            true);

        Assert.Equal(expected, health.IsExpectedExtension);
    }

    private static ChromeProtectionHealth Valid(
        long policyRevision = 1,
        DateTimeOffset? observedAt = null,
        bool protectionReady = true) => new(
        ChromeProtectionHealth.ExpectedExtensionId,
        "1.0.0",
        new string('a', 64),
        policyRevision,
        true,
        observedAt ?? ObservedAt,
        protectionReady);
}
