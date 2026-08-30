using System.Collections.Immutable;
using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class RuleSettingsPolicyTests
{
    private static readonly TimeZoneInfo ChinaTime = TimeZoneInfo.CreateCustomTimeZone(
        "NightGate-Rules-UTC+8",
        TimeSpan.FromHours(8),
        "NightGate Rules UTC+8",
        "NightGate Rules UTC+8");

    [Theory]
    [InlineData("2026-07-14T12:59:59+00:00", "2026-07-14")]
    [InlineData("2026-07-14T14:30:00+00:00", "2026-07-15")]
    [InlineData("2026-07-14T16:30:00+00:00", "2026-07-15")]
    public void OutsideLiveEditingWindow_SavesPendingForTheNextApplicableNight(
        string observedAtUtc,
        string effectiveNight)
    {
        RuleSettingsState result = RuleSettingsPolicy.Save(
            RuleSettingsState.Initial,
            Apps("new-game"),
            Sites("video.example"),
            DateTimeOffset.Parse(observedAtUtc),
            ChinaTime);

        Assert.Empty(result.ActiveAppRules);
        Assert.Empty(result.ActiveSiteRules);
        AssertAppsEqual(Apps("new-game"), result.PendingAppRules!.Value);
        AssertSitesEqual(Sites("video.example"), result.PendingSiteRules!.Value);
        Assert.Equal(DateOnly.Parse(effectiveNight), result.PendingEffectiveNightDate);
        Assert.Equal(DateTimeOffset.Parse(observedAtUtc), result.PendingSavedAtUtc);
    }

    [Theory]
    [InlineData("2026-07-14T13:00:00+00:00")]
    [InlineData("2026-07-14T14:29:59+00:00")]
    public void BetweenProtectedStartAndCutoff_AppliesToTheCurrentNightImmediately(
        string observedAtUtc)
    {
        RuleSettingsState current = RuleSettingsPolicy.Save(
            RuleSettingsState.Initial,
            Apps("old-game"),
            Sites("old.example"),
            new(2026, 7, 13, 13, 30, 0, TimeSpan.Zero),
            ChinaTime);

        RuleSettingsState result = RuleSettingsPolicy.Save(
            current,
            Apps("new-game"),
            Sites("video.example"),
            DateTimeOffset.Parse(observedAtUtc),
            ChinaTime);

        AssertAppsEqual(Apps("new-game"), result.ActiveAppRules);
        AssertSitesEqual(Sites("video.example"), result.ActiveSiteRules);
        Assert.Null(result.PendingAppRules);
        Assert.Null(result.PendingEffectiveNightDate);
    }

    [Fact]
    public void AfterMidnight_DoesNotModifyTheStillActivePriorNight()
    {
        RuleSettingsState current = RuleSettingsPolicy.Save(
            RuleSettingsState.Initial,
            Apps("old-game"),
            Sites("old.example"),
            new(2026, 7, 13, 13, 30, 0, TimeSpan.Zero),
            ChinaTime);

        RuleSettingsState result = RuleSettingsPolicy.Save(
            current,
            Apps("new-game"),
            Sites("video.example"),
            new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero),
            ChinaTime);

        AssertAppsEqual(Apps("old-game"), result.ActiveAppRules);
        AssertSitesEqual(Sites("old.example"), result.ActiveSiteRules);
        Assert.Equal(new DateOnly(2026, 7, 15), result.PendingEffectiveNightDate);
    }

    [Fact]
    public void Activate_ReplacesBothRuleSetsAtomicallyOnOrAfterEffectiveNight()
    {
        RuleSettingsState pending = RuleSettingsPolicy.Save(
            new(Apps("old-game"), Sites("old.example")),
            Apps("new-game"),
            Sites("video.example"),
            new(2026, 7, 14, 14, 30, 0, TimeSpan.Zero),
            ChinaTime);

        RuleSettingsState early = RuleSettingsPolicy.Activate(
            pending,
            new(2026, 7, 14));
        RuleSettingsState active = RuleSettingsPolicy.Activate(
            pending,
            new(2026, 7, 15));

        Assert.Equal(pending, early);
        AssertAppsEqual(Apps("new-game"), active.ActiveAppRules);
        AssertSitesEqual(Sites("video.example"), active.ActiveSiteRules);
        Assert.Null(active.PendingAppRules);
        Assert.Null(active.PendingSiteRules);
        Assert.Null(active.PendingEffectiveNightDate);
        Assert.Null(active.PendingSavedAtUtc);
    }

    [Fact]
    public void Save_RejectsNonUtcServiceObservation()
    {
        Assert.Throws<ArgumentException>(() => RuleSettingsPolicy.Save(
            RuleSettingsState.Initial,
            Apps("game"),
            Sites("video.example"),
            new(2026, 7, 14, 21, 0, 0, TimeSpan.FromHours(8)),
            ChinaTime));
    }

    private static ImmutableArray<AppRule> Apps(string id) =>
        [new(id, $@"C:\Games\{id}.exe", [], AppRuleCategory.Game, 35)];

    private static ImmutableArray<SiteRule> Sites(string domain) => [new(domain)];

    private static void AssertAppsEqual(
        ImmutableArray<AppRule> expected,
        ImmutableArray<AppRule> actual)
    {
        Assert.Equal(expected.Select(rule => rule.Id), actual.Select(rule => rule.Id));
        Assert.Equal(
            expected.Select(rule => rule.RootExecutablePath),
            actual.Select(rule => rule.RootExecutablePath));
        Assert.Equal(
            expected.Select(rule => rule.Category),
            actual.Select(rule => rule.Category));
        Assert.Equal(
            expected.Select(rule => rule.SessionMinutes),
            actual.Select(rule => rule.SessionMinutes));
    }

    private static void AssertSitesEqual(
        ImmutableArray<SiteRule> expected,
        ImmutableArray<SiteRule> actual) =>
        Assert.Equal(
            expected.Select(rule => rule.Domain),
            actual.Select(rule => rule.Domain));
}
