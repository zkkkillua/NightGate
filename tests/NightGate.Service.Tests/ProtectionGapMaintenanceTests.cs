using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class ProtectionGapMaintenanceTests
{
    private static readonly DateOnly NightDate = new(2026, 7, 6);
    private static readonly Guid FirstBoot =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RestartedBoot =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task DegradedRuntime_RecoveryDuringGrace_AtomicallyMarksProtectionGap()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = await SeedAsync(
            database.Path,
            StateAt(NightPhase.Grace, At(7, 0, 10)));
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));

        await Iteration(repository, new ObservationClock(
            new(At(7, 0, 11), TimeSpan.FromHours(10), FirstBoot)), status)
            .ExecuteAsync();

        NightState state = (await repository.ReadActiveStateAsync()).Value!;
        Assert.True(state.ProtectionGapObserved);
        Assert.Equal(1, await CountServiceDegradedEventsAsync(database.Path));
        Assert.False(status.Current.IsDegraded);
        Assert.True(status.Current.EnforcementEnabled);
    }

    [Fact]
    public async Task DegradedRuntime_RecoveryDuringFree_DoesNotMarkProtectionGap()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = await SeedAsync(
            database.Path,
            StateAt(NightPhase.Free, At(6, 23, 0)));
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));

        await Iteration(repository, new ObservationClock(
            new(At(6, 23, 1), TimeSpan.FromHours(10), FirstBoot)), status)
            .ExecuteAsync();

        Assert.False((await repository.ReadActiveStateAsync()).Value!.ProtectionGapObserved);
        Assert.Equal(0, await CountServiceDegradedEventsAsync(database.Path));
    }

    [Fact]
    public async Task DegradedRuntime_AfterNinetyMinuteRuleCutoff_MarksProtectionGap()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = await SeedAsync(
            database.Path,
            StateAt(NightPhase.Free, At(6, 23, 0)));
        await repository.SaveRuleSettingsAsync(new(
            [GameRule("long-game", 90)],
            []));
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));

        await Iteration(
                repository,
                new ObservationClock(new(
                    At(6, 23, 20),
                    TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(20)),
                    FirstBoot)),
                status,
                repository)
            .ExecuteAsync();

        Assert.True((await repository.ReadActiveStateAsync()).Value!.ProtectionGapObserved);
        Assert.Equal(1, await CountServiceDegradedEventsAsync(database.Path));
    }

    [Fact]
    public async Task DegradedRuntime_StepChangedAfterNightStarted_UsesPersistedLockBoundary()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = await SeedAsync(
            database.Path,
            StateAt(NightPhase.Grace, At(6, 23, 29)));
        await repository.SaveProgressAsync(new(4, null, null));
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));

        await Iteration(repository, new ObservationClock(new(
            At(6, 23, 30),
            TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(30)),
            FirstBoot)), status).ExecuteAsync();

        Assert.False((await repository.ReadActiveStateAsync()).Value!.ProtectionGapObserved);
        Assert.Equal(0, await CountServiceDegradedEventsAsync(database.Path));
    }

    [Fact]
    public async Task DegradedRuntime_TimeZoneChangedAfterNightStarted_UsesPersistedLockBoundary()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = await SeedAsync(
            database.Path,
            StateAt(NightPhase.Free, At(6, 23, 59)));
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));
        TimeZoneInfo changedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "ProtectionGap-UTC-1",
            TimeSpan.FromHours(-1),
            "ProtectionGap UTC-1",
            "ProtectionGap UTC-1");

        await Iteration(
                repository,
                new ObservationClock(new(
                    At(7, 0, 10),
                    TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(11)),
                    FirstBoot)),
                status,
                timeZone: changedTimeZone)
            .ExecuteAsync();

        Assert.True((await repository.ReadActiveStateAsync()).Value!.ProtectionGapObserved);
        Assert.Equal(1, await CountServiceDegradedEventsAsync(database.Path));
    }

    [Fact]
    public async Task DegradedRuntime_FuturePendingLongRule_DoesNotMoveTonightCutoffEarlier()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = await SeedAsync(
            database.Path,
            StateAt(NightPhase.Free, At(6, 23, 0)));
        await repository.SaveRuleSettingsAsync(new(
            [GameRule("active-short", 15)],
            [],
            [GameRule("pending-long", 90)],
            [],
            NightDate.AddDays(1),
            At(6, 22, 30)));
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));

        await Iteration(
                repository,
                new ObservationClock(new(
                    At(6, 23, 20),
                    TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(20)),
                    FirstBoot)),
                status,
                repository)
            .ExecuteAsync();

        NightState state = (await repository.ReadActiveStateAsync()).Value!;
        RuleSettingsState rules = (await repository.ReadRuleSettingsAsync()).Value;
        Assert.False(state.ProtectionGapObserved);
        Assert.Equal(NightDate.AddDays(1), rules.PendingEffectiveNightDate);
        Assert.Equal("active-short", Assert.Single(rules.ActiveAppRules).Id);
    }

    [Fact]
    public async Task DegradedRuntime_PendingLongRuleEffectiveTonight_IsActivatedForCutoff()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = await SeedAsync(
            database.Path,
            StateAt(NightPhase.Free, At(6, 23, 0)));
        await repository.SaveRuleSettingsAsync(new(
            [GameRule("active-short", 15)],
            [],
            [GameRule("pending-long", 90)],
            [],
            NightDate,
            At(6, 22, 30)));
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));

        await Iteration(
                repository,
                new ObservationClock(new(
                    At(6, 23, 20),
                    TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(20)),
                    FirstBoot)),
                status,
                repository)
            .ExecuteAsync();

        NightState state = (await repository.ReadActiveStateAsync()).Value!;
        RuleSettingsState rules = (await repository.ReadRuleSettingsAsync()).Value;
        Assert.True(state.ProtectionGapObserved);
        Assert.Null(rules.PendingEffectiveNightDate);
        Assert.Equal("pending-long", Assert.Single(rules.ActiveAppRules).Id);
    }

    [Fact]
    public async Task StartingRuntime_AfterRestartWithRestrictedActiveNight_MarksUsingLogicalTime()
    {
        using TempDatabase database = new();
        NightState beforeRestart = StateAt(NightPhase.Grace, At(7, 0, 10)) with
        {
            LastObservedUptime = TimeSpan.FromHours(20),
            LastObservedBootSessionId = FirstBoot,
        };
        SqliteNightGateRepository seed = await SeedAsync(database.Path, beforeRestart);
        SqliteNightGateRepository restarted = new(database.Path);
        InMemoryServiceStatus startingStatus = new();

        await Iteration(restarted, new ObservationClock(
            new(At(6, 22, 0), TimeSpan.FromMinutes(3), RestartedBoot)), startingStatus)
            .ExecuteAsync();

        NightState state = (await restarted.ReadActiveStateAsync()).Value!;
        Assert.True(state.ProtectionGapObserved);
        Assert.True(state.LastObservedUtc >= beforeRestart.LastObservedUtc);
        Assert.Equal(1, await CountServiceDegradedEventsAsync(database.Path));
        GC.KeepAlive(seed);
    }

    [Fact]
    public async Task StartingRuntime_MonotonicTimeCrossedIntoRestriction_MarksDespiteWallClockRollback()
    {
        using TempDatabase database = new();
        NightState freeState = StateAt(NightPhase.Free, At(6, 23, 59)) with
        {
            LastObservedUptime = TimeSpan.FromHours(9),
            LastObservedBootSessionId = FirstBoot,
        };
        SqliteNightGateRepository repository = await SeedAsync(database.Path, freeState);
        InMemoryServiceStatus startingStatus = new();

        await Iteration(repository, new ObservationClock(
            new(At(6, 23, 0), TimeSpan.FromHours(9).Add(TimeSpan.FromMinutes(20)), FirstBoot)),
            startingStatus).ExecuteAsync();

        NightState state = (await repository.ReadActiveStateAsync()).Value!;
        Assert.True(state.ProtectionGapObserved);
        Assert.Equal(NightPhase.Grace, state.HighestBasePhaseReached);
        Assert.Equal(1, await CountServiceDegradedEventsAsync(database.Path));
    }

    [Fact]
    public async Task RecoveryAtWake_MarksBeforeClosingAndPropagatesToOutcome()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = await SeedAsync(
            database.Path,
            StateAt(NightPhase.LandingLocked, At(7, 0, 40)));
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));

        await Iteration(repository, new ObservationClock(
            new(At(7, 9, 0), TimeSpan.FromHours(20), FirstBoot)), status)
            .ExecuteAsync();

        NightState closed = (await repository.ReadActiveStateAsync()).Value!;
        NightOutcome outcome = Assert.Single((await repository.ReadLatestOutcomesAsync(10)).Value);
        Assert.True(closed.IsClosed);
        Assert.True(closed.ProtectionGapObserved);
        Assert.True(outcome.ProtectionGapObserved);
        Assert.False(outcome.Qualifies);
        Assert.Equal(1, await CountServiceDegradedEventsAsync(database.Path));
        Assert.Equal(
            ["ServiceDegraded", "NightClosed"],
            await ReadRecoveryAndCloseEventKindsAsync(database.Path));
    }

    [Fact]
    public async Task RepeatedSuccessfulMaintenance_WritesOnlyOneProtectionGapEvent()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = await SeedAsync(
            database.Path,
            StateAt(NightPhase.Grace, At(7, 0, 10)));
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));
        PolicyMaintenanceIteration iteration = Iteration(repository, new ObservationClock(
            new(At(7, 0, 11), TimeSpan.FromHours(10), FirstBoot)), status);

        await iteration.ExecuteAsync();
        await iteration.ExecuteAsync();

        Assert.True((await repository.ReadActiveStateAsync()).Value!.ProtectionGapObserved);
        Assert.Equal(1, await CountServiceDegradedEventsAsync(database.Path));
    }

    [Fact]
    public async Task ProtectionGapCompareExchangeConflict_RereadsAndConvergesOnce()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = await SeedAsync(
            database.Path,
            StateAt(NightPhase.Grace, At(7, 0, 10)));
        ConflictOnceNightStateRepository conflicting = new(repository);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));

        await Iteration(conflicting, repository, new ObservationClock(
            new(At(7, 0, 11), TimeSpan.FromHours(10), FirstBoot)), status)
            .ExecuteAsync();

        Assert.True((await repository.ReadActiveStateAsync()).Value!.ProtectionGapObserved);
        Assert.True(conflicting.SaveCalls >= 2);
        Assert.Equal(1, await CountServiceDegradedEventsAsync(database.Path));
    }

    [Fact]
    public async Task DegradationPublishedDuringMaintenance_IsNotClearedBeforeTheGapIsRecorded()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = await SeedAsync(
            database.Path,
            StateAt(NightPhase.Grace, At(7, 0, 10)));
        InMemoryServiceStatus status = new();
        NightWindow window = ScheduleEvaluator.CreateWindow(
            NightDate,
            ScheduleProfile.Default.Steps[0],
            TimeZoneInfo.Utc);
        await status.PublishAsync(new(
            true,
            false,
            null,
            new PolicySnapshot(
                At(7, 0, 10),
                NightPhase.Grace,
                window,
                [],
                [])));
        BlockingPurgeHistoryRepository blockingHistory = new(repository);
        PolicyMaintenanceIteration interrupted = new(
            repository,
            repository,
            blockingHistory,
            new NightMutationGate(),
            new ObservationClock(new(
                At(7, 0, 11),
                TimeSpan.FromHours(10),
                FirstBoot)),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            new FixedConfiguredRuleProvider(ConfiguredRuleProviderResult.Success([])),
            new FixedConfiguredSiteRuleProvider(ConfiguredSiteRuleProviderResult.Success([])));

        Task firstMaintenance = interrupted.ExecuteAsync().AsTask();
        await blockingHistory.PurgeReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("concurrent-pipe-failure"));
        blockingHistory.AllowPurge();
        await Assert.ThrowsAsync<IOException>(() =>
            firstMaintenance.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(status.Current.IsDegraded);
        Assert.False((await repository.ReadActiveStateAsync()).Value!.ProtectionGapObserved);

        await Iteration(repository, new ObservationClock(
            new(At(7, 0, 12), TimeSpan.FromHours(10).Add(TimeSpan.FromMinutes(1)), FirstBoot)), status)
            .ExecuteAsync();

        Assert.False(status.Current.IsDegraded);
        Assert.True((await repository.ReadActiveStateAsync()).Value!.ProtectionGapObserved);
        Assert.Equal(1, await CountServiceDegradedEventsAsync(database.Path));
    }

    [Fact]
    public async Task StartingRuntime_WithoutActiveNight_DoesNotBackfillAProtectionGap()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();

        await Iteration(repository, new ObservationClock(
            new(At(7, 0, 10), TimeSpan.FromHours(10), FirstBoot)), status)
            .ExecuteAsync();

        Assert.False((await repository.ReadActiveStateAsync()).Value!.ProtectionGapObserved);
        Assert.Equal(0, await CountServiceDegradedEventsAsync(database.Path));
    }

    [Fact]
    public async Task DegradedRuntime_WithClosedState_DoesNotRewriteIt()
    {
        using TempDatabase database = new();
        NightState closed = StateAt(NightPhase.LandingLocked, At(7, 9, 0)) with
        {
            IsClosed = true,
        };
        SqliteNightGateRepository repository = await SeedAsync(database.Path, closed);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(ServiceRuntimeStatus.Degraded("policy-maintenance-failure"));

        await Iteration(repository, new ObservationClock(
            new(At(7, 9, 1), TimeSpan.FromHours(20), FirstBoot)), status)
            .ExecuteAsync();

        Assert.False((await repository.ReadActiveStateAsync()).Value!.ProtectionGapObserved);
        Assert.Equal(0, await CountServiceDegradedEventsAsync(database.Path));
    }

    private static async Task<SqliteNightGateRepository> SeedAsync(
        string path,
        NightState state)
    {
        SqliteNightGateRepository repository = new(path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        await repository.SaveActiveStateWithEventAsync(
            state,
            new(
                Guid.NewGuid(),
                state.NightId,
                state.LastObservedUtc,
                NightEventKind.StateObserved,
                state.HighestBasePhaseReached));
        return repository;
    }

    private static NightState StateAt(NightPhase phase, DateTimeOffset observedAt) => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        NightDate,
        observedAt,
        phase,
        null,
        false,
        false,
        false,
        false,
        false,
        false,
        LastObservedUptime: TimeSpan.FromHours(9),
        LastObservedBootSessionId: FirstBoot,
        ScheduledLockAtUtc: At(7, 0, 40));

    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);

    private static AppRule GameRule(string id, int sessionMinutes) => new(
        id,
        $@"C:\Games\{id}\{id}.exe",
        [],
        AppRuleCategory.Game,
        sessionMinutes);

    private static PolicyMaintenanceIteration Iteration(
        SqliteNightGateRepository repository,
        IClock clock,
        InMemoryServiceStatus status,
        IRuleSettingsRepository? ruleSettingsRepository = null,
        TimeZoneInfo? timeZone = null) => Iteration(
            repository,
            repository,
            clock,
            status,
            ruleSettingsRepository,
            timeZone);

    private static PolicyMaintenanceIteration Iteration(
        INightStateRepository stateRepository,
        SqliteNightGateRepository otherRepositories,
        IClock clock,
        InMemoryServiceStatus status,
        IRuleSettingsRepository? ruleSettingsRepository = null,
        TimeZoneInfo? timeZone = null) => new(
            stateRepository,
            otherRepositories,
            otherRepositories,
            new NightMutationGate(),
            clock,
            new FixedTimeZoneProvider(timeZone ?? TimeZoneInfo.Utc),
            status,
            new FixedConfiguredRuleProvider(ConfiguredRuleProviderResult.Success([])),
            new FixedConfiguredSiteRuleProvider(ConfiguredSiteRuleProviderResult.Success([])),
            ruleSettingsRepository);

    private static async Task<long> CountServiceDegradedEventsAsync(string path)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM raw_events WHERE json_extract(json, '$.kind') = 'ServiceDegraded';";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string[]> ReadRecoveryAndCloseEventKindsAsync(string path)
    {
        await using SqliteConnection connection = new($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT json_extract(json, '$.kind')
            FROM raw_events
            WHERE json_extract(json, '$.kind') IN ('ServiceDegraded', 'NightClosed')
            ORDER BY rowid;
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        List<string> kinds = [];
        while (await reader.ReadAsync())
        {
            kinds.Add(reader.GetString(0));
        }

        return [.. kinds];
    }

    private sealed class ObservationClock(ClockObservation observation) : IClock
    {
        public DateTimeOffset UtcNow => observation.UtcNow;

        public ClockObservation Observe() => observation;
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

    private sealed class FixedConfiguredSiteRuleProvider(ConfiguredSiteRuleProviderResult result) :
        IConfiguredSiteRuleProvider
    {
        public ConfiguredSiteRuleProviderResult GetRules() => result;
    }

    private sealed class ConflictOnceNightStateRepository(INightStateRepository inner) :
        INightStateRepository
    {
        private int _conflictsRemaining = 1;

        public int SaveCalls { get; private set; }

        public ValueTask<StorageResult<NightState?>> ReadActiveStateAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadActiveStateAsync(cancellationToken);

        public ValueTask<StorageWriteResult> SaveActiveStateWithEventAsync(
            NightState state,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (Interlocked.Exchange(ref _conflictsRemaining, 0) == 1)
            {
                return ValueTask.FromResult(StorageWriteResult.Conflict);
            }

            return inner.SaveActiveStateWithEventAsync(
                state,
                nightEvent,
                expectedVersion,
                cancellationToken);
        }

        public ValueTask<StorageWriteResult> SaveActiveStateProgressWithEventAsync(
            NightState state,
            ProgressState progress,
            NightEvent nightEvent,
            long? expectedStateVersion = null,
            long? expectedProgressVersion = null,
            CancellationToken cancellationToken = default) =>
            inner.SaveActiveStateProgressWithEventAsync(
                state,
                progress,
                nightEvent,
                expectedStateVersion,
                expectedProgressVersion,
                cancellationToken);

        public ValueTask<StorageWriteResult> CloseActiveStateWithOutcomeAndEventAsync(
            NightState closedState,
            NightOutcome outcome,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            inner.CloseActiveStateWithOutcomeAndEventAsync(
                closedState,
                outcome,
                nightEvent,
                expectedVersion,
                cancellationToken);
    }

    private sealed class BlockingPurgeHistoryRepository(IHistoryRepository inner) :
        IHistoryRepository
    {
        private readonly TaskCompletionSource _allowPurge = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource PurgeReached { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void AllowPurge() => _allowPurge.TrySetResult();

        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestOutcomesAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            inner.ReadLatestOutcomesAsync(count, cancellationToken);

        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>>
            ReadLatestEligibleOutcomesAsync(
                int count,
                CancellationToken cancellationToken = default) =>
            inner.ReadLatestEligibleOutcomesAsync(count, cancellationToken);

        public ValueTask<StorageWriteResult> SaveOutcomeAsync(
            NightOutcome outcome,
            CancellationToken cancellationToken = default) =>
            inner.SaveOutcomeAsync(outcome, cancellationToken);

        public ValueTask<StorageWriteResult> RecordEventAsync(
            NightEvent nightEvent,
            CancellationToken cancellationToken = default) =>
            inner.RecordEventAsync(nightEvent, cancellationToken);

        public async ValueTask<StorageWriteResult> PurgeEventsOlderThanAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken = default)
        {
            PurgeReached.TrySetResult();
            await _allowPurge.Task.WaitAsync(cancellationToken);
            return await inner.PurgeEventsOlderThanAsync(cutoffUtc, cancellationToken);
        }

        public ValueTask<StorageWriteResult> ClearHistoryAsync(
            CancellationToken cancellationToken = default) =>
            inner.ClearHistoryAsync(cancellationToken);
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
