using System.Collections.Immutable;
using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class ContractTests
{
    [Fact]
    public void SiteRule_HasValueEquality()
    {
        Assert.Equal(new SiteRule("example.com"), new SiteRule("example.com"));
    }

    [Fact]
    public void IClock_ExposesDeterministicUtcInstant()
    {
        var expected = new DateTimeOffset(2026, 7, 11, 1, 2, 3, TimeSpan.Zero);
        IClock clock = new TestClock(expected);

        Assert.Equal(expected, clock.UtcNow);
    }

    [Fact]
    public void PolicySnapshot_HasImmutableValueSemantics()
    {
        var evaluatedAt = new DateTimeOffset(2026, 7, 11, 0, 5, 0, TimeSpan.Zero);
        var window = new NightWindow(
            new(2026, 7, 10),
            evaluatedAt.AddHours(-3),
            evaluatedAt,
            evaluatedAt.AddMinutes(35),
            evaluatedAt.AddMinutes(55),
            evaluatedAt.AddHours(9));
        ImmutableArray<AppRule> apps = [new("game")];
        ImmutableArray<SiteRule> sites = [new("example.com")];

        var first = new PolicySnapshot(
            evaluatedAt,
            NightPhase.LastStart,
            window,
            apps,
            sites);
        var second = new PolicySnapshot(
            evaluatedAt,
            NightPhase.LastStart,
            window,
            apps,
            sites);

        Assert.Equal(first, second);
        Assert.Equal(apps, first.AppRules);
        Assert.Equal(sites, first.SiteRules);
    }

    [Fact]
    public void PolicySnapshot_EquivalentEnforcementUsesDeepRuleAndOverrideValues()
    {
        DateTimeOffset evaluatedAt = new(2026, 7, 11, 0, 5, 0, TimeSpan.Zero);
        NightWindow window = new(
            new(2026, 7, 10),
            evaluatedAt.AddHours(-3),
            evaluatedAt,
            evaluatedAt.AddMinutes(35),
            evaluatedAt.AddMinutes(55),
            evaluatedAt.AddHours(9));
        PolicySnapshot first = new(
            evaluatedAt,
            NightPhase.OverrideActive,
            window,
            [new AppRule(
                "game",
                @"C:\Games\Game.exe",
                [@"C:\Games\Helper.exe"],
                AppRuleCategory.Game,
                45)],
            [new SiteRule("example.com")],
            ActiveOverride: new(
                OverrideKind.TeamRescue,
                evaluatedAt,
                evaluatedAt,
                evaluatedAt.AddMinutes(20),
                ["game"]));
        PolicySnapshot reloaded = new(
            evaluatedAt.AddSeconds(30),
            NightPhase.OverrideActive,
            window,
            [new AppRule(
                "game",
                @"C:\Games\Game.exe",
                [@"C:\Games\Helper.exe"],
                AppRuleCategory.Game,
                45)],
            [new SiteRule("example.com")],
            ActiveOverride: new(
                OverrideKind.TeamRescue,
                evaluatedAt,
                evaluatedAt,
                evaluatedAt.AddMinutes(20),
                ["game"]));

        Assert.True(first.HasEquivalentEnforcementTo(reloaded));
    }

    private sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
