using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class NightNoticePolicyTests
{
    private static readonly NightWindow Window = new(
        new(2026, 7, 6),
        new(2026, 7, 6, 21, 0, 0, TimeSpan.Zero),
        new(2026, 7, 7, 0, 5, 0, TimeSpan.Zero),
        new(2026, 7, 7, 0, 40, 0, TimeSpan.Zero),
        new(2026, 7, 7, 1, 0, 0, TimeSpan.Zero),
        new(2026, 7, 7, 9, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(NightPhase.LastStart, "2026-07-07T00:05:30+00:00", NightNoticeKind.LastStart)]
    public void DueNotice_ReturnsOnlyTheSingleMostRelevantKind(
        NightPhase phase,
        string observedAt,
        NightNoticeKind expected)
    {
        Assert.Equal(
            expected,
            NightNoticePolicy.GetDueNotice(
                Window,
                phase,
                DateTimeOffset.Parse(observedAt)));
    }

    [Theory]
    [InlineData(NightPhase.Morning, "2026-07-07T09:00:00+00:00")]
    [InlineData(NightPhase.LandingLocked, "2026-07-07T00:40:00+00:00")]
    [InlineData(NightPhase.CoolingOff, "2026-07-07T00:30:00+00:00")]
    [InlineData(NightPhase.OverrideActive, "2026-07-07T00:30:00+00:00")]
    [InlineData(NightPhase.Free, "2026-07-06T20:59:59+00:00")]
    [InlineData(NightPhase.Grace, "2026-07-07T00:30:00+00:00")]
    [InlineData(NightPhase.Grace, "2026-07-07T00:38:00+00:00")]
    public void NoNoticeOutsideItsCalmWindow(NightPhase phase, string observedAt)
    {
        Assert.Null(NightNoticePolicy.GetDueNotice(
            Window,
            phase,
            DateTimeOffset.Parse(observedAt)));
    }

    [Fact]
    public void FreeTimeStaysSilentUntilThirtyMinutesBeforeTheEffectiveLastStart()
    {
        Assert.Null(NightNoticePolicy.GetDueNotice(
            Window,
            NightPhase.Free,
            Window.ProtectedStart));
        Assert.Null(NightNoticePolicy.GetDueNotice(
            Window,
            NightPhase.Free,
            Window.LastStart.AddMinutes(-30).AddSeconds(-1)));
        Assert.Equal(
            NightNoticeKind.IfThenPlan,
            NightNoticePolicy.GetDueNotice(
                Window,
                NightPhase.Free,
                Window.LastStart.AddMinutes(-30)));
    }

    [Fact]
    public void PlanWindowFollowsAnEarlierConfiguredGameCutoff()
    {
        DateTimeOffset effectiveLastStart = Window.Lock.AddMinutes(-90);

        Assert.Null(NightNoticePolicy.GetDueNotice(
            Window,
            NightPhase.Free,
            effectiveLastStart.AddMinutes(-30).AddSeconds(-1),
            effectiveLastStart));
        Assert.Equal(
            NightNoticeKind.IfThenPlan,
            NightNoticePolicy.GetDueNotice(
                Window,
                NightPhase.Free,
                effectiveLastStart.AddMinutes(-30),
                effectiveLastStart));
    }

    [Fact]
    public void DueNotice_RejectsNonUtcOrMismatchedWindowPhase()
    {
        Assert.Throws<ArgumentException>(() => NightNoticePolicy.GetDueNotice(
            Window,
            NightPhase.Free,
            new(2026, 7, 6, 21, 0, 0, TimeSpan.FromHours(8))));
        Assert.Null(NightNoticePolicy.GetDueNotice(
            Window,
            NightPhase.LastStart,
            Window.LastStart.AddMinutes(1)));
    }

    [Fact]
    public void LastStartNotice_UsesTheEarlierConfiguredGameCutoffWhilePhaseIsFree()
    {
        DateTimeOffset longestGameCutoff = Window.Lock.AddMinutes(-90);

        Assert.Equal(
            NightNoticeKind.LastStart,
            NightNoticePolicy.GetDueNotice(
                Window,
                NightPhase.Free,
                longestGameCutoff,
                longestGameCutoff));
        Assert.Equal(
            NightNoticeKind.LastStart,
            NightNoticePolicy.GetDueNotice(
                Window,
                NightPhase.LastStart,
                Window.LastStart,
                longestGameCutoff));
    }

    [Fact]
    public void MissedLastStart_RemainsDueUntilTheGraceTenWindow()
    {
        DateTimeOffset longestGameCutoff = Window.Lock.AddMinutes(-90);

        Assert.Equal(
            NightNoticeKind.LastStart,
            NightNoticePolicy.GetDueNotice(
                Window,
                NightPhase.Free,
                longestGameCutoff.AddMinutes(2),
                longestGameCutoff));
        Assert.Equal(
            NightNoticeKind.LastStart,
            NightNoticePolicy.GetDueNotice(
                Window,
                NightPhase.Grace,
                Window.Lock.AddMinutes(-11),
                longestGameCutoff));
    }

    [Fact]
    public void FinalTenMinutesUseThePersistentCountdownInsteadOfBalloons()
    {
        DateTimeOffset longestGameCutoff = Window.Lock.AddMinutes(-90);

        Assert.Null(NightNoticePolicy.GetDueNotice(
            Window,
            NightPhase.Grace,
            Window.Lock.AddMinutes(-10),
            longestGameCutoff));
        Assert.Null(NightNoticePolicy.GetDueNotice(
            Window,
            NightPhase.Grace,
            Window.Lock.AddMinutes(-2),
            longestGameCutoff));
    }
}
