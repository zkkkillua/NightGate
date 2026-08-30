using System.Collections.Immutable;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class IPhoneConfirmationCommandTests
{
    private static readonly DateOnly UnlockNight = new(2026, 7, 14);
    private static readonly TimeZoneInfo ChinaTime = TimeZoneInfo.CreateCustomTimeZone(
        "NightGate-Test-UTC+8",
        TimeSpan.FromHours(8),
        "NightGate Test UTC+8",
        "NightGate Test UTC+8");

    [Theory]
    [InlineData("2026-07-14T14:29:59+00:00", "2026-07-14")]
    [InlineData("2026-07-14T14:30:00+00:00", "2026-07-15")]
    public async Task CompleteChecklist_UsesServiceClockAndTimeZoneForEffectiveNight(
        string observedAt,
        string expectedNight)
    {
        ProgressRepository repository = new(PendingProgress());
        MutableClock clock = new(DateTimeOffset.Parse(observedAt));
        NightGateProtocolCommandHandler handler = CreateHandler(repository, clock);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new ConfirmIPhoneStepCommand(2, CompleteChecklist()));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(expectedNight, result.Payload.GetProperty("effectiveNightDate").GetString());
        Assert.Equal(clock.UtcNow, repository.State.PendingStepConfirmedAtUtc);
        Assert.Equal(DateOnly.Parse(expectedNight), repository.State.PendingStepEffectiveNightDate);
    }

    [Fact]
    public async Task CompleteChecklist_ChangedSystemTimeZoneUsesPinnedActiveNightCutoff()
    {
        ProgressRepository repository = new(PendingProgress());
        NightState activeNight = new(
            Guid.NewGuid(),
            UnlockNight,
            new DateTimeOffset(2026, 7, 14, 13, 1, 0, TimeSpan.Zero),
            NightPhase.Free,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            ScheduleTimeZoneSerialized: NightScheduleTimeZone.Capture(ChinaTime));
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new MutableClock(new(2026, 7, 14, 14, 30, 0, TimeSpan.Zero)),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            activeNight);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new ConfirmIPhoneStepCommand(2, CompleteChecklist()));

        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "2026-07-15",
            result.Payload.GetProperty("effectiveNightDate").GetString());
        Assert.Equal(new DateOnly(2026, 7, 15), repository.State.PendingStepEffectiveNightDate);
    }

    [Fact]
    public async Task RepeatedConfirmationAfterClockRollback_IsIdempotent()
    {
        ProgressRepository repository = new(PendingProgress());
        MutableClock clock = new(new(2026, 7, 14, 14, 30, 0, TimeSpan.Zero));
        NightGateProtocolCommandHandler handler = CreateHandler(repository, clock);
        ConfirmIPhoneStepCommand command = new(2, CompleteChecklist());
        await handler.ExecuteAsync(command);
        ProgressState confirmed = repository.State;
        clock.UtcNow = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

        ProtocolCommandResult repeated = await handler.ExecuteAsync(command);

        Assert.Equal(StorageMode.Success, repeated.Mode);
        Assert.True(repeated.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(confirmed, repository.State);
    }

    [Fact]
    public async Task IncompleteChecklist_IsRejectedWithoutWriting()
    {
        ProgressState original = PendingProgress();
        ProgressRepository repository = new(original);
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new MutableClock(new(2026, 7, 14, 14, 0, 0, TimeSpan.Zero)));
        IPhoneStepConfirmation incomplete = CompleteChecklist() with
        {
            EntertainmentCategoriesRestricted = false,
        };

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new ConfirmIPhoneStepCommand(2, incomplete));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.False(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal("incompleteChecklist", result.Payload.GetProperty("error").GetString());
        Assert.Equal(original, repository.State);
        Assert.Equal(0, repository.SaveCalls);
    }

    [Fact]
    public async Task NoPendingOrWrongStep_IsRejectedWithFixedError()
    {
        ProgressRepository noPending = new(ProgressState.Initial);
        ProgressRepository wrongStep = new(PendingProgress());
        MutableClock clock = new(new(2026, 7, 14, 14, 0, 0, TimeSpan.Zero));

        ProtocolCommandResult missing = await CreateHandler(noPending, clock)
            .ExecuteAsync(new ConfirmIPhoneStepCommand(2, CompleteChecklist()));
        ProtocolCommandResult wrong = await CreateHandler(wrongStep, clock)
            .ExecuteAsync(new ConfirmIPhoneStepCommand(3, CompleteChecklist()));

        Assert.Equal("noPendingStep", missing.Payload.GetProperty("error").GetString());
        Assert.Equal("pendingStepMismatch", wrong.Payload.GetProperty("error").GetString());
        Assert.Equal(0, noPending.SaveCalls);
        Assert.Equal(0, wrongStep.SaveCalls);
    }

    [Fact]
    public async Task CompareExchangeConflict_RereadsAndRetriesWithoutPartialState()
    {
        ProgressRepository repository = new(PendingProgress())
        {
            ConflictsRemaining = 1,
        };
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new MutableClock(new(2026, 7, 14, 14, 0, 0, TimeSpan.Zero)));

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new ConfirmIPhoneStepCommand(2, CompleteChecklist()));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(2, repository.SaveCalls);
        Assert.Equal(2, repository.ReadCalls);
    }

    [Fact]
    public async Task CompareExchangeConflictCrossingCutoff_UsesOneCommandObservation()
    {
        ProgressRepository repository = new(PendingProgress())
        {
            ConflictsRemaining = 1,
        };
        SequenceClock clock = new(
            new(2026, 7, 14, 14, 29, 59, TimeSpan.Zero),
            new(2026, 7, 14, 14, 30, 0, TimeSpan.Zero));
        NightGateProtocolCommandHandler handler = CreateHandler(repository, clock);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new ConfirmIPhoneStepCommand(2, CompleteChecklist()));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal(new DateOnly(2026, 7, 14), repository.State.PendingStepEffectiveNightDate);
        Assert.Equal(1, clock.ReadCalls);
    }

    [Fact]
    public async Task ClockDependencyFailure_DegradesInsteadOfBecomingBusinessRejection()
    {
        ProgressRepository repository = new(PendingProgress());
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new ThrowingClock());

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new ConfirmIPhoneStepCommand(2, CompleteChecklist()));

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.Equal(0, repository.SaveCalls);
    }

    [Fact]
    public async Task TimeZoneDependencyFailure_DegradesInsteadOfBecomingBusinessRejection()
    {
        ProgressRepository repository = new(PendingProgress());
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new MutableClock(new(2026, 7, 14, 14, 0, 0, TimeSpan.Zero)),
            new ThrowingTimeZoneProvider());

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new ConfirmIPhoneStepCommand(2, CompleteChecklist()));

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.Equal(0, repository.SaveCalls);
    }

    [Fact]
    public async Task AlreadyConfirmed_IsIdempotentWithoutReadingClockOrTimeZone()
    {
        ProgressState confirmed = PendingProgress() with
        {
            PendingStepConfirmedAtUtc = new(2026, 7, 14, 14, 0, 0, TimeSpan.Zero),
            PendingStepEffectiveNightDate = new(2026, 7, 14),
        };
        ProgressRepository repository = new(confirmed);
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new ThrowingClock(),
            new ThrowingTimeZoneProvider());

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new ConfirmIPhoneStepCommand(2, CompleteChecklist()));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(confirmed, repository.State);
        Assert.Equal(0, repository.SaveCalls);
    }

    private static NightGateProtocolCommandHandler CreateHandler(
        IProgressRepository progressRepository,
        IClock clock,
        ITimeZoneProvider? timeZoneProvider = null,
        NightState? activeNight = null) => new(
            new UnusedRepository(activeNight),
            progressRepository,
            new UnusedRepository(),
            new InMemoryServiceStatus(),
            new InMemoryServiceStatus(),
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock,
            timeZoneProvider: timeZoneProvider ?? new FixedTimeZoneProvider(ChinaTime));

    private static ProgressState PendingProgress() => new(
        1,
        null,
        UnlockNight,
        2,
        UnlockNight);

    private static IPhoneStepConfirmation CompleteChecklist() => new(
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        true);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class SequenceClock(params DateTimeOffset[] values) : IClock
    {
        private readonly Queue<DateTimeOffset> _values = new(values);

        public int ReadCalls { get; private set; }

        public DateTimeOffset UtcNow
        {
            get
            {
                ReadCalls++;
                return _values.Count > 1 ? _values.Dequeue() : _values.Peek();
            }
        }
    }

    private sealed class ThrowingClock : IClock
    {
        public DateTimeOffset UtcNow =>
            throw new InvalidOperationException("clock unavailable");
    }

    private sealed class FixedTimeZoneProvider(TimeZoneInfo local) : ITimeZoneProvider
    {
        public TimeZoneInfo Local { get; } = local;
    }

    private sealed class ThrowingTimeZoneProvider : ITimeZoneProvider
    {
        public TimeZoneInfo Local =>
            throw new InvalidOperationException("time zone unavailable");
    }

    private sealed class EmptyAllowedProcesses : IAllowedProcessSnapshotProvider
    {
        public ImmutableArray<string> GetSnapshot() => [];
    }

    private sealed class ProgressRepository(ProgressState state) : IProgressRepository
    {
        private long _version = 1;

        public ProgressState State { get; private set; } = state;

        public int ConflictsRemaining { get; set; }

        public int ReadCalls { get; private set; }

        public int SaveCalls { get; private set; }

        public ValueTask<StorageResult<ProgressState>> ReadProgressAsync(
            CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return ValueTask.FromResult(new StorageResult<ProgressState>(
                StorageMode.Success,
                State,
                Version: _version));
        }

        public ValueTask<StorageWriteResult> SaveProgressAsync(
            ProgressState progress,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (ConflictsRemaining-- > 0)
            {
                return ValueTask.FromResult(StorageWriteResult.Conflict);
            }

            Assert.Equal(_version, expectedVersion);
            State = progress;
            _version++;
            return ValueTask.FromResult(StorageWriteResult.Success);
        }
    }

    private sealed class UnusedRepository(NightState? activeState = null) :
        INightStateRepository,
        IHistoryRepository
    {
        public ValueTask<StorageResult<NightState?>> ReadActiveStateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StorageResult<NightState?>(
                StorageMode.Success,
                activeState,
                Version: 1));
        public ValueTask<StorageWriteResult> SaveActiveStateWithEventAsync(NightState state, NightEvent nightEvent, long? expectedVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageWriteResult> SaveActiveStateProgressWithEventAsync(NightState state, ProgressState progress, NightEvent nightEvent, long? expectedStateVersion = null, long? expectedProgressVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageWriteResult> CloseActiveStateWithOutcomeAndEventAsync(NightState closedState, NightOutcome outcome, NightEvent nightEvent, long? expectedVersion = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestOutcomesAsync(int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestEligibleOutcomesAsync(int count, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageWriteResult> SaveOutcomeAsync(NightOutcome outcome, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageWriteResult> RecordEventAsync(NightEvent nightEvent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageWriteResult> PurgeEventsOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<StorageWriteResult> ClearHistoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
