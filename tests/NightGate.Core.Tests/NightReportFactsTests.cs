using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class NightReportFactsTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 0, 40, 0, TimeSpan.Zero);

    [Fact]
    public void OverrideReasonSummary_RejectsNegativeOrOverboundCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverrideReasonSummary(
            TeamRescueCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverrideReasonSummary(
            EmergencyHealthCount: OverrideReasonSummary.MaximumCount + 1));
    }

    [Fact]
    public void AcceptedTeamRescueAndEntertainmentIncrementTheirCountersOnce()
    {
        OverridePolicy policy = new(new FixedSnapshotProvider(["game"]));
        OverrideDecision team = policy.Request(
            CreateState(),
            ProgressState.Initial,
            new(OverrideKind.TeamRescue, null),
            Now);
        OverrideDecision entertainment = policy.Request(
            CreateState(),
            ProgressState.Initial,
            new(OverrideKind.Entertainment, null),
            Now);

        Assert.True(team.Accepted);
        Assert.Equal(1, team.State.OverrideReasons.TeamRescueCount);
        Assert.Equal(0, team.State.OverrideReasons.EntertainmentCount);
        Assert.True(entertainment.Accepted);
        Assert.Equal(1, entertainment.State.OverrideReasons.EntertainmentCount);
        Assert.Equal(0, entertainment.State.OverrideReasons.TeamRescueCount);
    }

    [Theory]
    [InlineData(EmergencyReason.Health, 1, 0, 0, 0)]
    [InlineData(EmergencyReason.Safety, 0, 1, 0, 0)]
    [InlineData(EmergencyReason.UrgentWork, 0, 0, 1, 0)]
    public void AcceptedEmergencyIncrementsOnlySelectedReason(
        EmergencyReason reason,
        int health,
        int safety,
        int urgentWork,
        int other)
    {
        OverrideDecision decision = new OverridePolicy(new FixedSnapshotProvider([])).Request(
            CreateState(),
            ProgressState.Initial,
            new(OverrideKind.Emergency, reason),
            Now);

        Assert.True(decision.Accepted);
        Assert.Equal(health, decision.State.OverrideReasons.EmergencyHealthCount);
        Assert.Equal(safety, decision.State.OverrideReasons.EmergencySafetyCount);
        Assert.Equal(urgentWork, decision.State.OverrideReasons.EmergencyUrgentWorkCount);
        Assert.Equal(other, decision.State.OverrideReasons.EmergencyOtherCount);
    }

    [Fact]
    public void RepeatedEmergencyReasonIncrementsOncePerAcceptedRequest()
    {
        OverridePolicy policy = new(new FixedSnapshotProvider([]));
        OverrideDecision first = policy.Request(
            CreateState(),
            ProgressState.Initial,
            new(OverrideKind.Emergency, EmergencyReason.Health),
            Now);
        OverrideDecision preemptedWhileActive = policy.Request(
            first.State,
            first.Progress,
            new(OverrideKind.Emergency, EmergencyReason.Health),
            Now.AddMinutes(1));
        OverrideDecision third = policy.Request(
            preemptedWhileActive.State,
            preemptedWhileActive.Progress,
            new(OverrideKind.Emergency, EmergencyReason.Health),
            Now.AddMinutes(31));

        Assert.True(preemptedWhileActive.Accepted);
        Assert.Equal(2, preemptedWhileActive.State.OverrideReasons.EmergencyHealthCount);
        Assert.True(third.Accepted);
        Assert.Equal(3, third.State.OverrideReasons.EmergencyHealthCount);
    }

    [Fact]
    public void EmergencyCounterSaturatesWithoutBlockingEmergencyAccess()
    {
        NightState state = CreateState() with
        {
            OverrideReasons = new OverrideReasonSummary(
                EmergencySafetyCount: OverrideReasonSummary.MaximumCount),
        };

        OverrideDecision decision = new OverridePolicy(new FixedSnapshotProvider([])).Request(
            state,
            ProgressState.Initial,
            new(OverrideKind.Emergency, EmergencyReason.Safety),
            Now);

        Assert.True(decision.Accepted);
        Assert.Equal(
            OverrideReasonSummary.MaximumCount,
            decision.State.OverrideReasons.EmergencySafetyCount);
    }

    [Fact]
    public async Task LandingPhaseDoesNotInventAWorkstationLockAndClosingCopiesObservedFact()
    {
        NightWindow window = Window();
        MemoryNightStateRepository repository = new();
        NightStateCoordinator coordinator = new(repository);
        await coordinator.ObserveAsync(window, NightPhase.Free, window.ProtectedStart);

        CoordinatorObservation locked = await coordinator.ObserveAsync(
            window,
            NightPhase.LandingLocked,
            window.Lock);
        CoordinatorObservation observedAgain = await coordinator.ObserveAsync(
            window,
            NightPhase.LandingLocked,
            window.Lock.AddMinutes(2));
        DateTimeOffset actualLockObservedAt = window.Lock.AddMinutes(1);
        OverrideDecision emergency = new OverridePolicy(new FixedSnapshotProvider([])).Request(
            observedAgain.State! with { FirstLockObservedAtUtc = actualLockObservedAt },
            ProgressState.Initial,
            new(OverrideKind.Emergency, EmergencyReason.UrgentWork),
            observedAgain.State!.LastObservedUtc);
        repository.State = emergency.State;

        await coordinator.ObserveAsync(window, NightPhase.Morning, window.Wake);

        Assert.Null(locked.State!.FirstLockObservedAtUtc);
        Assert.Null(observedAgain.State!.FirstLockObservedAtUtc);
        Assert.NotNull(repository.ClosedOutcome);
        Assert.Equal(actualLockObservedAt, repository.ClosedOutcome!.FirstLockObservedAtUtc);
        Assert.Equal(1, repository.ClosedOutcome.OverrideReasons.EmergencyUrgentWorkCount);
    }

    [Fact]
    public void OldConstructorsDefaultReportFactsAndCannotProveQualification()
    {
        NightState state = CreateState();
        NightOutcome outcome = new(
            Guid.NewGuid(),
            new DateOnly(2026, 7, 6),
            new DateTimeOffset(2026, 7, 7, 9, 0, 0, TimeSpan.Zero),
            false,
            false,
            false,
            false,
            false,
            false);

        Assert.Equal(OverrideReasonSummary.Empty, state.OverrideReasons);
        Assert.Null(state.FirstLockObservedAtUtc);
        Assert.Equal(OverrideReasonSummary.Empty, outcome.OverrideReasons);
        Assert.Null(outcome.FirstLockObservedAtUtc);
        Assert.Null(outcome.ScheduledLockAtUtc);
        Assert.True(outcome.IsEligible);
        Assert.False(outcome.Qualifies);
    }

    [Fact]
    public void LockFactsRequireNondefaultUtcTimestamps()
    {
        DateTimeOffset nonUtc = new(2026, 7, 7, 0, 40, 0, TimeSpan.FromHours(8));

        Assert.Throws<ArgumentException>(() => new NightState(
            Guid.NewGuid(),
            new DateOnly(2026, 7, 6),
            Now,
            NightPhase.LandingLocked,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            FirstLockObservedAtUtc: nonUtc));
        Assert.Throws<ArgumentException>(() => new NightOutcome(
            Guid.NewGuid(),
            new DateOnly(2026, 7, 6),
            Now.AddHours(8),
            false,
            false,
            false,
            false,
            false,
            false,
            FirstLockObservedAtUtc: default(DateTimeOffset)));
        Assert.Throws<ArgumentException>(() => CreateState() with
        {
            FirstLockObservedAtUtc = nonUtc,
        });
        Assert.Throws<ArgumentException>(() => CreateState() with
        {
            ScheduledLockAtUtc = nonUtc,
        });
        Assert.Throws<ArgumentException>(() => outcomeWithScheduledLock(default));

        static NightOutcome outcomeWithScheduledLock(DateTimeOffset scheduledLock) => new(
            Guid.NewGuid(),
            new DateOnly(2026, 7, 6),
            Now.AddHours(8),
            false,
            false,
            false,
            false,
            false,
            false,
            ScheduledLockAtUtc: scheduledLock);
    }

    private static NightState CreateState() => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        new DateOnly(2026, 7, 6),
        Now,
        NightPhase.LandingLocked,
        null,
        false,
        false,
        false,
        false,
        false,
        false);

    private static NightWindow Window() => new(
        new DateOnly(2026, 7, 6),
        new DateTimeOffset(2026, 7, 6, 21, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 6, 23, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 7, 0, 40, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 7, 1, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 7, 9, 0, 0, TimeSpan.Zero));

    private sealed class FixedSnapshotProvider(
        System.Collections.Immutable.ImmutableArray<string> identifiers) :
        IAllowedProcessSnapshotProvider
    {
        public System.Collections.Immutable.ImmutableArray<string> GetSnapshot() => identifiers;
    }

    private sealed class MemoryNightStateRepository : INightStateRepository
    {
        public NightState? State { get; set; }

        public NightOutcome? ClosedOutcome { get; private set; }

        public ValueTask<StorageResult<NightState?>> ReadActiveStateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StorageResult<NightState?>(StorageMode.Success, State));

        public ValueTask<StorageWriteResult> SaveActiveStateWithEventAsync(
            NightState state,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            State = state;
            return ValueTask.FromResult(new StorageWriteResult(StorageMode.Success));
        }

        public ValueTask<StorageWriteResult> SaveActiveStateProgressWithEventAsync(
            NightState state,
            ProgressState progress,
            NightEvent nightEvent,
            long? expectedStateVersion = null,
            long? expectedProgressVersion = null,
            CancellationToken cancellationToken = default) =>
            SaveActiveStateWithEventAsync(state, nightEvent, expectedStateVersion, cancellationToken);

        public ValueTask<StorageWriteResult> CloseActiveStateWithOutcomeAndEventAsync(
            NightState closedState,
            NightOutcome outcome,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            State = closedState;
            ClosedOutcome = outcome;
            return ValueTask.FromResult(new StorageWriteResult(StorageMode.Success));
        }
    }
}
