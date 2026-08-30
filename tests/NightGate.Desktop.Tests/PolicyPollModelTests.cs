using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class PolicyPollModelTests
{
    private static readonly DateTimeOffset LastStart =
        new(2026, 7, 7, 0, 5, 0, TimeSpan.Zero);

    [Fact]
    public void Free_PollsAtThirtySecondsButNeverPastAuthoritativeLastStart()
    {
        DesktopPolicyPollModel model = new();
        DesktopPolicySnapshotDto policy = Policy(DesktopNightPhase.Free);

        Assert.Equal(TimeSpan.FromSeconds(30), model.GetNextDelay(policy, LastStart.AddMinutes(-5)));
        Assert.Equal(TimeSpan.FromSeconds(10), model.GetNextDelay(policy, LastStart.AddSeconds(-10)));
        Assert.Equal(TimeSpan.Zero, model.GetNextDelay(policy, LastStart));
    }

    [Theory]
    [InlineData(DesktopNightPhase.LastStart)]
    [InlineData(DesktopNightPhase.Grace)]
    [InlineData(DesktopNightPhase.LandingLocked)]
    [InlineData(DesktopNightPhase.CoolingOff)]
    [InlineData(DesktopNightPhase.OverrideActive)]
    public void RestrictedPhases_PollEverySecond(DesktopNightPhase phase)
    {
        DesktopPolicyPollModel model = new();

        Assert.Equal(TimeSpan.FromSeconds(1), model.GetNextDelay(Policy(phase), LastStart.AddMinutes(1)));
    }

    [Fact]
    public void AcceptedOverride_RequestsOneImmediateRefresh()
    {
        DesktopPolicyPollModel model = new();
        DesktopPolicySnapshotDto policy = Policy(DesktopNightPhase.Grace);

        model.MarkOverrideAccepted();

        Assert.Equal(TimeSpan.Zero, model.GetNextDelay(policy, LastStart.AddMinutes(1)));
        Assert.Equal(TimeSpan.FromSeconds(1), model.GetNextDelay(policy, LastStart.AddMinutes(1)));
    }

    [Fact]
    public void StaleSnapshotBehindBoundary_RefreshesImmediatelyOnlyOnceThenWaitsOneSecond()
    {
        DesktopPolicyPollModel model = new();
        DesktopPolicySnapshotDto staleFree = Policy(DesktopNightPhase.Free);

        Assert.Equal(TimeSpan.Zero, model.GetNextDelay(staleFree, LastStart));
        Assert.Equal(TimeSpan.FromSeconds(1), model.GetNextDelay(staleFree, LastStart));
        Assert.Equal(TimeSpan.FromSeconds(1), model.GetNextDelay(staleFree, LastStart.AddSeconds(1)));
    }

    [Fact]
    public void LaterBoundary_AllowsOneNewImmediateRefreshForTheSameNight()
    {
        DesktopPolicyPollModel model = new();
        DesktopPolicySnapshotDto staleFree = Policy(DesktopNightPhase.Free);
        DateTimeOffset lockTime = staleFree.Window.Lock;

        Assert.Equal(TimeSpan.Zero, model.GetNextDelay(staleFree, LastStart));
        Assert.Equal(TimeSpan.FromSeconds(1), model.GetNextDelay(staleFree, LastStart));
        Assert.Equal(TimeSpan.Zero, model.GetNextDelay(staleFree, lockTime));
        Assert.Equal(TimeSpan.FromSeconds(1), model.GetNextDelay(staleFree, lockTime));
    }

    [Fact]
    public void SameBoundaryOnNewNight_AllowsOneNewImmediateRefresh()
    {
        DesktopPolicyPollModel model = new();
        DesktopPolicySnapshotDto firstNight = Policy(DesktopNightPhase.Free);
        DateOnly secondDate = firstNight.Window.NightDate.AddDays(1);
        DesktopPolicySnapshotDto secondNight = Policy(DesktopNightPhase.Free, secondDate);

        Assert.Equal(TimeSpan.Zero, model.GetNextDelay(firstNight, firstNight.Window.LastStart));
        Assert.Equal(TimeSpan.FromSeconds(1), model.GetNextDelay(firstNight, firstNight.Window.LastStart));
        Assert.Equal(TimeSpan.Zero, model.GetNextDelay(secondNight, secondNight.Window.LastStart));
        Assert.Equal(TimeSpan.FromSeconds(1), model.GetNextDelay(secondNight, secondNight.Window.LastStart));
    }

    [Fact]
    public void AcceptedOverride_KeepsItsSingleImmediateRefreshWithoutRepeatingStaleBoundary()
    {
        DesktopPolicyPollModel model = new();
        DesktopPolicySnapshotDto staleFree = Policy(DesktopNightPhase.Free);
        model.MarkOverrideAccepted();

        Assert.Equal(TimeSpan.Zero, model.GetNextDelay(staleFree, LastStart));
        Assert.Equal(TimeSpan.FromSeconds(1), model.GetNextDelay(staleFree, LastStart));
    }

    [Fact]
    public void Observation_DoesNotMoveBackwardWithinOneNightButResetsForNextNight()
    {
        DesktopPolicyPollModel model = new();
        DesktopPolicySnapshotDto landing = Policy(DesktopNightPhase.LandingLocked);
        DesktopPolicySnapshotDto staleGrace = Policy(DesktopNightPhase.Grace);
        DesktopPolicySnapshotDto nextNight = Policy(
            DesktopNightPhase.Free,
            new DateOnly(2026, 7, 7));

        Assert.Equal(DesktopNightPhase.LandingLocked, model.Observe(landing));
        Assert.Equal(DesktopNightPhase.LandingLocked, model.Observe(staleGrace));
        Assert.Equal(DesktopNightPhase.Free, model.Observe(nextNight));
    }

    private static DesktopPolicySnapshotDto Policy(
        DesktopNightPhase phase,
        DateOnly? nightDate = null)
    {
        DateOnly actualNightDate = nightDate ?? new DateOnly(2026, 7, 6);
        DateTimeOffset protectedStart = new(
            actualNightDate.ToDateTime(new TimeOnly(21, 0)),
            TimeSpan.Zero);
        DateTimeOffset lastStart = new(
            actualNightDate.AddDays(1).ToDateTime(new TimeOnly(0, 5)),
            TimeSpan.Zero);
        DesktopNightWindowDto window = new(
            actualNightDate,
            protectedStart,
            lastStart,
            lastStart.AddMinutes(35),
            lastStart.AddMinutes(55),
            lastStart.AddHours(8).AddMinutes(55));
        return new(
            lastStart,
            phase,
            window,
            [],
            [],
            true,
            false,
            null);
    }
}
