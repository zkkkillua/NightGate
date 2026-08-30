using System.Collections.Immutable;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class RuleSettingsNoticeCommandTests
{
    [Fact]
    public async Task SaveRules_At222959AppliesImmediatelyUsingOneServiceObservation()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        CountingObservationClock clock = new(new(2026, 7, 14, 22, 29, 59, TimeSpan.Zero));
        AppRule appRule = GameRule();

        ProtocolCommandResult result = await Handler(repository, clock).ExecuteAsync(
            new SaveRuleSettingsCommand([appRule], [new("youtube.com")]));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True(result.Payload.GetProperty("saved").GetBoolean());
        Assert.True(result.Payload.GetProperty("appliesImmediately").GetBoolean());
        Assert.True(result.Payload.GetProperty("appliesTonight").GetBoolean());
        Assert.False(result.Payload.TryGetProperty("effectiveNight", out _));
        RuleSettingsState stored = (await repository.ReadRuleSettingsAsync()).Value;
        Assert.Equal([appRule], stored.ActiveAppRules.ToArray());
        Assert.Equal([new SiteRule("youtube.com")], stored.ActiveSiteRules.ToArray());
        Assert.Null(stored.PendingEffectiveNightDate);
        Assert.Equal(1, clock.ObservationCalls);
    }

    [Fact]
    public async Task SaveRules_At223000DefersToNextNight()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        DateTimeOffset now = new(2026, 7, 14, 22, 30, 0, TimeSpan.Zero);

        ProtocolCommandResult result = await Handler(
                repository,
                new CountingObservationClock(now))
            .ExecuteAsync(new SaveRuleSettingsCommand([GameRule()], [new("youtube.com")]));

        Assert.True(result.Payload.GetProperty("saved").GetBoolean());
        Assert.False(result.Payload.GetProperty("appliesImmediately").GetBoolean());
        Assert.False(result.Payload.GetProperty("appliesTonight").GetBoolean());
        Assert.Equal(
            "2026-07-15",
            result.Payload.GetProperty("effectiveNight").GetString());
        RuleSettingsState stored = (await repository.ReadRuleSettingsAsync()).Value;
        Assert.Empty(stored.ActiveAppRules);
        Assert.Equal(new DateOnly(2026, 7, 15), stored.PendingEffectiveNightDate);
        Assert.Equal(now, stored.PendingSavedAtUtc);
    }

    [Fact]
    public async Task SaveRules_ImmediateRefreshesPolicyAfterReleasingMutationLease()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        DateTimeOffset now = new(2026, 7, 14, 22, 29, 59, TimeSpan.Zero);
        FixedServiceClock clock = new(now);
        InMemoryServiceStatus status = new();
        NightWindow window = ScheduleEvaluator.CreateWindow(
            new DateOnly(2026, 7, 14),
            ScheduleProfile.Default.Steps[0],
            TimeZoneInfo.Utc);
        await status.PublishAsync(new(
            true,
            false,
            null,
            new(now, NightPhase.Free, window, [], [])));
        TrackingMutationGate gate = new();
        RulePublishingScheduler scheduler = new(repository, status, status, gate, now);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            gate,
            clock,
            timeZoneProvider: new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            ruleSettingsRepository: repository,
            policyMaintenanceScheduler: scheduler);
        AppRule replacement = GameRule("immediate");

        ProtocolCommandResult save = await handler.ExecuteAsync(
            new SaveRuleSettingsCommand([replacement], []));
        ProtocolCommandResult policy = await handler.ExecuteAsync(new GetPolicyCommand());

        Assert.True(save.Payload.GetProperty("appliesImmediately").GetBoolean());
        Assert.Equal(1, scheduler.DirtyMarks);
        Assert.Equal(1, scheduler.ForceRefreshes);
        Assert.Equal(
            "immediate",
            Assert.Single(policy.Payload.GetProperty("policy")
                    .GetProperty("appRules")
                    .EnumerateArray())
                .GetProperty("id")
                .GetString());
    }

    [Fact]
    public async Task SaveRules_PendingDoesNotForcePolicyRefresh()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        DateTimeOffset now = new(2026, 7, 14, 22, 30, 0, TimeSpan.Zero);
        FixedServiceClock clock = new(now);
        InMemoryServiceStatus status = new();
        NightWindow window = ScheduleEvaluator.CreateWindow(
            new DateOnly(2026, 7, 14),
            ScheduleProfile.Default.Steps[0],
            TimeZoneInfo.Utc);
        await status.PublishAsync(new(
            true,
            false,
            null,
            new(now, NightPhase.Free, window, [], [])));
        TrackingMutationGate gate = new();
        RulePublishingScheduler scheduler = new(repository, status, status, gate, now);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            gate,
            clock,
            timeZoneProvider: new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            ruleSettingsRepository: repository,
            policyMaintenanceScheduler: scheduler);

        ProtocolCommandResult save = await handler.ExecuteAsync(
            new SaveRuleSettingsCommand([GameRule("pending")], []));

        Assert.False(save.Payload.GetProperty("appliesImmediately").GetBoolean());
        Assert.Equal(0, scheduler.DirtyMarks);
        Assert.Equal(0, scheduler.ForceRefreshes);
    }

    [Fact]
    public async Task SaveRules_ChangedSystemTimeZoneCannotReopenPinnedNightsEditingWindow()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        TimeZoneInfo pinnedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Rule-Pinned-UTC+8",
            TimeSpan.FromHours(8),
            "NightGate Rule Pinned UTC+8",
            "NightGate Rule Pinned UTC+8");
        DateTimeOffset nightStartedAt = new(2026, 7, 14, 13, 1, 0, TimeSpan.Zero);
        NightState activeNight = new(
            Guid.NewGuid(),
            new DateOnly(2026, 7, 14),
            nightStartedAt,
            NightPhase.Free,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            ScheduledLockAtUtc: new(2026, 7, 14, 16, 40, 0, TimeSpan.Zero),
            ScheduleTimeZoneSerialized: NightScheduleTimeZone.Capture(pinnedTimeZone));
        await repository.SaveActiveStateWithEventAsync(
            activeNight,
            new(
                Guid.NewGuid(),
                activeNight.NightId,
                nightStartedAt,
                NightEventKind.NightStarted,
                NightPhase.Free));
        AppRule existing = GameRule("existing");
        await repository.SaveRuleSettingsAsync(new([existing], []));
        DateTimeOffset local2300ButUtc1500 = new(
            2026,
            7,
            14,
            15,
            0,
            0,
            TimeSpan.Zero);

        ProtocolCommandResult result = await Handler(
                repository,
                new CountingObservationClock(local2300ButUtc1500),
                timeZone: TimeZoneInfo.Utc)
            .ExecuteAsync(new SaveRuleSettingsCommand([GameRule("next")], []));

        Assert.False(result.Payload.GetProperty("appliesImmediately").GetBoolean());
        Assert.False(result.Payload.GetProperty("appliesTonight").GetBoolean());
        Assert.Equal(
            "2026-07-15",
            result.Payload.GetProperty("effectiveNight").GetString());
        RuleSettingsState stored = (await repository.ReadRuleSettingsAsync()).Value;
        Assert.Equal("existing", Assert.Single(stored.ActiveAppRules).Id);
        Assert.Equal("next", Assert.Single(stored.PendingAppRules!.Value).Id);
        Assert.Equal(new DateOnly(2026, 7, 15), stored.PendingEffectiveNightDate);
    }

    [Fact]
    public async Task SaveRules_AfterMidnightDoesNotChangeTheOpenPriorNight()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        AppRule active = GameRule("active");
        await repository.SaveRuleSettingsAsync(new([active], [new("bilibili.com")]));

        ProtocolCommandResult result = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 15, 0, 10, 0, TimeSpan.Zero)))
            .ExecuteAsync(new SaveRuleSettingsCommand([GameRule("next")], [new("youtube.com")]));

        Assert.False(result.Payload.GetProperty("appliesImmediately").GetBoolean());
        Assert.True(result.Payload.GetProperty("appliesTonight").GetBoolean());
        Assert.Equal(
            "2026-07-15",
            result.Payload.GetProperty("effectiveNight").GetString());
        RuleSettingsState stored = (await repository.ReadRuleSettingsAsync()).Value;
        Assert.Equal("active", Assert.Single(stored.ActiveAppRules).Id);
        Assert.Equal("next", Assert.Single(stored.PendingAppRules!.Value).Id);
    }

    [Fact]
    public async Task SaveRules_Before2100StagesForTonightWithoutApplyingImmediately()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);

        ProtocolCommandResult result = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 14, 20, 59, 59, TimeSpan.Zero)))
            .ExecuteAsync(new SaveRuleSettingsCommand([GameRule()], []));

        Assert.True(result.Payload.GetProperty("saved").GetBoolean());
        Assert.False(result.Payload.GetProperty("appliesImmediately").GetBoolean());
        Assert.True(result.Payload.GetProperty("appliesTonight").GetBoolean());
        Assert.Equal(
            "2026-07-14",
            result.Payload.GetProperty("effectiveNight").GetString());
        RuleSettingsState stored = (await repository.ReadRuleSettingsAsync()).Value;
        Assert.Empty(stored.ActiveAppRules);
        Assert.Equal(new DateOnly(2026, 7, 14), stored.PendingEffectiveNightDate);
    }

    [Fact]
    public async Task SaveRules_WallClockRollbackCannotReopenTheLiveEditingWindow()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Guid bootSession = Guid.Parse("72727272-7272-7272-7272-727272727272");
        DateTimeOffset lastLogicalTime = new(2026, 7, 14, 22, 31, 0, TimeSpan.Zero);
        NightState state = new(
            Guid.Parse("71717171-7171-7171-7171-717171717171"),
            new DateOnly(2026, 7, 14),
            lastLogicalTime,
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
                lastLogicalTime,
                NightEventKind.StateObserved,
                NightPhase.Free));
        CountingObservationClock rolledBackClock = new(
            new(2026, 7, 14, 22, 0, 0, TimeSpan.Zero),
            TimeSpan.FromHours(100).Add(TimeSpan.FromMinutes(1)),
            bootSession);

        ProtocolCommandResult result = await Handler(repository, rolledBackClock)
            .ExecuteAsync(new SaveRuleSettingsCommand([GameRule()], []));

        Assert.False(result.Payload.GetProperty("appliesImmediately").GetBoolean());
        Assert.False(result.Payload.GetProperty("appliesTonight").GetBoolean());
        Assert.Equal(
            "2026-07-15",
            result.Payload.GetProperty("effectiveNight").GetString());
        Assert.Equal(1, rolledBackClock.ObservationCalls);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 14, 22, 32, 0, TimeSpan.Zero),
            (await repository.ReadActiveStateAsync()).Value!.LastObservedUtc);
    }

    [Fact]
    public async Task SaveRules_CasConflictRetriesWithoutReobservingTime()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        ConflictOnceRuleRepository conflicting = new(repository);
        CountingObservationClock clock = new(new(2026, 7, 14, 22, 0, 0, TimeSpan.Zero));

        ProtocolCommandResult result = await Handler(
                repository,
                clock,
                ruleRepository: conflicting)
            .ExecuteAsync(new SaveRuleSettingsCommand([GameRule()], []));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal(2, conflicting.SaveCalls);
        Assert.Equal(1, clock.ObservationCalls);
    }

    [Fact]
    public async Task SaveRules_ImmediatePersistedRulesDoNotPretendTheGameIsCurrentlyRunning()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        PersistedActiveRuleSnapshot snapshot = new();
        AppRule savedRule = GameRule("saved-game");

        ProtocolCommandResult saved = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 14, 22, 0, 0, TimeSpan.Zero)),
                activeRuleSnapshotPublisher: snapshot)
            .ExecuteAsync(new SaveRuleSettingsCommand([savedRule], []));
        OverrideDecision rescue = new OverridePolicy(snapshot).Request(
            ActiveNight(new(2026, 7, 14)),
            ProgressState.Initial,
            new(OverrideKind.TeamRescue, null),
            new(2026, 7, 14, 22, 1, 0, TimeSpan.Zero));

        Assert.True(saved.Payload.GetProperty("saved").GetBoolean());
        Assert.False(rescue.Accepted);
        Assert.Equal(OverrideError.TeamRescueUnavailable, rescue.Error);
        Assert.Null(rescue.State.ActiveOverride);
        Assert.Null(rescue.Progress.LastTeamRescueAtUtc);
    }

    [Fact]
    public async Task SaveRules_StorageFailureReturnsOnlyDegradedResult()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        InMemoryServiceStatus status = new();

        ProtocolCommandResult result = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 14, 22, 0, 0, TimeSpan.Zero)),
                status,
                new DegradedRuleRepository())
            .ExecuteAsync(new SaveRuleSettingsCommand([GameRule()], []));

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.Payload.TryGetProperty("saved", out _));
        Assert.True(status.Current.IsDegraded);
    }

    [Fact]
    public async Task ClaimDueNotice_IsAtomicAndSurvivesRepositoryRestart()
    {
        using TempDatabase database = new();
        DateTimeOffset now = new(2026, 7, 14, 23, 35, 0, TimeSpan.Zero);
        SqliteNightGateRepository firstRepository = new(database.Path);
        await firstRepository.SaveProgressAsync(ProgressState.Initial);

        ProtocolCommandResult first = await Handler(
                firstRepository,
                new CountingObservationClock(now),
                noticeRepository: firstRepository)
            .ExecuteAsync(new ClaimDueNoticeCommand());

        SqliteNightGateRepository restartedRepository = new(database.Path);
        ProtocolCommandResult repeated = await Handler(
                restartedRepository,
                new CountingObservationClock(now.AddSeconds(1)),
                noticeRepository: restartedRepository)
            .ExecuteAsync(new ClaimDueNoticeCommand());

        Assert.True(first.Payload.GetProperty("claimed").GetBoolean());
        Assert.Equal("ifThenPlan", first.Payload.GetProperty("kind").GetString());
        Assert.Equal("2026-07-14", first.Payload.GetProperty("nightDate").GetString());
        Assert.False(repeated.Payload.GetProperty("claimed").GetBoolean());
        Assert.False(repeated.Payload.TryGetProperty("kind", out _));
    }

    [Fact]
    public async Task ClaimDueNotice_UsesLongestActiveGameAndClaimsLastStartOnlyOnce()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        await repository.SaveRuleSettingsAsync(new(
            [GameRule("short", 15), GameRule("default", 35), GameRule("long", 90)],
            []));
        DateTimeOffset earliestCutoff = new(2026, 7, 14, 23, 10, 0, TimeSpan.Zero);

        ProtocolCommandResult first = await Handler(
                repository,
                new CountingObservationClock(earliestCutoff),
                noticeRepository: repository)
            .ExecuteAsync(new ClaimDueNoticeCommand());
        ProtocolCommandResult repeated = await Handler(
                repository,
                new CountingObservationClock(earliestCutoff.AddSeconds(1)),
                noticeRepository: repository)
            .ExecuteAsync(new ClaimDueNoticeCommand());
        ProtocolCommandResult atDefaultCutoff = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 15, 0, 5, 0, TimeSpan.Zero)),
                noticeRepository: repository)
            .ExecuteAsync(new ClaimDueNoticeCommand());

        Assert.True(first.Payload.GetProperty("claimed").GetBoolean());
        Assert.Equal("lastStart", first.Payload.GetProperty("kind").GetString());
        Assert.False(repeated.Payload.GetProperty("claimed").GetBoolean());
        Assert.False(atDefaultCutoff.Payload.GetProperty("claimed").GetBoolean());
    }

    [Fact]
    public async Task ClaimDueNotice_LateLastStartLeavesFinalTenMinutesToThePersistentCountdown()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        await repository.SaveRuleSettingsAsync(new([GameRule("long", 90)], []));

        ProtocolCommandResult lateLastStart = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 14, 23, 12, 0, TimeSpan.Zero)),
                noticeRepository: repository)
            .ExecuteAsync(new ClaimDueNoticeCommand());
        ProtocolCommandResult finalTen = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 15, 0, 30, 0, TimeSpan.Zero)),
                noticeRepository: repository)
            .ExecuteAsync(new ClaimDueNoticeCommand());
        ProtocolCommandResult finalTwo = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 15, 0, 38, 0, TimeSpan.Zero)),
                noticeRepository: repository)
            .ExecuteAsync(new ClaimDueNoticeCommand());

        Assert.Equal("lastStart", ClaimedKind(lateLastStart));
        Assert.False(finalTen.Payload.GetProperty("claimed").GetBoolean());
        Assert.False(finalTen.Payload.TryGetProperty("kind", out _));
        Assert.False(finalTwo.Payload.GetProperty("claimed").GetBoolean());
        Assert.False(finalTwo.Payload.TryGetProperty("kind", out _));
    }

    [Fact]
    public async Task ClaimDueNotice_DoesNotUseRulesPendingForTheNextNight()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        await repository.SaveRuleSettingsAsync(new(
            [GameRule("active-short", 15)],
            [],
            [GameRule("tomorrow-long", 90)],
            [],
            new DateOnly(2026, 7, 15),
            new DateTimeOffset(2026, 7, 14, 22, 30, 0, TimeSpan.Zero)));

        ProtocolCommandResult beforeActivePlan = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 14, 23, 10, 0, TimeSpan.Zero)),
                noticeRepository: repository)
            .ExecuteAsync(new ClaimDueNoticeCommand());
        ProtocolCommandResult activePlan = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 14, 23, 35, 0, TimeSpan.Zero)),
                noticeRepository: repository)
            .ExecuteAsync(new ClaimDueNoticeCommand());

        Assert.False(beforeActivePlan.Payload.GetProperty("claimed").GetBoolean());
        Assert.True(activePlan.Payload.GetProperty("claimed").GetBoolean());
        Assert.Equal("ifThenPlan", activePlan.Payload.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ClaimDueNotice_AppliesWeekendOffsetBeforeGameDuration()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        await repository.SaveRuleSettingsAsync(new([GameRule("weekend-long", 90)], []));

        ProtocolCommandResult result = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 19, 0, 10, 0, TimeSpan.Zero)),
                noticeRepository: repository)
            .ExecuteAsync(new ClaimDueNoticeCommand());

        Assert.True(result.Payload.GetProperty("claimed").GetBoolean());
        Assert.Equal("lastStart", result.Payload.GetProperty("kind").GetString());
        Assert.Equal("2026-07-18", result.Payload.GetProperty("nightDate").GetString());
    }

    [Fact]
    public async Task ClaimDueNotice_WhenNothingIsDueDoesNotTouchClaimStorage()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        RecordingNoticeRepository notices = new();

        ProtocolCommandResult result = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 14, 20, 0, 0, TimeSpan.Zero)),
                noticeRepository: notices)
            .ExecuteAsync(new ClaimDueNoticeCommand());

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.False(result.Payload.GetProperty("claimed").GetBoolean());
        Assert.Equal(0, notices.CallCount);
    }

    [Fact]
    public async Task ClaimDueNotice_ObservesProgressAndNightStateInsideOneMutationLease()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        TrackingMutationGate gate = new();
        GateCheckingProgressRepository progress = new(repository, gate);
        InMemoryServiceStatus status = new();
        NightGateProtocolCommandHandler handler = new(
            repository,
            progress,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            gate,
            new CountingObservationClock(
                new(2026, 7, 14, 23, 35, 0, TimeSpan.Zero)),
            timeZoneProvider: new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            noticeClaimRepository: repository);

        ProtocolCommandResult result = await handler.ExecuteAsync(new ClaimDueNoticeCommand());

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True(result.Payload.GetProperty("claimed").GetBoolean());
        Assert.Equal(1, gate.EnterCalls);
    }

    [Fact]
    public async Task ClaimDueNotice_ClaimStorageFailureDegradesFailOpen()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();

        ProtocolCommandResult result = await Handler(
                repository,
                new CountingObservationClock(
                    new(2026, 7, 14, 23, 35, 0, TimeSpan.Zero)),
                status,
                noticeRepository: new DegradedNoticeRepository())
            .ExecuteAsync(new ClaimDueNoticeCommand());

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.Payload.TryGetProperty("claimed", out _));
        Assert.True(status.Current.IsDegraded);
        Assert.False(status.Current.EnforcementEnabled);
    }

    private static NightGateProtocolCommandHandler Handler(
        SqliteNightGateRepository repository,
        IClock clock,
        InMemoryServiceStatus? status = null,
        IRuleSettingsRepository? ruleRepository = null,
        INoticeClaimRepository? noticeRepository = null,
        IActiveRuleSnapshotPublisher? activeRuleSnapshotPublisher = null,
        TimeZoneInfo? timeZone = null)
    {
        InMemoryServiceStatus sharedStatus = status ?? new();
        return new(
            repository,
            repository,
            repository,
            sharedStatus,
            sharedStatus,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock,
            timeZoneProvider: new FixedTimeZoneProvider(timeZone ?? TimeZoneInfo.Utc),
            ruleSettingsRepository: ruleRepository ?? repository,
            noticeClaimRepository: noticeRepository,
            activeRuleSnapshotPublisher: activeRuleSnapshotPublisher);
    }

    private static string ClaimedKind(ProtocolCommandResult result)
    {
        Assert.True(result.Payload.GetProperty("claimed").GetBoolean());
        return result.Payload.GetProperty("kind").GetString()!;
    }

    private static NightState ActiveNight(DateOnly nightDate) => new(
        Guid.NewGuid(),
        nightDate,
        new DateTimeOffset(nightDate.ToDateTime(new TimeOnly(21, 0)), TimeSpan.Zero),
        NightPhase.Free,
        null,
        false,
        false,
        false,
        false,
        false,
        false);

    private static AppRule GameRule(string id = "game", int sessionMinutes = 35) => new(
        id,
        Path.Combine(Path.GetTempPath(), "NightGate", $"{id}.exe"),
        [],
        AppRuleCategory.Game,
        sessionMinutes);

    private sealed class EmptyAllowedProcesses : IAllowedProcessSnapshotProvider
    {
        public ImmutableArray<string> GetSnapshot() => [];
    }

    private sealed class FixedTimeZoneProvider(TimeZoneInfo local) : ITimeZoneProvider
    {
        public TimeZoneInfo Local { get; } = local;
    }

    private sealed class CountingObservationClock(
        DateTimeOffset now,
        TimeSpan? uptime = null,
        Guid? bootSessionId = null) : IClock
    {
        public int ObservationCalls { get; private set; }

        public DateTimeOffset UtcNow =>
            throw new InvalidOperationException("Use the single clock observation.");

        public ClockObservation Observe()
        {
            ObservationCalls++;
            return new(now, uptime, bootSessionId);
        }
    }

    private sealed class FixedServiceClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;

        public ClockObservation Observe() => new(UtcNow, TimeSpan.FromHours(100), Guid.NewGuid());
    }

    private sealed class RulePublishingScheduler(
        IRuleSettingsRepository rules,
        IServiceStatusReader statusReader,
        IServiceStatusPublisher statusPublisher,
        TrackingMutationGate mutationGate,
        DateTimeOffset evaluatedAt) : IPolicyMaintenanceScheduler
    {
        public int ForceRefreshes { get; private set; }

        public int DirtyMarks { get; private set; }

        public void MarkDirty() => DirtyMarks++;

        public async ValueTask RefreshAsync(
            bool force,
            CancellationToken cancellationToken = default)
        {
            if (!force)
            {
                return;
            }

            Assert.False(mutationGate.IsHeld);
            ForceRefreshes++;
            StorageResult<RuleSettingsState> stored = await rules
                .ReadRuleSettingsAsync(cancellationToken);
            ServiceRuntimeStatus current = statusReader.Current;
            PolicySnapshot policy = current.Policy
                ?? throw new InvalidOperationException("A cached policy is required.");
            await statusPublisher.PublishAsync(
                current with
                {
                    Policy = policy with
                    {
                        EvaluatedAt = evaluatedAt,
                        AppRules = stored.Value.ActiveAppRules,
                        SiteRules = stored.Value.ActiveSiteRules,
                    },
                },
                cancellationToken);
        }
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

    private sealed class RecordingNoticeRepository : INoticeClaimRepository
    {
        public int CallCount { get; private set; }

        public ValueTask<StorageResult<bool>> TryClaimNoticeAsync(
            NoticeClaim claim,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(new StorageResult<bool>(StorageMode.Success, true));
        }

        public ValueTask<StorageWriteResult> PurgeNoticeClaimsOlderThanAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);
    }

    private sealed class DegradedNoticeRepository : INoticeClaimRepository
    {
        public ValueTask<StorageResult<bool>> TryClaimNoticeAsync(
            NoticeClaim claim,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new StorageResult<bool>(StorageMode.Degraded, false, "notice-unavailable"));

        public ValueTask<StorageWriteResult> PurgeNoticeClaimsOlderThanAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StorageWriteResult(StorageMode.Degraded));
    }

    private sealed class GateCheckingProgressRepository(
        IProgressRepository inner,
        TrackingMutationGate gate) : IProgressRepository
    {
        public ValueTask<StorageResult<ProgressState>> ReadProgressAsync(
            CancellationToken cancellationToken = default)
        {
            if (!gate.IsHeld)
            {
                throw new InvalidOperationException("Progress must share the mutation lease.");
            }

            return inner.ReadProgressAsync(cancellationToken);
        }

        public ValueTask<StorageWriteResult> SaveProgressAsync(
            ProgressState progress,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            inner.SaveProgressAsync(progress, expectedVersion, cancellationToken);
    }

    private sealed class TrackingMutationGate : INightMutationGate
    {
        public bool IsHeld { get; private set; }

        public int EnterCalls { get; private set; }

        public ValueTask<IDisposable> EnterAsync(
            CancellationToken cancellationToken = default)
        {
            Assert.False(IsHeld);
            IsHeld = true;
            EnterCalls++;
            return ValueTask.FromResult<IDisposable>(new Lease(this));
        }

        private sealed class Lease(TrackingMutationGate owner) : IDisposable
        {
            public void Dispose() => owner.IsHeld = false;
        }
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
