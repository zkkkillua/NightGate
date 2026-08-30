namespace NightGate.Desktop.Tests;

public sealed class CommitmentCountdownTests
{
    private static readonly DateTimeOffset Lock =
        new(2026, 7, 15, 0, 40, 0, TimeSpan.Zero);

    [Fact]
    public void GraceCountdown_AppearsOnlyDuringTheFinalTenMinutes()
    {
        CommitmentCountdownTarget? beforeWindow = CommitmentCountdownModel.Resolve(
            Policy(DesktopNightPhase.Grace, Lock.AddMinutes(-10).AddSeconds(-1)));
        CommitmentCountdownTarget? atBoundary = CommitmentCountdownModel.Resolve(
            Policy(DesktopNightPhase.Grace, Lock.AddMinutes(-10)));
        CommitmentCountdownTarget? atExpiry = CommitmentCountdownModel.Resolve(
            Policy(DesktopNightPhase.Grace, Lock));

        Assert.Null(beforeWindow);
        Assert.NotNull(atBoundary);
        Assert.Equal(CommitmentCountdownKind.GraceToLock, atBoundary.Kind);
        Assert.Equal(TimeSpan.FromMinutes(10), atBoundary.InitialRemaining);
        Assert.Null(atExpiry);
    }

    [Theory]
    [InlineData(DesktopNightPhase.Free)]
    [InlineData(DesktopNightPhase.LastStart)]
    [InlineData(DesktopNightPhase.LandingLocked)]
    [InlineData(DesktopNightPhase.Morning)]
    public void OrdinaryPhasesOutsideTheFinalGraceWindowStayHidden(
        DesktopNightPhase phase)
    {
        Assert.Null(CommitmentCountdownModel.Resolve(
            Policy(phase, Lock.AddMinutes(-5))));
    }

    [Fact]
    public void UrgencyBeginsAtExactlyTwoMinutesWithoutChangingTheDeadline()
    {
        CommitmentCountdownTarget target = Assert.IsType<CommitmentCountdownTarget>(
            CommitmentCountdownModel.Resolve(
                Policy(DesktopNightPhase.Grace, Lock.AddMinutes(-5))));

        CommitmentCountdownPresentation calm =
            Assert.IsType<CommitmentCountdownPresentation>(
                CommitmentCountdownModel.Project(
                    target,
                    TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1))));
        CommitmentCountdownPresentation urgent =
            Assert.IsType<CommitmentCountdownPresentation>(
                CommitmentCountdownModel.Project(
                    target,
                    TimeSpan.FromMinutes(2)));

        Assert.False(calm.IsUrgent);
        Assert.True(urgent.IsUrgent);
        Assert.Equal(target.ServiceDeadline, urgent.ServiceDeadline);
    }

    [Fact]
    public void EntertainmentCoolingOff_CountsToTheAuthoritativeStart()
    {
        DateTimeOffset now = Lock;
        DesktopActiveOverrideDto active = new(
            DesktopOverrideKind.Entertainment,
            now,
            now.AddMinutes(10),
            now.AddMinutes(30),
            []);

        CommitmentCountdownTarget? target = CommitmentCountdownModel.Resolve(
            Policy(DesktopNightPhase.CoolingOff, now, active));

        Assert.NotNull(target);
        Assert.Equal(CommitmentCountdownKind.EntertainmentCoolingOff, target.Kind);
        Assert.Equal(now.AddMinutes(10), target.ServiceDeadline);
        Assert.Equal(TimeSpan.FromMinutes(10), target.InitialRemaining);
    }

    [Theory]
    [InlineData(DesktopOverrideKind.TeamRescue, CommitmentCountdownKind.TeamRescue)]
    [InlineData(DesktopOverrideKind.Emergency, CommitmentCountdownKind.Emergency)]
    [InlineData(DesktopOverrideKind.Entertainment, CommitmentCountdownKind.EntertainmentActive)]
    public void ActiveOverrides_CountToTheirAuthoritativeEnd(
        DesktopOverrideKind overrideKind,
        CommitmentCountdownKind expectedKind)
    {
        DateTimeOffset now = Lock;
        DesktopActiveOverrideDto active = new(
            overrideKind,
            now.AddMinutes(-1),
            now.AddMinutes(-1),
            now.AddMinutes(20),
            []);

        CommitmentCountdownTarget? target = CommitmentCountdownModel.Resolve(
            Policy(DesktopNightPhase.OverrideActive, now, active));

        Assert.NotNull(target);
        Assert.Equal(expectedKind, target.Kind);
        Assert.Equal(now.AddMinutes(20), target.ServiceDeadline);
        Assert.Equal(TimeSpan.FromMinutes(20), target.InitialRemaining);
    }

    [Fact]
    public void DegradedOrInconsistentPoliciesStayHidden()
    {
        DateTimeOffset now = Lock;
        DesktopActiveOverrideDto wrongKind = new(
            DesktopOverrideKind.TeamRescue,
            now,
            now.AddMinutes(10),
            now.AddMinutes(30),
            []);

        Assert.Null(CommitmentCountdownModel.Resolve(
            DesktopPolicyResult.FailOpen("service-unavailable")));
        Assert.Null(CommitmentCountdownModel.Resolve(
            Policy(DesktopNightPhase.Grace, Lock.AddMinutes(-5), degraded: true)));
        Assert.Null(CommitmentCountdownModel.Resolve(
            Policy(DesktopNightPhase.CoolingOff, now, wrongKind)));
        Assert.Null(CommitmentCountdownModel.Resolve(
            Policy(DesktopNightPhase.OverrideActive, now, activeOverride: null)));
    }

    [Fact]
    public void Controller_UsesMonotonicTimeAndNeverExtendsTheSameCountdown()
    {
        RecordingCountdownPresenter presenter = new();
        CommitmentCountdownController controller = new(presenter);
        DesktopPolicyResult policy = Policy(
            DesktopNightPhase.Grace,
            Lock.AddMinutes(-10));

        controller.ObservePolicy(policy, TimeSpan.FromSeconds(100));
        controller.Tick(TimeSpan.FromSeconds(101.1));
        controller.ObservePolicy(policy, TimeSpan.FromSeconds(105));
        int appliedBeforeRollback = presenter.Applied.Count;
        TimeSpan remainingBeforeRollback = Assert.IsType<CommitmentCountdownPresentation>(
            presenter.Applied[^1]).Remaining;
        controller.Tick(TimeSpan.FromSeconds(50));

        CommitmentCountdownPresentation[] visible = presenter.Applied
            .OfType<CommitmentCountdownPresentation>()
            .ToArray();
        Assert.Equal(TimeSpan.FromMinutes(10), visible[0].Remaining);
        Assert.Equal(TimeSpan.FromMinutes(9).Add(TimeSpan.FromSeconds(59)), visible[1].Remaining);
        Assert.True(remainingBeforeRollback <= visible[1].Remaining);
        Assert.Equal(appliedBeforeRollback, presenter.Applied.Count);
    }

    [Fact]
    public void Controller_UpdatesAtMostOncePerDisplayedSecondAndHidesAtExpiry()
    {
        RecordingCountdownPresenter presenter = new();
        CommitmentCountdownController controller = new(presenter);
        DateTimeOffset evaluated = Lock.AddSeconds(-2);

        controller.ObservePolicy(
            Policy(DesktopNightPhase.Grace, evaluated),
            TimeSpan.FromSeconds(10));
        controller.Tick(TimeSpan.FromSeconds(10.2));
        controller.Tick(TimeSpan.FromSeconds(11.1));
        controller.Tick(TimeSpan.FromSeconds(12));

        Assert.Collection(
            presenter.Applied,
            first => Assert.Equal(TimeSpan.FromSeconds(2), Assert.IsType<CommitmentCountdownPresentation>(first).Remaining),
            second => Assert.Equal(TimeSpan.FromSeconds(1), Assert.IsType<CommitmentCountdownPresentation>(second).Remaining),
            hidden => Assert.Null(hidden));
    }

    [Fact]
    public void Controller_RetriesAVisualFailureWithoutThrowingOrChangingPolicy()
    {
        ThrowOnceCountdownPresenter presenter = new();
        CommitmentCountdownController controller = new(presenter);
        DesktopPolicyResult policy = Policy(
            DesktopNightPhase.Grace,
            Lock.AddMinutes(-5));

        Exception? failure = Record.Exception(() =>
            controller.ObservePolicy(policy, TimeSpan.FromMinutes(1)));
        controller.Tick(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));

        Assert.Null(failure);
        Assert.Equal(2, presenter.Attempts);
        Assert.NotNull(presenter.LastSuccessful);
    }

    [Fact]
    public void Controller_ReportsAContinuousUpdateFailureOnlyOnce()
    {
        ThrowAlwaysCountdownPresenter presenter = new();
        RecordingCountdownDiagnostics diagnostics = new();
        CommitmentCountdownController controller = new(presenter, diagnostics);
        DesktopPolicyResult policy = Policy(
            DesktopNightPhase.Grace,
            Lock.AddMinutes(-5));

        Exception? failure = Record.Exception(() =>
        {
            controller.ObservePolicy(policy, TimeSpan.FromMinutes(1));
            controller.Tick(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(1)));
            controller.Tick(TimeSpan.FromMinutes(1).Add(TimeSpan.FromSeconds(2)));
        });

        Assert.Null(failure);
        Assert.Collection(
            diagnostics.Failures,
            item =>
            {
                Assert.Equal(CommitmentCountdownVisualOperation.Update, item.Operation);
                Assert.IsType<InvalidOperationException>(item.Exception);
            });
    }

    [Fact]
    public void Controller_ReportsAContinuousHideFailureOnlyOnce()
    {
        ThrowOnHideCountdownPresenter presenter = new();
        RecordingCountdownDiagnostics diagnostics = new();
        CommitmentCountdownController controller = new(presenter, diagnostics);

        controller.ObservePolicy(
            Policy(DesktopNightPhase.Grace, Lock.AddMinutes(-5)),
            TimeSpan.FromMinutes(1));
        Exception? failure = Record.Exception(() =>
        {
            controller.ObservePolicy(
                DesktopPolicyResult.FailOpen("service-lost"),
                TimeSpan.FromMinutes(2));
            controller.Tick(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        });

        Assert.Null(failure);
        Assert.Collection(
            diagnostics.Failures,
            item =>
            {
                Assert.Equal(CommitmentCountdownVisualOperation.Hide, item.Operation);
                Assert.IsType<InvalidOperationException>(item.Exception);
            });
    }

    [Fact]
    public void Controller_ReportsAgainAfterTheVisualPathRecovers()
    {
        ToggleHideCountdownPresenter presenter = new() { FailHide = true };
        RecordingCountdownDiagnostics diagnostics = new();
        CommitmentCountdownController controller = new(presenter, diagnostics);
        DesktopPolicyResult visible = Policy(
            DesktopNightPhase.Grace,
            Lock.AddMinutes(-5));
        DesktopPolicyResult hidden = DesktopPolicyResult.FailOpen("service-lost");

        controller.ObservePolicy(visible, TimeSpan.FromMinutes(1));
        controller.ObservePolicy(hidden, TimeSpan.FromMinutes(2));
        presenter.FailHide = false;
        controller.Tick(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));

        controller.ObservePolicy(visible, TimeSpan.FromMinutes(3));
        presenter.FailHide = true;
        controller.ObservePolicy(hidden, TimeSpan.FromMinutes(4));

        Assert.Equal(2, diagnostics.Failures.Count);
        Assert.All(
            diagnostics.Failures,
            item => Assert.Equal(
                CommitmentCountdownVisualOperation.Hide,
                item.Operation));
    }

    [Fact]
    public void NewOverrideIdentityMayStartANewCountdownAfterThePreviousOne()
    {
        RecordingCountdownPresenter presenter = new();
        CommitmentCountdownController controller = new(presenter);
        DateTimeOffset now = Lock;
        DesktopActiveOverrideDto first = new(
            DesktopOverrideKind.Emergency,
            now,
            now,
            now.AddMinutes(30),
            []);
        DesktopActiveOverrideDto second = new(
            DesktopOverrideKind.Emergency,
            now.AddHours(1),
            now.AddHours(1),
            now.AddHours(1).AddMinutes(30),
            []);

        controller.ObservePolicy(
            Policy(DesktopNightPhase.OverrideActive, now, first),
            TimeSpan.FromMinutes(1));
        controller.ObservePolicy(
            Policy(DesktopNightPhase.OverrideActive, now.AddHours(1), second),
            TimeSpan.FromMinutes(20));

        CommitmentCountdownPresentation last = Assert.IsType<CommitmentCountdownPresentation>(
            presenter.Applied[^1]);
        Assert.Equal(TimeSpan.FromMinutes(30), last.Remaining);
    }

    [Fact]
    public void ExpiredCountdownCannotBeResurrectedByAStalePolicyReplay()
    {
        RecordingCountdownPresenter presenter = new();
        CommitmentCountdownController controller = new(presenter);
        DesktopPolicyResult stale = Policy(
            DesktopNightPhase.Grace,
            Lock.AddSeconds(-2));

        controller.ObservePolicy(stale, TimeSpan.FromSeconds(10));
        controller.Tick(TimeSpan.FromSeconds(12));
        int appliedAtExpiry = presenter.Applied.Count;
        controller.ObservePolicy(stale, TimeSpan.FromSeconds(30));

        Assert.Equal(appliedAtExpiry, presenter.Applied.Count);
        Assert.Null(presenter.Applied[^1]);
    }

    [Fact]
    public void FailOpenHidesAndRecoveryCannotExtendTheSameCountdown()
    {
        RecordingCountdownPresenter presenter = new();
        CommitmentCountdownController controller = new(presenter);
        DesktopPolicyResult stale = Policy(
            DesktopNightPhase.Grace,
            Lock.AddMinutes(-5));

        controller.ObservePolicy(stale, TimeSpan.Zero);
        controller.Tick(TimeSpan.FromMinutes(1));
        controller.ObservePolicy(
            DesktopPolicyResult.FailOpen("service-lost"),
            TimeSpan.FromMinutes(1));
        controller.ObservePolicy(stale, TimeSpan.FromMinutes(2));

        CommitmentCountdownPresentation recovered =
            Assert.IsType<CommitmentCountdownPresentation>(presenter.Applied[^1]);
        Assert.Equal(TimeSpan.FromMinutes(3), recovered.Remaining);
    }

    [Fact]
    public void ClearIsIdempotentAndHidesAVisibleCountdownOnce()
    {
        RecordingCountdownPresenter presenter = new();
        CommitmentCountdownController controller = new(presenter);

        controller.ObservePolicy(
            Policy(DesktopNightPhase.Grace, Lock.AddMinutes(-5)),
            TimeSpan.Zero);
        controller.Clear();
        controller.Clear();

        Assert.Equal(2, presenter.Applied.Count);
        Assert.NotNull(presenter.Applied[0]);
        Assert.Null(presenter.Applied[1]);
    }

    private static DesktopPolicyResult Policy(
        DesktopNightPhase phase,
        DateTimeOffset evaluatedAt,
        DesktopActiveOverrideDto? activeOverride = null,
        bool degraded = false)
    {
        DateOnly night = new(2026, 7, 14);
        DesktopPolicySnapshotDto snapshot = new(
            evaluatedAt,
            phase,
            new(
                night,
                new DateTimeOffset(2026, 7, 14, 21, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 15, 0, 5, 0, TimeSpan.Zero),
                Lock,
                new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero)),
            [],
            [],
            !degraded,
            degraded,
            activeOverride);
        return new(
            !degraded,
            degraded,
            degraded ? "degraded" : null,
            new(!degraded, degraded, degraded ? "degraded" : null, snapshot));
    }

    private sealed class RecordingCountdownPresenter : ICommitmentCountdownPresenter
    {
        public List<CommitmentCountdownPresentation?> Applied { get; } = [];

        public void Apply(CommitmentCountdownPresentation? presentation) =>
            Applied.Add(presentation);
    }

    private sealed class ThrowOnceCountdownPresenter : ICommitmentCountdownPresenter
    {
        public int Attempts { get; private set; }

        public CommitmentCountdownPresentation? LastSuccessful { get; private set; }

        public void Apply(CommitmentCountdownPresentation? presentation)
        {
            Attempts++;
            if (Attempts == 1)
            {
                throw new InvalidOperationException("visual-only failure");
            }

            LastSuccessful = presentation;
        }
    }

    private sealed class ThrowAlwaysCountdownPresenter :
        ICommitmentCountdownPresenter
    {
        public void Apply(CommitmentCountdownPresentation? presentation) =>
            throw new InvalidOperationException("visual-only failure");
    }

    private sealed class ThrowOnHideCountdownPresenter :
        ICommitmentCountdownPresenter
    {
        public void Apply(CommitmentCountdownPresentation? presentation)
        {
            if (presentation is null)
            {
                throw new InvalidOperationException("hide failure");
            }
        }
    }

    private sealed class ToggleHideCountdownPresenter :
        ICommitmentCountdownPresenter
    {
        public bool FailHide { get; set; }

        public void Apply(CommitmentCountdownPresentation? presentation)
        {
            if (presentation is null && FailHide)
            {
                throw new InvalidOperationException("hide failure");
            }
        }
    }

    private sealed class RecordingCountdownDiagnostics :
        ICommitmentCountdownDiagnostics
    {
        public List<(CommitmentCountdownVisualOperation Operation, Exception Exception)>
            Failures { get; } = [];

        public void RecordVisualFailure(
            CommitmentCountdownVisualOperation operation,
            Exception exception) => Failures.Add((operation, exception));
    }
}
