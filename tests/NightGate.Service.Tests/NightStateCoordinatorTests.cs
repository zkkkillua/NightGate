using NightGate.Core;

namespace NightGate.Service.Tests;

public sealed class NightStateCoordinatorTests
{
    private static readonly NightWindow Window = new(
        new(2026, 7, 6),
        At(6, 21, 0),
        At(6, 23, 0),
        At(7, 0, 0),
        At(7, 0, 30),
        At(7, 9, 0));

    private static readonly NightWindow TuesdayWindow = new(
        new(2026, 7, 7),
        At(7, 21, 0),
        At(7, 23, 0),
        At(8, 0, 0),
        At(8, 0, 30),
        At(8, 9, 0));

    [Fact]
    public async Task ObserveAsync_SameNightRestartKeepsStableNightId()
    {
        MemoryNightStateRepository repository = new();
        NightStateCoordinator firstCoordinator = new(repository);
        CoordinatorObservation started = await firstCoordinator.ObserveAsync(
            Window, NightPhase.Free, At(6, 21, 1));

        NightStateCoordinator restartedCoordinator = new(repository);
        CoordinatorObservation restarted = await restartedCoordinator.ObserveAsync(
            Window, NightPhase.Grace, At(6, 23, 30));

        Assert.NotEqual(Guid.Empty, started.State!.NightId);
        Assert.Equal(started.State.NightId, restarted.State!.NightId);
    }

    [Fact]
    public async Task ObserveScheduleAsync_ExtremeZoneChangeAfterSleepCannotRestartTheClosedNightDate()
    {
        ScheduleStep step = ScheduleProfile.Default.Steps.Single(candidate => candidate.Number == 1);
        TimeZoneInfo utcPlusFourteen = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Coordinator-UTC+14",
            TimeSpan.FromHours(14),
            "NightGate Coordinator UTC+14",
            "NightGate Coordinator UTC+14");
        TimeZoneInfo utcMinusTwelve = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Coordinator-UTC-12",
            TimeSpan.FromHours(-12),
            "NightGate Coordinator UTC-12",
            "NightGate Coordinator UTC-12");
        DateTimeOffset firstNightStartedAtUtc = new(
            2026, 7, 6, 7, 1, 0, TimeSpan.Zero);
        DateTimeOffset resumedAfterSkippedWakeUtc = new(
            2026, 7, 7, 9, 1, 0, TimeSpan.Zero);
        MemoryNightStateRepository repository = new();
        ScheduledCoordinatorObservation started = await new NightStateCoordinator(repository)
            .ObserveScheduleAsync(
                step,
                utcPlusFourteen,
                new ClockObservation(firstNightStartedAtUtc));
        Guid originalNightId = started.Observation.State!.NightId;

        ScheduledCoordinatorObservation resumed = await new NightStateCoordinator(repository)
            .ObserveScheduleAsync(
                step,
                utcMinusTwelve,
                new ClockObservation(resumedAfterSkippedWakeUtc));

        Assert.Null(resumed.Observation.State);
        Assert.Equal(NightPhase.Morning, resumed.Observation.BasePhase);
        Assert.Equal(NightPhase.Morning, resumed.Observation.EffectivePhase);
        Assert.True(repository.State!.IsClosed);
        Assert.Equal(originalNightId, repository.State.NightId);
        Assert.Equal(new DateOnly(2026, 7, 6), repository.State.NightDate);
        Assert.Equal(1, repository.CloseCount);
        Assert.Equal(1, repository.SaveCount);
    }

    [Fact]
    public async Task ObserveAsync_WallClockRollbackCannotReducePhaseOrLastObservedTime()
    {
        MemoryNightStateRepository repository = new();
        NightStateCoordinator coordinator = new(repository);
        CoordinatorObservation locked = await coordinator.ObserveAsync(
            Window, NightPhase.LandingLocked, At(7, 0, 30));

        CoordinatorObservation rolledBack = await coordinator.ObserveAsync(
            Window, NightPhase.Free, At(6, 22, 0));

        Assert.Equal(locked.State!.LastObservedUtc, rolledBack.State!.LastObservedUtc);
        Assert.Equal(NightPhase.LandingLocked, rolledBack.State.HighestBasePhaseReached);
        Assert.Equal(NightPhase.LandingLocked, rolledBack.EffectivePhase);
    }

    [Fact]
    public async Task ObserveAsync_HighestBasePhaseIsMonotonic()
    {
        MemoryNightStateRepository repository = new();
        NightStateCoordinator coordinator = new(repository);

        await coordinator.ObserveAsync(Window, NightPhase.Free, At(6, 21, 1));
        await coordinator.ObserveAsync(Window, NightPhase.LastStart, At(6, 23, 0));
        await coordinator.ObserveAsync(Window, NightPhase.Grace, At(6, 23, 5));
        CoordinatorObservation laterFree = await coordinator.ObserveAsync(
            Window, NightPhase.Free, At(6, 23, 10));

        Assert.Equal(NightPhase.Grace, laterFree.State!.HighestBasePhaseReached);
        Assert.Equal(NightPhase.Grace, laterFree.BasePhase);
    }

    [Fact]
    public async Task ObserveAsync_RestartPreservesOverrideAndKeepsItSeparateFromBaseSeverity()
    {
        DateTimeOffset requestedAt = At(7, 0, 0);
        ActiveOverride activeOverride = new(
            OverrideKind.Entertainment,
            requestedAt,
            requestedAt.AddMinutes(10),
            requestedAt.AddMinutes(30),
            []);
        NightState saved = CreateState(
            lastObservedUtc: requestedAt,
            highestBasePhase: NightPhase.LandingLocked,
            activeOverride: activeOverride,
            entertainmentUsed: true);
        MemoryNightStateRepository repository = new(saved);

        CoordinatorObservation cooling = await new NightStateCoordinator(repository).ObserveAsync(
            Window, NightPhase.LandingLocked, requestedAt.AddMinutes(5));
        CoordinatorObservation active = await new NightStateCoordinator(repository).ObserveAsync(
            Window, NightPhase.LandingLocked, requestedAt.AddMinutes(10));

        Assert.Equal(NightPhase.CoolingOff, cooling.EffectivePhase);
        Assert.Equal(NightPhase.OverrideActive, active.EffectivePhase);
        Assert.Equal(NightPhase.LandingLocked, active.State!.HighestBasePhaseReached);
        Assert.Equal(activeOverride, active.State.ActiveOverride);
    }

    [Fact]
    public async Task ObserveAsync_LegitimateWakeBoundaryClosesNightAndReturnsMorning()
    {
        NightState saved = CreateState(
            lastObservedUtc: Window.Wake.AddMinutes(-1),
            highestBasePhase: NightPhase.LandingLocked,
            teamRescueUsed: true,
            deliberateBypass: true);
        MemoryNightStateRepository repository = new(saved);
        NightStateCoordinator coordinator = new(repository);

        CoordinatorObservation result = await coordinator.ObserveAsync(
            Window, NightPhase.Morning, Window.Wake);

        Assert.Null(result.State);
        Assert.Equal(NightPhase.Morning, result.BasePhase);
        Assert.Equal(NightPhase.Morning, result.EffectivePhase);
        Assert.True(repository.State!.IsClosed);
        Assert.NotNull(repository.ClosedOutcome);
        Assert.True(repository.ClosedOutcome!.TeamRescueUsed);
        Assert.True(repository.ClosedOutcome.DeliberateBypass);
    }

    [Fact]
    public async Task ObserveAsync_CloseNightCopiesPinnedScheduleTimeZoneIntoOutcome()
    {
        TimeZoneInfo pinnedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Coordinator-UTC+8-History",
            TimeSpan.FromHours(8),
            "NightGate Coordinator UTC+8 History",
            "NightGate Coordinator UTC+8 History");
        string serializedTimeZone = NightScheduleTimeZone.Capture(pinnedTimeZone);
        NightState saved = CreateState(
            lastObservedUtc: Window.Wake.AddMinutes(-1),
            highestBasePhase: NightPhase.LandingLocked) with
        {
            ScheduleTimeZoneSerialized = serializedTimeZone,
        };
        MemoryNightStateRepository repository = new(saved);

        await new NightStateCoordinator(repository).ObserveAsync(
            Window,
            NightPhase.Morning,
            Window.Wake);

        NightOutcome outcome = Assert.IsType<NightOutcome>(repository.ClosedOutcome);
        Assert.Equal(serializedTimeZone, outcome.ScheduleTimeZoneSerialized);
        Assert.Equal(
            TimeSpan.FromHours(8),
            NightScheduleTimeZone.Restore(outcome.ScheduleTimeZoneSerialized!).BaseUtcOffset);
    }

    [Fact]
    public async Task ObserveAsync_ForwardToWakeThenRollbackCannotReopenSameNight()
    {
        NightState saved = CreateState(
            lastObservedUtc: Window.Wake.AddMinutes(-1),
            highestBasePhase: NightPhase.LandingLocked,
            entertainmentUsed: true);
        MemoryNightStateRepository repository = new(saved);
        NightStateCoordinator coordinator = new(repository);

        CoordinatorObservation morning = await coordinator.ObserveAsync(
            Window, NightPhase.Morning, Window.Wake);
        CoordinatorObservation rolledBack = await coordinator.ObserveAsync(
            Window, NightPhase.LandingLocked, Window.Lock.AddMinutes(1));

        Assert.Null(morning.State);
        Assert.Null(rolledBack.State);
        Assert.Equal(NightPhase.Morning, rolledBack.EffectivePhase);
        Assert.Equal(saved.NightId, repository.ClosedOutcome!.NightId);
    }

    [Fact]
    public async Task ObserveScheduleAsync_DifferentBootAndRolledBackWallCannotReopenClosedNight()
    {
        Guid originalBoot = Guid.Parse("66666666-6666-6666-6666-666666666666");
        Guid restartedBoot = Guid.Parse("77777777-7777-7777-7777-777777777777");
        NightState saved = CreateState(
            lastObservedUtc: Window.Wake,
            highestBasePhase: NightPhase.LandingLocked,
            entertainmentUsed: true) with
        {
            IsClosed = true,
            LastObservedUptime = TimeSpan.FromHours(100),
            LastObservedBootSessionId = originalBoot,
        };
        MemoryNightStateRepository repository = new(saved);

        ScheduledCoordinatorObservation result = await new NightStateCoordinator(repository)
            .ObserveScheduleAsync(
                ScheduleProfile.Default.Steps[0],
                TimeZoneInfo.Utc,
                new ClockObservation(
                    Window.Lock.AddHours(-1),
                    TimeSpan.FromHours(150),
                    restartedBoot));

        Assert.Null(result.Observation.State);
        Assert.Equal(NightPhase.Morning, result.Observation.EffectivePhase);
        Assert.Equal(Window.Wake, result.EvaluatedAtUtc);
        Assert.Equal(saved.NightId, repository.State!.NightId);
        Assert.True(repository.State.IsClosed);
        Assert.True(repository.State.EntertainmentUsed);
        Assert.Equal(restartedBoot, repository.State.LastObservedBootSessionId);
        Assert.Equal(TimeSpan.FromHours(150), repository.State.LastObservedUptime);
    }

    [Fact]
    public async Task ObserveAsync_StaleSameBootSamplePreservesAnchorSoFreshSampleAdvancesOnce()
    {
        Guid bootSessionId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        DateTimeOffset requestedAt = At(7, 0, 0);
        DateTimeOffset persistedAt = requestedAt.AddMinutes(2);
        ActiveOverride activeOverride = new(
            OverrideKind.Emergency,
            requestedAt,
            requestedAt,
            requestedAt.AddMinutes(30),
            []);
        NightState saved = CreateState(
            lastObservedUtc: persistedAt,
            highestBasePhase: NightPhase.LandingLocked,
            activeOverride: activeOverride,
            emergencyUsed: true) with
        {
            LastObservedUptime = TimeSpan.FromHours(102),
            LastObservedBootSessionId = bootSessionId,
        };
        MemoryNightStateRepository repository = new(saved);
        NightStateCoordinator coordinator = new(repository);

        CoordinatorObservation stale = await coordinator.ObserveAsync(
            Window,
            NightPhase.LandingLocked,
            new ClockObservation(
                requestedAt.AddMinutes(1),
                TimeSpan.FromHours(101),
                bootSessionId));
        CoordinatorObservation fresh = await coordinator.ObserveAsync(
            Window,
            NightPhase.LandingLocked,
            new ClockObservation(
                requestedAt.AddMinutes(1),
                TimeSpan.FromHours(103),
                bootSessionId));

        Assert.Equal(persistedAt, stale.State!.LastObservedUtc);
        Assert.Equal(TimeSpan.FromHours(102), stale.State.LastObservedUptime);
        Assert.Equal(bootSessionId, stale.State.LastObservedBootSessionId);
        Assert.Equal(activeOverride, stale.State.ActiveOverride);
        Assert.Equal(persistedAt.AddHours(1), fresh.State!.LastObservedUtc);
        Assert.Equal(TimeSpan.FromHours(103), fresh.State.LastObservedUptime);
        Assert.Equal(bootSessionId, fresh.State.LastObservedBootSessionId);
        Assert.Null(fresh.State.ActiveOverride);
    }

    [Fact]
    public async Task ObserveAsync_StaleSameBootWallForwardSampleCannotCloseActiveNightOrStartAnother()
    {
        Guid bootSessionId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        DateTimeOffset requestedAt = At(7, 0, 0);
        ActiveOverride activeOverride = new(
            OverrideKind.TeamRescue,
            requestedAt,
            requestedAt,
            requestedAt.AddMinutes(20),
            ["discord.exe"]);
        NightState saved = CreateState(
            lastObservedUtc: requestedAt.AddMinutes(2),
            highestBasePhase: NightPhase.LandingLocked,
            activeOverride: activeOverride,
            emergencyUsed: true,
            teamRescueUsed: true,
            entertainmentUsed: true,
            deliberateBypass: true,
            lateNewEntertainment: true,
            missedLock: true) with
        {
            LastObservedUptime = TimeSpan.FromHours(102),
            LastObservedBootSessionId = bootSessionId,
        };
        MemoryNightStateRepository repository = new(saved);

        CoordinatorObservation result = await new NightStateCoordinator(repository).ObserveAsync(
            TuesdayWindow,
            NightPhase.LastStart,
            new ClockObservation(
                At(7, 23, 0),
                TimeSpan.FromHours(101),
                bootSessionId));

        Assert.Equal(saved, result.State);
        Assert.Equal(saved, repository.State);
        Assert.Equal(NightPhase.LandingLocked, result.BasePhase);
        Assert.Equal(NightPhase.OverrideActive, result.EffectivePhase);
        Assert.Null(repository.ClosedOutcome);
        Assert.Equal(0, repository.SaveCount);
        Assert.Equal(0, repository.CloseCount);
    }

    [Fact]
    public async Task ObserveAsync_StaleSameBootWallForwardSampleCannotReopenClosedNight()
    {
        Guid bootSessionId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        NightState saved = CreateState(
            lastObservedUtc: Window.Wake,
            highestBasePhase: NightPhase.LandingLocked,
            emergencyUsed: true,
            teamRescueUsed: true,
            entertainmentUsed: true,
            deliberateBypass: true,
            lateNewEntertainment: true,
            missedLock: true) with
        {
            IsClosed = true,
            LastObservedUptime = TimeSpan.FromHours(110),
            LastObservedBootSessionId = bootSessionId,
        };
        MemoryNightStateRepository repository = new(saved);

        CoordinatorObservation result = await new NightStateCoordinator(repository).ObserveAsync(
            TuesdayWindow,
            NightPhase.LastStart,
            new ClockObservation(
                At(7, 23, 0),
                TimeSpan.FromHours(109),
                bootSessionId));

        Assert.Null(result.State);
        Assert.Equal(NightPhase.Morning, result.BasePhase);
        Assert.Equal(NightPhase.Morning, result.EffectivePhase);
        Assert.Equal(saved, repository.State);
        Assert.Null(repository.ClosedOutcome);
        Assert.Equal(0, repository.SaveCount);
        Assert.Equal(0, repository.CloseCount);
    }

    [Fact]
    public async Task ObserveAsync_MultiDayForwardCloseThenRollbackCannotStartIntermediateNight()
    {
        MemoryNightStateRepository repository = new();
        NightStateCoordinator coordinator = new(repository);
        CoordinatorObservation monday = await coordinator.ObserveAsync(
            Window,
            NightPhase.Free,
            new ClockObservation(At(6, 21, 1), TimeSpan.FromHours(100)));
        repository.State = monday.State! with { EntertainmentUsed = true };

        CoordinatorObservation wednesdayMorning = await coordinator.ObserveAsync(
            TuesdayWindow,
            NightPhase.Morning,
            new ClockObservation(TuesdayWindow.Wake, TimeSpan.FromHours(136)));
        CoordinatorObservation tuesdayRollback = await coordinator.ObserveAsync(
            TuesdayWindow,
            NightPhase.Free,
            new ClockObservation(At(7, 22, 0), TimeSpan.FromMinutes(5)));

        Assert.Null(wednesdayMorning.State);
        Assert.Null(tuesdayRollback.State);
        Assert.Equal(NightPhase.Morning, tuesdayRollback.EffectivePhase);
        Assert.Equal(monday.State!.NightId, repository.State!.NightId);
        Assert.True(repository.State.IsClosed);
        Assert.True(repository.State.EntertainmentUsed);
        Assert.Equal(TimeSpan.FromMinutes(5), repository.State.LastObservedUptime);
    }

    [Fact]
    public async Task ObserveAsync_PreWakeMorningFromRollbackDoesNotCloseNight()
    {
        NightState saved = CreateState(
            lastObservedUtc: At(7, 0, 30),
            highestBasePhase: NightPhase.LandingLocked);
        MemoryNightStateRepository repository = new(saved);

        CoordinatorObservation result = await new NightStateCoordinator(repository).ObserveAsync(
            Window, NightPhase.Morning, At(6, 20, 0));

        Assert.NotNull(result.State);
        Assert.Equal(saved.NightId, result.State!.NightId);
        Assert.Equal(NightPhase.LandingLocked, result.EffectivePhase);
        Assert.Null(repository.ClosedOutcome);
    }

    [Fact]
    public async Task ObserveAsync_StorageReadFailureReturnsDegradedFailOpenObservation()
    {
        MemoryNightStateRepository repository = new(readMode: StorageMode.Degraded);

        CoordinatorObservation result = await new NightStateCoordinator(repository).ObserveAsync(
            Window, NightPhase.LandingLocked, At(7, 0, 1));

        Assert.True(result.IsDegraded);
        Assert.False(result.EnforcementEnabled);
        Assert.Null(result.State);
    }

    [Fact]
    public async Task ObserveAsync_StorageWriteFailureReturnsDegradedFailOpenObservation()
    {
        MemoryNightStateRepository repository = new(writeMode: StorageMode.Degraded);

        CoordinatorObservation result = await new NightStateCoordinator(repository).ObserveAsync(
            Window, NightPhase.Free, At(6, 21, 1));

        Assert.True(result.IsDegraded);
        Assert.False(result.EnforcementEnabled);
    }

    private static NightState CreateState(
        DateTimeOffset lastObservedUtc,
        NightPhase highestBasePhase,
        ActiveOverride? activeOverride = null,
        bool emergencyUsed = false,
        bool teamRescueUsed = false,
        bool entertainmentUsed = false,
        bool deliberateBypass = false,
        bool lateNewEntertainment = false,
        bool missedLock = false) => new(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Window.NightDate,
            lastObservedUtc,
            highestBasePhase,
            activeOverride,
            emergencyUsed,
            teamRescueUsed,
            entertainmentUsed,
            deliberateBypass,
            lateNewEntertainment,
            missedLock);

    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);

    private sealed class MemoryNightStateRepository : INightStateRepository
    {
        private readonly StorageMode _readMode;
        private readonly StorageMode _writeMode;

        public MemoryNightStateRepository(
            NightState? state = null,
            StorageMode readMode = StorageMode.Success,
            StorageMode writeMode = StorageMode.Success)
        {
            State = state;
            _readMode = readMode;
            _writeMode = writeMode;
        }

        public NightState? State { get; set; }

        public NightOutcome? ClosedOutcome { get; private set; }

        public int SaveCount { get; private set; }

        public int CloseCount { get; private set; }

        public ValueTask<StorageResult<NightState?>> ReadActiveStateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StorageResult<NightState?>(_readMode, State, _readMode == StorageMode.Degraded ? "read" : null));

        public ValueTask<StorageWriteResult> SaveActiveStateWithEventAsync(
            NightState state,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (_writeMode == StorageMode.Success)
            {
                State = state;
            }

            return ValueTask.FromResult(new StorageWriteResult(
                _writeMode,
                _writeMode == StorageMode.Degraded ? "write" : null));
        }

        public ValueTask<StorageWriteResult> SaveActiveStateProgressWithEventAsync(
            NightState state,
            ProgressState progress,
            NightEvent nightEvent,
            long? expectedStateVersion = null,
            long? expectedProgressVersion = null,
            CancellationToken cancellationToken = default) =>
            SaveActiveStateWithEventAsync(
                state,
                nightEvent,
                expectedStateVersion,
                cancellationToken);

        public ValueTask<StorageWriteResult> CloseActiveStateWithOutcomeAndEventAsync(
            NightState closedState,
            NightOutcome outcome,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            CloseCount++;
            if (_writeMode == StorageMode.Success)
            {
                State = closedState;
                ClosedOutcome = outcome;
            }

            return ValueTask.FromResult(new StorageWriteResult(
                _writeMode,
                _writeMode == StorageMode.Degraded ? "write" : null));
        }
    }
}
