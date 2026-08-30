using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class AllowedProcessSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TeamRescue_UsesImmutableTrustedServerSnapshotNotRequestData()
    {
        MutableAllowedProcessSnapshotProvider provider = new(["game.exe", "voice.exe"]);
        OverridePolicy policy = new(provider);

        OverrideDecision decision = policy.Request(
            CreateState(),
            ProgressState.Initial,
            new(OverrideKind.TeamRescue, null),
            Now);
        provider.Replace(["browser.exe"]);

        Assert.True(decision.Accepted);
        Assert.Equal(
            ["game.exe", "voice.exe"],
            decision.State.ActiveOverride!.AllowedProcessIdentifiers.ToArray());
        Assert.DoesNotContain(
            typeof(OverrideRequest).GetProperties(),
            property => property.Name.Contains("Process", StringComparison.Ordinal));
    }

    [Fact]
    public void PersistedActiveRuleSnapshot_RulesAloneDoNotPretendAConfiguredGameIsRunning()
    {
        PersistedActiveRuleSnapshot snapshot = new();
        AppRule first = new(
            "game",
            @"C:\Games\game.exe",
            [],
            AppRuleCategory.Game,
            35);
        AppRule voice = new(
            "voice",
            @"C:\Voice\voice.exe",
            [],
            AppRuleCategory.Voice,
            35);

        Assert.False(snapshot.GetSnapshotResult().IsAvailable);

        snapshot.Publish([first, voice]);

        AllowedProcessSnapshotResult published = snapshot.GetSnapshotResult();
        Assert.False(published.IsAvailable);
        Assert.Empty(published.Identifiers);
        Assert.Equal("process-snapshot-unavailable", published.DegradationCode);
    }

    [Fact]
    public void PersistedActiveRuleSnapshot_UsesOnlyObservedCurrentGameAndConfiguredVoiceRules()
    {
        MutableClock clock = TrustedClock();
        MutableCurrentProcessWitnessProvider witness = new(
            Witness(@"C:\Games\Running\game.exe", pid: 42));
        PersistedActiveRuleSnapshot snapshot = new(
            clock,
            configuredUserSid: "S-1-5-21-1000",
            witness);
        snapshot.Publish(
        [
            Game("running", @"C:\Games\Running\game.exe"),
            Game("configured-only", @"C:\Games\ConfiguredOnly\game.exe"),
            Voice("voice", @"C:\Voice\voice.exe"),
        ]);

        snapshot.PublishProcessSnapshot(ProcessRecord(
            @"C:\Games\Running\game.exe",
            pid: 42));

        AllowedProcessSnapshotResult published = snapshot.GetSnapshotResult();
        Assert.True(published.IsAvailable);
        Assert.Equal(["running", "voice"], published.Identifiers.ToArray());
    }

    [Fact]
    public async Task PersistedActiveRuleSnapshot_DoesNotCommitAnObservationBuiltFromSupersededRules()
    {
        using ManualResetEventSlim rulesCaptured = new(false);
        using ManualResetEventSlim rulesReplaced = new(false);
        PersistedActiveRuleSnapshot snapshot = new(
            clock: null,
            processSnapshotRulesCaptured: () =>
            {
                rulesCaptured.Set();
                if (!rulesReplaced.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Replacement rules were not published.");
                }
            });
        snapshot.Publish([Game("old-game", @"C:\Games\Old\game.exe")]);

        Task observation = Task.Run(() => snapshot.PublishProcessSnapshot(
            ProcessRecord(@"C:\Games\Old\game.exe", pid: 42)));
        Assert.True(rulesCaptured.Wait(TimeSpan.FromSeconds(5)));
        Task replacement = Task.Run(() =>
        {
            snapshot.Publish([Game("new-game", @"C:\Games\New\game.exe")]);
            rulesReplaced.Set();
        });

        await Task.WhenAll(observation, replacement).WaitAsync(TimeSpan.FromSeconds(10));

        AllowedProcessSnapshotResult result = snapshot.GetSnapshotResult();
        Assert.False(result.IsAvailable);
        Assert.Empty(result.Identifiers);
        Assert.Equal("process-snapshot-unavailable", result.DegradationCode);
    }

    [Fact]
    public void TeamRescue_WithoutAnObservedCurrentGameIsRejectedWithoutConsumingCooldown()
    {
        PersistedActiveRuleSnapshot snapshot = new(
            TrustedClock(),
            configuredUserSid: "S-1-5-21-1000");
        snapshot.Publish(
        [
            Game("configured-only", @"C:\Games\ConfiguredOnly\game.exe"),
            Voice("voice", @"C:\Voice\voice.exe"),
        ]);
        snapshot.PublishProcessSnapshot(ProcessRecord(
            @"C:\Voice\voice.exe",
            pid: 43,
            ruleId: "voice"));
        NightState state = CreateState();
        ProgressState progress = ProgressState.Initial;

        OverrideDecision decision = new OverridePolicy(snapshot).Request(
            state,
            progress,
            new(OverrideKind.TeamRescue, null),
            Now);

        Assert.False(decision.Accepted);
        Assert.Equal(OverrideError.TeamRescueUnavailable, decision.Error);
        Assert.Equal(state, decision.State);
        Assert.Equal(progress, decision.Progress);
        Assert.Null(decision.Progress.LastTeamRescueAtUtc);
    }

    [Fact]
    public void TeamRescue_RejectsProcessEvidenceForAUserOtherThanTheConfiguredTarget()
    {
        MutableClock clock = new(
            Now,
            TimeSpan.FromHours(100),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        PersistedActiveRuleSnapshot snapshot = new(
            clock,
            configuredUserSid: "S-1-5-21-2000");
        snapshot.Publish([Game("running", @"C:\Games\Running\game.exe")]);

        snapshot.PublishProcessSnapshot(ProcessRecord(
            @"C:\Games\Running\game.exe",
            pid: 42));

        AllowedProcessSnapshotResult result = snapshot.GetSnapshotResult();
        Assert.False(result.IsAvailable);
        Assert.Equal("process-snapshot-identity-untrusted", result.DegradationCode);
    }

    [Fact]
    public void TeamRescue_StaleObservationCannotBeRevivedByWallClockRollback()
    {
        MutableClock clock = new(
            Now,
            TimeSpan.FromHours(100),
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        PersistedActiveRuleSnapshot snapshot = new(
            clock,
            configuredUserSid: "S-1-5-21-1000",
            new MutableCurrentProcessWitnessProvider(
                Witness(@"C:\Games\Running\game.exe", pid: 42)));
        snapshot.Publish([Game("running", @"C:\Games\Running\game.exe")]);
        snapshot.PublishProcessSnapshot(ProcessRecord(
            @"C:\Games\Running\game.exe",
            pid: 42));
        clock.UtcNow = Now.AddSeconds(11);
        clock.Uptime += TimeSpan.FromSeconds(11);

        AllowedProcessSnapshotResult stale = snapshot.GetSnapshotResult();
        clock.UtcNow = Now.AddSeconds(1);
        AllowedProcessSnapshotResult afterWallClockRollback =
            snapshot.GetSnapshotResult();

        Assert.False(stale.IsAvailable);
        Assert.Equal("process-snapshot-stale", stale.DegradationCode);
        Assert.False(afterWallClockRollback.IsAvailable);
        Assert.Equal(
            "process-snapshot-stale",
            afterWallClockRollback.DegradationCode);
    }

    [Fact]
    public void TeamRescue_RejectsEvidenceWhoseLiveProcessIsInAnotherSession()
    {
        PersistedActiveRuleSnapshot snapshot = new(
            TrustedClock(),
            configuredUserSid: "S-1-5-21-1000",
            new MutableCurrentProcessWitnessProvider(
                Witness(
                    @"C:\Games\Running\game.exe",
                    pid: 42,
                    sessionId: 4)));
        snapshot.Publish([Game("running", @"C:\Games\Running\game.exe")]);

        snapshot.PublishProcessSnapshot(ProcessRecord(
            @"C:\Games\Running\game.exe",
            pid: 42,
            sessionId: 3));

        AllowedProcessSnapshotResult result = snapshot.GetSnapshotResult();
        Assert.False(result.IsAvailable);
        Assert.Equal("process-snapshot-identity-untrusted", result.DegradationCode);
    }

    [Fact]
    public void TeamRescue_ExitedRetainedKnownInstanceCannotConsumeCooldown()
    {
        MutableCurrentProcessWitnessProvider witness = new(
            Witness(@"C:\Games\Running\game.exe", pid: 42))
        {
            IsAvailable = false,
        };
        PersistedActiveRuleSnapshot snapshot = new(
            TrustedClock(),
            configuredUserSid: "S-1-5-21-1000",
            witness);
        snapshot.Publish([Game("running", @"C:\Games\Running\game.exe")]);
        snapshot.PublishProcessSnapshot(ProcessRecord(
            @"C:\Games\Running\game.exe",
            pid: 42));

        ProgressState progress = ProgressState.Initial;
        OverrideDecision decision = new OverridePolicy(snapshot).Request(
            CreateState(),
            progress,
            new(OverrideKind.TeamRescue, null),
            Now);

        Assert.False(decision.Accepted);
        Assert.Equal(OverrideError.TeamRescueUnavailable, decision.Error);
        Assert.Null(decision.Progress.LastTeamRescueAtUtc);
        Assert.Equal(progress, decision.Progress);
    }

    [Fact]
    public void TeamRescue_RejectsPersistedEvidenceAfterBootChanges()
    {
        MutableClock clock = TrustedClock();
        PersistedActiveRuleSnapshot snapshot = new(
            clock,
            configuredUserSid: "S-1-5-21-1000",
            new MutableCurrentProcessWitnessProvider(
                Witness(@"C:\Games\Running\game.exe", pid: 42)));
        snapshot.Publish([Game("running", @"C:\Games\Running\game.exe")]);
        snapshot.PublishProcessSnapshot(ProcessRecord(
            @"C:\Games\Running\game.exe",
            pid: 42));
        clock.BootSessionId = Guid.Parse(
            "cccccccc-cccc-cccc-cccc-cccccccccccc");

        AllowedProcessSnapshotResult result = snapshot.GetSnapshotResult();

        Assert.False(result.IsAvailable);
        Assert.Equal("process-snapshot-stale", result.DegradationCode);
    }

    [Fact]
    public void TeamRescue_RejectsEvidenceWithoutMonotonicClockProof()
    {
        PersistedActiveRuleSnapshot snapshot = new(
            new MutableClock(Now),
            configuredUserSid: "S-1-5-21-1000",
            new MutableCurrentProcessWitnessProvider(
                Witness(@"C:\Games\Running\game.exe", pid: 42)));
        snapshot.Publish([Game("running", @"C:\Games\Running\game.exe")]);
        snapshot.PublishProcessSnapshot(ProcessRecord(
            @"C:\Games\Running\game.exe",
            pid: 42));

        AllowedProcessSnapshotResult result = snapshot.GetSnapshotResult();

        Assert.False(result.IsAvailable);
        Assert.Equal("process-snapshot-stale", result.DegradationCode);
    }

    [Fact]
    public void TeamRescue_RejectsPersistedObservationContinuityLoss()
    {
        PersistedActiveRuleSnapshot snapshot = new(
            TrustedClock(),
            configuredUserSid: "S-1-5-21-1000",
            new MutableCurrentProcessWitnessProvider(
                Witness(@"C:\Games\Running\game.exe", pid: 42)));
        snapshot.Publish([Game("running", @"C:\Games\Running\game.exe")]);
        snapshot.PublishProcessSnapshot(ProcessRecord(
            @"C:\Games\Running\game.exe",
            pid: 42,
            continuityLost: true));

        AllowedProcessSnapshotResult result = snapshot.GetSnapshotResult();

        Assert.False(result.IsAvailable);
        Assert.Equal(
            "process-snapshot-continuity-untrusted",
            result.DegradationCode);
    }

    [Fact]
    public void PersistedSnapshot_RulePublicationInvalidatesReadGeneration()
    {
        PersistedActiveRuleSnapshot snapshot = new(
            TrustedClock(),
            configuredUserSid: "S-1-5-21-1000",
            new MutableCurrentProcessWitnessProvider(
                Witness(@"C:\Games\Running\game.exe", pid: 42)));
        snapshot.Publish([Game("running", @"C:\Games\Running\game.exe")]);
        snapshot.PublishProcessSnapshot(ProcessRecord(
            @"C:\Games\Running\game.exe",
            pid: 42));
        AllowedProcessSnapshotResult read = snapshot.GetSnapshotResult();
        Assert.True(read.IsAvailable);
        Assert.NotNull(read.Generation);

        snapshot.Publish([Game("replacement", @"C:\Games\Other\game.exe")]);

        Assert.Null(snapshot.TryAcquireValidationLease(read.Generation));
        AllowedProcessSnapshotResult afterRuleChange = snapshot.GetSnapshotResult();
        Assert.False(afterRuleChange.IsAvailable);
        Assert.NotEqual(read.Generation, afterRuleChange.Generation);
    }

    private static AppRule Game(string id, string path) => new(
        id,
        path,
        [],
        AppRuleCategory.Game,
        35);

    private static AppRule Voice(string id, string path) => new(
        id,
        path,
        [],
        AppRuleCategory.Voice,
        35);

    private static ProcessPersistenceRecord ProcessRecord(
        string executablePath,
        int pid,
        string ruleId = "running",
        string userSid = "S-1-5-21-1000",
        int sessionId = 3,
        bool continuityLost = false)
    {
        long creationTicks = CreationTicks;
        object key = new { pid, creationUtcTicks = creationTicks };
        string payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            envelope = new
            {
                revision = 1,
                reducerState = new
                {
                    nightDate = "2026-07-06",
                    knownInstances = new[]
                    {
                        new
                        {
                            key,
                            value = new
                            {
                                identity = new
                                {
                                    key,
                                    creationInstantUtc = new DateTimeOffset(
                                        2026,
                                        7,
                                        6,
                                        23,
                                        0,
                                        0,
                                        TimeSpan.Zero),
                                    executablePath,
                                    userSid,
                                    sessionId,
                                },
                                parent = new
                                {
                                    kind = "none",
                                    exactParent = (object?)null,
                                },
                            },
                        },
                    },
                    eligibleInstances = new[] { new { key, ruleId } },
                    taintedInstances = Array.Empty<object>(),
                    observerContinuityEpoch = "epoch-a",
                    creationTimelineTrusted = true,
                    morningReleased = false,
                },
                observationContinuity = new
                {
                    isLost = continuityLost,
                    trustSeverPersisted = continuityLost,
                    lastTrustedEpoch = "epoch-a",
                    lossEpoch = continuityLost ? "epoch-a" : null,
                    clockEpoch = "epoch-a",
                    sampleUtcHighWater = Now,
                    sampleMonotonicHighWater = TimeSpan.FromHours(100),
                    acknowledgementCheckpoint = (object?)null,
                },
            },
        });
        return new(
            ProcessPersistenceSlot.ProcessGateEnvelope,
            ProcessPersistenceLimits.CurrentSchemaVersion,
            1,
            payload);
    }

    private static readonly long CreationTicks = new DateTimeOffset(
        2026, 7, 6, 23, 0, 0, TimeSpan.Zero).UtcTicks;

    private static MutableClock TrustedClock() => new(
        Now,
        TimeSpan.FromHours(100),
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    private static CurrentProcessWitness Witness(
        string executablePath,
        int pid,
        int sessionId = 3) => new(
        pid,
        CreationTicks,
        executablePath,
        sessionId);

    private sealed class MutableClock(
        DateTimeOffset utcNow,
        TimeSpan? uptime = null,
        Guid? bootSessionId = null) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public TimeSpan? Uptime { get; set; } = uptime;

        public Guid? BootSessionId { get; set; } = bootSessionId;

        public ClockObservation Observe() => new(UtcNow, Uptime, BootSessionId);
    }

    private sealed class MutableCurrentProcessWitnessProvider(
        CurrentProcessWitness current) : ICurrentProcessWitnessProvider
    {
        public CurrentProcessWitness Current { get; set; } = current;

        public bool IsAvailable { get; set; } = true;

        public bool TryRead(int processId, out CurrentProcessWitness witness)
        {
            witness = Current;
            return IsAvailable && processId == Current.ProcessId;
        }
    }

    [Fact]
    public async Task Dispatcher_RejectsClientSuppliedRescueAllowlistWithoutExecution()
    {
        CountingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        byte[] message = Encoding.UTF8.GetBytes(
            "{\"version\":1,\"type\":\"requestOverride\",\"requestId\":\"allowlist\",\"payload\":{" +
            "\"kind\":\"teamRescue\",\"allowedProcessIdentifiers\":[\"browser.exe\"]}}");

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(message);

        Assert.False(result.CommandExecuted);
        Assert.Equal(0, handler.ExecutionCount);
    }

    private static NightState CreateState() => new(
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
        false);

    private sealed class MutableAllowedProcessSnapshotProvider(
        ImmutableArray<string> initial) : IAllowedProcessSnapshotProvider
    {
        private ImmutableArray<string> _current = initial;

        public ImmutableArray<string> GetSnapshot() => _current;

        public void Replace(ImmutableArray<string> replacement) => _current = replacement;
    }

    private sealed class CountingHandler : IProtocolCommandHandler
    {
        public int ExecutionCount { get; private set; }

        public ValueTask<ProtocolCommandResult> ExecuteAsync(
            ServiceCommand command,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(ProtocolCommandResult.Success(new { accepted = true }));
        }
    }
}
