using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class PersistedRulePolicyMaintenanceTests
{
    [Fact]
    public async Task Execute_PublishesPersistedActiveRulesWithoutConsultingLegacyProviders()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        AppRule active = GameRule("active");
        await repository.SaveRuleSettingsAsync(
            new([active], [new("youtube.com")]));
        InMemoryServiceStatus status = new();
        RecordingActiveRuleSnapshotPublisher snapshot = new();
        PolicyMaintenanceIteration iteration = Iteration(
            repository,
            repository,
            new FixedClock(new(2026, 7, 14, 22, 0, 0, TimeSpan.Zero)),
            status,
            snapshot);

        await iteration.ExecuteAsync();

        Assert.Equal([active], status.Current.Policy!.AppRules.ToArray());
        Assert.Equal(
            [new SiteRule("youtube.com")],
            status.Current.Policy.SiteRules.ToArray());
        Assert.Equal([active], snapshot.Published);
    }

    [Fact]
    public async Task Execute_AtomicallyActivatesPendingRulesForLogicalNightBeforePublication()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        AppRule oldRule = GameRule("old");
        AppRule nextRule = GameRule("next");
        await repository.SaveRuleSettingsAsync(new(
            [oldRule],
            [new("bilibili.com")],
            [nextRule],
            [new("youtube.com")],
            new DateOnly(2026, 7, 14),
            new(2026, 7, 14, 14, 30, 0, TimeSpan.Zero)));
        ConflictOnceRuleRepository rules = new(repository);
        InMemoryServiceStatus status = new();

        await Iteration(
                repository,
                rules,
                new FixedClock(new(2026, 7, 14, 22, 0, 0, TimeSpan.Zero)),
                status)
            .ExecuteAsync();

        RuleSettingsState stored = (await repository.ReadRuleSettingsAsync()).Value;
        Assert.Equal("next", Assert.Single(stored.ActiveAppRules).Id);
        Assert.Null(stored.PendingEffectiveNightDate);
        Assert.Equal([nextRule], status.Current.Policy!.AppRules.ToArray());
        Assert.Equal(2, rules.SaveCalls);
    }

    [Fact]
    public async Task Execute_AfterMidnightUsesOpenLogicalNightAndKeepsFutureRulesPending()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        AppRule active = GameRule("active");
        AppRule next = GameRule("next");
        await repository.SaveRuleSettingsAsync(new(
            [active],
            [],
            [next],
            [new("youtube.com")],
            new DateOnly(2026, 7, 15),
            new(2026, 7, 14, 22, 30, 0, TimeSpan.Zero)));
        InMemoryServiceStatus status = new();

        await Iteration(
                repository,
                repository,
                new FixedClock(new(2026, 7, 15, 0, 10, 0, TimeSpan.Zero)),
                status)
            .ExecuteAsync();

        RuleSettingsState stored = (await repository.ReadRuleSettingsAsync()).Value;
        Assert.Equal(new DateOnly(2026, 7, 15), stored.PendingEffectiveNightDate);
        Assert.Equal([active], status.Current.Policy!.AppRules.ToArray());
        Assert.Equal(new DateOnly(2026, 7, 14), status.Current.Policy.Window.NightDate);
    }

    [Fact]
    public async Task Execute_DegradedPersistedRulesTakesFailOpenFailurePath()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();
        PolicyMaintenanceIteration iteration = Iteration(
            repository,
            new DegradedRuleRepository(),
            new FixedClock(new(2026, 7, 14, 22, 0, 0, TimeSpan.Zero)),
            status);

        await Assert.ThrowsAsync<IOException>(async () => await iteration.ExecuteAsync());

        Assert.True(status.Current.IsDegraded);
        Assert.False(status.Current.EnforcementEnabled);
        Assert.Null(status.Current.Policy);
    }

    [Fact]
    public async Task Execute_PurgesNoticeClaimsUsingLogicalServiceTimeAfterWallClockRollback()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        Guid bootSession = Guid.Parse("83838383-8383-8383-8383-838383838383");
        DateTimeOffset persistedLogicalTime =
            new(2026, 7, 15, 22, 0, 0, TimeSpan.Zero);
        NightState state = new(
            Guid.Parse("82828282-8282-8282-8282-828282828282"),
            new DateOnly(2026, 7, 15),
            persistedLogicalTime,
            NightPhase.Free,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            LastObservedUptime: TimeSpan.FromHours(100),
            LastObservedBootSessionId: bootSession);
        await repository.SaveActiveStateWithEventAsync(
            state,
            new(
                Guid.NewGuid(),
                state.NightId,
                persistedLogicalTime,
                NightEventKind.StateObserved,
                NightPhase.Free));
        DateTimeOffset logicalNow = persistedLogicalTime.AddMinutes(1);
        DateTimeOffset cutoff = logicalNow.AddDays(-90);
        DateOnly oldNight = new(2026, 4, 16);
        DateOnly boundaryNight = new(2026, 4, 17);
        await repository.TryClaimNoticeAsync(
            new(oldNight, NightNoticeKind.LastStart, cutoff.AddTicks(-1)));
        await repository.TryClaimNoticeAsync(
            new(boundaryNight, NightNoticeKind.LastStart, cutoff));
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedObservationClock(new(
                new(2026, 7, 14, 22, 0, 0, TimeSpan.Zero),
                TimeSpan.FromHours(100).Add(TimeSpan.FromMinutes(1)),
                bootSession)),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            new InMemoryServiceStatus(),
            new ThrowingConfiguredRuleProvider(),
            new ThrowingConfiguredSiteRuleProvider(),
            ruleSettingsRepository: repository,
            noticeClaimRepository: repository);

        await iteration.ExecuteAsync();

        Assert.True((await repository.TryClaimNoticeAsync(
            new(oldNight, NightNoticeKind.LastStart, logicalNow))).Value);
        Assert.False((await repository.TryClaimNoticeAsync(
            new(boundaryNight, NightNoticeKind.LastStart, logicalNow))).Value);
    }

    private static PolicyMaintenanceIteration Iteration(
        SqliteNightGateRepository repository,
        IRuleSettingsRepository ruleRepository,
        IClock clock,
        InMemoryServiceStatus status,
        IActiveRuleSnapshotPublisher? activeRuleSnapshotPublisher = null) => new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            clock,
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            new ThrowingConfiguredRuleProvider(),
            new ThrowingConfiguredSiteRuleProvider(),
            ruleSettingsRepository: ruleRepository,
            activeRuleSnapshotPublisher: activeRuleSnapshotPublisher);

    private static AppRule GameRule(string id) => new(
        id,
        Path.Combine(Path.GetTempPath(), "NightGate", $"{id}.exe"),
        [],
        AppRuleCategory.Game,
        35);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class RecordingActiveRuleSnapshotPublisher :
        IActiveRuleSnapshotPublisher
    {
        public AppRule[] Published { get; private set; } = [];

        public void Publish(
            System.Collections.Immutable.ImmutableArray<AppRule> activeAppRules) =>
            Published = activeAppRules.ToArray();
    }

    private sealed class FixedObservationClock(ClockObservation observation) : IClock
    {
        public DateTimeOffset UtcNow => observation.UtcNow;

        public ClockObservation Observe() => observation;
    }

    private sealed class FixedTimeZoneProvider(TimeZoneInfo local) : ITimeZoneProvider
    {
        public TimeZoneInfo Local { get; } = local;
    }

    private sealed class ThrowingConfiguredRuleProvider : IConfiguredRuleProvider
    {
        public ConfiguredRuleProviderResult GetRules() =>
            throw new InvalidOperationException("Persisted rules must be authoritative.");
    }

    private sealed class ThrowingConfiguredSiteRuleProvider : IConfiguredSiteRuleProvider
    {
        public ConfiguredSiteRuleProviderResult GetRules() =>
            throw new InvalidOperationException("Persisted rules must be authoritative.");
    }

    private sealed class ConflictOnceRuleRepository(IRuleSettingsRepository inner) :
        IRuleSettingsRepository
    {
        private int _conflicts = 1;

        public int SaveCalls { get; private set; }

        public ValueTask<StorageResult<RuleSettingsState>> ReadRuleSettingsAsync(
            CancellationToken cancellationToken = default) =>
            inner.ReadRuleSettingsAsync(cancellationToken);

        public ValueTask<StorageWriteResult> SaveRuleSettingsAsync(
            RuleSettingsState state,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (Interlocked.Exchange(ref _conflicts, 0) == 1)
            {
                return ValueTask.FromResult(StorageWriteResult.Conflict);
            }

            return inner.SaveRuleSettingsAsync(state, expectedVersion, cancellationToken);
        }
    }

    private sealed class DegradedRuleRepository : IRuleSettingsRepository
    {
        public ValueTask<StorageResult<RuleSettingsState>> ReadRuleSettingsAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new StorageResult<RuleSettingsState>(
                StorageMode.Degraded,
                RuleSettingsState.Initial,
                "rules-unavailable"));

        public ValueTask<StorageWriteResult> SaveRuleSettingsAsync(
            RuleSettingsState state,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
