namespace NightGate.Desktop.Tests;

public sealed class GameCommitmentCountdownTests
{
    private static readonly DateTimeOffset Lock =
        new(2026, 8, 31, 0, 40, 0, TimeSpan.FromHours(8));

    [Fact]
    public void RunningGame_UsesLargeCountdownFromLastStartNotTheEarlierPlanCard()
    {
        Assert.Null(CommitmentCountdownModel.Resolve(
            Policy(Lock.AddMinutes(-36)), hasRunningGame: true));
        CommitmentCountdownTarget target = Assert.IsType<CommitmentCountdownTarget>(
            CommitmentCountdownModel.Resolve(
                Policy(Lock.AddMinutes(-35), DesktopNightPhase.LastStart),
                hasRunningGame: true));

        Assert.Equal(CommitmentCountdownKind.GameGraceToLock, target.Kind);
        Assert.Equal(TimeSpan.FromMinutes(35), target.InitialRemaining);
        Assert.Equal(Lock, target.ServiceDeadline);
    }

    [Fact]
    public void LongGame_UsesTheSameEarlierCutoffAsTheLastStartNotice()
    {
        DesktopPolicyResult policy = Policy(Lock.AddMinutes(-90), rules:
            [Game(90), Game(15), Game(90) with { Category = DesktopAppRuleCategory.Voice }]);

        Assert.True(CommitmentCountdownModel.IsGameReminderWindow(policy));
        CommitmentCountdownTarget target = Assert.IsType<CommitmentCountdownTarget>(
            CommitmentCountdownModel.Resolve(policy, hasRunningGame: true));
        Assert.Equal(TimeSpan.FromMinutes(90), target.InitialRemaining);
        Assert.Equal(CommitmentCountdownKind.GameGraceToLock, target.Kind);
        Assert.False(CommitmentCountdownModel.IsGameReminderWindow(
            Policy(Lock.AddMinutes(-90).AddSeconds(-1), rules: [Game(90)])));
    }

    [Fact]
    public void VoiceAndUnconfiguredGames_DoNotMoveTheReminderEarlier()
    {
        DesktopPolicyResult policy = Policy(Lock.AddMinutes(-60), rules:
            [Game(90) with { Category = DesktopAppRuleCategory.Voice },
             Game(90) with { IsConfigured = false }]);

        Assert.False(CommitmentCountdownModel.IsGameReminderWindow(policy));
        Assert.Null(CommitmentCountdownModel.Resolve(policy, hasRunningGame: true));
    }

    [Fact]
    public void NoGame_KeepsTheExistingFinalTenMinuteCountdown()
    {
        Assert.Null(CommitmentCountdownModel.Resolve(
            Policy(Lock.AddMinutes(-20), DesktopNightPhase.Grace)));
        CommitmentCountdownTarget small = Assert.IsType<CommitmentCountdownTarget>(
            CommitmentCountdownModel.Resolve(
                Policy(Lock.AddMinutes(-10), DesktopNightPhase.Grace)));
        CommitmentCountdownTarget large = Assert.IsType<CommitmentCountdownTarget>(
            CommitmentCountdownModel.Resolve(
                Policy(Lock.AddMinutes(-10), DesktopNightPhase.Grace), true));

        Assert.Equal(CommitmentCountdownKind.GraceToLock, small.Kind);
        Assert.Equal(CommitmentCountdownKind.GameGraceToLock, large.Kind);
        Assert.Equal(small.Identity, large.Identity);
        Assert.Equal(small.ServiceDeadline, large.ServiceDeadline);
    }

    [Theory]
    [InlineData(DesktopNightPhase.LandingLocked)]
    [InlineData(DesktopNightPhase.Morning)]
    [InlineData(DesktopNightPhase.CoolingOff)]
    [InlineData(DesktopNightPhase.OverrideActive)]
    public void GameCannotOverrideOtherPhases(DesktopNightPhase phase)
    {
        Assert.False(CommitmentCountdownModel.IsGameReminderWindow(
            Policy(Lock.AddMinutes(-20), phase)));
        Assert.Null(CommitmentCountdownModel.Resolve(
            Policy(Lock.AddMinutes(-20), phase), true));
    }

    [Fact]
    public void GameDetectionCannotReplaceAnActiveEntertainmentWindow()
    {
        DesktopPolicyResult original = Policy(Lock, DesktopNightPhase.OverrideActive);
        DesktopPolicySnapshotDto snapshot = original.ExecutablePolicy! with
        {
            ActiveOverride = new(DesktopOverrideKind.Entertainment,
                Lock.AddMinutes(-10), Lock, Lock.AddMinutes(20), []),
        };
        DesktopPolicyResult policy = original with
        {
            Status = original.Status! with { Policy = snapshot },
        };

        Assert.Equal(CommitmentCountdownKind.EntertainmentActive,
            CommitmentCountdownModel.Resolve(policy, true)!.Kind);
    }

    [Fact]
    public void Controller_GameOpenCloseAndReopenNeverRestartTheLockCountdown()
    {
        Presenter presenter = new();
        CommitmentCountdownController controller = new(presenter);
        DesktopPolicyResult policy = Policy(Lock.AddMinutes(-35), DesktopNightPhase.LastStart);
        controller.ObservePolicy(policy, TimeSpan.Zero);
        Assert.Null(presenter.Last);

        controller.ObserveGamePresence(true, TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromMinutes(35) - TimeSpan.FromSeconds(10), presenter.Last!.Remaining);
        controller.ObserveGamePresence(false, TimeSpan.FromSeconds(20));
        Assert.Null(presenter.Last);
        controller.ObserveGamePresence(true, TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromMinutes(35) - TimeSpan.FromSeconds(30), presenter.Last!.Remaining);
        controller.ObservePolicy(policy, TimeSpan.FromSeconds(40));
        Assert.Equal(TimeSpan.FromMinutes(35) - TimeSpan.FromSeconds(40), presenter.Last!.Remaining);
    }

    [Fact]
    public void Controller_ChangingGameRulesClearsPresenceWithoutResettingTheDeadline()
    {
        Presenter presenter = new();
        CommitmentCountdownController controller = new(presenter);
        DesktopPolicyResult oldPolicy = Policy(Lock.AddMinutes(-20), DesktopNightPhase.Grace);
        controller.ObservePolicy(oldPolicy, TimeSpan.Zero);
        controller.ObserveGamePresence(true, TimeSpan.Zero);
        Assert.Equal(CommitmentCountdownKind.GameGraceToLock, presenter.Last!.Kind);

        DesktopPolicyResult changed = Policy(Lock.AddMinutes(-20), DesktopNightPhase.Grace,
            [Game(35) with { RootExecutablePath = @"C:\Games\different.exe" }]);
        controller.ObservePolicy(changed, TimeSpan.FromSeconds(10));
        Assert.Null(presenter.Last);
        controller.ObserveGamePresence(true, TimeSpan.FromSeconds(20));
        Assert.Equal(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(20), presenter.Last!.Remaining);
    }

    [Fact]
    public void Controller_ChangingNightClearsTheOldGameObservation()
    {
        Presenter presenter = new();
        CommitmentCountdownController controller = new(presenter);
        DesktopPolicyResult policy = Policy(Lock.AddMinutes(-20), DesktopNightPhase.Grace);
        controller.ObservePolicy(policy, TimeSpan.Zero);
        controller.ObserveGamePresence(true, TimeSpan.Zero);
        DesktopPolicySnapshotDto snapshot = policy.ExecutablePolicy!;
        DesktopPolicyResult nextNight = policy with
        {
            Status = policy.Status! with
            {
                Policy = snapshot with
                {
                    Window = snapshot.Window with { NightDate = snapshot.Window.NightDate.AddDays(1) },
                },
            },
        };
        controller.ObservePolicy(nextNight, TimeSpan.FromSeconds(10));
        Assert.Null(presenter.Last);
    }

    [Fact]
    public void Controller_UsesElapsedMonotonicTimeForEarlyGameCutoff()
    {
        Presenter presenter = new();
        CommitmentCountdownController controller = new(presenter);
        controller.ObservePolicy(Policy(Lock.AddMinutes(-90).AddSeconds(-1),
            rules: [Game(90)]), TimeSpan.Zero);
        controller.ObserveGamePresence(true, TimeSpan.Zero);
        Assert.Null(presenter.Last);
        controller.Tick(TimeSpan.FromSeconds(1));
        Assert.Equal(CommitmentCountdownKind.GameGraceToLock, presenter.Last!.Kind);
        Assert.Equal(TimeSpan.FromMinutes(90), presenter.Last.Remaining);
        controller.Tick(TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMinutes(90), presenter.Last.Remaining);
    }

    [Fact]
    public void FailOpenOrStop_HidesGameCountdownAndRejectsLateObservation()
    {
        Presenter presenter = new();
        CommitmentCountdownController controller = new(presenter);
        controller.ObservePolicy(Policy(Lock.AddMinutes(-20), DesktopNightPhase.Grace), TimeSpan.Zero);
        controller.ObserveGamePresence(true, TimeSpan.Zero);
        Assert.NotNull(presenter.Last);
        controller.ObservePolicy(DesktopPolicyResult.FailOpen("service-lost"), TimeSpan.FromSeconds(1));
        controller.ObserveGamePresence(true, TimeSpan.FromSeconds(2));
        Assert.Null(presenter.Last);
        controller.Clear();
        controller.ObserveGamePresence(true, TimeSpan.FromSeconds(3));
        Assert.Null(presenter.Last);
    }

    private static DesktopAppRuleDto Game(int minutes) => new(
        "game", @"C:\Games\game.exe", [], DesktopAppRuleCategory.Game, minutes, true);

    private static DesktopPolicyResult Policy(
        DateTimeOffset evaluatedAt,
        DesktopNightPhase phase = DesktopNightPhase.Free,
        IReadOnlyList<DesktopAppRuleDto>? rules = null)
    {
        DesktopPolicySnapshotDto snapshot = new(evaluatedAt, phase,
            new(new DateOnly(2026, 8, 30), Lock.AddHours(-3).AddMinutes(-40),
                Lock.AddMinutes(-35), Lock, Lock.AddMinutes(20), Lock.AddHours(8).AddMinutes(20)),
            rules ?? [Game(35)], [], true, false, null);
        return new(true, false, null, new(true, false, null, snapshot));
    }

    private sealed class Presenter : ICommitmentCountdownPresenter
    {
        public CommitmentCountdownPresentation? Last { get; private set; }
        public void Apply(CommitmentCountdownPresentation? presentation) => Last = presentation;
    }
}
