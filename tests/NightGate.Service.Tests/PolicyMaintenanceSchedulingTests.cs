using System.Collections.Immutable;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class PolicyMaintenanceSchedulingTests
{
    private static readonly Guid BootSessionId =
        Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void NextDelay_WakesAtLockBoundaryInsteadOfFixedThirtySecondCadence()
    {
        NightWindow window = Window();
        DateTimeOffset evaluatedAt = window.Lock.ToUniversalTime().AddMilliseconds(-250);
        ServiceRuntimeStatus status = Healthy(new(
            evaluatedAt,
            NightPhase.Grace,
            window,
            [],
            []));

        TimeSpan delay = PolicyMaintenanceTiming.GetNextDelay(status, evaluatedAt);

        Assert.Equal(TimeSpan.FromMilliseconds(250), delay);
    }

    [Fact]
    public void NextDelay_IncludesPerGameCutoffAndOverrideStartAndEnd()
    {
        NightWindow window = Window();
        AppRule longGame = new(
            "long-game",
            @"C:\Games\Long\game.exe",
            [],
            AppRuleCategory.Game,
            90);
        DateTimeOffset gameCutoff = window.Lock.ToUniversalTime().AddMinutes(-90);
        ActiveOverride activeOverride = new(
            OverrideKind.Entertainment,
            gameCutoff.AddMinutes(-15),
            gameCutoff.AddSeconds(3),
            gameCutoff.AddSeconds(3).AddMinutes(20),
            []);
        PolicySnapshot policy = new(
            gameCutoff.AddMilliseconds(-400),
            NightPhase.Free,
            window,
            [longGame],
            [],
            ActiveOverride: activeOverride);

        TimeSpan gameDelay = PolicyMaintenanceTiming.GetNextDelay(
            Healthy(policy),
            policy.EvaluatedAt);
        TimeSpan overrideStartDelay = PolicyMaintenanceTiming.GetNextDelay(
            Healthy(policy with { EvaluatedAt = gameCutoff.AddSeconds(2) }),
            gameCutoff.AddSeconds(2));
        TimeSpan overrideEndDelay = PolicyMaintenanceTiming.GetNextDelay(
            Healthy(policy with
            {
                EvaluatedAt = activeOverride.EndsAtUtc.AddMilliseconds(-250),
            }),
            activeOverride.EndsAtUtc.AddMilliseconds(-250));

        Assert.Equal(TimeSpan.FromMilliseconds(400), gameDelay);
        Assert.Equal(TimeSpan.FromSeconds(1), overrideStartDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(250), overrideEndDelay);
    }

    [Fact]
    public void WallClockForwardJumpPastLockBoundary_IsImmediatelyDue()
    {
        NightWindow window = Window();
        DateTimeOffset evaluatedAt = window.Lock.ToUniversalTime().AddMinutes(-5);
        DateTimeOffset jumpedForward = window.Lock.ToUniversalTime().AddMinutes(10);
        ServiceRuntimeStatus status = Healthy(new(
            evaluatedAt,
            NightPhase.Grace,
            window,
            [],
            []));

        Assert.True(PolicyMaintenanceTiming.IsRefreshDue(status, jumpedForward));
        Assert.Equal(
            TimeSpan.Zero,
            PolicyMaintenanceTiming.GetNextDelay(status, jumpedForward));
    }

    [Fact]
    public void WallClockRollbackBeforeEvaluation_DoesNotRewindOrHotLoop()
    {
        NightWindow window = Window();
        DateTimeOffset evaluatedAt = window.Lock.ToUniversalTime().AddMinutes(10);
        DateTimeOffset rolledBack = window.Lock.ToUniversalTime().AddMinutes(2);
        ServiceRuntimeStatus status = Healthy(new(
            evaluatedAt,
            NightPhase.LandingLocked,
            window,
            [],
            []));

        Assert.False(PolicyMaintenanceTiming.IsRefreshDue(status, rolledBack));
        Assert.Equal(
            PolicyMaintenanceTiming.WatchdogInterval,
            PolicyMaintenanceTiming.GetNextDelay(status, rolledBack));
    }

    [Fact]
    public void HealthySnapshotWithoutCrossedBoundary_KeepsThirtySecondWatchdog()
    {
        NightWindow window = Window();
        DateTimeOffset now = window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        ServiceRuntimeStatus status = Healthy(new(
            now.AddSeconds(-10),
            NightPhase.Free,
            window,
            [],
            []));

        Assert.False(PolicyMaintenanceTiming.IsRefreshDue(status, now));
        Assert.Equal(
            PolicyMaintenanceTiming.WatchdogInterval,
            PolicyMaintenanceTiming.GetNextDelay(status, now));
    }

    [Fact]
    public async Task GetPolicy_AfterLockBoundarySynchronouslyRefreshesStaleGraceCache()
    {
        NightWindow window = Window();
        DateTimeOffset now = window.Lock.ToUniversalTime().AddMilliseconds(1);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            window.Lock.ToUniversalTime().AddSeconds(-1),
            NightPhase.Grace,
            window,
            [],
            [])));
        PublishingIteration iteration = new(
            status,
            Healthy(new(now, NightPhase.LandingLocked, window, [], [])));
        FixedClock clock = new(now);
        PolicyMaintenanceScheduler scheduler = new(iteration, status, clock);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock,
            policyMaintenanceScheduler: scheduler);

        ProtocolCommandResult result = await handler.ExecuteAsync(new GetPolicyCommand());

        Assert.Equal(1, iteration.ExecutionCount);
        Assert.Equal(
            "landingLocked",
            result.Payload.GetProperty("policy").GetProperty("phase").GetString());
    }

    [Fact]
    public async Task GetPolicy_AtNextProtectedStartSynchronouslyRefreshesStaleMorningCache()
    {
        DateTimeOffset beforeProtectedStart =
            new(2026, 7, 6, 20, 59, 59, TimeSpan.Zero);
        DateTimeOffset protectedStart =
            new(2026, 7, 6, 21, 0, 0, TimeSpan.Zero);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();
        await new PolicyMaintenanceIteration(
                repository,
                repository,
                repository,
                new NightMutationGate(),
                new FixedClock(beforeProtectedStart),
                new FixedTimeZoneProvider(TimeZoneInfo.Utc),
                status,
                new EmptyConfiguredRuleProvider(),
                new EmptyConfiguredSiteRuleProvider())
            .ExecuteAsync();
        Assert.Equal(NightPhase.Morning, status.Current.Policy!.Phase);
        Assert.Equal(new DateOnly(2026, 7, 5), status.Current.Policy.Window.NightDate);

        PublishingIteration iteration = new(
            status,
            Healthy(new(
                protectedStart,
                NightPhase.Free,
                ScheduleEvaluator.CreateWindow(
                    new DateOnly(2026, 7, 6),
                    ScheduleProfile.Default.Steps[0],
                    TimeZoneInfo.Utc),
                [],
                [])));
        FixedClock boundaryClock = new(protectedStart);
        PolicyMaintenanceScheduler scheduler = new(iteration, status, boundaryClock);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            boundaryClock,
            policyMaintenanceScheduler: scheduler);

        ProtocolCommandResult result = await handler.ExecuteAsync(new GetPolicyCommand());

        Assert.Equal(1, iteration.ExecutionCount);
        Assert.Equal(
            "free",
            result.Payload.GetProperty("policy").GetProperty("phase").GetString());
    }

    [Theory]
    [InlineData(2026, 7, 10, 20, 59, 59, 2026, 7, 10, 22)]
    [InlineData(2026, 7, 11, 22, 0, 0, 2026, 7, 12, 21)]
    public async Task NextProtectedStart_UsesWeekendOffset(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second,
        int expectedYear,
        int expectedMonth,
        int expectedDay,
        int expectedHour)
    {
        DateTimeOffset now = new(year, month, day, hour, minute, second, TimeSpan.Zero);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();

        await Maintenance(repository, new FixedClock(now), TimeZoneInfo.Utc, status)
            .ExecuteAsync();

        Assert.Equal(
            new DateTimeOffset(
                expectedYear,
                expectedMonth,
                expectedDay,
                expectedHour,
                0,
                0,
                TimeSpan.Zero),
            status.Current.NextProtectedStartAtUtc);
    }

    [Fact]
    public async Task NextProtectedStart_UsesPostTransitionDstOffset()
    {
        TimeZoneInfo daylightTimeZone = CreateMarchDstTimeZone();
        DateTimeOffset now = new(2026, 3, 7, 23, 0, 0, TimeSpan.Zero);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();

        await Maintenance(repository, new FixedClock(now), daylightTimeZone, status)
            .ExecuteAsync();

        Assert.Equal(
            new DateTimeOffset(2026, 3, 8, 20, 0, 0, TimeSpan.Zero),
            status.Current.NextProtectedStartAtUtc);
    }

    [Fact]
    public async Task ActiveNightKeepsPinnedBoundariesWhileNextNightUsesCurrentTimeZone()
    {
        TimeZoneInfo originalTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Scheduling-Pinned-UTC+8",
            TimeSpan.FromHours(8),
            "NightGate Scheduling Pinned UTC+8",
            "NightGate Scheduling Pinned UTC+8");
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        await repository.SaveProgressAsync(ProgressState.Initial);
        DateTimeOffset startedAt = new(2026, 7, 6, 13, 1, 0, TimeSpan.Zero);
        await Maintenance(
                repository,
                new FixedClock(startedAt),
                originalTimeZone,
                new InMemoryServiceStatus())
            .ExecuteAsync();

        DateTimeOffset observedAfterZoneChange =
            new(2026, 7, 6, 16, 30, 0, TimeSpan.Zero);
        InMemoryServiceStatus changedZoneStatus = new();
        await Maintenance(
                repository,
                new FixedClock(observedAfterZoneChange),
                TimeZoneInfo.Utc,
                changedZoneStatus)
            .ExecuteAsync();

        Assert.Equal(NightPhase.Grace, changedZoneStatus.Current.Policy!.Phase);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 6, 16, 40, 0, TimeSpan.Zero),
            changedZoneStatus.Current.Policy.Window.Lock.ToUniversalTime());
        Assert.Equal(
            new DateTimeOffset(2026, 7, 6, 21, 0, 0, TimeSpan.Zero),
            changedZoneStatus.Current.NextProtectedStartAtUtc);
    }

    [Fact]
    public async Task RepeatedGetPolicyEvery250Milliseconds_DoesNotRunFullMaintenance()
    {
        NightWindow window = Window();
        DateTimeOffset startedAt =
            window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            startedAt,
            NightPhase.Free,
            window,
            [],
            [])));
        PublishingIteration iteration = new(
            status,
            Healthy(new(startedAt, NightPhase.Free, window, [], [])));
        MutableClock clock = new(startedAt);
        PolicyMaintenanceScheduler scheduler = new(iteration, status, clock);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock,
            policyMaintenanceScheduler: scheduler);

        for (int index = 0; index < 6; index++)
        {
            clock.UtcNow = startedAt.AddMilliseconds(index * 250);
            _ = await handler.ExecuteAsync(new GetPolicyCommand());
        }

        Assert.Equal(0, iteration.ExecutionCount);
    }

    [Fact]
    public async Task SlowMaintenance_DoesNotCauseImmediateSecondMaintenance()
    {
        NightWindow window = Window();
        DateTimeOffset startedAt =
            window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        InMemoryServiceStatus status = new();
        MutableClock clock = new(startedAt);
        AdvancingPublishingIteration iteration = new(
            status,
            clock,
            TimeSpan.FromSeconds(2),
            Healthy(new(startedAt, NightPhase.Free, window, [], [])));
        PolicyMaintenanceScheduler scheduler = new(iteration, status, clock);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock,
            policyMaintenanceScheduler: scheduler);

        _ = await handler.ExecuteAsync(new GetPolicyCommand());
        _ = await handler.ExecuteAsync(new GetPolicyCommand());

        Assert.Equal(1, iteration.ExecutionCount);
    }

    [Fact]
    public async Task GetPolicy_LeasesFreshEvaluatedAtWithoutUpdatingCachedStatus()
    {
        NightWindow window = Window();
        DateTimeOffset cachedAt =
            window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        DateTimeOffset requestedAt = cachedAt.AddMilliseconds(500);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            cachedAt,
            NightPhase.Free,
            window,
            [],
            [])));
        PublishingIteration iteration = new(
            status,
            Healthy(new(requestedAt, NightPhase.Free, window, [], [])));
        FixedClock clock = new(requestedAt);
        PolicyMaintenanceScheduler scheduler = new(iteration, status, clock);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock,
            policyMaintenanceScheduler: scheduler);

        ProtocolCommandResult result = await handler.ExecuteAsync(new GetPolicyCommand());

        Assert.Equal(
            requestedAt,
            result.Payload.GetProperty("policy").GetProperty("evaluatedAt")
                .GetDateTimeOffset());
        Assert.Equal(cachedAt, status.Current.Policy!.EvaluatedAt);
        Assert.Equal(0, iteration.ExecutionCount);
    }

    [Fact]
    public async Task GetPolicy_ClockRollbackDoesNotMoveLeaseBackward()
    {
        NightWindow window = Window();
        DateTimeOffset cachedAt =
            window.Lock.ToUniversalTime().AddMinutes(-1);
        DateTimeOffset firstRequestAt =
            window.Lock.ToUniversalTime().AddTicks(-1);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            cachedAt,
            NightPhase.Grace,
            window,
            [],
            [])));
        PublishingIteration iteration = new(
            status,
            Healthy(new(cachedAt, NightPhase.Grace, window, [], [])));
        MutableClock clock = new(firstRequestAt);
        PolicyMaintenanceScheduler scheduler = new(iteration, status, clock);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock,
            policyMaintenanceScheduler: scheduler);

        ProtocolCommandResult first = await handler.ExecuteAsync(new GetPolicyCommand());
        clock.UtcNow = cachedAt.AddHours(-1);
        ProtocolCommandResult rolledBack = await handler.ExecuteAsync(new GetPolicyCommand());

        DateTimeOffset firstLease = first.Payload.GetProperty("policy")
            .GetProperty("evaluatedAt")
            .GetDateTimeOffset();
        DateTimeOffset rollbackLease = rolledBack.Payload.GetProperty("policy")
            .GetProperty("evaluatedAt")
            .GetDateTimeOffset();
        Assert.Equal(firstRequestAt, firstLease);
        Assert.Equal(firstLease, rollbackLease);
        Assert.True(rollbackLease < window.Lock.ToUniversalTime());
        Assert.Equal(cachedAt, status.Current.Policy!.EvaluatedAt);
        Assert.Equal(0, iteration.ExecutionCount);
    }

    [Fact]
    public async Task GetPolicy_SameClockWithChangedPayloadUsesStrictlyNewerLease()
    {
        NightWindow window = Window();
        DateTimeOffset now = window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            now,
            NightPhase.Free,
            window,
            [],
            [])));
        FixedClock clock = new(now);
        PublishingIteration iteration = new(status, status.Current);
        PolicyMaintenanceScheduler scheduler = new(iteration, status, clock);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock,
            policyMaintenanceScheduler: scheduler);

        ProtocolCommandResult first = await handler.ExecuteAsync(new GetPolicyCommand());
        await status.PublishAsync(Healthy(new(
            now,
            NightPhase.Free,
            window,
            [new(
                "new-game",
                @"C:\Games\New\game.exe",
                [],
                AppRuleCategory.Game,
                35)],
            [])));
        ProtocolCommandResult changed = await handler.ExecuteAsync(new GetPolicyCommand());

        DateTimeOffset firstLease = first.Payload.GetProperty("policy")
            .GetProperty("evaluatedAt")
            .GetDateTimeOffset();
        DateTimeOffset changedLease = changed.Payload.GetProperty("policy")
            .GetProperty("evaluatedAt")
            .GetDateTimeOffset();
        Assert.Equal(firstLease.AddTicks(1), changedLease);
        Assert.Equal(
            "new-game",
            Assert.Single(changed.Payload.GetProperty("policy")
                    .GetProperty("appRules")
                    .EnumerateArray())
                .GetProperty("id")
                .GetString());
        Assert.Equal(now, status.Current.Policy!.EvaluatedAt);
    }

    [Fact]
    public async Task ConcurrentGetPolicyCompletionOrderDoesNotMoveLeaseBackward()
    {
        NightWindow window = Window();
        DateTimeOffset cachedAt =
            window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        DateTimeOffset laterSample = cachedAt.AddSeconds(10);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            cachedAt,
            NightPhase.Free,
            window,
            [],
            [])));
        SequenceClock clock = new(cachedAt, laterSample);
        OutOfOrderPolicyScheduler scheduler = new();
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock,
            policyMaintenanceScheduler: scheduler);

        Task<ProtocolCommandResult> first = handler
            .ExecuteAsync(new GetPolicyCommand())
            .AsTask();
        await scheduler.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ProtocolCommandResult completedFirst = await handler
            .ExecuteAsync(new GetPolicyCommand());
        scheduler.ReleaseFirst();
        ProtocolCommandResult completedSecond = await first.WaitAsync(TimeSpan.FromSeconds(5));

        DateTimeOffset earlierCompletionLease = completedFirst.Payload
            .GetProperty("policy")
            .GetProperty("evaluatedAt")
            .GetDateTimeOffset();
        DateTimeOffset laterCompletionLease = completedSecond.Payload
            .GetProperty("policy")
            .GetProperty("evaluatedAt")
            .GetDateTimeOffset();
        Assert.Equal(laterSample, earlierCompletionLease);
        Assert.Equal(earlierCompletionLease, laterCompletionLease);
    }

    [Fact]
    public async Task OlderPayloadCompletingLate_FailsOpenInsteadOfRecastingRevision()
    {
        NightWindow window = Window();
        DateTimeOffset now = window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        PolicySnapshot olderPolicy = new(
            now,
            NightPhase.Free,
            window,
            [],
            [])
        {
            Revision = 10,
        };
        PolicySnapshot newerPolicy = olderPolicy with
        {
            AppRules = [new(
                "newer-game",
                @"C:\Games\Newer\game.exe",
                [],
                AppRuleCategory.Game,
                35)],
            Revision = 11,
        };
        SequenceServiceStatusReader statusReader = new(
            Healthy(newerPolicy),
            Healthy(olderPolicy));
        InMemoryServiceStatus statusPublisher = new();
        OutOfOrderPolicyScheduler scheduler = new();
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            statusReader,
            statusPublisher,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            new FixedClock(now),
            policyMaintenanceScheduler: scheduler);

        Task<ProtocolCommandResult> olderCall = handler
            .ExecuteAsync(new GetPolicyCommand())
            .AsTask();
        await scheduler.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ProtocolCommandResult newerResponse = await handler
            .ExecuteAsync(new GetPolicyCommand());
        scheduler.ReleaseFirst();
        ProtocolCommandResult olderResponse = await olderCall.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StorageMode.Success, newerResponse.Mode);
        Assert.Equal(StorageMode.Degraded, olderResponse.Mode);
        Assert.Equal(
            "policy-response-authority-conflict",
            olderResponse.Payload.GetProperty("degradationCode").GetString());
    }

    [Fact]
    public async Task GetPolicy_AfterMaximumLeaseFailsOpenWithoutChangingCachedStatus()
    {
        NightWindow window = Window();
        DateTimeOffset maximum = DateTimeOffset.MaxValue;
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            maximum,
            NightPhase.Free,
            window,
            [],
            [])));
        NoOpPolicyScheduler scheduler = new();
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            new FixedClock(maximum),
            policyMaintenanceScheduler: scheduler);

        ProtocolCommandResult maximumLease = await handler
            .ExecuteAsync(new GetPolicyCommand());
        ProtocolCommandResult replay = await handler
            .ExecuteAsync(new GetPolicyCommand());
        await status.PublishAsync(Healthy(new(
            maximum,
            NightPhase.Free,
            window,
            [new(
                "changed-at-maximum",
                @"C:\Games\Maximum\game.exe",
                [],
                AppRuleCategory.Game,
                35)],
            [])));
        ProtocolCommandResult exhausted = await handler
            .ExecuteAsync(new GetPolicyCommand());

        Assert.Equal(StorageMode.Success, maximumLease.Mode);
        Assert.Equal(StorageMode.Success, replay.Mode);
        Assert.Equal(StorageMode.Degraded, exhausted.Mode);
        Assert.Equal(
            "policy-response-lease-exhausted",
            exhausted.Payload.GetProperty("degradationCode").GetString());
        Assert.Equal(maximum, status.Current.Policy!.EvaluatedAt);
    }

    [Fact]
    public async Task GetPolicy_BoundaryCrossedAfterLeaseSample_StillRefreshes()
    {
        NightWindow window = Window();
        DateTimeOffset beforeLock =
            window.Lock.ToUniversalTime().AddMilliseconds(-1);
        DateTimeOffset afterLock =
            window.Lock.ToUniversalTime().AddMilliseconds(1);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            beforeLock.AddSeconds(-1),
            NightPhase.Grace,
            window,
            [],
            [])));
        PublishingIteration iteration = new(
            status,
            Healthy(new(afterLock, NightPhase.LandingLocked, window, [], [])));
        SequenceClock clock = new(beforeLock, afterLock);
        PolicyMaintenanceScheduler scheduler = new(iteration, status, clock);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock,
            policyMaintenanceScheduler: scheduler);

        ProtocolCommandResult result = await handler.ExecuteAsync(new GetPolicyCommand());

        Assert.Equal(1, iteration.ExecutionCount);
        Assert.Equal(
            "landingLocked",
            result.Payload.GetProperty("policy").GetProperty("phase").GetString());
    }

    [Fact]
    public async Task ConcurrentBoundaryRefreshes_SerializeAndRecheckFreshStatus()
    {
        NightWindow window = Window();
        DateTimeOffset now = window.Lock.ToUniversalTime().AddMilliseconds(1);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            window.Lock.ToUniversalTime().AddSeconds(-1),
            NightPhase.Grace,
            window,
            [],
            [])));
        BlockingPublishingIteration iteration = new(
            status,
            Healthy(new(now, NightPhase.LandingLocked, window, [], [])));
        PolicyMaintenanceScheduler scheduler = new(
            iteration,
            status,
            new FixedClock(now));

        Task[] refreshes = Enumerable.Range(0, 8).Select(_ =>
            scheduler.RefreshAsync(force: false)
                .AsTask()).ToArray();
        await iteration.FirstExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(1, iteration.ExecutionCount);
        }
        finally
        {
            iteration.Release();
        }

        await Task.WhenAll(refreshes).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, iteration.ExecutionCount);
        Assert.Equal(now, status.Current.Policy!.EvaluatedAt);
    }

    [Fact]
    public async Task DirtyPolicy_RefreshesOnceWithoutForceOrCrossedBoundary()
    {
        NightWindow window = Window();
        DateTimeOffset now = window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            now,
            NightPhase.Free,
            window,
            [],
            [])));
        PublishingIteration iteration = new(
            status,
            Healthy(new(now, NightPhase.Free, window, [], [])));
        PolicyMaintenanceScheduler scheduler = new(
            iteration,
            status,
            new FixedClock(now));

        scheduler.MarkDirty();
        await scheduler.RefreshAsync(force: false);
        await scheduler.RefreshAsync(force: false);

        Assert.Equal(1, iteration.ExecutionCount);
    }

    [Fact]
    public async Task FailedDirtyRefresh_RemainsDirtyUntilSuccessfulRetry()
    {
        NightWindow window = Window();
        DateTimeOffset now = window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            now,
            NightPhase.Free,
            window,
            [],
            [])));
        FailOncePublishingIteration iteration = new(
            status,
            Healthy(new(now, NightPhase.Free, window, [], [])));
        PolicyMaintenanceScheduler scheduler = new(
            iteration,
            status,
            new FixedClock(now));
        scheduler.MarkDirty();

        await Assert.ThrowsAsync<IOException>(() =>
            scheduler.RefreshAsync(force: false).AsTask());
        await scheduler.RefreshAsync(force: false);
        await scheduler.RefreshAsync(force: false);

        Assert.Equal(2, iteration.ExecutionCount);
    }

    [Fact]
    public async Task CancelledDirtyRefresh_RemainsDirtyUntilSuccessfulRetry()
    {
        NightWindow window = Window();
        DateTimeOffset now = window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            now,
            NightPhase.Free,
            window,
            [],
            [])));
        CancelOncePublishingIteration iteration = new(
            status,
            Healthy(new(now, NightPhase.Free, window, [], [])));
        PolicyMaintenanceScheduler scheduler = new(
            iteration,
            status,
            new FixedClock(now));
        scheduler.MarkDirty();
        using CancellationTokenSource cancellation = new();

        Task first = scheduler.RefreshAsync(force: false, cancellation.Token).AsTask();
        await iteration.FirstExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await scheduler.RefreshAsync(force: false);
        await scheduler.RefreshAsync(force: false);

        Assert.Equal(2, iteration.ExecutionCount);
    }

    [Fact]
    public async Task DirtyMarkedDuringRefresh_IsNotClearedByOlderRefresh()
    {
        NightWindow window = Window();
        DateTimeOffset now = window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            now,
            NightPhase.Free,
            window,
            [],
            [])));
        BlockFirstPublishingIteration iteration = new(
            status,
            Healthy(new(now, NightPhase.Free, window, [], [])));
        PolicyMaintenanceScheduler scheduler = new(
            iteration,
            status,
            new FixedClock(now));
        scheduler.MarkDirty();

        Task first = scheduler.RefreshAsync(force: false).AsTask();
        await iteration.FirstExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.MarkDirty();
        iteration.ReleaseFirst();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.RefreshAsync(force: false);
        await scheduler.RefreshAsync(force: false);

        Assert.Equal(2, iteration.ExecutionCount);
    }

    [Fact]
    public async Task ConcurrentDirtyRefreshes_RunOnceForOneGeneration()
    {
        NightWindow window = Window();
        DateTimeOffset now = window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        InMemoryServiceStatus status = new();
        await status.PublishAsync(Healthy(new(
            now,
            NightPhase.Free,
            window,
            [],
            [])));
        BlockingPublishingIteration iteration = new(
            status,
            Healthy(new(now, NightPhase.Free, window, [], [])));
        PolicyMaintenanceScheduler scheduler = new(
            iteration,
            status,
            new FixedClock(now));
        scheduler.MarkDirty();

        Task[] refreshes = Enumerable.Range(0, 8)
            .Select(_ => scheduler.RefreshAsync(force: false).AsTask())
            .ToArray();
        await iteration.FirstExecutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, iteration.ExecutionCount);
        iteration.Release();
        await Task.WhenAll(refreshes).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, iteration.ExecutionCount);
    }

    [Fact]
    public async Task RejectedStatusRecovery_DoesNotAcknowledgeDirtyGeneration()
    {
        NightWindow window = Window();
        DateTimeOffset now = window.ProtectedStart.ToUniversalTime().AddMinutes(30);
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.DatabasePath);
        await repository.SaveProgressAsync(ProgressState.Initial);
        RejectOnceRecoveryStatus status = new();
        await status.PublishAsync(Healthy(new(
            now,
            NightPhase.Free,
            window,
            [],
            [])));
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            new NightMutationGate(),
            new FixedClock(now),
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            new EmptyConfiguredRuleProvider(),
            new EmptyConfiguredSiteRuleProvider());
        PolicyMaintenanceScheduler scheduler = new(
            iteration,
            status,
            new FixedClock(now));
        scheduler.MarkDirty();

        await Assert.ThrowsAsync<IOException>(() =>
            scheduler.RefreshAsync(force: false).AsTask());
        await scheduler.RefreshAsync(force: false);
        await scheduler.RefreshAsync(force: false);

        Assert.Equal(2, status.RecoveryAttempts);
        Assert.False(status.Current.IsDegraded);
    }

    private static ServiceRuntimeStatus Healthy(PolicySnapshot policy) =>
        new(true, false, null, policy);

    private static NightWindow Window() => ScheduleEvaluator.CreateWindow(
        new DateOnly(2026, 7, 6),
        ScheduleProfile.Default.Steps[0],
        TimeZoneInfo.Utc);

    private static PolicyMaintenanceIteration Maintenance(
        SqliteNightGateRepository repository,
        IClock clock,
        TimeZoneInfo timeZone,
        InMemoryServiceStatus status) => new(
        repository,
        repository,
        repository,
        new NightMutationGate(),
        clock,
        new FixedTimeZoneProvider(timeZone),
        status,
        new EmptyConfiguredRuleProvider(),
        new EmptyConfiguredSiteRuleProvider());

    private static TimeZoneInfo CreateMarchDstTimeZone()
    {
        TimeZoneInfo.TransitionTime daylightStart =
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0),
                3,
                2,
                DayOfWeek.Sunday);
        TimeZoneInfo.TransitionTime daylightEnd =
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0),
                11,
                1,
                DayOfWeek.Sunday);
        TimeZoneInfo.AdjustmentRule adjustment =
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2026, 1, 1),
                new DateTime(2026, 12, 31),
                TimeSpan.FromHours(1),
                daylightStart,
                daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Scheduling-DST",
            TimeSpan.Zero,
            "NightGate Scheduling DST",
            "NightGate Scheduling Standard",
            "NightGate Scheduling Daylight",
            [adjustment]);
    }

    private sealed class PublishingIteration(
        IServiceStatusPublisher publisher,
        ServiceRuntimeStatus replacement) : IPolicyMaintenanceIteration
    {
        public int ExecutionCount { get; private set; }

        public async ValueTask ExecuteAsync(
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            await publisher.PublishAsync(replacement, cancellationToken);
        }
    }

    private sealed class BlockingPublishingIteration(
        IServiceStatusPublisher publisher,
        ServiceRuntimeStatus replacement) : IPolicyMaintenanceIteration
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executionCount;

        public TaskCompletionSource FirstExecutionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public async ValueTask ExecuteAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executionCount);
            FirstExecutionStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            await publisher.PublishAsync(replacement, cancellationToken);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class AdvancingPublishingIteration(
        IServiceStatusPublisher publisher,
        MutableClock clock,
        TimeSpan elapsed,
        ServiceRuntimeStatus replacement) : IPolicyMaintenanceIteration
    {
        public int ExecutionCount { get; private set; }

        public async ValueTask ExecuteAsync(
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            clock.UtcNow += elapsed;
            await publisher.PublishAsync(replacement, cancellationToken);
        }
    }

    private sealed class FailOncePublishingIteration(
        IServiceStatusPublisher publisher,
        ServiceRuntimeStatus replacement) : IPolicyMaintenanceIteration
    {
        public int ExecutionCount { get; private set; }

        public async ValueTask ExecuteAsync(
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            if (ExecutionCount == 1)
            {
                throw new IOException("simulated maintenance failure");
            }

            await publisher.PublishAsync(replacement, cancellationToken);
        }
    }

    private sealed class CancelOncePublishingIteration(
        IServiceStatusPublisher publisher,
        ServiceRuntimeStatus replacement) : IPolicyMaintenanceIteration
    {
        public TaskCompletionSource FirstExecutionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecutionCount { get; private set; }

        public async ValueTask ExecuteAsync(
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            if (ExecutionCount == 1)
            {
                FirstExecutionStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return;
            }

            await publisher.PublishAsync(replacement, cancellationToken);
        }
    }

    private sealed class BlockFirstPublishingIteration(
        IServiceStatusPublisher publisher,
        ServiceRuntimeStatus replacement) : IPolicyMaintenanceIteration
    {
        private readonly TaskCompletionSource _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstExecutionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ExecutionCount { get; private set; }

        public async ValueTask ExecuteAsync(
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            if (ExecutionCount == 1)
            {
                FirstExecutionStarted.TrySetResult();
                await _releaseFirst.Task.WaitAsync(cancellationToken);
            }

            await publisher.PublishAsync(replacement, cancellationToken);
        }

        public void ReleaseFirst() => _releaseFirst.TrySetResult();
    }

    private sealed class OutOfOrderPolicyScheduler : IPolicyMaintenanceScheduler
    {
        private readonly TaskCompletionSource _releaseFirst =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void MarkDirty()
        {
        }

        public async ValueTask RefreshAsync(
            bool force,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) != 1)
            {
                return;
            }

            FirstCallStarted.TrySetResult();
            await _releaseFirst.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseFirst() => _releaseFirst.TrySetResult();
    }

    private sealed class NoOpPolicyScheduler : IPolicyMaintenanceScheduler
    {
        public void MarkDirty()
        {
        }

        public ValueTask RefreshAsync(
            bool force,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public ClockObservation Observe() => new(
            UtcNow,
            TimeSpan.FromHours(100),
            BootSessionId);
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public ClockObservation Observe() => new(
            UtcNow,
            TimeSpan.FromHours(100),
            BootSessionId);
    }

    private sealed class SequenceClock(params DateTimeOffset[] utcValues) : IClock
    {
        private int _nextIndex;

        public DateTimeOffset UtcNow
        {
            get
            {
                int index = Interlocked.Increment(ref _nextIndex) - 1;
                return utcValues[Math.Min(index, utcValues.Length - 1)];
            }
        }

        public ClockObservation Observe() => new(
            UtcNow,
            TimeSpan.FromHours(100),
            BootSessionId);
    }

    private sealed class FixedTimeZoneProvider(TimeZoneInfo timeZone) : ITimeZoneProvider
    {
        public TimeZoneInfo Local => timeZone;
    }

    private sealed class EmptyConfiguredRuleProvider : IConfiguredRuleProvider
    {
        public ConfiguredRuleProviderResult GetRules() =>
            ConfiguredRuleProviderResult.Success([]);
    }

    private sealed class EmptyConfiguredSiteRuleProvider : IConfiguredSiteRuleProvider
    {
        public ConfiguredSiteRuleProviderResult GetRules() =>
            ConfiguredSiteRuleProviderResult.Success([]);
    }

    private sealed class EmptyAllowedProcesses : IAllowedProcessSnapshotProvider
    {
        public ImmutableArray<string> GetSnapshot() => [];
    }

    private sealed class RejectOnceRecoveryStatus :
        IServiceStatusPublisher,
        IServiceStatusReader,
        IServiceStatusRecovery
    {
        private readonly InMemoryServiceStatus _inner = new();

        public int RecoveryAttempts { get; private set; }

        public ServiceRuntimeStatus Current => _inner.Current;

        public ValueTask PublishAsync(
            ServiceRuntimeStatus status,
            CancellationToken cancellationToken = default) =>
            _inner.PublishAsync(status, cancellationToken);

        public ServiceRuntimeStatusSnapshot ReadSnapshot() => _inner.ReadSnapshot();

        public ValueTask<bool> TryRecoverAsync(
            long expectedRevision,
            ServiceRuntimeStatus status,
            CancellationToken cancellationToken = default)
        {
            RecoveryAttempts++;
            if (RecoveryAttempts == 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(false);
            }

            return _inner.TryRecoverAsync(
                expectedRevision,
                status,
                cancellationToken);
        }
    }

    private sealed class SequenceServiceStatusReader(
        params ServiceRuntimeStatus[] values) : IServiceStatusReader
    {
        private int _nextIndex;

        public ServiceRuntimeStatus Current
        {
            get
            {
                int index = Interlocked.Increment(ref _nextIndex) - 1;
                return values[Math.Min(index, values.Length - 1)];
            }
        }
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "NightGate.Service.Tests",
                Guid.NewGuid().ToString("N"));
            DatabasePath = Path.Combine(DirectoryPath, "state.db");
        }

        public string DirectoryPath { get; }

        public string DatabasePath { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
