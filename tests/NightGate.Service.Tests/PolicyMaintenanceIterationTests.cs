using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class PolicyMaintenanceIterationTests
{
    private static readonly DateTimeOffset Wake = new(2026, 7, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActiveNight_SystemTimeZoneChangesAcrossRestart_KeepsOriginalNightSchedule()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        TimeZoneInfo originalTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Pinned-UTC+8",
            TimeSpan.FromHours(8),
            "NightGate Pinned UTC+8",
            "NightGate Pinned UTC+8");
        MutableObservationClock clock = new(new(
            new DateTimeOffset(2026, 7, 6, 13, 1, 0, TimeSpan.Zero)));
        InMemoryServiceStatus firstStatus = new();
        PolicyMaintenanceIteration firstProcess = Iteration(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            clock,
            new FixedTimeZoneProvider(originalTimeZone),
            firstStatus);

        await firstProcess.ExecuteAsync();
        AssertPolicy(
            firstStatus,
            clock.Observation.UtcNow,
            NightPhase.Free,
            new DateOnly(2026, 7, 6));

        clock.Observation = new(new DateTimeOffset(2026, 7, 6, 16, 50, 0, TimeSpan.Zero));
        InMemoryServiceStatus restartedStatus = new();
        SqliteNightGateRepository restartedRepository = new(database.Path);
        PolicyMaintenanceIteration restartedInChangedTimeZone = Iteration(
            restartedRepository,
            restartedRepository,
            restartedRepository,
            new NightMutationGate(),
            clock,
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            restartedStatus);

        await restartedInChangedTimeZone.ExecuteAsync();

        AssertPolicy(
            restartedStatus,
            clock.Observation.UtcNow,
            NightPhase.LandingLocked,
            new DateOnly(2026, 7, 6));
        NightState active = (await repository.ReadActiveStateAsync()).Value!;
        Assert.False(active.IsClosed);
        Assert.Equal(new DateOnly(2026, 7, 6), active.NightDate);
        Assert.NotNull(active.ScheduleTimeZoneSerialized);
        Assert.Equal(
            TimeSpan.FromHours(8),
            NightScheduleTimeZone.Restore(active.ScheduleTimeZoneSerialized).BaseUtcOffset);
    }

    [Fact]
    public async Task ClosedNight_SystemTimeZoneChange_AppliesWhenNextNightStarts()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        TimeZoneInfo originalTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Previous-UTC+8",
            TimeSpan.FromHours(8),
            "NightGate Previous UTC+8",
            "NightGate Previous UTC+8");
        MutableObservationClock clock = new(new(
            new DateTimeOffset(2026, 7, 6, 13, 1, 0, TimeSpan.Zero)));

        await Iteration(
                repository,
                repository,
                repository,
                new NightMutationGate(),
                clock,
                new FixedTimeZoneProvider(originalTimeZone),
                new InMemoryServiceStatus())
            .ExecuteAsync();

        clock.Observation = new(new DateTimeOffset(2026, 7, 7, 1, 0, 0, TimeSpan.Zero));
        await Iteration(
                repository,
                repository,
                repository,
                new NightMutationGate(),
                clock,
                new FixedTimeZoneProvider(TimeZoneInfo.Utc),
                new InMemoryServiceStatus())
            .ExecuteAsync();
        Assert.True((await repository.ReadActiveStateAsync()).Value!.IsClosed);

        clock.Observation = new(new DateTimeOffset(2026, 7, 7, 21, 1, 0, TimeSpan.Zero));
        InMemoryServiceStatus nextNightStatus = new();
        await Iteration(
                repository,
                repository,
                repository,
                new NightMutationGate(),
                clock,
                new FixedTimeZoneProvider(TimeZoneInfo.Utc),
                nextNightStatus)
            .ExecuteAsync();

        AssertPolicy(
            nextNightStatus,
            clock.Observation.UtcNow,
            NightPhase.Free,
            new DateOnly(2026, 7, 7));
        Assert.Equal(
            new DateTimeOffset(2026, 7, 8, 0, 40, 0, TimeSpan.Zero),
            nextNightStatus.Current.Policy!.Window.Lock);
        NightState nextNight = (await repository.ReadActiveStateAsync()).Value!;
        Assert.False(nextNight.IsClosed);
        Assert.Equal(
            TimeZoneInfo.Utc.Id,
            NightScheduleTimeZone.Restore(nextNight.ScheduleTimeZoneSerialized!).Id);
    }

    [Fact]
    public async Task ExtremeZoneChangeAfterSkippedWake_DoesNotPersistDuplicateNightOrUnlockProgression()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        TimeZoneInfo utcPlusFourteen = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Persisted-UTC+14",
            TimeSpan.FromHours(14),
            "NightGate Persisted UTC+14",
            "NightGate Persisted UTC+14");
        TimeZoneInfo utcMinusTwelve = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Persisted-UTC-12",
            TimeSpan.FromHours(-12),
            "NightGate Persisted UTC-12",
            "NightGate Persisted UTC-12");
        DateOnly originalNightDate = new(2026, 7, 6);
        ScheduleStep step = ScheduleProfile.Default.Steps.Single(candidate => candidate.Number == 1);
        DateTimeOffset scheduledLockAtUtc = ScheduleEvaluator.CreateWindow(
                originalNightDate,
                step,
                utcPlusFourteen)
            .Lock
            .ToUniversalTime();
        NightState persistedNight = new(
            Guid.NewGuid(),
            originalNightDate,
            scheduledLockAtUtc,
            NightPhase.LandingLocked,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            FirstLockObservedAtUtc: scheduledLockAtUtc,
            ScheduledLockAtUtc: scheduledLockAtUtc,
            ScheduleTimeZoneSerialized: NightScheduleTimeZone.Capture(utcPlusFourteen));
        await repository.SaveActiveStateWithEventAsync(
            persistedNight,
            new(
                Guid.NewGuid(),
                persistedNight.NightId,
                scheduledLockAtUtc,
                NightEventKind.StateObserved,
                NightPhase.LandingLocked));
        await repository.SaveProgressAsync(ProgressState.Initial);
        await repository.SaveOutcomeAsync(Outcome(
            new DateOnly(2026, 7, 2),
            new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero)));
        await repository.SaveOutcomeAsync(Outcome(
            new DateOnly(2026, 7, 5),
            new DateTimeOffset(2026, 7, 6, 1, 0, 0, TimeSpan.Zero)));
        MutableObservationClock clock = new(new(
            new DateTimeOffset(2026, 7, 7, 9, 1, 0, TimeSpan.Zero)));
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = Iteration(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            clock,
            new FixedTimeZoneProvider(utcMinusTwelve),
            status);

        await iteration.ExecuteAsync();
        AssertPolicy(status, clock.Observation.UtcNow, NightPhase.Morning, originalNightDate);
        NightState afterResume = (await repository.ReadActiveStateAsync()).Value!;
        Assert.True(afterResume.IsClosed);
        Assert.Equal(persistedNight.NightId, afterResume.NightId);

        clock.Observation = new(
            new DateTimeOffset(2026, 7, 7, 21, 0, 0, TimeSpan.Zero));
        await iteration.ExecuteAsync();

        IReadOnlyList<NightOutcome> outcomes =
            (await repository.ReadLatestOutcomesAsync(10)).Value;
        Assert.Equal(3, outcomes.Count);
        Assert.Single(outcomes, outcome => outcome.NightDate == originalNightDate);
        ProgressState progress = (await repository.ReadProgressAsync()).Value;
        Assert.Null(progress.PendingStep);
        Assert.Null(progress.LastProgressionNightDate);
    }

    [Fact]
    public async Task PersistentWallClockRollback_LogicalScheduleCrossesLifecycleAndStartsNextNight()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        DateTimeOffset startedAt = new(2026, 7, 6, 21, 1, 0, TimeSpan.Zero);
        TimeSpan startedUptime = TimeSpan.FromHours(100);
        Guid bootSessionId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        MutableObservationClock clock = new(new(startedAt, startedUptime, bootSessionId));
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            clock,
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            SuccessfulEmptyRuleProvider(),
            SuccessfulEmptySiteRuleProvider());

        await iteration.ExecuteAsync();
        AssertPolicy(status, startedAt, NightPhase.Free, new DateOnly(2026, 7, 6));

        DateTimeOffset rolledBackWallClock = new(2026, 7, 6, 20, 0, 0, TimeSpan.Zero);
        NightEvent rollbackRetentionBoundary = Event(rolledBackWallClock.AddDays(-90));
        await repository.RecordEventAsync(rollbackRetentionBoundary);
        (DateTimeOffset LogicalUtc, NightPhase Phase, DateOnly NightDate)[] observations =
        [
            (new(2026, 7, 7, 0, 5, 0, TimeSpan.Zero), NightPhase.LastStart, new(2026, 7, 6)),
            (new(2026, 7, 7, 0, 6, 0, TimeSpan.Zero), NightPhase.Grace, new(2026, 7, 6)),
            (new(2026, 7, 7, 0, 40, 0, TimeSpan.Zero), NightPhase.LandingLocked, new(2026, 7, 6)),
            (new(2026, 7, 7, 9, 0, 0, TimeSpan.Zero), NightPhase.Morning, new(2026, 7, 6)),
            (new(2026, 7, 7, 21, 1, 0, TimeSpan.Zero), NightPhase.Free, new(2026, 7, 7)),
            (new(2026, 7, 8, 0, 5, 0, TimeSpan.Zero), NightPhase.LastStart, new(2026, 7, 7)),
        ];

        foreach ((DateTimeOffset logicalUtc, NightPhase phase, DateOnly nightDate) in observations)
        {
            clock.Observation = new(
                rolledBackWallClock,
                startedUptime + (logicalUtc - startedAt),
                bootSessionId);

            await iteration.ExecuteAsync();

            AssertPolicy(status, logicalUtc, phase, nightDate);
        }

        NightState active = (await repository.ReadActiveStateAsync()).Value!;
        Assert.False(active.IsClosed);
        Assert.Equal(new DateOnly(2026, 7, 7), active.NightDate);
        Assert.Single((await repository.ReadLatestOutcomesAsync(10)).Value);
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(1, await CountEventsOfKindAsync(connection, NightEventKind.NightClosed));
        Assert.Equal(1, await CountEventAsync(connection, rollbackRetentionBoundary.EventId));
    }

    [Fact]
    public async Task ExecuteWithoutIpc_ReconcilesMorningAdvancesProgressPurgesRetentionAndPublishesPolicy()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = new(
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            new DateOnly(2026, 7, 6),
            Wake.AddMinutes(-1),
            NightPhase.LandingLocked,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            FirstLockObservedAtUtc: new(2026, 7, 7, 0, 40, 0, TimeSpan.Zero),
            ScheduledLockAtUtc: new(2026, 7, 7, 0, 40, 0, TimeSpan.Zero));
        await repository.SaveActiveStateWithEventAsync(state, Event(Wake.AddMinutes(-1)));
        await repository.SaveProgressAsync(ProgressState.Initial);
        await repository.SaveOutcomeAsync(Outcome(new DateOnly(2026, 7, 1), Wake.AddDays(-6)));
        await repository.SaveOutcomeAsync(Outcome(new DateOnly(2026, 7, 2), Wake.AddDays(-5)));
        await repository.SaveOutcomeAsync(Outcome(new DateOnly(2026, 7, 3), Wake.AddDays(-4)));
        await repository.SaveOutcomeAsync(Outcome(new DateOnly(2026, 7, 4), Wake.AddDays(-3)));
        await repository.SaveOutcomeAsync(Outcome(new DateOnly(2026, 7, 5), Wake.AddDays(-2)));
        NightEvent oldEvent = Event(Wake.AddDays(-90).AddTicks(-1));
        NightEvent exactBoundary = Event(Wake.AddDays(-90));
        await repository.RecordEventAsync(oldEvent);
        await repository.RecordEventAsync(exactBoundary);
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(Wake),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            SuccessfulEmptyRuleProvider(),
            SuccessfulEmptySiteRuleProvider());

        await iteration.ExecuteAsync();

        NightState closed = (await repository.ReadActiveStateAsync()).Value!;
        ProgressState progress = (await repository.ReadProgressAsync()).Value;
        Assert.True(closed.IsClosed);
        Assert.Equal(1, progress.CurrentStep);
        Assert.Equal(2, progress.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 6), progress.PendingStepUnlockedByNightDate);
        Assert.Null(progress.PendingStepConfirmedAtUtc);
        Assert.Null(progress.PendingStepEffectiveNightDate);
        Assert.False(status.Current.IsDegraded);
        Assert.True(status.Current.EnforcementEnabled);
        Assert.Equal(NightPhase.Morning, status.Current.Policy!.Phase);
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(0, await CountEventAsync(connection, oldEvent.EventId));
        Assert.Equal(1, await CountEventAsync(connection, exactBoundary.EventId));
    }

    [Fact]
    public async Task SuccessfulMaintenanceCycle_RecoversAPreviouslyDegradedRuntime()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("worker-loop-failure"));
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(Wake),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            SuccessfulEmptyRuleProvider(),
            SuccessfulEmptySiteRuleProvider());

        await iteration.ExecuteAsync();

        Assert.False(status.Current.IsDegraded);
        Assert.True(status.Current.EnforcementEnabled);
        Assert.Null(status.Current.DegradationCode);
        Assert.NotNull(status.Current.Policy);
        Assert.True(status.Current.Policy.EnforcementEnabled);
        Assert.False(status.Current.Policy.IsDegraded);
    }

    [Fact]
    public async Task Execute_ConfirmedStepEffectiveForLogicalNightActivatesAndReobservesPolicy()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        DateOnly effectiveNight = new(2026, 7, 14);
        await repository.SaveProgressAsync(new(
            1,
            null,
            new(2026, 7, 10),
            2,
            new(2026, 7, 10),
            new(2026, 7, 14, 14, 0, 0, TimeSpan.Zero),
            effectiveNight));
        DateTimeOffset now = new(2026, 7, 14, 23, 55, 0, TimeSpan.Zero);
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(now),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            SuccessfulEmptyRuleProvider(),
            SuccessfulEmptySiteRuleProvider());

        await iteration.ExecuteAsync();

        ProgressState progress = (await repository.ReadProgressAsync()).Value;
        Assert.Equal(2, progress.CurrentStep);
        Assert.Null(progress.PendingStep);
        Assert.Null(progress.PendingStepUnlockedByNightDate);
        Assert.Null(progress.PendingStepConfirmedAtUtc);
        Assert.Null(progress.PendingStepEffectiveNightDate);
        Assert.Equal(effectiveNight, status.Current.Policy!.Window.NightDate);
        Assert.Equal(new TimeOnly(23, 50), TimeOnly.FromDateTime(
            status.Current.Policy.Window.LastStart.UtcDateTime));
        Assert.Equal(NightPhase.Grace, status.Current.Policy.Phase);
    }

    [Fact]
    public async Task Execute_ConfirmedStepBeforeEffectiveLogicalNightRemainsPending()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(new(
            1,
            null,
            new(2026, 7, 10),
            2,
            new(2026, 7, 10),
            new(2026, 7, 14, 14, 30, 0, TimeSpan.Zero),
            new(2026, 7, 15)));
        DateTimeOffset now = new(2026, 7, 14, 23, 55, 0, TimeSpan.Zero);
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(now),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            SuccessfulEmptyRuleProvider(),
            SuccessfulEmptySiteRuleProvider());

        await iteration.ExecuteAsync();

        ProgressState progress = (await repository.ReadProgressAsync()).Value;
        Assert.Equal(1, progress.CurrentStep);
        Assert.Equal(2, progress.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 15), progress.PendingStepEffectiveNightDate);
        Assert.Equal(NightPhase.Free, status.Current.Policy!.Phase);
        Assert.Equal(new TimeOnly(0, 5), TimeOnly.FromDateTime(
            status.Current.Policy.Window.LastStart.UtcDateTime));
    }

    [Fact]
    public async Task Execute_ConfirmedStepAfterEffectiveNightActivatesAfterRepositoryRestart()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository seedRepository = new(database.Path);
        await seedRepository.SaveProgressAsync(ConfirmedPendingStep(
            currentStep: 1,
            effectiveNight: new(2026, 7, 13)));
        SqliteNightGateRepository restartedRepository = new(database.Path);
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = Iteration(
            restartedRepository,
            restartedRepository,
            restartedRepository,
            new NightMutationGate(),
            new FixedClock(new(2026, 7, 14, 22, 0, 0, TimeSpan.Zero)),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status);

        await iteration.ExecuteAsync();

        ProgressState progress = (await restartedRepository.ReadProgressAsync()).Value;
        Assert.Equal(2, progress.CurrentStep);
        Assert.Null(progress.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 14), status.Current.Policy!.Window.NightDate);
    }

    [Fact]
    public async Task Execute_ActivationReobservesGraceAsLandingLockedInSameIteration()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ConfirmedPendingStep(
            currentStep: 1,
            effectiveNight: new(2026, 7, 14)));
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = Iteration(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(new(2026, 7, 15, 0, 30, 0, TimeSpan.Zero)),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status);

        await iteration.ExecuteAsync();

        Assert.Equal(2, (await repository.ReadProgressAsync()).Value.CurrentStep);
        Assert.Equal(NightPhase.LandingLocked, status.Current.Policy!.Phase);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 15, 0, 25, 0, TimeSpan.Zero),
            status.Current.Policy.Window.Lock);
    }

    [Fact]
    public async Task Execute_ActivationReobservesNewStepMorningBoundaryInSameIteration()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ConfirmedPendingStep(
            currentStep: 3,
            effectiveNight: new(2026, 7, 14)));
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = Iteration(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(new(2026, 7, 15, 8, 20, 0, TimeSpan.Zero)),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status);

        await iteration.ExecuteAsync();

        ProgressState progress = (await repository.ReadProgressAsync()).Value;
        Assert.Equal(4, progress.CurrentStep);
        Assert.Equal(NightPhase.Morning, status.Current.Policy!.Phase);
        Assert.True((await repository.ReadActiveStateAsync()).Value!.IsClosed);
    }

    [Fact]
    public async Task Execute_ActivationCompareExchangeConflictRereadsAndConverges()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ConfirmedPendingStep(
            currentStep: 1,
            effectiveNight: new(2026, 7, 14)));
        ConflictOnceProgressRepository progress = new(repository);
        PolicyMaintenanceIteration iteration = Iteration(
            repository,
            progress,
            repository,
            new NightMutationGate(),
            new FixedClock(new(2026, 7, 14, 23, 0, 0, TimeSpan.Zero)),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            new InMemoryServiceStatus());

        await iteration.ExecuteAsync();

        Assert.Equal(2, (await repository.ReadProgressAsync()).Value.CurrentStep);
        Assert.True(progress.SaveCalls >= 2);
    }

    [Fact]
    public async Task ConcurrentConfirmationAndMaintenance_ConvergeOnNextMaintenanceIteration()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        DateOnly unlockNight = new(2026, 7, 10);
        await repository.SaveProgressAsync(new(
            1,
            null,
            unlockNight,
            2,
            unlockNight));
        NightMutationGate gate = new();
        FixedClock clock = new(new(2026, 7, 14, 14, 0, 0, TimeSpan.Zero));
        FixedTimeZoneProvider china = new(TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Policy-UTC+8",
            TimeSpan.FromHours(8),
            "NightGate Policy UTC+8",
            "NightGate Policy UTC+8"));
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = Iteration(
            repository,
            repository,
            repository,
            gate,
            clock,
            china,
            status);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcessSnapshot()),
            gate,
            clock,
            timeZoneProvider: china);

        await Task.WhenAll(
            iteration.ExecuteAsync().AsTask(),
            handler.ExecuteAsync(new ConfirmIPhoneStepCommand(
                2,
                new(true, true, true, true, true, true, true, true, true, true))).AsTask());
        await iteration.ExecuteAsync();

        ProgressState progress = (await repository.ReadProgressAsync()).Value;
        Assert.Equal(2, progress.CurrentStep);
        Assert.Null(progress.PendingStep);
    }

    [Fact]
    public async Task Execute_PublishesConfiguredSiteRulesAndPreservesAppRules()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        string root = Path.Combine(Path.GetTempPath(), "NightGate", "configured-game.exe");
        AppRule configuredRule = new(
            "configured-game",
            root,
            [],
            AppRuleCategory.Game,
            35);
        var provider = new FixedConfiguredRuleProvider(
            ConfiguredRuleProviderResult.Success([configuredRule]));
        SiteRule[] configuredSites = [new("example.com"), new("video.example.com")];
        var siteProvider = new FixedConfiguredSiteRuleProvider(
            ConfiguredSiteRuleProviderResult.Success(configuredSites));
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(Wake),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            provider,
            siteProvider);

        await iteration.ExecuteAsync();

        Assert.Equal([configuredRule], status.Current.Policy!.AppRules.ToArray());
        Assert.Equal(configuredSites, status.Current.Policy.SiteRules.ToArray());
    }

    [Fact]
    public async Task Execute_DegradedRuleConfigurationUsesMaintenanceFailurePath()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(Wake),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            new InMemoryServiceStatus(),
            new FixedConfiguredRuleProvider(
                ConfiguredRuleProviderResult.Degraded("configured-rules-invalid")));

        await Assert.ThrowsAsync<IOException>(async () => await iteration.ExecuteAsync());
    }

    [Fact]
    public async Task Execute_DegradedSiteConfigurationPreventsPolicyPublication()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(Wake),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            SuccessfulEmptyRuleProvider(),
            new FixedConfiguredSiteRuleProvider(
                ConfiguredSiteRuleProviderResult.Degraded(
                    "configured-site-rules-invalid")));

        await Assert.ThrowsAsync<IOException>(async () => await iteration.ExecuteAsync());
        Assert.True(status.Current.IsDegraded);
        Assert.False(status.Current.EnforcementEnabled);
        Assert.Null(status.Current.Policy);
    }

    [Theory]
    [InlineData("EXAMPLE.COM")]
    [InlineData("localhost")]
    public async Task Execute_NoncanonicalInjectedSiteSourceCannotPublishPolicy(string domain)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(Wake),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            SuccessfulEmptyRuleProvider(),
            new FixedConfiguredSiteRuleProvider(
                ConfiguredSiteRuleProviderResult.Success([new(domain)])));

        await Assert.ThrowsAsync<IOException>(async () => await iteration.ExecuteAsync());
        Assert.True(status.Current.IsDegraded);
        Assert.False(status.Current.EnforcementEnabled);
        Assert.Null(status.Current.Policy);
    }

    [Fact]
    public Task Execute_DuplicateInjectedSiteSourceCannotPublishPolicy() =>
        AssertInjectedSiteRulesRejectedAsync(
            [new("example.com"), new("example.com")]);

    [Fact]
    public Task Execute_UnsortedInjectedSiteSourceCannotPublishPolicy() =>
        AssertInjectedSiteRulesRejectedAsync(
            [new("video.example.com"), new("example.com")]);

    [Fact]
    public Task Execute_OverboundInjectedSiteSourceCannotPublishPolicy() =>
        AssertInjectedSiteRulesRejectedAsync(
            Enumerable.Range(0, 101)
                .Select(index => new SiteRule($"site-{index:D3}.example.com")));

    [Fact]
    public async Task Execute_MissingSiteProviderCannotPublishEnforceableEmptyPolicy()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(Wake),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            SuccessfulEmptyRuleProvider(),
            null);

        await Assert.ThrowsAsync<IOException>(async () => await iteration.ExecuteAsync());
        Assert.True(status.Current.IsDegraded);
        Assert.False(status.Current.EnforcementEnabled);
        Assert.Null(status.Current.Policy);
    }

    [Fact]
    public async Task Execute_MissingRuleProviderCannotPublishEnforceableEmptyPolicy()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(Wake),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            null);

        await Assert.ThrowsAsync<IOException>(async () => await iteration.ExecuteAsync());
        Assert.True(status.Current.IsDegraded);
        Assert.False(status.Current.EnforcementEnabled);
        Assert.Null(status.Current.Policy);
    }

    private static NightOutcome Outcome(DateOnly date, DateTimeOffset closedAt)
    {
        DateTimeOffset scheduledLock = closedAt.AddMinutes(-1);
        return new(
            Guid.NewGuid(),
            date,
            closedAt,
            false,
            false,
            false,
            false,
            false,
            false,
            FirstLockObservedAtUtc: scheduledLock,
            ScheduledLockAtUtc: scheduledLock);
    }

    private static async Task AssertInjectedSiteRulesRejectedAsync(
        IEnumerable<SiteRule> rules)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(Wake),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            SuccessfulEmptyRuleProvider(),
            new FixedConfiguredSiteRuleProvider(
                ConfiguredSiteRuleProviderResult.Success(rules)));

        await Assert.ThrowsAsync<IOException>(async () => await iteration.ExecuteAsync());
        Assert.True(status.Current.IsDegraded);
        Assert.False(status.Current.EnforcementEnabled);
        Assert.Null(status.Current.Policy);
    }

    private static IConfiguredRuleProvider SuccessfulEmptyRuleProvider() =>
        new FixedConfiguredRuleProvider(ConfiguredRuleProviderResult.Success([]));

    private static IConfiguredSiteRuleProvider SuccessfulEmptySiteRuleProvider() =>
        new FixedConfiguredSiteRuleProvider(ConfiguredSiteRuleProviderResult.Success([]));

    private static ProgressState ConfirmedPendingStep(
        int currentStep,
        DateOnly effectiveNight)
    {
        DateOnly unlockNight = new(2026, 7, 10);
        return new(
            currentStep,
            null,
            unlockNight,
            currentStep + 1,
            unlockNight,
            new(2026, 7, 14, 14, 0, 0, TimeSpan.Zero),
            effectiveNight);
    }

    private static PolicyMaintenanceIteration Iteration(
        INightStateRepository stateRepository,
        IProgressRepository progressRepository,
        IHistoryRepository historyRepository,
        INightMutationGate gate,
        IClock clock,
        ITimeZoneProvider timeZone,
        IServiceStatusPublisher status) => new(
            stateRepository,
            progressRepository,
            historyRepository,
            gate,
            clock,
            timeZone,
            status,
            SuccessfulEmptyRuleProvider(),
            SuccessfulEmptySiteRuleProvider());

    private static NightEvent Event(DateTimeOffset occurredAt) => new(
        Guid.NewGuid(),
        null,
        occurredAt,
        NightEventKind.StateObserved,
        NightPhase.LandingLocked);

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static async Task<long> CountEventAsync(SqliteConnection connection, Guid eventId)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_events WHERE event_id = $id;";
        command.Parameters.AddWithValue("$id", eventId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountEventsOfKindAsync(
        SqliteConnection connection,
        NightEventKind kind)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_events WHERE json_extract(json, '$.kind') = $kind;";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void AssertPolicy(
        InMemoryServiceStatus status,
        DateTimeOffset evaluatedAt,
        NightPhase phase,
        DateOnly nightDate)
    {
        Assert.False(status.Current.IsDegraded);
        Assert.Equal(evaluatedAt, status.Current.Policy!.EvaluatedAt);
        Assert.Equal(phase, status.Current.Policy.Phase);
        Assert.Equal(nightDate, status.Current.Policy.Window.NightDate);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class MutableObservationClock(ClockObservation observation) : IClock
    {
        public ClockObservation Observation { get; set; } = observation;

        public DateTimeOffset UtcNow => Observation.UtcNow;

        public ClockObservation Observe() => Observation;
    }

    private sealed class FixedTimeZoneProvider(TimeZoneInfo timeZone) : ITimeZoneProvider
    {
        public TimeZoneInfo Local => timeZone;
    }

    private sealed class FixedConfiguredRuleProvider(ConfiguredRuleProviderResult result) :
        IConfiguredRuleProvider
    {
        public ConfiguredRuleProviderResult GetRules() => result;
    }

    private sealed class FixedConfiguredSiteRuleProvider(
        ConfiguredSiteRuleProviderResult result) : IConfiguredSiteRuleProvider
    {
        public ConfiguredSiteRuleProviderResult GetRules() => result;
    }

    private sealed class ConflictOnceProgressRepository(
        IProgressRepository inner) : IProgressRepository
    {
        private int _conflictsRemaining = 1;

        public int SaveCalls { get; private set; }

        public ValueTask<StorageResult<ProgressState>> ReadProgressAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadProgressAsync(cancellationToken);

        public ValueTask<StorageWriteResult> SaveProgressAsync(
            ProgressState progress,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (Interlocked.Exchange(ref _conflictsRemaining, 0) == 1)
            {
                return ValueTask.FromResult(StorageWriteResult.Conflict);
            }

            return inner.SaveProgressAsync(progress, expectedVersion, cancellationToken);
        }
    }

    private sealed class EmptyAllowedProcessSnapshot : IAllowedProcessSnapshotProvider
    {
        public System.Collections.Immutable.ImmutableArray<string> GetSnapshot() => [];
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            DirectoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NightGate.Service.Tests",
                Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(DirectoryPath, "state.db");
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, true);
            }
        }
    }
}
