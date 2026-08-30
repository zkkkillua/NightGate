using NightGate.Core;

namespace NightGate.Service.Tests;

public sealed class ProtectionGapCoordinatorTests
{
    private static readonly NightWindow Window = new(
        new DateOnly(2026, 7, 6),
        At(6, 21, 0),
        At(6, 23, 0),
        At(7, 0, 40),
        At(7, 1, 0),
        At(7, 9, 0));

    [Fact]
    public async Task CloseNight_PropagatesProtectionGapIntoOutcome()
    {
        MemoryRepository repository = new(new NightState(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Window.NightDate,
            Window.Wake.AddMinutes(-1),
            NightPhase.LandingLocked,
            ActiveOverride: null,
            EmergencyUsed: false,
            TeamRescueUsed: false,
            EntertainmentUsed: false,
            DeliberateBypass: false,
            LateNewEntertainment: false,
            MissedLock: false,
            FirstLockObservedAtUtc: Window.Lock,
            ScheduledLockAtUtc: Window.Lock,
            ProtectionGapObserved: true));

        CoordinatorObservation observation = await new NightStateCoordinator(repository)
            .ObserveAsync(Window, NightPhase.Morning, Window.Wake);

        Assert.Null(observation.State);
        Assert.NotNull(repository.Outcome);
        Assert.True(repository.Outcome!.ProtectionGapObserved);
        Assert.True(repository.Outcome.IsEligible);
        Assert.False(repository.Outcome.Qualifies);
    }

    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);

    private sealed class MemoryRepository(NightState state) : INightStateRepository
    {
        public NightState? State { get; private set; } = state;

        public NightOutcome? Outcome { get; private set; }

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
            Outcome = outcome;
            return ValueTask.FromResult(new StorageWriteResult(StorageMode.Success));
        }
    }
}
