using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class AtomicOverrideMutationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid BootSessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RestartedBootSessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task ConcurrentEntertainmentRequests_OnlyOneCanSucceed()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState();
        await repository.SaveActiveStateWithEventAsync(state, CreateEvent());
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();
        NightMutationGate gate = new();
        RecordingPolicyMaintenanceScheduler scheduler = new();
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new FixedSnapshotProvider([])),
            gate,
            new FixedClock(Now),
            policyMaintenanceScheduler: scheduler);
        RequestOverrideCommand command = new(new(OverrideKind.Entertainment, null));

        ProtocolCommandResult[] results = await Task.WhenAll(
            handler.ExecuteAsync(command).AsTask(),
            handler.ExecuteAsync(command).AsTask());

        Assert.Equal(1, results.Count(result => result.Payload.GetProperty("accepted").GetBoolean()));
        NightState reloaded = (await repository.ReadActiveStateAsync()).Value!;
        Assert.True(reloaded.EntertainmentUsed);
        Assert.Equal(OverrideKind.Entertainment, reloaded.ActiveOverride!.Kind);
        Assert.Equal(1, scheduler.DirtyMarks);
        Assert.Equal(1, scheduler.CallCount);
        Assert.True(scheduler.LastForce);
    }

    [Fact]
    public async Task OverrideCommittedThenCallerCancelled_NextGetPolicyRefreshesDirtyPolicy()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveActiveStateWithEventAsync(CreateState(), CreateEvent());
        await repository.SaveProgressAsync(ProgressState.Initial);
        InMemoryServiceStatus status = new();
        NightWindow window = CreateWindow();
        await status.PublishAsync(new(
            true,
            false,
            null,
            new(Now, NightPhase.LandingLocked, window, [], [])));
        NightMutationGate gate = new();
        FixedClock clock = new(Now);
        PolicyMaintenanceIteration iteration = new(
            repository,
            repository,
            repository,
            gate,
            clock,
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            new SuccessfulEmptyConfiguredRuleProvider(),
            new SuccessfulEmptyConfiguredSiteRuleProvider());
        using PolicyMaintenanceScheduler innerScheduler = new(iteration, status, clock);
        using CancellationTokenSource cancellation = new();
        CancellingAfterMarkScheduler scheduler = new(innerScheduler, cancellation);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new FixedSnapshotProvider([])),
            gate,
            clock,
            policyMaintenanceScheduler: scheduler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler
            .ExecuteAsync(
                new RequestOverrideCommand(Request(OverrideKind.Emergency)),
                cancellation.Token)
            .AsTask());
        Assert.NotNull((await repository.ReadActiveStateAsync()).Value!.ActiveOverride);
        Assert.Null(status.Current.Policy!.ActiveOverride);

        ProtocolCommandResult policy = await handler.ExecuteAsync(new GetPolicyCommand());

        Assert.Equal(1, scheduler.DirtyMarks);
        Assert.Equal(1, scheduler.ForceRefreshCalls);
        Assert.Equal(1, scheduler.NonForceRefreshCalls);
        Assert.Equal(
            "overrideActive",
            policy.Payload.GetProperty("policy").GetProperty("phase").GetString());
        Assert.Equal(
            "emergency",
            policy.Payload.GetProperty("policy")
                .GetProperty("activeOverride")
                .GetProperty("kind")
                .GetString());
    }

    [Fact]
    public async Task IndependentConcurrentEntertainmentRequests_OnlyOneCommits()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository setup = new(database.Path);
        await setup.SaveActiveStateWithEventAsync(CreateState(), CreateEvent());
        await setup.SaveProgressAsync(ProgressState.Initial);
        FirstReadRendezvous rendezvous = new();
        SqliteNightGateRepository firstInner = new(database.Path);
        SqliteNightGateRepository secondInner = new(database.Path);
        NightGateProtocolCommandHandler first = CreateDatabaseHandler(
            new RendezvousStateRepository(firstInner, rendezvous),
            firstInner);
        NightGateProtocolCommandHandler second = CreateDatabaseHandler(
            new RendezvousStateRepository(secondInner, rendezvous),
            secondInner);
        RequestOverrideCommand command = new(new(OverrideKind.Entertainment, null));

        ProtocolCommandResult[] results = await Task.WhenAll(
                first.ExecuteAsync(command).AsTask(),
                second.ExecuteAsync(command).AsTask())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, results.Count(result => result.Payload.GetProperty("accepted").GetBoolean()));
        ProtocolCommandResult rejected = Assert.Single(
            results,
            result => !result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal("alreadyUsedTonight", rejected.Payload.GetProperty("error").GetString());
        Assert.Equal(2, await CountEventsAsync(database.Path));
    }

    [Fact]
    public async Task EmergencyPreemptionAtomicallyReplacesActiveRescueAndPreservesUsageFacts()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        ActiveOverride rescue = new(
            OverrideKind.TeamRescue,
            Now,
            Now,
            Now.AddMinutes(20),
            ["game.exe"]);
        NightState initial = CreateState() with
        {
            ActiveOverride = rescue,
            TeamRescueUsed = true,
        };
        await repository.SaveActiveStateWithEventAsync(initial, CreateEvent());
        await repository.SaveProgressAsync(ProgressState.Initial with
        {
            LastTeamRescueAtUtc = Now,
        });
        InMemoryServiceStatus status = new();
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new FixedSnapshotProvider(["game.exe"])),
            new NightMutationGate(),
            new FixedClock(Now.AddMinutes(1)));

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RequestOverrideCommand(
                new(OverrideKind.Emergency, EmergencyReason.Safety)));

        NightState persisted = (await repository.ReadActiveStateAsync()).Value!;
        ProgressState progress = (await repository.ReadProgressAsync()).Value;
        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(OverrideKind.Emergency, persisted.ActiveOverride!.Kind);
        Assert.Equal(Now.AddMinutes(1), persisted.ActiveOverride.StartsAtUtc);
        Assert.Equal(Now.AddMinutes(31), persisted.ActiveOverride.EndsAtUtc);
        Assert.True(persisted.TeamRescueUsed);
        Assert.True(persisted.EmergencyUsed);
        Assert.Equal(Now, progress.LastTeamRescueAtUtc);
    }

    [Fact]
    public async Task TeamRescue_RuleGenerationChangedAfterSnapshotReadIsRejectedWithoutCooldown()
    {
        InterleavingRepository repository = new(CreateState());
        repository.ReleaseFirstRead.TrySetResult();
        SupersedingSnapshotProvider snapshots = new();
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            new InMemoryServiceStatus(),
            new InMemoryServiceStatus(),
            new OverridePolicy(snapshots),
            new NightMutationGate(),
            new FixedClock(Now));

        Task<ProtocolCommandResult> request = Task.Run(async () => await handler
            .ExecuteAsync(new RequestOverrideCommand(
                new(OverrideKind.TeamRescue, null))));
        await snapshots.SnapshotCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        snapshots.Supersede();

        ProtocolCommandResult result = await request.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "teamRescueUnavailable",
            result.Payload.GetProperty("error").GetString());
        Assert.Null(repository.State.ActiveOverride);
        Assert.False(repository.State.TeamRescueUsed);
        Assert.Null(repository.Progress.LastTeamRescueAtUtc);
    }

    [Fact]
    public async Task CoordinatorAndOverrideMutation_AreSerializedSoCoordinatorCannotEraseOverride()
    {
        InterleavingRepository repository = new(CreateState());
        InMemoryServiceStatus status = new();
        NightMutationGate gate = new();
        NightStateCoordinator coordinator = new(repository, gate);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new FixedSnapshotProvider(["game.exe"])),
            gate,
            new FixedClock(Now.AddMinutes(2)));
        NightWindow window = new(
            new DateOnly(2026, 7, 6),
            Now.AddHours(-3),
            Now.AddHours(-2),
            Now.AddHours(-1),
            Now.AddMinutes(-30),
            Now.AddHours(9));

        Task<CoordinatorObservation> observation = coordinator
            .ObserveAsync(window, NightPhase.LandingLocked, Now.AddMinutes(1))
            .AsTask();
        await repository.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<ProtocolCommandResult> overrideRequest = handler
            .ExecuteAsync(new RequestOverrideCommand(new(OverrideKind.TeamRescue, null)))
            .AsTask();
        await Task.Yield();

        Assert.Equal(1, repository.ReadCount);
        repository.ReleaseFirstRead.TrySetResult();
        await Task.WhenAll(observation, overrideRequest);

        Assert.Equal(OverrideKind.TeamRescue, repository.State.ActiveOverride!.Kind);
        Assert.True(repository.State.TeamRescueUsed);
    }

    [Theory]
    [InlineData(OverrideKind.TeamRescue, NightPhase.OverrideActive)]
    [InlineData(OverrideKind.Emergency, NightPhase.OverrideActive)]
    [InlineData(OverrideKind.Entertainment, NightPhase.CoolingOff)]
    public async Task MaintenanceSampleCapturedBeforeNewerOverride_DoesNotConsumeOverride(
        OverrideKind kind,
        NightPhase expectedPhase)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        TimeSpan initialUptime = TimeSpan.FromHours(100);
        NightState initialState = CreateState() with
        {
            LastObservedUptime = initialUptime,
            LastObservedBootSessionId = BootSessionId,
        };
        await repository.SaveActiveStateWithEventAsync(initialState, CreateEvent());
        await repository.SaveProgressAsync(ProgressState.Initial);
        NightMutationGate mutationGate = new();
        InMemoryServiceStatus status = new();
        BlockingObservationClock maintenanceClock = new(new(
            Now.AddMinutes(1),
            initialUptime.Add(TimeSpan.FromMinutes(1)),
            BootSessionId));
        PolicyMaintenanceIteration maintenanceIteration = new(
            repository,
            repository,
            repository,
            mutationGate,
            maintenanceClock,
            new FixedTimeZoneProvider(TimeZoneInfo.Utc),
            status,
            new SuccessfulEmptyConfiguredRuleProvider(),
            new SuccessfulEmptyConfiguredSiteRuleProvider());
        Task maintenance = Task.Run(async () => await maintenanceIteration.ExecuteAsync());
        await maintenanceClock.SampleCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ClockObservation newerObservation = new(
            Now.AddMinutes(2),
            initialUptime.Add(TimeSpan.FromMinutes(2)),
            BootSessionId);
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new FixedSnapshotProvider(["game.exe"])),
            mutationGate,
            new ObservationClock(newerObservation));

        ProtocolCommandResult request = await handler.ExecuteAsync(
            new RequestOverrideCommand(Request(kind)));
        NightState afterRequest = (await repository.ReadActiveStateAsync()).Value!;
        ActiveOverride acceptedOverride = afterRequest.ActiveOverride!;
        maintenanceClock.Release();
        await maintenance.WaitAsync(TimeSpan.FromSeconds(5));

        NightState afterMaintenance = (await repository.ReadActiveStateAsync()).Value!;
        ProgressState progress = (await repository.ReadProgressAsync()).Value;
        Assert.True(request.Payload.GetProperty("accepted").GetBoolean());
        Assert.NotNull(afterMaintenance.ActiveOverride);
        Assert.Equal(acceptedOverride.Kind, afterMaintenance.ActiveOverride!.Kind);
        Assert.Equal(acceptedOverride.RequestedAtUtc, afterMaintenance.ActiveOverride.RequestedAtUtc);
        Assert.Equal(acceptedOverride.StartsAtUtc, afterMaintenance.ActiveOverride.StartsAtUtc);
        Assert.Equal(acceptedOverride.EndsAtUtc, afterMaintenance.ActiveOverride.EndsAtUtc);
        Assert.Equal(newerObservation.UtcNow, afterMaintenance.LastObservedUtc);
        Assert.Equal(newerObservation.Uptime, afterMaintenance.LastObservedUptime);
        Assert.Equal(BootSessionId, afterMaintenance.LastObservedBootSessionId);
        Assert.Equal(expectedPhase, status.Current.Policy!.Phase);
        Assert.True(kind switch
        {
            OverrideKind.TeamRescue => afterMaintenance.TeamRescueUsed,
            OverrideKind.Emergency => afterMaintenance.EmergencyUsed,
            OverrideKind.Entertainment => afterMaintenance.EntertainmentUsed,
            _ => false,
        });
        if (kind == OverrideKind.TeamRescue)
        {
            Assert.Equal(newerObservation.UtcNow, progress.LastTeamRescueAtUtc);
        }
    }

    [Theory]
    [InlineData(OverrideKind.TeamRescue, NightPhase.OverrideActive)]
    [InlineData(OverrideKind.Emergency, NightPhase.OverrideActive)]
    [InlineData(OverrideKind.Entertainment, NightPhase.CoolingOff)]
    public async Task AcceptedOverride_AdvancesLastObservedSoRollbackCannotDelayTiming(
        OverrideKind kind,
        NightPhase expectedPhase)
    {
        DateTimeOffset requestedAt = Now.AddMinutes(2);
        InterleavingRepository repository = new(CreateState());
        repository.ReleaseFirstRead.TrySetResult();
        NightMutationGate gate = new();
        NightGateProtocolCommandHandler handler = CreateHandler(repository, gate, requestedAt);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RequestOverrideCommand(Request(kind)));
        CoordinatorObservation rollback = await new NightStateCoordinator(repository, gate).ObserveAsync(
            CreateWindow(), NightPhase.LandingLocked, Now.AddMinutes(1));

        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(requestedAt, repository.State.LastObservedUtc);
        Assert.Equal(requestedAt, rollback.State!.LastObservedUtc);
        Assert.Equal(expectedPhase, rollback.EffectivePhase);
    }

    [Theory]
    [InlineData(OverrideKind.TeamRescue, NightPhase.OverrideActive)]
    [InlineData(OverrideKind.Emergency, NightPhase.OverrideActive)]
    [InlineData(OverrideKind.Entertainment, NightPhase.CoolingOff)]
    public async Task AcceptedOverride_UsesPersistedLastObservedWhenClockIsBehind(
        OverrideKind kind,
        NightPhase expectedPhase)
    {
        InterleavingRepository repository = new(CreateState());
        repository.ReleaseFirstRead.TrySetResult();
        NightMutationGate gate = new();
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            gate,
            Now.AddHours(-1));

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RequestOverrideCommand(Request(kind)));
        CoordinatorObservation rollback = await new NightStateCoordinator(repository, gate).ObserveAsync(
            CreateWindow(), NightPhase.LandingLocked, Now.AddMinutes(-30));

        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(Now, repository.State.LastObservedUtc);
        Assert.Equal(Now, repository.State.ActiveOverride!.RequestedAtUtc);
        Assert.Equal(expectedPhase, rollback.EffectivePhase);
    }

    [Theory]
    [InlineData(OverrideKind.TeamRescue)]
    [InlineData(OverrideKind.Emergency)]
    [InlineData(OverrideKind.Entertainment)]
    public async Task SameBootRollback_ConsumesRealElapsedOverrideTimeAndPersistsAnchors(
        OverrideKind kind)
    {
        TimeSpan initialUptime = TimeSpan.FromHours(100);
        InterleavingRepository repository = new(
            CreateState() with
            {
                LastObservedUptime = initialUptime,
                LastObservedBootSessionId = BootSessionId,
            });
        repository.ReleaseFirstRead.TrySetResult();
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            new NightMutationGate(),
            new ObservationClock(new(Now, initialUptime, BootSessionId)));

        ProtocolCommandResult request = await handler.ExecuteAsync(
            new RequestOverrideCommand(Request(kind)));
        CoordinatorObservation elapsed = await new NightStateCoordinator(
                repository,
                new NightMutationGate())
            .ObserveAsync(
                CreateWindow(),
                NightPhase.LandingLocked,
                new ClockObservation(
                    Now.AddHours(-1),
                    initialUptime.Add(TimeSpan.FromMinutes(10)),
                    BootSessionId));

        Assert.True(request.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(Now.AddMinutes(10), elapsed.State!.LastObservedUtc);
        Assert.Equal(initialUptime.Add(TimeSpan.FromMinutes(10)), elapsed.State.LastObservedUptime);
        Assert.Equal(BootSessionId, elapsed.State.LastObservedBootSessionId);
        Assert.Equal(NightPhase.OverrideActive, elapsed.EffectivePhase);
    }

    [Theory]
    [InlineData(OverrideKind.TeamRescue, 20)]
    [InlineData(OverrideKind.Emergency, 30)]
    [InlineData(OverrideKind.Entertainment, 30)]
    public async Task SameBootRollback_ExpiresOverrideAtExactElapsedEnd(
        OverrideKind kind,
        int durationMinutes)
    {
        TimeSpan initialUptime = TimeSpan.FromHours(100);
        InterleavingRepository repository = new(
            CreateState() with
            {
                LastObservedUptime = initialUptime,
                LastObservedBootSessionId = BootSessionId,
            });
        repository.ReleaseFirstRead.TrySetResult();
        await CreateHandler(
                repository,
                new NightMutationGate(),
                new ObservationClock(new(Now, initialUptime, BootSessionId)))
            .ExecuteAsync(new RequestOverrideCommand(Request(kind)));

        CoordinatorObservation elapsed = await new NightStateCoordinator(
                repository,
                new NightMutationGate())
            .ObserveAsync(
                CreateWindow(),
                NightPhase.LandingLocked,
                new ClockObservation(
                    Now.AddHours(-1),
                    initialUptime.Add(TimeSpan.FromMinutes(durationMinutes)),
                    BootSessionId));

        Assert.Equal(Now.AddMinutes(durationMinutes), elapsed.State!.LastObservedUtc);
        Assert.Equal(
            initialUptime.Add(TimeSpan.FromMinutes(durationMinutes)),
            elapsed.State.LastObservedUptime);
        Assert.Equal(BootSessionId, elapsed.State.LastObservedBootSessionId);
        Assert.Null(elapsed.State.ActiveOverride);
        Assert.Equal(NightPhase.LandingLocked, elapsed.EffectivePhase);
    }

    [Theory]
    [InlineData(OverrideKind.TeamRescue, 5)]
    [InlineData(OverrideKind.TeamRescue, 100)]
    [InlineData(OverrideKind.TeamRescue, 150)]
    [InlineData(OverrideKind.Emergency, 5)]
    [InlineData(OverrideKind.Emergency, 100)]
    [InlineData(OverrideKind.Emergency, 150)]
    [InlineData(OverrideKind.Entertainment, 5)]
    [InlineData(OverrideKind.Entertainment, 100)]
    [InlineData(OverrideKind.Entertainment, 150)]
    public async Task DifferentBootIdWithAnyUptime_ConservativelyExpiresOverrideAtExactEnd(
        OverrideKind kind,
        int restartedUptimeHours)
    {
        TimeSpan initialUptime = TimeSpan.FromHours(100);
        InterleavingRepository repository = new(
            CreateState() with
            {
                LastObservedUptime = initialUptime,
                LastObservedBootSessionId = BootSessionId,
            });
        repository.ReleaseFirstRead.TrySetResult();
        await CreateHandler(
                repository,
                new NightMutationGate(),
                new ObservationClock(new(Now, initialUptime, BootSessionId)))
            .ExecuteAsync(new RequestOverrideCommand(Request(kind)));
        DateTimeOffset expectedEnd = repository.State.ActiveOverride!.EndsAtUtc;

        CoordinatorObservation restarted = await new NightStateCoordinator(
                repository,
                new NightMutationGate())
            .ObserveAsync(
                CreateWindow(),
                NightPhase.LandingLocked,
                new ClockObservation(
                    Now.AddHours(-1),
                    TimeSpan.FromHours(restartedUptimeHours),
                    RestartedBootSessionId));

        Assert.Equal(expectedEnd, restarted.State!.LastObservedUtc);
        Assert.Equal(TimeSpan.FromHours(restartedUptimeHours), restarted.State.LastObservedUptime);
        Assert.Equal(RestartedBootSessionId, restarted.State.LastObservedBootSessionId);
        Assert.Null(restarted.State.ActiveOverride);
        Assert.Equal(NightPhase.LandingLocked, restarted.EffectivePhase);
        Assert.True(kind switch
        {
            OverrideKind.TeamRescue => restarted.State.TeamRescueUsed,
            OverrideKind.Emergency => restarted.State.EmergencyUsed,
            OverrideKind.Entertainment => restarted.State.EntertainmentUsed,
            _ => false,
        });
        if (kind == OverrideKind.TeamRescue)
        {
            Assert.Equal(Now, repository.Progress.LastTeamRescueAtUtc);
        }
    }

    [Fact]
    public async Task LegacyUptimeAnchorWithoutBootId_IsUntrustedAndRefreshesConservatively()
    {
        TimeSpan legacyUptime = TimeSpan.FromHours(100);
        ActiveOverride activeOverride = new(
            OverrideKind.TeamRescue,
            Now,
            Now,
            Now.AddMinutes(20),
            ["game.exe"]);
        InterleavingRepository repository = new(
            CreateState() with
            {
                LastObservedUptime = legacyUptime,
                ActiveOverride = activeOverride,
                TeamRescueUsed = true,
            });
        repository.ReleaseFirstRead.TrySetResult();

        CoordinatorObservation result = await new NightStateCoordinator(repository).ObserveAsync(
            CreateWindow(),
            NightPhase.LandingLocked,
            new ClockObservation(
                Now.AddHours(-1),
                legacyUptime + TimeSpan.FromHours(1),
                RestartedBootSessionId));

        Assert.Equal(activeOverride.EndsAtUtc, result.State!.LastObservedUtc);
        Assert.Null(result.State.ActiveOverride);
        Assert.True(result.State.TeamRescueUsed);
        Assert.Equal(legacyUptime + TimeSpan.FromHours(1), result.State.LastObservedUptime);
        Assert.Equal(RestartedBootSessionId, result.State.LastObservedBootSessionId);
    }

    private static NightGateProtocolCommandHandler CreateHandler(
        InterleavingRepository repository,
        NightMutationGate gate,
        DateTimeOffset clockUtc) => new(
            repository,
            repository,
            repository,
            new InMemoryServiceStatus(),
            new InMemoryServiceStatus(),
            new OverridePolicy(new FixedSnapshotProvider(["game.exe"])),
            gate,
            new FixedClock(clockUtc));

    private static NightGateProtocolCommandHandler CreateHandler(
        InterleavingRepository repository,
        NightMutationGate gate,
        IClock clock) => new(
            repository,
            repository,
            repository,
            new InMemoryServiceStatus(),
            new InMemoryServiceStatus(),
            new OverridePolicy(new FixedSnapshotProvider(["game.exe"])),
            gate,
            clock);

    private static NightGateProtocolCommandHandler CreateDatabaseHandler(
        INightStateRepository stateRepository,
        SqliteNightGateRepository repository)
    {
        InMemoryServiceStatus status = new();
        return new(
            stateRepository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new FixedSnapshotProvider([])),
            new NightMutationGate(),
            new FixedClock(Now));
    }

    private static NightWindow CreateWindow() => new(
        new DateOnly(2026, 7, 6),
        Now.AddHours(-3),
        Now.AddHours(-2),
        Now.AddHours(-1),
        Now.AddMinutes(-30),
        Now.AddHours(9));

    private static OverrideRequest Request(OverrideKind kind) => new(
        kind,
        kind == OverrideKind.Emergency ? EmergencyReason.Health : null);

    private static NightState CreateState() => new(
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
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

    private static NightEvent CreateEvent() => new(
        Guid.NewGuid(),
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
        Now,
        NightEventKind.StateObserved,
        NightPhase.LandingLocked);

    private sealed class FixedSnapshotProvider(ImmutableArray<string> snapshot) :
        IAllowedProcessSnapshotProvider
    {
        public ImmutableArray<string> GetSnapshot() => snapshot;
    }

    private sealed class SupersedingSnapshotProvider :
        IAllowedProcessSnapshotProvider
    {
        private readonly TaskCompletionSource _continue =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _generation = 1;
        private int _firstRead = 1;

        public TaskCompletionSource SnapshotCaptured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ImmutableArray<string> GetSnapshot() =>
            GetSnapshotResult().Identifiers;

        public AllowedProcessSnapshotResult GetSnapshotResult()
        {
            int generation = Volatile.Read(ref _generation);
            AllowedProcessSnapshotResult result = generation == 1
                ? AllowedProcessSnapshotResult.Available(
                    ["game.exe"],
                    generation)
                : AllowedProcessSnapshotResult.Unavailable(
                    "rules-superseded",
                    generation);
            if (Interlocked.Exchange(ref _firstRead, 0) == 1)
            {
                SnapshotCaptured.TrySetResult();
                _continue.Task.WaitAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter()
                    .GetResult();
            }

            return result;
        }

        public IDisposable? TryAcquireValidationLease(long? expectedGeneration) =>
            expectedGeneration == Volatile.Read(ref _generation)
                ? NoopLease.Instance
                : null;

        public void Supersede()
        {
            Volatile.Write(ref _generation, 2);
            _continue.TrySetResult();
        }

        private sealed class NoopLease : IDisposable
        {
            public static NoopLease Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class ObservationClock(ClockObservation observation) : IClock
    {
        public DateTimeOffset UtcNow => observation.UtcNow;

        public ClockObservation Observe() => observation;
    }

    private sealed class BlockingObservationClock(ClockObservation observation) : IClock
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SampleCaptured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset UtcNow => observation.UtcNow;

        public ClockObservation Observe()
        {
            SampleCaptured.TrySetResult();
            _release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return observation;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class FixedTimeZoneProvider(TimeZoneInfo timeZone) : ITimeZoneProvider
    {
        public TimeZoneInfo Local => timeZone;
    }

    private sealed class FirstReadRendezvous
    {
        private int _arrivals;
        private readonly TaskCompletionSource _bothArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _bothArrived.TrySetResult();
            }

            await _bothArrived.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class RendezvousStateRepository(
        SqliteNightGateRepository inner,
        FirstReadRendezvous rendezvous) : INightStateRepository
    {
        private int _readCount;

        public async ValueTask<StorageResult<NightState?>> ReadActiveStateAsync(
            CancellationToken cancellationToken = default)
        {
            StorageResult<NightState?> result = await inner
                .ReadActiveStateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                await rendezvous.ArriveAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        public ValueTask<StorageWriteResult> SaveActiveStateWithEventAsync(
            NightState state,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            inner.SaveActiveStateWithEventAsync(
                state,
                nightEvent,
                expectedVersion,
                cancellationToken);

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

    private static async Task<long> CountEventsAsync(string databasePath)
    {
        await using SqliteConnection connection = new(
            $"Data Source={databasePath};Pooling=False;Default Timeout=1");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_events;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class InterleavingRepository(NightState initialState) :
        INightStateRepository,
        IProgressRepository,
        IHistoryRepository
    {
        private int _readCount;

        public NightState State { get; private set; } = initialState;

        public ProgressState Progress { get; private set; } = ProgressState.Initial;

        public int ReadCount => Volatile.Read(ref _readCount);

        public TaskCompletionSource FirstReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<StorageResult<NightState?>> ReadActiveStateAsync(
            CancellationToken cancellationToken = default)
        {
            int count = Interlocked.Increment(ref _readCount);
            if (count == 1)
            {
                FirstReadStarted.TrySetResult();
                await ReleaseFirstRead.Task.WaitAsync(cancellationToken);
            }

            return new(StorageMode.Success, State);
        }

        public ValueTask<StorageWriteResult> SaveActiveStateWithEventAsync(
            NightState state,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            State = state;
            return ValueTask.FromResult(StorageWriteResult.Success);
        }

        public ValueTask<StorageWriteResult> SaveActiveStateProgressWithEventAsync(
            NightState state,
            ProgressState progress,
            NightEvent nightEvent,
            long? expectedStateVersion = null,
            long? expectedProgressVersion = null,
            CancellationToken cancellationToken = default)
        {
            State = state;
            Progress = progress;
            return ValueTask.FromResult(StorageWriteResult.Success);
        }

        public ValueTask<StorageWriteResult> CloseActiveStateWithOutcomeAndEventAsync(
            NightState closedState,
            NightOutcome outcome,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageResult<ProgressState>> ReadProgressAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StorageResult<ProgressState>(StorageMode.Success, Progress));

        public ValueTask<StorageWriteResult> SaveProgressAsync(
            ProgressState progress,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            Progress = progress;
            return ValueTask.FromResult(StorageWriteResult.Success);
        }

        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestOutcomesAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StorageResult<IReadOnlyList<NightOutcome>>(
                StorageMode.Success,
                Array.Empty<NightOutcome>()));

        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestEligibleOutcomesAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            ReadLatestOutcomesAsync(count, cancellationToken);

        public ValueTask<StorageWriteResult> SaveOutcomeAsync(
            NightOutcome outcome,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageWriteResult> RecordEventAsync(
            NightEvent nightEvent,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageWriteResult> PurgeEventsOlderThanAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageWriteResult> ClearHistoryAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);
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

    private sealed class SuccessfulEmptyConfiguredRuleProvider : IConfiguredRuleProvider
    {
        public ConfiguredRuleProviderResult GetRules() =>
            ConfiguredRuleProviderResult.Success([]);
    }

    private sealed class SuccessfulEmptyConfiguredSiteRuleProvider :
        IConfiguredSiteRuleProvider
    {
        public ConfiguredSiteRuleProviderResult GetRules() =>
            ConfiguredSiteRuleProviderResult.Success([]);
    }

    private sealed class RecordingPolicyMaintenanceScheduler :
        IPolicyMaintenanceScheduler
    {
        public int CallCount { get; private set; }

        public bool LastForce { get; private set; }

        public int DirtyMarks { get; private set; }

        public void MarkDirty() => DirtyMarks++;

        public ValueTask RefreshAsync(
            bool force,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastForce = force;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingAfterMarkScheduler(
        IPolicyMaintenanceScheduler inner,
        CancellationTokenSource cancellation) : IPolicyMaintenanceScheduler
    {
        public int DirtyMarks { get; private set; }

        public int ForceRefreshCalls { get; private set; }

        public int NonForceRefreshCalls { get; private set; }

        public void MarkDirty()
        {
            DirtyMarks++;
            inner.MarkDirty();
            cancellation.Cancel();
        }

        public ValueTask RefreshAsync(
            bool force,
            CancellationToken cancellationToken = default)
        {
            if (force)
            {
                ForceRefreshCalls++;
            }
            else
            {
                NonForceRefreshCalls++;
            }

            return inner.RefreshAsync(force, cancellationToken);
        }
    }
}
