using System.Collections.Immutable;
using System.Text;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class ProcessGateCoordinatorTests
{
    private const string UserSid = "S-1-5-21-1000";
    private const int SessionId = 7;
    private static readonly DateOnly NightDate = new(2026, 7, 6);
    private static readonly DateTimeOffset Cutoff = At(7, 0, 5);
    private static readonly ProcessObservation NewRoot = Root(42, Cutoff.AddTicks(1));

    [Fact]
    public async Task PullsCarryTheSameValidatedPolicyBindingUsedForEachEvaluation()
    {
        DesktopAppRuleDto boundRule = new(
            "game",
            @"C:\Games\game.exe",
            [@"C:\Games\helper.exe"],
            DesktopAppRuleCategory.Game,
            35,
            true);
        ScriptedObservationSource observations = new(
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot));
        await using ProcessGateCoordinator coordinator = new(
            new MemoryEnvelopeStore([]),
            new QueuePolicySource(
                PolicyEvidence(11, "policy-11", At(7, 0, 6), Policy(At(7, 0, 6), rules: [boundRule])),
                PolicyEvidence(12, "policy-12", At(7, 0, 11), Policy(At(7, 0, 11), rules: [boundRule]))),
            observations,
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        ProcessCatalogReadRequest batch = Assert.Single(observations.BatchRequests);
        Assert.Equal(ProcessObservationBatchKind.StartDelta, batch.RequestedKind);
        Assert.Equal(11, batch.PolicyBinding.PolicyRevision);
        Assert.Equal("policy-11", batch.PolicyBinding.EvaluationIdentity);
        Assert.Equal(UserSid, batch.PolicyBinding.InteractiveUserSid);
        Assert.Equal(SessionId, batch.PolicyBinding.InteractiveSessionId);
        Assert.Equal(
            [@"C:\Games\game.exe", @"C:\Games\helper.exe"],
            batch.PolicyBinding.CanonicalExecutablePaths,
            StringComparer.OrdinalIgnoreCase);

        (ProcessExactTarget Target, ProcessCatalogPolicyBinding Binding) exact =
            Assert.Single(observations.ExactRequests);
        Assert.Equal(NewRoot.Identity!.Key, exact.Target.InstanceKey);
        Assert.Equal(12, exact.Binding.PolicyRevision);
        Assert.Equal("policy-12", exact.Binding.EvaluationIdentity);
        Assert.Equal(
            batch.PolicyBinding.CanonicalExecutablePaths,
            exact.Binding.CanonicalExecutablePaths,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewRoot_PersistsEachClaimBeforeCloseAndTermination()
    {
        List<string> order = [];
        MemoryEnvelopeStore store = new(order);
        QueuePolicySource policies = new(
            PolicyEvidence(1, "policy-1", At(7, 0, 6)),
            PolicyEvidence(2, "policy-2", At(7, 0, 11)));
        ScriptedObservationSource observations = new(
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot));
        RecordingActions actions = new(order);
        RecordingMonotonicDelay delay = new(order);
        await using ProcessGateCoordinator coordinator = new(
            store,
            policies,
            observations,
            actions,
            delay);

        ProcessGateRunResult result = await coordinator.EvaluateAsync(
            new(ProcessObservationBatchKind.StartDelta, UserSid, SessionId));

        Assert.Equal(1, actions.CloseCalls);
        await EventuallyAsync(() => actions.TerminationCalls == 1);
        Assert.Equal(1, actions.TerminationCalls);
        Assert.Equal([TimeSpan.FromSeconds(5)], delay.Requests);
        Assert.True(order.IndexOf("save:close-claim") < order.IndexOf("action:close"));
        Assert.True(order.IndexOf("save:terminate-claim") < order.IndexOf("action:terminate"));
        Assert.Contains(result.Outcomes, outcome =>
            outcome.Kind == ProcessGateOutcomeKind.CloseRequested);
        Assert.DoesNotContain(result.Outcomes, outcome =>
            outcome.Kind == ProcessGateOutcomeKind.TerminateAttempted);
        Assert.Equal(2, policies.ReadCount);
        Assert.Equal(1, observations.ExactReadCount);
    }

    [Fact]
    public async Task GraceRecheck_ExactLeaseReplayAfterClockRollbackStillTerminates()
    {
        List<string> order = [];
        DateTimeOffset leaseAt = At(7, 0, 6);
        ValidatedProcessPolicy lease = PolicyEvidence(
            leaseAt.UtcTicks,
            "rollback-stable-lease",
            leaseAt,
            payloadFingerprint: "rollback-stable-payload");
        QueuePolicySource policies = new(lease, lease);
        ScriptedObservationSource observations = new(
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot));
        RecordingActions actions = new(order);
        await using ProcessGateCoordinator coordinator = new(
            new MemoryEnvelopeStore(order),
            policies,
            observations,
            actions,
            new RecordingMonotonicDelay(order));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await EventuallyAsync(() => actions.TerminationCalls == 1);

        Assert.Equal(2, policies.ReadCount);
        Assert.Equal(1, observations.ExactReadCount);
        Assert.Equal(1, actions.TerminationCalls);
    }

    [Fact]
    public async Task GraceContinuation_DoesNotStopProductionCadenceAndTerminatesAfterFreshRecheck()
    {
        List<string> order = [];
        CadenceClock clock = new(At(7, 0, 6));
        CadencePolicySource policies = new(clock);
        CadenceObservationSource observations = new(clock, NewRoot);
        SignalledActions actions = new(order);
        await using ProcessGateCoordinator coordinator = new(
            new MemoryEnvelopeStore(order),
            policies,
            observations,
            actions,
            clock);
        ProcessGateRunRequest request = new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId);

        Task<ProcessGateRunResult> first = coordinator.EvaluateAsync(request);
        await clock.GraceStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        ProcessGateRunResult immediate = await first.WaitAsync(TimeSpan.FromMilliseconds(250));
        Assert.Contains(immediate.Outcomes, outcome =>
            outcome.Kind == ProcessGateOutcomeKind.CloseRequested);
        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);

        for (int second = 1; second <= 4; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await coordinator.EvaluateAsync(request).WaitAsync(TimeSpan.FromMilliseconds(250));
        }

        Assert.True(observations.BatchReadCount >= 5);
        Assert.True(observations.ScansDuringGrace >= 4);
        Assert.Equal(0, observations.ExactReadCount);

        clock.Advance(TimeSpan.FromSeconds(1));
        clock.ReleaseGrace();
        await actions.TerminationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, observations.ExactReadCount);
        Assert.False(observations.ContinuityLostAtExactRead);
        Assert.Equal(1, actions.TerminationCalls);
        Assert.True(order.IndexOf("save:terminate-claim") < order.IndexOf("action:terminate"));
    }

    [Fact]
    public async Task UnconfiguredProcess_PersistsReducerStateButRequestsNoEffect()
    {
        MemoryEnvelopeStore store = new([]);
        QueuePolicySource policies = new(PolicyEvidence(1, "policy-1", At(7, 0, 6)));
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        ScriptedObservationSource observations = new(
            Batch(ProcessObservationBatchKind.StartDelta, other));
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            policies,
            observations,
            actions,
            new RecordingMonotonicDelay([]));

        ProcessGateRunResult result = await coordinator.EvaluateAsync(
            new(ProcessObservationBatchKind.StartDelta, UserSid, SessionId));

        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
        Assert.NotNull(store.Current);
        Assert.Empty(store.Current!.ActionJournal);
        Assert.Contains(result.Outcomes, outcome =>
            outcome.Kind == ProcessGateOutcomeKind.Healthy);
    }

    [Fact]
    public async Task ContinuityLoss_RequiresPersistedSeverThenAuthoritativeNewEpoch()
    {
        MemoryEnvelopeStore store = new([]);
        QueuePolicySource policies = new(
            PolicyEvidence(1, "policy-1", At(7, 0, 6)),
            PolicyEvidence(2, "policy-2", At(7, 0, 7)),
            PolicyEvidence(3, "policy-3", At(7, 0, 8)),
            PolicyEvidence(4, "policy-4", At(7, 0, 9)));
        ScriptedObservationSource observations = new(
            Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-lost",
                isComplete: false,
                isAllProcessCatalog: false,
                creationTimelineTrusted: false,
                continuityLost: true,
                clockSample: Sample(At(7, 0, 6), 100),
                values: [NewRoot]),
            Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-new",
                clockSample: Sample(At(7, 0, 7), 10),
                values: [NewRoot]),
            Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-lost",
                clockSample: Sample(At(7, 0, 8), 200),
                values: [NewRoot]),
            Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-new",
                clockSample: Sample(At(7, 0, 9), 20),
                values: [NewRoot]));
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            policies,
            observations,
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        Assert.True(store.Current!.ObservationContinuity.IsLost);
        Assert.True(store.Current.ObservationContinuity.TrustSeverPersisted);
        Assert.False(store.Current.ReducerState.CreationTimelineTrusted);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        Assert.True(store.Current.ObservationContinuity.IsLost);
        Assert.False(store.Current.ReducerState.CreationTimelineTrusted);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        Assert.True(store.Current.ObservationContinuity.IsLost);
        Assert.False(store.Current.ReducerState.CreationTimelineTrusted);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        Assert.False(store.Current.ObservationContinuity.IsLost);
        Assert.True(store.Current.ReducerState.CreationTimelineTrusted);
        Assert.Equal(0, actions.CloseCalls);
    }

    [Fact]
    public async Task SavedResponseWithoutExactAcceptedClaim_CannotStartClose()
    {
        MemoryEnvelopeStore store = new([])
        {
            AcceptedEnvelopeProjection = replacement => replacement with
            {
                ActionJournal = ImmutableDictionary<
                    ProcessActionKey,
                    ProcessActionJournalEntry>.Empty,
            },
        };
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task StructurallyEquivalentReconstructedAcceptedEnvelope_CanStartClose()
    {
        MemoryEnvelopeStore store = new([])
        {
            AcceptedEnvelopeProjection = CloneEnvelopeCollections,
        };
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(1, actions.CloseCalls);
        await EventuallyAsync(() => actions.TerminationCalls == 1);
        Assert.Equal(1, actions.TerminationCalls);
    }

    [Fact]
    public async Task NestedTamperInAcceptedEnvelope_CannotStartClose()
    {
        MemoryEnvelopeStore store = new([])
        {
            AcceptedEnvelopeProjection = replacement =>
            {
                ProcessGateEnvelope clone = CloneEnvelopeCollections(replacement);
                ProcessKnownInstance known = clone.ReducerState.KnownInstances[NewRoot.Identity!.Key];
                return clone with
                {
                    ReducerState = clone.ReducerState with
                    {
                        KnownInstances = clone.ReducerState.KnownInstances.SetItem(
                            NewRoot.Identity.Key,
                            known with
                            {
                                Identity = known.Identity with
                                {
                                    ExecutablePath = @"C:\Games\tampered.exe",
                                },
                            }),
                    },
                };
            },
        };
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Theory]
    [InlineData(ProcessCloseOutcome.Unavailable)]
    [InlineData(ProcessCloseOutcome.Ambiguous)]
    public async Task RuleIdCaseChangeAcrossRestart_DoesNotRepeatPermanentClose(
        ProcessCloseOutcome closeOutcome)
    {
        Assert.Equal(
            new ProcessActionKey(NewRoot.Identity!.Key, NightDate, "game", Cutoff, "fingerprint"),
            new ProcessActionKey(NewRoot.Identity.Key, NightDate, "GAME", Cutoff, "fingerprint"));

        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([], closeOutcome);
        await using (ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(PolicyEvidence(
                1,
                "policy-1",
                At(7, 0, 6),
                Policy(At(7, 0, 6), rules: [GameRule(ruleId: "game")]))),
            new ScriptedObservationSource(Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromSeconds(1)),
                values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await first.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        await using (ProcessGateCoordinator restarted = new(
            store,
            new QueuePolicySource(PolicyEvidence(
                2,
                "policy-2",
                At(7, 0, 7),
                Policy(At(7, 0, 7), rules: [GameRule(ruleId: "GAME")]))),
            new ScriptedObservationSource(Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                clockSample: SampleTime(At(7, 0, 7), TimeSpan.FromSeconds(2)),
                values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await restarted.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
        Assert.Single(store.Current!.ActionJournal);
    }

    [Theory]
    [InlineData(ProcessCloseOutcome.Requested, true)]
    [InlineData(ProcessCloseOutcome.NoEligibleWindow, true)]
    [InlineData(ProcessCloseOutcome.TargetExited, false)]
    [InlineData(ProcessCloseOutcome.IdentityMismatch, false)]
    [InlineData(ProcessCloseOutcome.Ambiguous, false)]
    [InlineData(ProcessCloseOutcome.Unavailable, false)]
    public async Task OnlyDurableRequestedOrNoWindowCompletionCanReachTermination(
        ProcessCloseOutcome closeOutcome,
        bool shouldTerminate)
    {
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([], closeOutcome);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: Sample(At(7, 0, 7), TimeSpan.TicksPerSecond * 60),
                    values: [NewRoot]),
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: Sample(At(7, 0, 12), TimeSpan.TicksPerSecond * 360),
                    values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(1, actions.CloseCalls);
        if (shouldTerminate)
        {
            await EventuallyAsync(() => actions.TerminationCalls == 1);
        }
        Assert.Equal(shouldTerminate ? 1 : 0, actions.TerminationCalls);
        ProcessActionJournalEntry entry = Assert.Single(store.Current!.ActionJournal.Values);
        Assert.Equal(closeOutcome, entry.CloseCompletion);
        Assert.Equal(shouldTerminate, entry.TerminationClaimed);
    }

    [Fact]
    public async Task CloseCompletionSaveFailure_LeavesPermanentAmbiguousClaimAcrossRestart()
    {
        MemoryEnvelopeStore store = new([]);
        store.ForcedSaveStatuses.Enqueue(ProcessGateStoreSaveStatus.Saved);
        store.ForcedSaveStatuses.Enqueue(ProcessGateStoreSaveStatus.Unavailable);
        RecordingActions actions = new([]);
        await using (ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await first.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        ProcessActionJournalEntry crashed = Assert.Single(store.Current!.ActionJournal.Values);
        Assert.Null(crashed.CloseCompletion);
        await using (ProcessGateCoordinator restarted = new(
            store,
            new QueuePolicySource(PolicyEvidence(2, "policy-2", At(7, 0, 7))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await restarted.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task CloseAdapterException_IsPersistedAmbiguousAndNeverTerminates()
    {
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([], throwOnClose: true);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        ProcessActionJournalEntry entry = Assert.Single(store.Current!.ActionJournal.Values);
        Assert.Equal(ProcessCloseOutcome.Ambiguous, entry.CloseCompletion);
        Assert.Equal(ProcessActionTerminalReason.CloseAmbiguous, entry.TerminalReason);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task CasConflict_DiscardsStaleBlockAndReacquiresPolicyAndObservation()
    {
        MemoryEnvelopeStore store = new([]);
        store.ForcedSaveStatuses.Enqueue(ProcessGateStoreSaveStatus.Conflict);
        QueuePolicySource policies = new(
            PolicyEvidence(1, "policy-1", At(7, 0, 6)),
            PolicyEvidence(2, "policy-2", At(7, 0, 7)));
        ScriptedObservationSource observations = new(
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
            Batch(
                ProcessObservationBatchKind.StartDelta,
                Root(44, Cutoff.AddTicks(1), @"C:\Other\tool.exe")));
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            policies,
            observations,
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(2, policies.ReadCount);
        Assert.Equal(2, observations.BatchReadCount);
        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("pidReuse")]
    [InlineData("path")]
    [InlineData("sid")]
    [InlineData("session")]
    [InlineData("creation")]
    public async Task FreshExactIdentityMismatch_CancelsTermination(string mismatch)
    {
        ProcessObservation[] fresh = mismatch switch
        {
            "missing" => [],
            "pidReuse" => [Root(42, Cutoff.AddSeconds(1))],
            "path" => [WithIdentity(NewRoot, executablePath: @"C:\Other\game.exe")],
            "sid" => [WithIdentity(NewRoot, userSid: "S-1-5-21-OTHER")],
            "session" => [WithIdentity(NewRoot, sessionId: SessionId + 1)],
            "creation" => [WithIdentity(
                NewRoot,
                creationInstantUtc: NewRoot.Identity!.CreationInstantUtc.AddTicks(1))],
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, fresh)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
        await EventuallyAsync(() =>
            Assert.Single(store.Current!.ActionJournal.Values).TerminalReason
                == ProcessActionTerminalReason.RecheckCancelled);
        ProcessActionJournalEntry entry = Assert.Single(store.Current!.ActionJournal.Values);
        Assert.Equal(ProcessActionTerminalReason.RecheckCancelled, entry.TerminalReason);
        Assert.Null(entry.DeferredByOverride);
        Assert.StartsWith(
            ProcessActionJournalEntry.ModernRecheckClaimPrefix,
            entry.RecheckClaimIdentity,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactIdentityMismatchWhileOverrideIsActive_IsNotTaggedAsOverrideDeferral()
    {
        MemoryEnvelopeStore store = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(
                    2,
                    "policy-2",
                    At(7, 0, 11),
                    Policy(
                        At(7, 0, 11),
                        DesktopNightPhase.OverrideActive,
                        activeOverride: ActiveOverride(DesktopOverrideKind.Emergency)))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta)),
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await EventuallyAsync(() =>
            Assert.Single(store.Current!.ActionJournal.Values).TerminalReason
                == ProcessActionTerminalReason.RecheckCancelled);

        ProcessActionJournalEntry entry = Assert.Single(store.Current!.ActionJournal.Values);
        Assert.Null(entry.DeferredByOverride);
    }

    [Fact]
    public async Task ExactSingletonRecheckClaimingAuthoritativeCatalog_IsRejected()
    {
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.AuthoritativeSnapshot, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
        await EventuallyAsync(() =>
            Assert.Single(store.Current!.ActionJournal.Values).TerminalReason
                == ProcessActionTerminalReason.RecheckCancelled);
        Assert.Equal(
            ProcessActionTerminalReason.RecheckCancelled,
            Assert.Single(store.Current!.ActionJournal.Values).TerminalReason);
    }

    [Theory]
    [InlineData("emergency")]
    [InlineData("entertainment")]
    [InlineData("teamRescue")]
    [InlineData("morning")]
    [InlineData("wakeCrossing")]
    [InlineData("ruleRemoved")]
    [InlineData("ruleCutoffChanged")]
    [InlineData("rulePathChanged")]
    public async Task FreshPolicyChangeCancelsOldTerminationChain(string scenario)
    {
        DesktopPolicySnapshotDto freshPolicy = scenario switch
        {
            "emergency" => Policy(
                At(7, 0, 11),
                DesktopNightPhase.OverrideActive,
                activeOverride: ActiveOverride(DesktopOverrideKind.Emergency)),
            "entertainment" => Policy(
                At(7, 0, 11),
                DesktopNightPhase.OverrideActive,
                activeOverride: ActiveOverride(DesktopOverrideKind.Entertainment)),
            "teamRescue" => Policy(
                At(7, 0, 11),
                DesktopNightPhase.OverrideActive,
                activeOverride: ActiveOverride(
                    DesktopOverrideKind.TeamRescue,
                    "game")),
            "morning" => Policy(At(7, 9, 1), DesktopNightPhase.Morning),
            "wakeCrossing" => Policy(At(7, 9, 1), DesktopNightPhase.LastStart),
            "ruleRemoved" => Policy(
                At(7, 0, 11),
                DesktopNightPhase.LastStart,
                []),
            "ruleCutoffChanged" => Policy(
                At(7, 0, 11),
                DesktopNightPhase.LastStart,
                [GameRule(sessionMinutes: 36)]),
            "rulePathChanged" => Policy(
                At(7, 0, 11),
                DesktopNightPhase.LastStart,
                [GameRule(@"C:\Games\other.exe")]),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(
                    2,
                    "policy-2",
                    freshPolicy.EvaluatedAt,
                    freshPolicy,
                    $"payload-{scenario}")),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(1, actions.CloseCalls);
        await EventuallyAsync(() =>
            Assert.Single(store.Current!.ActionJournal.Values).TerminalReason
                == ProcessActionTerminalReason.RecheckCancelled);
        Assert.Equal(0, actions.TerminationCalls);
        ProcessActionJournalEntry entry = Assert.Single(store.Current!.ActionJournal.Values);
        if (scenario is "emergency" or "entertainment" or "teamRescue")
        {
            Assert.NotNull(entry.DeferredByOverride);
        }
        else
        {
            Assert.Null(entry.DeferredByOverride);
        }
    }

    [Theory]
    [InlineData(DesktopOverrideKind.TeamRescue)]
    [InlineData(DesktopOverrideKind.Emergency)]
    [InlineData(DesktopOverrideKind.Entertainment)]
    public async Task OverrideAcceptedFirst_WinsSharedBarrierAndPersistsRecheckCancellation(
        DesktopOverrideKind kind)
    {
        SignallingRaceBarrier barrier = new();
        BlockingDelay delay = new();
        OverrideRacePolicySource policies = new();
        MemoryEnvelopeStore store = new([]);
        SignalledActions actions = new([]);
        RaceSequence sequence = new();
        CoordinatedOverrideTransport transport = new(kind, policies, sequence);
        DesktopClientOverrideGateway gateway = new(
            new NightGateDesktopClient(transport, new OverrideRaceRequestIds()),
            barrier);
        await using ProcessGateCoordinator coordinator = new(
            store,
            policies,
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            delay,
            cutoffBarrier: barrier);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<DesktopOverrideResult> overrideRequest = gateway.RequestAsync(
                OverrideRequest(kind))
            .AsTask();
        await transport.OverrideRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await barrier.FirstLeaseAcquired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        delay.Release();

        Task winner = await Task.WhenAny(
                barrier.SecondEntryRequested.Task,
                actions.TerminationObserved.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));
        transport.AllowAcceptance();
        DesktopOverrideResult result = await overrideRequest.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(barrier.SecondEntryRequested.Task, winner);
        Assert.True(result.Accepted);
        await EventuallyAsync(() =>
            Assert.Single(store.Current!.ActionJournal.Values).TerminalReason
                == ProcessActionTerminalReason.RecheckCancelled);
        Assert.Equal(
            kind,
            Assert.Single(store.Current!.ActionJournal.Values).DeferredByOverride!.Kind);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Theory]
    [InlineData(DesktopOverrideKind.TeamRescue)]
    [InlineData(DesktopOverrideKind.Emergency)]
    [InlineData(DesktopOverrideKind.Entertainment)]
    public async Task OverrideCancellation_AfterRestartAndExpiry_RearmsExactTargetFromAuthoritativeSnapshot(
        DesktopOverrideKind kind)
    {
        DesktopActiveOverrideDto activeOverride = kind == DesktopOverrideKind.TeamRescue
            ? ActiveOverride(kind, "game")
            : ActiveOverride(kind);
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using (ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(
                    2,
                    "policy-2",
                    At(7, 0, 11),
                    Policy(
                        At(7, 0, 11),
                        DesktopNightPhase.OverrideActive,
                        activeOverride: activeOverride))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 6), TimeSpan.Zero),
                    values: [NewRoot]),
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 11), TimeSpan.FromMinutes(5)),
                    values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await first.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
            await EventuallyAsync(() =>
                Assert.Single(store.Current!.ActionJournal.Values).TerminalReason
                    == ProcessActionTerminalReason.RecheckCancelled);
        }

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);

        await using (ProcessGateCoordinator restarted = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(3, "policy-3", At(7, 0, 41)),
                PolicyEvidence(4, "policy-4", At(7, 0, 46))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.AuthoritativeSnapshot,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 41), TimeSpan.FromMinutes(35)),
                    values: [NewRoot]),
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 46), TimeSpan.FromMinutes(40)),
                    values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await restarted.EvaluateAsync(new(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                UserSid,
                SessionId));
            await EventuallyAsync(() => actions.TerminationCalls == 1);
        }

        Assert.Equal(2, actions.CloseCalls);
        Assert.Equal(1, actions.TerminationCalls);
    }

    [Fact]
    public async Task LegacyOverrideCancellation_AfterUpgradeAndExpiry_RearmsFromExactAuthoritativeBlock()
    {
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using (ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(
                    2,
                    "policy-2",
                    At(7, 0, 11),
                    Policy(
                        At(7, 0, 11),
                        DesktopNightPhase.OverrideActive,
                        activeOverride: ActiveOverride(DesktopOverrideKind.Emergency)))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 6), TimeSpan.Zero),
                    values: [NewRoot]),
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 11), TimeSpan.FromMinutes(5)),
                    values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await first.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
            await EventuallyAsync(() =>
                Assert.Single(store.Current!.ActionJournal.Values).DeferredByOverride is not null);
        }

        KeyValuePair<ProcessActionKey, ProcessActionJournalEntry> persisted =
            Assert.Single(store.Current!.ActionJournal);
        store.Seed(store.Current with
        {
            ActionJournal = store.Current.ActionJournal.SetItem(
                persisted.Key,
                persisted.Value with
                {
                    RecheckClaimIdentity = "0123456789abcdef0123456789abcdef",
                    DeferredByOverride = null,
                }),
        });

        await using (ProcessGateCoordinator upgraded = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(3, "policy-3", At(7, 0, 41)),
                PolicyEvidence(4, "policy-4", At(7, 0, 46))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.AuthoritativeSnapshot,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 41), TimeSpan.FromMinutes(35)),
                    values: [NewRoot]),
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 46), TimeSpan.FromMinutes(40)),
                    values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await upgraded.EvaluateAsync(new(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                UserSid,
                SessionId));
            await EventuallyAsync(() => actions.TerminationCalls == 1);
        }

        Assert.Equal(2, actions.CloseCalls);
        Assert.Equal(1, actions.TerminationCalls);
    }

    [Fact]
    public async Task ModernNonOverrideCancellation_ExactAuthoritativeBlockDoesNotRearm()
    {
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using (ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta)),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await first.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
            await EventuallyAsync(() =>
                Assert.Single(store.Current!.ActionJournal.Values).TerminalReason
                    == ProcessActionTerminalReason.RecheckCancelled);
        }

        KeyValuePair<ProcessActionKey, ProcessActionJournalEntry> persisted =
            Assert.Single(store.Current!.ActionJournal);
        store.Seed(store.Current with
        {
            ActionJournal = store.Current.ActionJournal.SetItem(
                persisted.Key,
                persisted.Value with
                {
                    RecheckClaimIdentity = "ng2:0123456789abcdef0123456789abcdef",
                }),
        });

        await using (ProcessGateCoordinator restarted = new(
            store,
            new QueuePolicySource(PolicyEvidence(3, "policy-3", At(7, 0, 16))),
            new ScriptedObservationSource(Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                NewRoot)),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await restarted.EvaluateAsync(new(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                UserSid,
                SessionId));
        }

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
        Assert.Equal(
            "ng2:0123456789abcdef0123456789abcdef",
            Assert.Single(store.Current!.ActionJournal.Values).RecheckClaimIdentity);
    }

    [Fact]
    public async Task OverrideExpiry_DeltaAloneDoesNotRearmDeferredExactTarget()
    {
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using (ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(
                    2,
                    "policy-2",
                    At(7, 0, 11),
                    Policy(
                        At(7, 0, 11),
                        DesktopNightPhase.OverrideActive,
                        activeOverride: ActiveOverride(DesktopOverrideKind.Emergency)))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 6), TimeSpan.Zero),
                    values: [NewRoot]),
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 11), TimeSpan.FromMinutes(5)),
                    values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await first.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
            await EventuallyAsync(() =>
                Assert.Single(store.Current!.ActionJournal.Values).DeferredByOverride is not null);
        }

        await using (ProcessGateCoordinator restarted = new(
            store,
            new QueuePolicySource(PolicyEvidence(3, "policy-3", At(7, 0, 41))),
            new ScriptedObservationSource(Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                clockSample: SampleTime(At(7, 0, 41), TimeSpan.FromMinutes(35)),
                values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await restarted.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
        Assert.NotNull(Assert.Single(store.Current!.ActionJournal.Values).DeferredByOverride);
    }

    [Fact]
    public async Task ReplacementOverrideSnapshotAndDelta_DoNotPrematurelyRearm()
    {
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using (ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(
                    2,
                    "policy-2",
                    At(7, 0, 11),
                    Policy(
                        At(7, 0, 11),
                        DesktopNightPhase.OverrideActive,
                        activeOverride: ActiveOverride(DesktopOverrideKind.Emergency)))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 6), TimeSpan.Zero),
                    values: [NewRoot]),
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 11), TimeSpan.FromMinutes(5)),
                    values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await first.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
            await EventuallyAsync(() =>
                Assert.Single(store.Current!.ActionJournal.Values).DeferredByOverride is not null);
        }

        await using (ProcessGateCoordinator restarted = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(
                    3,
                    "policy-3",
                    At(7, 0, 21),
                    Policy(
                        At(7, 0, 21),
                        DesktopNightPhase.OverrideActive,
                        activeOverride: new(
                            DesktopOverrideKind.Emergency,
                            At(7, 0, 20),
                            At(7, 0, 20),
                            At(7, 0, 50),
                            []))),
                PolicyEvidence(4, "policy-4", At(7, 0, 51))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.AuthoritativeSnapshot,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 21), TimeSpan.FromMinutes(15)),
                    values: [NewRoot]),
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: SampleTime(At(7, 0, 51), TimeSpan.FromMinutes(45)),
                    values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await restarted.EvaluateAsync(new(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                UserSid,
                SessionId));
            Assert.NotNull(
                Assert.Single(store.Current!.ActionJournal.Values).DeferredByOverride);

            await restarted.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
            Assert.Equal(1, actions.CloseCalls);
            Assert.Equal(0, actions.TerminationCalls);
            Assert.NotNull(
                Assert.Single(store.Current!.ActionJournal.Values).DeferredByOverride);
        }

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task TerminationStartedFirst_HoldsSharedBarrierUntilTheActualEffectCompletes()
    {
        SignallingRaceBarrier barrier = new();
        BlockingDelay delay = new();
        OverrideRacePolicySource policies = new();
        MemoryEnvelopeStore store = new([]);
        RaceSequence sequence = new();
        BlockingTerminationActions actions = new(sequence);
        CoordinatedOverrideTransport transport = new(
            DesktopOverrideKind.Emergency,
            policies,
            sequence);
        transport.AllowAcceptance();
        DesktopClientOverrideGateway gateway = new(
            new NightGateDesktopClient(transport, new OverrideRaceRequestIds()),
            barrier);
        await using ProcessGateCoordinator coordinator = new(
            store,
            policies,
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            delay,
            cutoffBarrier: barrier);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        delay.Release();

        Task winner = await Task.WhenAny(
                barrier.FirstLeaseAcquired.Task,
                actions.TerminationStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));
        if (!ReferenceEquals(winner, barrier.FirstLeaseAcquired.Task))
        {
            actions.ReleaseTermination();
        }

        Assert.Same(barrier.FirstLeaseAcquired.Task, winner);
        await actions.TerminationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<DesktopOverrideResult> overrideRequest = gateway.RequestAsync(
                OverrideRequest(DesktopOverrideKind.Emergency))
            .AsTask();
        await barrier.SecondEntryRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(transport.Accepted.Task.IsCompleted);

        actions.ReleaseTermination();
        await transport.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        DesktopOverrideResult result = await overrideRequest.WaitAsync(TimeSpan.FromSeconds(2));
        await EventuallyAsync(() =>
            Assert.Single(store.Current!.ActionJournal.Values).TerminationCompletion is not null);

        Assert.True(result.Accepted);
        Assert.True(actions.CompletedOrder < transport.AcceptedOrder);
    }

    [Fact]
    public async Task Dispose_CancelsAContinuationWaitingForTheCutoffBarrierWithoutDeadlock()
    {
        SignallingRaceBarrier barrier = new();
        IDisposable held = await barrier.EnterAsync();
        BlockingDelay delay = new();
        SignalledActions actions = new([]);
        ProcessGateCoordinator coordinator = new(
            new MemoryEnvelopeStore([]),
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            delay,
            cutoffBarrier: barrier);
        bool disposed = false;
        try
        {
            await coordinator.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
            await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            delay.Release();
            await barrier.SecondEntryRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

            await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            disposed = true;

            Assert.Equal(0, actions.TerminationCalls);
        }
        finally
        {
            held.Dispose();
            if (!disposed)
            {
                await coordinator.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData("olderRevision")]
    [InlineData("sameRevisionOtherIdentity")]
    [InlineData("sameIdentityDifferentPayload")]
    [InlineData("evaluationPredatesTarget")]
    public async Task StaleOrPayloadInconsistentFreshPolicyCannotTerminate(string scenario)
    {
        ValidatedProcessPolicy fresh = scenario switch
        {
            "olderRevision" => PolicyEvidence(0, "policy-old", At(7, 0, 11)),
            "sameRevisionOtherIdentity" => PolicyEvidence(
                1,
                "policy-other",
                At(7, 0, 11)),
            "sameIdentityDifferentPayload" => PolicyEvidence(
                2,
                "policy-1",
                At(7, 0, 11),
                payloadFingerprint: "changed-payload"),
            "evaluationPredatesTarget" => PolicyEvidence(
                2,
                "policy-2",
                At(7, 0, 5)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                fresh),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task CrashAfterCloseClaim_NeverRepeatsCloseOrContinuesAfterRestart()
    {
        using CancellationTokenSource crash = new();
        MemoryEnvelopeStore store = new([])
        {
            AfterSaved = envelope =>
            {
                if (envelope.ActionJournal.Values.Any(entry =>
                        entry.CloseCompletion is null))
                {
                    crash.Cancel();
                }
            },
        };
        RecordingActions actions = new([]);
        await using (ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await first.EvaluateAsync(
                new(ProcessObservationBatchKind.StartDelta, UserSid, SessionId),
                crash.Token);
            await EventuallyAsync(() => crash.IsCancellationRequested);
        }

        store.AfterSaved = null;
        await using (ProcessGateCoordinator restarted = new(
            store,
            new QueuePolicySource(PolicyEvidence(2, "policy-2", At(7, 0, 7))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await restarted.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
        Assert.Null(Assert.Single(store.Current!.ActionJournal.Values).CloseCompletion);
    }

    [Fact]
    public async Task CrashAfterTerminationClaim_NeverRepeatsTerminationAfterRestart()
    {
        using CancellationTokenSource crash = new();
        MemoryEnvelopeStore store = new([])
        {
            AfterSaved = envelope =>
            {
                if (envelope.ActionJournal.Values.Any(entry =>
                        entry.TerminationClaimed
                        && entry.TerminationCompletion is null))
                {
                    crash.Cancel();
                }
            },
        };
        RecordingActions actions = new([]);
        await using (ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await first.EvaluateAsync(
                new(ProcessObservationBatchKind.StartDelta, UserSid, SessionId),
                crash.Token);
            await EventuallyAsync(() => crash.IsCancellationRequested);
        }

        store.AfterSaved = null;
        await using (ProcessGateCoordinator restarted = new(
            store,
            new QueuePolicySource(PolicyEvidence(3, "policy-3", At(7, 0, 12))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await restarted.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
        ProcessActionJournalEntry entry = Assert.Single(store.Current!.ActionJournal.Values);
        Assert.True(entry.TerminationClaimed);
        Assert.Null(entry.TerminationCompletion);
    }

    [Fact]
    public async Task RestartAfterDurableCloseCompletion_ResumesAtDelayWithoutRepeatingClose()
    {
        using CancellationTokenSource crash = new();
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using (ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new CancellingDelay(crash)))
        {
            await first.EvaluateAsync(
                new(ProcessObservationBatchKind.StartDelta, UserSid, SessionId),
                crash.Token);
        }

        ProcessActionJournalEntry completed = Assert.Single(store.Current!.ActionJournal.Values);
        Assert.Equal(ProcessCloseOutcome.Requested, completed.CloseCompletion);
        Assert.Null(completed.RecheckClaimIdentity);
        await using (ProcessGateCoordinator restarted = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(2, "policy-2", At(7, 0, 7)),
                PolicyEvidence(3, "policy-3", At(7, 0, 12))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: Sample(At(7, 0, 7), TimeSpan.TicksPerSecond * 60),
                    values: [NewRoot]),
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: Sample(At(7, 0, 12), TimeSpan.TicksPerSecond * 360),
                    values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([])))
        {
            await restarted.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
            await EventuallyAsync(() => actions.TerminationCalls == 1);
        }

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(1, actions.TerminationCalls);
    }

    [Fact]
    public async Task PendingGraceDelay_DoesNotBlockAnotherEvaluation()
    {
        BlockingDelay delay = new();
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        ScriptedObservationSource observations = new(
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
            Batch(
                ProcessObservationBatchKind.StartDelta,
                Root(44, Cutoff.AddTicks(1), @"C:\Other\tool.exe")),
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot));
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 7)),
                PolicyEvidence(3, "policy-3", At(7, 0, 12))),
            observations,
            actions,
            delay);

        Task<ProcessGateRunResult> first = coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ProcessGateRunResult> second = coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first.IsCompleted);
        delay.Release();
        await EventuallyAsync(() => actions.TerminationCalls == 1);
        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(1, actions.TerminationCalls);
    }

    [Fact]
    public async Task DisposeCancelsPendingDelayAndPreventsTerminationOrLaterEffects()
    {
        BlockingDelay delay = new();
        RecordingActions actions = new([]);
        ProcessGateCoordinator coordinator = new(
            new MemoryEnvelopeStore([]),
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            delay);
        Task<ProcessGateRunResult> running = coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.DisposeAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(2));
        ProcessGateRunResult afterDispose = await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
        Assert.Contains(afterDispose.Outcomes, outcome =>
            outcome.Kind == ProcessGateOutcomeKind.Cancelled);
    }

    [Fact]
    public async Task DisposeAwaitsStartedOperationEvenWhenCancellationCallbackThrows()
    {
        ThrowingCancellationDelay delay = new();
        RecordingActions actions = new([]);
        ProcessGateCoordinator coordinator = new(
            new MemoryEnvelopeStore([]),
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            delay);
        Task<ProcessGateRunResult> running = coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.DisposeAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(2));
        ProcessGateRunResult afterDispose = await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
        Assert.Contains(afterDispose.Outcomes, outcome =>
            outcome.Kind == ProcessGateOutcomeKind.Cancelled);
    }

    [Fact]
    public async Task EarlyReturningDelayCompletesFullGraceBeforeExactRecheck()
    {
        EarlyReturningDelay delay = new();
        ScriptedObservationSource observations = new(
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot))
        {
            BeforeExactRead = () => Assert.True(delay.Elapsed >= TimeSpan.FromSeconds(5)),
        };
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            new MemoryEnvelopeStore([]),
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11))),
            observations,
            actions,
            delay);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        await EventuallyAsync(() => actions.TerminationCalls == 1);

        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)],
            delay.Requests);
        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(1, actions.TerminationCalls);
    }

    [Fact]
    public async Task InitialGrandfatheredRootRequestsNoEffect()
    {
        ProcessObservation grandfathered = Root(42, Cutoff.AddTicks(-1));
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            new MemoryEnvelopeStore([]),
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                grandfathered)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));

        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task InitialEmergencyTemporaryGrantRequestsNoEffect()
    {
        ProcessObservation temporary = Root(42, At(7, 0, 10).AddTicks(1));
        RecordingActions actions = new([]);
        DesktopPolicySnapshotDto emergency = Policy(
            At(7, 0, 11),
            DesktopNightPhase.OverrideActive,
            activeOverride: ActiveOverride(DesktopOverrideKind.Emergency));
        await using ProcessGateCoordinator coordinator = new(
            new MemoryEnvelopeStore([]),
            new QueuePolicySource(PolicyEvidence(
                1,
                "policy-1",
                emergency.EvaluatedAt,
                emergency)),
            new ScriptedObservationSource(Batch(
                ProcessObservationBatchKind.StartDelta,
                temporary)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task NewerValidatedEmergencyPreemptsActiveTeamRescueWithoutClosingNewRoot()
    {
        DesktopActiveOverrideDto rescue = new(
            DesktopOverrideKind.TeamRescue,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 30),
            ["game"]);
        DesktopActiveOverrideDto emergency = new(
            DesktopOverrideKind.Emergency,
            At(7, 0, 16),
            At(7, 0, 16),
            At(7, 0, 46),
            []);
        ProcessObservation newRoot = Root(43, At(7, 0, 16));
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(
                    1,
                    "policy-rescue",
                    At(7, 0, 15),
                    Policy(
                        At(7, 0, 15),
                        DesktopNightPhase.OverrideActive,
                        activeOverride: rescue),
                    "payload-rescue"),
                PolicyEvidence(
                    2,
                    "policy-emergency",
                    At(7, 0, 16),
                    Policy(
                        At(7, 0, 16),
                        DesktopNightPhase.OverrideActive,
                        activeOverride: emergency),
                    "payload-emergency")),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta),
                Batch(ProcessObservationBatchKind.StartDelta, newRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        ProcessGateRunResult result = await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
        Assert.DoesNotContain(
            result.Outcomes,
            outcome => outcome.Kind is ProcessGateOutcomeKind.CloseRequested
                or ProcessGateOutcomeKind.NoEligibleWindow
                or ProcessGateOutcomeKind.TerminateAttempted);
        Assert.Equal(
            new ProcessOverrideIdentity(
                DesktopOverrideKind.Emergency,
                At(7, 0, 16),
                At(7, 0, 16),
                At(7, 0, 46)),
            store.Current!.ReducerState.OverrideHighWater);
        Assert.Contains(
            newRoot.Identity!.Key,
            store.Current.ReducerState.TemporaryInstances.Keys);
    }

    [Fact]
    public async Task LowerRevisionEmergencyCannotReplacePersistedOverrideHighWater()
    {
        DesktopActiveOverrideDto acceptedEmergency = new(
            DesktopOverrideKind.Emergency,
            At(7, 0, 16),
            At(7, 0, 16),
            At(7, 0, 46),
            []);
        DesktopActiveOverrideDto lowerRevisionEmergency = new(
            DesktopOverrideKind.Emergency,
            At(7, 0, 17),
            At(7, 0, 17),
            At(7, 0, 47),
            []);
        MemoryEnvelopeStore store = new([]);
        ScriptedObservationSource observations = new(
            Batch(ProcessObservationBatchKind.StartDelta));
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(
                    2,
                    "policy-emergency-2",
                    At(7, 0, 16),
                    Policy(
                        At(7, 0, 16),
                        DesktopNightPhase.OverrideActive,
                        activeOverride: acceptedEmergency),
                    "payload-emergency-2"),
                PolicyEvidence(
                    1,
                    "policy-emergency-1",
                    At(7, 0, 17),
                    Policy(
                        At(7, 0, 17),
                        DesktopNightPhase.OverrideActive,
                        activeOverride: lowerRevisionEmergency),
                    "payload-emergency-1")),
            observations,
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        ProcessGateEnvelope accepted = store.Current!;
        ProcessGateRunResult replay = await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Same(accepted, store.Current);
        Assert.Equal(2, store.Current!.PolicyLedger.HighestRevision);
        Assert.Equal(
            new ProcessOverrideIdentity(
                DesktopOverrideKind.Emergency,
                At(7, 0, 16),
                At(7, 0, 16),
                At(7, 0, 46)),
            store.Current.ReducerState.OverrideHighWater);
        Assert.Single(observations.BatchRequests);
        Assert.Contains(
            replay.Outcomes,
            outcome => outcome is
            {
                Kind: ProcessGateOutcomeKind.Cancelled,
                Code: "initial-fail-open",
            });
        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task ClockSampleAcceptsElapsedTimeConvertedFromNonTenMegahertzSource()
    {
        TimeSpan converted = TimeSpan.FromSeconds(1234d / 3_579_545d);
        ProcessObservationClockSample sample = SampleTime(At(7, 0, 6), converted);
        MemoryEnvelopeStore store = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                clockSample: sample,
                values: [Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe")])),
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(
            sample.CompletedMonotonic,
            store.Current!.ObservationContinuity.SampleMonotonicHighWater);
    }

    [Fact]
    public async Task TwoCoordinatorsRacingOneExactTarget_InvokeEachEffectAtMostOnce()
    {
        MemoryEnvelopeStore store = new([]);
        GateThenUnavailablePolicySource stalePolicy = new(
            PolicyEvidence(1, "policy-1", At(7, 0, 6)));
        int released = 0;
        store.AfterSaved = envelope =>
        {
            if (envelope.ActionJournal.Count == 1
                && Interlocked.Exchange(ref released, 1) == 0)
            {
                stalePolicy.Release();
            }
        };
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator stale = new(
            store,
            stalePolicy,
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));
        await using ProcessGateCoordinator winner = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        Task<ProcessGateRunResult> staleRun = stale.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await stalePolicy.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ProcessGateRunResult> winnerRun = winner.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await Task.WhenAll(staleRun, winnerRun).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(1, actions.TerminationCalls);
        ProcessActionJournalEntry entry = Assert.Single(store.Current!.ActionJournal.Values);
        Assert.True(entry.TerminationClaimed);
        Assert.Equal(ProcessTerminationOutcome.Terminated, entry.TerminationCompletion);
        Assert.True(stalePolicy.ReadCount >= 2);
    }

    [Theory]
    [InlineData(ProcessGateStoreLoadStatus.Unavailable)]
    [InlineData(ProcessGateStoreLoadStatus.Corrupt)]
    public async Task StoreLoadFailureOrCorruptionCannotCauseEffects(
        ProcessGateStoreLoadStatus status)
    {
        MemoryEnvelopeStore store = new([]);
        store.ForcedLoadStatuses.Enqueue(status);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task StoreSaveUnavailableCannotCauseClose()
    {
        MemoryEnvelopeStore store = new([]);
        store.ForcedSaveStatuses.Enqueue(ProcessGateStoreSaveStatus.Unavailable);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Null(store.Current);
        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task PolicyUnavailableCannotLoadObservationSaveStateOrCauseEffects()
    {
        MemoryEnvelopeStore store = new([]);
        ScriptedObservationSource observations = new(
            Batch(ProcessObservationBatchKind.StartDelta, NewRoot));
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new UnavailablePolicySource(),
            observations,
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(0, observations.BatchReadCount);
        Assert.Equal(0, store.SaveCount);
        Assert.Equal(0, actions.CloseCalls);
    }

    [Fact]
    public async Task ObservationExceptionStillDurablySeversTrustAndNeverEnforces()
    {
        MemoryEnvelopeStore store = new([]);
        ThrowingObservationSource observations = new();
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            observations,
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.True(store.Current!.ObservationContinuity.IsLost);
        Assert.True(store.Current.ObservationContinuity.TrustSeverPersisted);
        Assert.False(store.Current.ReducerState.CreationTimelineTrusted);
        Assert.Equal(0, actions.CloseCalls);
    }

    [Fact]
    public async Task IncompleteObservationAtWakeDurablyLatchesMorning()
    {
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        DesktopPolicySnapshotDto morning = Policy(
            At(7, 9, 1),
            DesktopNightPhase.Morning);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(
                1,
                "policy-morning",
                morning.EvaluatedAt,
                morning)),
            new ScriptedObservationSource(Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-lost",
                isComplete: false,
                isAllProcessCatalog: false,
                creationTimelineTrusted: false,
                continuityLost: true,
                values: [NewRoot])),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));

        Assert.True(store.Current!.ReducerState.MorningReleased);
        Assert.True(store.Current.ObservationContinuity.IsLost);
        Assert.Equal(0, actions.CloseCalls);
    }

    [Fact]
    public async Task ReducerDegradationStillDurablyAdvancesLogicalEvidence()
    {
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        DesktopPolicySnapshotDto changed = Policy(
            At(7, 0, 7),
            DesktopNightPhase.LastStart,
            [GameRule(sessionMinutes: 36)]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(
                    2,
                    "policy-2",
                    changed.EvaluatedAt,
                    changed,
                    "changed-rule")),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.AuthoritativeSnapshot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        ProcessGateRunResult degraded = await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(At(7, 0, 7), store.Current!.ReducerState.LastEffectiveLogicalTime);
        Assert.Contains(degraded.Outcomes, outcome =>
            outcome.Kind == ProcessGateOutcomeKind.Degraded);
        Assert.Equal(0, actions.CloseCalls);
    }

    [Fact]
    public async Task ReusedPidWithDifferentCreationTicksGetsIndependentDurableChains()
    {
        ProcessObservation reused = Root(42, Cutoff.AddSeconds(1));
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11)),
                PolicyEvidence(3, "policy-3", At(7, 0, 12))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot, reused),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, reused)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(2, actions.CloseCalls);
        await EventuallyAsync(() => actions.TerminationCalls == 2);
        Assert.Equal(2, actions.TerminationCalls);
        Assert.Equal(2, store.Current!.ActionJournal.Count);
    }

    [Fact]
    public async Task DisposeAwaitsAlreadyStartedCloseAndDoesNotContinueItsChain()
    {
        BlockingCloseActions actions = new();
        ProcessGateCoordinator coordinator = new(
            new MemoryEnvelopeStore([]),
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));
        Task<ProcessGateRunResult> running = coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await actions.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        ValueTask disposing = coordinator.DisposeAsync();
        await Task.Yield();
        Assert.False(disposing.IsCompleted);
        actions.ReleaseClose();
        await disposing.AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task CompleteNewEpochSnapshotWithUtcRollbackCannotRecoverContinuity()
    {
        MemoryEnvelopeStore store = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 7)),
                PolicyEvidence(3, "policy-3", At(7, 0, 8)),
                PolicyEvidence(4, "policy-4", At(7, 0, 9))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.AuthoritativeSnapshot,
                    "epoch-a",
                    clockSample: Sample(At(7, 0, 6), 100)),
                Batch(
                    ProcessObservationBatchKind.AuthoritativeSnapshot,
                    "epoch-a",
                    isComplete: false,
                    isAllProcessCatalog: false,
                    creationTimelineTrusted: false,
                    continuityLost: true,
                    clockSample: Sample(At(7, 0, 7), 200)),
                Batch(
                    ProcessObservationBatchKind.AuthoritativeSnapshot,
                    "epoch-b",
                    clockSample: Sample(At(7, 0, 5), 10)),
                Batch(
                    ProcessObservationBatchKind.AuthoritativeSnapshot,
                    "epoch-c",
                    clockSample: Sample(At(7, 0, 9), 10))),
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        Assert.True(store.Current!.ObservationContinuity.IsLost);
        Assert.False(store.Current.ReducerState.CreationTimelineTrusted);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        Assert.False(store.Current.ObservationContinuity.IsLost);
        Assert.True(store.Current.ReducerState.CreationTimelineTrusted);
    }

    [Fact]
    public async Task SameEpochAbnormalUtcToMonotonicDriftBreaksTrustSticky()
    {
        MemoryEnvelopeStore store = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 16))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.AuthoritativeSnapshot,
                    "epoch-a",
                    clockSample: Sample(At(7, 0, 6), 100)),
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    clockSample: Sample(At(7, 0, 16), 110))),
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.True(store.Current!.ObservationContinuity.IsLost);
        Assert.True(store.Current.ObservationContinuity.TrustSeverPersisted);
        Assert.False(store.Current.ReducerState.CreationTimelineTrusted);
    }

    [Fact]
    public async Task NormalPairedClockSampleAllowsNewEpochAuthoritativeRecovery()
    {
        MemoryEnvelopeStore store = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 7)),
                PolicyEvidence(3, "policy-3", At(7, 0, 8))),
            new ScriptedObservationSource(
                Batch(
                    ProcessObservationBatchKind.AuthoritativeSnapshot,
                    "epoch-a",
                    clockSample: Sample(At(7, 0, 6), 100)),
                Batch(
                    ProcessObservationBatchKind.StartDelta,
                    "epoch-a",
                    isComplete: false,
                    creationTimelineTrusted: false,
                    continuityLost: true,
                    clockSample: Sample(At(7, 0, 7), 200)),
                Batch(
                    ProcessObservationBatchKind.AuthoritativeSnapshot,
                    "epoch-b",
                    clockSample: Sample(At(7, 0, 8), 10))),
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));

        Assert.False(store.Current!.ObservationContinuity.IsLost);
        Assert.Equal("epoch-b", store.Current.ObservationContinuity.ClockEpoch);
        Assert.Equal(At(7, 0, 8).AddMilliseconds(1),
            store.Current.ObservationContinuity.SampleUtcHighWater);
        Assert.True(store.Current.ReducerState.CreationTimelineTrusted);
    }

    [Fact]
    public async Task NonExecutableButValidatedPolicyStillPersistsTrustSeverAndTaint()
    {
        DesktopPolicySnapshotDto snapshot = Policy(At(7, 0, 6));
        ValidatedProcessPolicy degraded = NonExecutablePolicyEvidence(
            1,
            "policy-degraded",
            snapshot);
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(degraded),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.NotNull(store.Current);
        Assert.False(store.Current!.ReducerState.CreationTimelineTrusted);
        Assert.Contains(NewRoot.Identity!.Key, store.Current.ReducerState.TaintedInstances);
        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task NonExecutablePolicyLossStaysStickyUntilNewEpochAuthoritativeRecovery()
    {
        DesktopPolicySnapshotDto degradedSnapshot = Policy(At(7, 0, 6));
        MemoryEnvelopeStore store = new([]);
        ScriptedObservationSource observations = new(
            Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                clockSample: SampleTime(At(7, 0, 6), TimeSpan.Zero),
                values: [NewRoot]),
            Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                clockSample: SampleTime(At(7, 0, 7), TimeSpan.FromMinutes(1)),
                values: [NewRoot]),
            Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-b",
                clockSample: SampleTime(At(7, 0, 8), TimeSpan.Zero),
                values: [NewRoot]));
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                NonExecutablePolicyEvidence(1, "policy-degraded", degradedSnapshot),
                PolicyEvidence(2, "policy-2", At(7, 0, 7)),
                PolicyEvidence(3, "policy-3", At(7, 0, 8))),
            observations,
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        Assert.True(store.Current!.ObservationContinuity.IsLost);
        Assert.True(store.Current.ObservationContinuity.TrustSeverPersisted);
        Assert.False(store.Current.ReducerState.CreationTimelineTrusted);
        Assert.Equal(
            [ProcessObservationAcknowledgementKind.TrustSeverPersisted],
            observations.Acknowledgements.Select(value => value.Kind));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        Assert.True(store.Current.ObservationContinuity.IsLost);
        Assert.False(store.Current.ReducerState.CreationTimelineTrusted);
        Assert.Single(observations.Acknowledgements);
        Assert.Equal(0, actions.CloseCalls);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        Assert.False(store.Current.ObservationContinuity.IsLost);
        Assert.True(store.Current.ReducerState.CreationTimelineTrusted);
        Assert.Equal(
            [
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            ],
            observations.Acknowledgements.Select(value => value.Kind));
        Assert.Equal(0, actions.CloseCalls);
    }

    [Fact]
    public async Task RejectedRecoveryCandidateGetsNewSeverAckAndRequiresFollowingEpoch()
    {
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        DesktopPolicySnapshotDto rejectedSnapshot = Policy(At(7, 0, 7));
        MemoryEnvelopeStore store = new([]);
        ScriptedObservationSource observations = new(
            Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                isComplete: false,
                creationTimelineTrusted: false,
                continuityLost: true,
                clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromSeconds(1)),
                values: [other]),
            Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-b",
                clockSample: SampleTime(At(7, 0, 7), TimeSpan.FromSeconds(2)),
                values: [other]),
            Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-b",
                clockSample: SampleTime(At(7, 0, 8), TimeSpan.FromSeconds(62)),
                values: [other]),
            Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-c",
                clockSample: SampleTime(At(7, 0, 9), TimeSpan.FromMinutes(1)),
                values: [other]));
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                NonExecutablePolicyEvidence(2, "policy-rejected", rejectedSnapshot),
                PolicyEvidence(3, "policy-3", At(7, 0, 8)),
                PolicyEvidence(4, "policy-4", At(7, 0, 9))),
            observations,
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));

        Assert.True(store.Current!.ObservationContinuity.IsLost);
        Assert.Equal("epoch-b", store.Current.ObservationContinuity.LossEpoch);
        Assert.Equal(
            [
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            ],
            observations.Acknowledgements.Select(value => value.Kind));
        Assert.Equal(["epoch-a", "epoch-b"],
            observations.Acknowledgements.Select(value => value.ObserverEpoch));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        Assert.True(store.Current.ObservationContinuity.IsLost);
        Assert.Equal("epoch-b", store.Current.ObservationContinuity.LossEpoch);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));

        Assert.False(store.Current.ObservationContinuity.IsLost);
        Assert.True(store.Current.ReducerState.CreationTimelineTrusted);
        Assert.Equal(
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            observations.Acknowledgements[^1].Kind);
        Assert.Equal("epoch-c", observations.Acknowledgements[^1].ObserverEpoch);
    }

    [Fact]
    public async Task FailedTrustSeverAckRetriesBeforeNewEpochCanRecover()
    {
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([]);
        ScriptedObservationSource observations = new(
            Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                isComplete: false,
                creationTimelineTrusted: false,
                continuityLost: true,
                clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromMinutes(1)),
                values: [other]),
            Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-a",
                clockSample: SampleTime(At(7, 0, 7), TimeSpan.FromMinutes(2)),
                values: [other]),
            Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-b",
                clockSample: SampleTime(At(7, 0, 8), TimeSpan.FromMinutes(1)),
                values: [other]));
        observations.AcknowledgementFailures.Enqueue(true);
        observations.AcknowledgementFailures.Enqueue(false);
        observations.AcknowledgementFailures.Enqueue(false);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 7)),
                PolicyEvidence(3, "policy-3", At(7, 0, 8))),
            observations,
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        for (int index = 0; index < 3; index++)
        {
            await coordinator.EvaluateAsync(new(
                index == 0
                    ? ProcessObservationBatchKind.StartDelta
                    : ProcessObservationBatchKind.AuthoritativeSnapshot,
                UserSid,
                SessionId));
        }

        Assert.False(store.Current!.ObservationContinuity.IsLost);
        Assert.Equal(
            [
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            ],
            observations.AcknowledgementAttempts.Select(value => value.Kind));
        Assert.Equal(
            observations.AcknowledgementAttempts[0],
            observations.AcknowledgementAttempts[1]);
    }

    [Fact]
    public async Task FailedRecoveryAckRemainsPendingAcrossHealthyObservationAndRetries()
    {
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([]);
        ScriptedObservationSource observations = new(
            Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                isComplete: false,
                creationTimelineTrusted: false,
                continuityLost: true,
                clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromMinutes(1)),
                values: [other]),
            Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-b",
                clockSample: SampleTime(At(7, 0, 7), TimeSpan.FromMinutes(1)),
                values: [other]),
            Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-b",
                clockSample: SampleTime(At(7, 0, 8), TimeSpan.FromMinutes(2)),
                values: [other]),
            Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-b",
                clockSample: SampleTime(At(7, 0, 9), TimeSpan.FromMinutes(3)),
                values: [other]));
        observations.AcknowledgementFailures.Enqueue(false);
        observations.AcknowledgementFailures.Enqueue(true);
        observations.AcknowledgementFailures.Enqueue(true);
        observations.AcknowledgementFailures.Enqueue(false);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 7)),
                PolicyEvidence(3, "policy-3", At(7, 0, 8)),
                PolicyEvidence(4, "policy-4", At(7, 0, 9))),
            observations,
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        Assert.False(store.Current!.ObservationContinuity.IsLost);
        ProcessObservationAcknowledgementCheckpoint pending = Assert.IsType<
            ProcessObservationAcknowledgementCheckpoint>(
                store.Current.ObservationContinuity.AcknowledgementCheckpoint);
        Assert.False(pending.Delivered);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        Assert.Equal(
            pending,
            store.Current.ObservationContinuity.AcknowledgementCheckpoint);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.False(store.Current.ObservationContinuity.IsLost);
        Assert.True(store.Current.ObservationContinuity.AcknowledgementCheckpoint!.Delivered);
        Assert.Equal(
            [
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
                ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
                ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            ],
            observations.AcknowledgementAttempts.Select(value => value.Kind));
        Assert.Equal(
            observations.AcknowledgementAttempts[1],
            observations.AcknowledgementAttempts[2]);
        Assert.Equal(
            observations.AcknowledgementAttempts[1],
            observations.AcknowledgementAttempts[3]);
    }

    [Fact]
    public async Task PendingSeverSurvivesNewEpochRecoveryCandidateAndPolicyDegradation()
    {
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([]);
        ScriptedObservationSource observations = new(
            Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                isComplete: false,
                creationTimelineTrusted: false,
                continuityLost: true,
                clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromMinutes(1)),
                values: [other]),
            Batch(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                "epoch-b",
                clockSample: SampleTime(At(7, 0, 7), TimeSpan.FromMinutes(1)),
                values: [other]));
        observations.AcknowledgementFailures.Enqueue(true);
        observations.AcknowledgementFailures.Enqueue(true);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                NonExecutablePolicyEvidence(
                    2,
                    "policy-degraded",
                    Policy(At(7, 0, 7)))),
            observations,
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        ProcessObservationAcknowledgement first = Assert.Single(
            observations.AcknowledgementAttempts);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));

        ProcessObservationContinuityState continuity = store.Current!.ObservationContinuity;
        Assert.True(continuity.IsLost);
        Assert.Equal("epoch-a", continuity.LossEpoch);
        ProcessObservationAcknowledgementCheckpoint checkpoint = Assert.IsType<
            ProcessObservationAcknowledgementCheckpoint>(continuity.AcknowledgementCheckpoint);
        Assert.False(checkpoint.Delivered);
        Assert.Equal("epoch-a", checkpoint.ObserverEpoch);
        Assert.Equal(first, observations.AcknowledgementAttempts[1]);
    }

    [Fact]
    public async Task PendingSeverRetriesSameIdAcrossRestartAfterDeliveryMarkerConflict()
    {
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([]);
        store.ForcedSaveStatuses.Enqueue(ProcessGateStoreSaveStatus.Saved);
        store.ForcedSaveStatuses.Enqueue(ProcessGateStoreSaveStatus.Conflict);
        ScriptedObservationSource firstSource = new(Batch(
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            isComplete: false,
            creationTimelineTrusted: false,
            continuityLost: true,
            clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromMinutes(1)),
            values: [other]));
        await using (ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            firstSource,
            new RecordingActions([]),
            new RecordingMonotonicDelay([])))
        {
            await first.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        ProcessObservationAcknowledgement original = Assert.Single(
            firstSource.AcknowledgementAttempts);
        Assert.False(store.Current!.ObservationContinuity.AcknowledgementCheckpoint!.Delivered);

        ScriptedObservationSource restartedSource = new(Batch(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            "epoch-a",
            clockSample: SampleTime(At(7, 0, 7), TimeSpan.FromMinutes(2)),
            values: [other]));
        await using (ProcessGateCoordinator restarted = new(
            store,
            new QueuePolicySource(PolicyEvidence(2, "policy-2", At(7, 0, 7))),
            restartedSource,
            new RecordingActions([]),
            new RecordingMonotonicDelay([])))
        {
            await restarted.EvaluateAsync(new(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                UserSid,
                SessionId));
        }

        Assert.Equal(original, Assert.Single(restartedSource.AcknowledgementAttempts));
        Assert.True(store.Current.ObservationContinuity.AcknowledgementCheckpoint!.Delivered);
        Assert.True(store.Current.ObservationContinuity.IsLost);
    }

    [Fact]
    public async Task TwoCoordinatorsUseSameAckIdAndStaleMarkerCannotUndoDeliveredWinner()
    {
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([]);
        ScriptedObservationSource seedSource = new(Batch(
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            isComplete: false,
            creationTimelineTrusted: false,
            continuityLost: true,
            clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromMinutes(1)),
            values: [other]));
        seedSource.AcknowledgementFailures.Enqueue(true);
        await using (ProcessGateCoordinator seed = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            seedSource,
            new RecordingActions([]),
            new RecordingMonotonicDelay([])))
        {
            await seed.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        TaskCompletionSource firstAckStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondAckStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseSecond = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedObservationSource firstSource = new(Batch(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            "epoch-a",
            clockSample: SampleTime(At(7, 0, 7), TimeSpan.FromMinutes(2)),
            values: [other]))
        {
            AcknowledgementHook = async (_, token) =>
            {
                firstAckStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            },
        };
        ScriptedObservationSource secondSource = new(Batch(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            "epoch-a",
            clockSample: SampleTime(At(7, 0, 8), TimeSpan.FromMinutes(3)),
            values: [other]))
        {
            AcknowledgementHook = async (_, token) =>
            {
                secondAckStarted.TrySetResult();
                await releaseSecond.Task.WaitAsync(token);
            },
        };
        await using ProcessGateCoordinator first = new(
            store,
            new QueuePolicySource(PolicyEvidence(2, "policy-2", At(7, 0, 7))),
            firstSource,
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));
        await using ProcessGateCoordinator second = new(
            store,
            new QueuePolicySource(PolicyEvidence(3, "policy-3", At(7, 0, 8))),
            secondSource,
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        Task<ProcessGateRunResult> firstRun = first.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        await firstAckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ProcessGateRunResult> secondRun = second.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        await secondAckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        releaseSecond.TrySetResult();
        await secondRun.WaitAsync(TimeSpan.FromSeconds(2));
        releaseFirst.TrySetResult();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(2));

        ProcessObservationAcknowledgement expected = Assert.Single(
            seedSource.AcknowledgementAttempts);
        Assert.Equal(expected, Assert.Single(firstSource.AcknowledgementAttempts));
        Assert.Equal(expected, Assert.Single(secondSource.AcknowledgementAttempts));
        ProcessObservationAcknowledgementCheckpoint checkpoint = Assert.IsType<
            ProcessObservationAcknowledgementCheckpoint>(
                store.Current!.ObservationContinuity.AcknowledgementCheckpoint);
        Assert.True(checkpoint.Delivered);
        Assert.Equal(expected.EnvelopeRevision, checkpoint.TransitionRevision);
    }

    [Fact]
    public async Task LateRecoveryAckCannotOverwriteSupersedingNewSever()
    {
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([]);
        await using (ProcessGateCoordinator seed = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(Batch(
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                isComplete: false,
                creationTimelineTrusted: false,
                continuityLost: true,
                clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromMinutes(1)),
                values: [other])),
            new RecordingActions([]),
            new RecordingMonotonicDelay([])))
        {
            await seed.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        TaskCompletionSource recoveryAckStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseRecovery = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedObservationSource recoverySource = new(Batch(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            "epoch-b",
            clockSample: SampleTime(At(7, 0, 7), TimeSpan.FromMinutes(1)),
            values: [other]))
        {
            AcknowledgementHook = async (_, token) =>
            {
                recoveryAckStarted.TrySetResult();
                await releaseRecovery.Task.WaitAsync(token);
            },
        };
        await using ProcessGateCoordinator recovering = new(
            store,
            new QueuePolicySource(PolicyEvidence(2, "policy-2", At(7, 0, 7))),
            recoverySource,
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));
        Task<ProcessGateRunResult> recoveryRun = recovering.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        await recoveryAckStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        ScriptedObservationSource lossSource = new(Batch(
            ProcessObservationBatchKind.StartDelta,
            "epoch-b",
            isComplete: false,
            creationTimelineTrusted: false,
            continuityLost: true,
            clockSample: SampleTime(At(7, 0, 8), TimeSpan.FromMinutes(2)),
            values: [other]));
        lossSource.AcknowledgementFailures.Enqueue(true);
        await using (ProcessGateCoordinator losing = new(
            store,
            new QueuePolicySource(PolicyEvidence(3, "policy-3", At(7, 0, 8))),
            lossSource,
            new RecordingActions([]),
            new RecordingMonotonicDelay([])))
        {
            await losing.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }
        ProcessObservationAcknowledgementCheckpoint superseding = Assert.IsType<
            ProcessObservationAcknowledgementCheckpoint>(
                store.Current!.ObservationContinuity.AcknowledgementCheckpoint);
        Assert.Equal(ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            superseding.Kind);
        Assert.False(superseding.Delivered);

        releaseRecovery.TrySetResult();
        ProcessGateRunResult recoveryResult = await recoveryRun.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.Equal(
            superseding,
            store.Current.ObservationContinuity.AcknowledgementCheckpoint);
        Assert.True(store.Current.ObservationContinuity.IsLost);
        ProcessObservationAcknowledgement late = Assert.Single(
            recoverySource.AcknowledgementAttempts);
        Assert.True(late.EnvelopeRevision < superseding.TransitionRevision);
        Assert.Contains(recoveryResult.Outcomes, outcome =>
            outcome.Kind == ProcessGateOutcomeKind.Degraded);
        Assert.DoesNotContain(recoveryResult.Outcomes, outcome =>
            outcome.Kind == ProcessGateOutcomeKind.Cancelled);
    }

    [Fact]
    public async Task HigherPendingSeverCannotSupersedePendingSeverCheckpoint()
    {
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([])
        {
            ForcedConflictWinnerProjection = (current, _) =>
            {
                long transitionRevision = checked(current.Revision + 1);
                return current with
                {
                    Revision = transitionRevision,
                    ObservationContinuity = current.ObservationContinuity with
                    {
                        LossEpoch = "epoch-b",
                        AcknowledgementCheckpoint = new(
                            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                            "epoch-b",
                            transitionRevision,
                            false),
                    },
                };
            },
        };
        store.ForcedSaveStatuses.Enqueue(ProcessGateStoreSaveStatus.Saved);
        store.ForcedSaveStatuses.Enqueue(ProcessGateStoreSaveStatus.Conflict);
        ScriptedObservationSource observations = new(Batch(
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            isComplete: false,
            creationTimelineTrusted: false,
            continuityLost: true,
            clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromMinutes(1)),
            values: [other]));
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            observations,
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        ProcessGateRunResult result = await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Contains(result.Outcomes, outcome =>
            outcome.Kind == ProcessGateOutcomeKind.Cancelled);
        Assert.DoesNotContain(result.Outcomes, outcome =>
            outcome.Kind == ProcessGateOutcomeKind.Degraded);
        Assert.Equal("epoch-b", store.Current!.ObservationContinuity.LossEpoch);
        Assert.False(store.Current.ObservationContinuity.AcknowledgementCheckpoint!.Delivered);
    }

    [Theory]
    [InlineData("futureRevision")]
    [InlineData("wrongEpoch")]
    [InlineData("wrongKind")]
    [InlineData("healthyWithSever")]
    [InlineData("lostDeliveredAtEnvelopeRevision")]
    [InlineData("healthyDeliveredAtEnvelopeRevision")]
    public async Task CorruptAcknowledgementCheckpointFailsOpenBeforeInputs(string scenario)
    {
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([]);
        ScriptedObservationSource seedSource = new(Batch(
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            isComplete: false,
            creationTimelineTrusted: false,
            continuityLost: true,
            clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromMinutes(1)),
            values: [other]));
        seedSource.AcknowledgementFailures.Enqueue(true);
        await using (ProcessGateCoordinator seed = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            seedSource,
            new RecordingActions([]),
            new RecordingMonotonicDelay([])))
        {
            await seed.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        ProcessGateEnvelope current = store.Current!;
        ProcessObservationAcknowledgementCheckpoint checkpoint = current
            .ObservationContinuity.AcknowledgementCheckpoint!;
        ProcessObservationContinuityState corrupt = scenario switch
        {
            "futureRevision" => current.ObservationContinuity with
            {
                AcknowledgementCheckpoint = checkpoint with
                {
                    TransitionRevision = checked(current.Revision + 1),
                },
            },
            "wrongEpoch" => current.ObservationContinuity with
            {
                AcknowledgementCheckpoint = checkpoint with
                {
                    ObserverEpoch = "epoch-z",
                },
            },
            "wrongKind" => current.ObservationContinuity with
            {
                AcknowledgementCheckpoint = checkpoint with
                {
                    Kind = ProcessObservationAcknowledgementKind
                        .AuthoritativeRecoveryPersisted,
                },
            },
            "healthyWithSever" => current.ObservationContinuity with
            {
                IsLost = false,
                TrustSeverPersisted = false,
                LossEpoch = null,
            },
            "lostDeliveredAtEnvelopeRevision" => current.ObservationContinuity with
            {
                AcknowledgementCheckpoint = checkpoint with
                {
                    TransitionRevision = current.Revision,
                    Delivered = true,
                },
            },
            "healthyDeliveredAtEnvelopeRevision" => current.ObservationContinuity with
            {
                IsLost = false,
                TrustSeverPersisted = false,
                LastTrustedEpoch = "epoch-b",
                LossEpoch = null,
                AcknowledgementCheckpoint = new(
                    ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
                    "epoch-b",
                    current.Revision,
                    true),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        store.Seed(current with { ObservationContinuity = corrupt });
        int savesBefore = store.SaveCount;
        QueuePolicySource policies = new(PolicyEvidence(2, "policy-2", At(7, 0, 7)));
        ScriptedObservationSource observations = new(Batch(
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            clockSample: SampleTime(At(7, 0, 7), TimeSpan.FromMinutes(2)),
            values: [other]));
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            policies,
            observations,
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Equal(0, policies.ReadCount);
        Assert.Equal(0, observations.BatchReadCount);
        Assert.Equal(savesBefore, store.SaveCount);
        Assert.Equal(0, actions.CloseCalls);
    }

    [Fact]
    public async Task ReconstructedCheckpointEchoIsComparedByValueAcrossBothAckCasWrites()
    {
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([])
        {
            AcceptedEnvelopeProjection = CloneEnvelopeCollections,
        };
        ScriptedObservationSource observations = new(Batch(
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            isComplete: false,
            creationTimelineTrusted: false,
            continuityLost: true,
            clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromMinutes(1)),
            values: [other]));
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            observations,
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Single(observations.Acknowledgements);
        Assert.True(store.Current!.ObservationContinuity.AcknowledgementCheckpoint!.Delivered);
    }

    [Fact]
    public async Task TamperedNestedCheckpointEchoCannotTriggerAck()
    {
        ProcessObservation other = Root(43, Cutoff.AddTicks(1), @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([])
        {
            AcceptedEnvelopeProjection = replacement => replacement with
            {
                ObservationContinuity = replacement.ObservationContinuity with
                {
                    AcknowledgementCheckpoint = replacement.ObservationContinuity
                        .AcknowledgementCheckpoint! with
                    {
                        ObserverEpoch = "tampered-epoch",
                    },
                },
            },
        };
        ScriptedObservationSource observations = new(Batch(
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            isComplete: false,
            creationTimelineTrusted: false,
            continuityLost: true,
            clockSample: SampleTime(At(7, 0, 6), TimeSpan.FromMinutes(1)),
            values: [other]));
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            observations,
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.Empty(observations.AcknowledgementAttempts);
    }

    [Theory]
    [InlineData("undefinedKind")]
    [InlineData("blankEpoch")]
    [InlineData("deltaClaimsCatalog")]
    [InlineData("snapshotMissingCatalog")]
    public async Task MalformedObservationEvidenceIsDurablyStickyFailOpen(string scenario)
    {
        ProcessObservationBatchEvidence valid = Batch(
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            clockSample: SampleTime(At(7, 0, 6), TimeSpan.Zero),
            values: [NewRoot]);
        ProcessObservationBatchEvidence malformed = scenario switch
        {
            "undefinedKind" => valid with
            {
                BatchKind = (ProcessObservationBatchKind)999,
            },
            "blankEpoch" => valid with { ObserverEpoch = "  " },
            "deltaClaimsCatalog" => valid with
            {
                IsAuthoritativeAllProcessCatalog = true,
            },
            "snapshotMissingCatalog" => valid with
            {
                BatchKind = ProcessObservationBatchKind.AuthoritativeSnapshot,
                IsAuthoritativeAllProcessCatalog = false,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(PolicyEvidence(1, "policy-1", At(7, 0, 6))),
            new ScriptedObservationSource(malformed),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));

        Assert.True(store.Current!.ObservationContinuity.IsLost);
        Assert.True(store.Current.ObservationContinuity.TrustSeverPersisted);
        Assert.False(store.Current.ReducerState.CreationTimelineTrusted);
        Assert.Contains(NewRoot.Identity!.Key, store.Current.ReducerState.TaintedInstances);
        Assert.Equal(0, actions.CloseCalls);
        Assert.Equal(0, actions.TerminationCalls);
    }

    [Fact]
    public async Task NonExecutableButValidatedMorningStillDurablyReleasesNight()
    {
        DesktopPolicySnapshotDto morning = Policy(
            At(7, 9, 1),
            DesktopNightPhase.Morning);
        MemoryEnvelopeStore store = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(NonExecutablePolicyEvidence(
                1,
                "policy-degraded-morning",
                morning)),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.AuthoritativeSnapshot, NewRoot)),
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));

        Assert.True(store.Current!.ReducerState.MorningReleased);
        Assert.Equal(At(7, 9, 1), store.Current.ReducerState.LastEffectiveLogicalTime);
    }

    [Fact]
    public async Task UntrustedAuthoritativeBatchCannotPruneTerminalJournalEntry()
    {
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 11)),
                PolicyEvidence(3, "policy-3", At(7, 0, 12))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(
                    ProcessObservationBatchKind.AuthoritativeSnapshot,
                    "epoch-a",
                    isComplete: false,
                    isAllProcessCatalog: false,
                    creationTimelineTrusted: false,
                    continuityLost: true)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        await EventuallyAsync(() =>
            Assert.Single(store.Current!.ActionJournal.Values).TerminalReason
                == ProcessActionTerminalReason.TerminationCompleted);
        Assert.Equal(
            ProcessActionTerminalReason.TerminationCompleted,
            Assert.Single(store.Current!.ActionJournal.Values).TerminalReason);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));

        Assert.Single(store.Current!.ActionJournal);
        Assert.Equal(1, actions.CloseCalls);
        await EventuallyAsync(() => actions.TerminationCalls == 1);
        Assert.Equal(1, actions.TerminationCalls);
    }

    [Fact]
    public async Task MoreThanSixtyFourNormalPolicyRevisionsRemainUsableWithBoundedBindings()
    {
        const int revisions = 70;
        ValidatedProcessPolicy[] policies = Enumerable.Range(0, revisions)
            .Select(index => PolicyEvidence(
                index,
                $"policy-{index}",
                At(7, 0, 6).AddSeconds(index)))
            .ToArray();
        ProcessObservation other = Root(
            44,
            Cutoff.AddTicks(1),
            @"C:\Other\tool.exe");
        ProcessObservationBatchEvidence[] observations = Enumerable.Range(0, revisions)
            .Select(_ => Batch(ProcessObservationBatchKind.StartDelta, other))
            .ToArray();
        MemoryEnvelopeStore store = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(policies),
            new ScriptedObservationSource(observations),
            new RecordingActions([]),
            new RecordingMonotonicDelay([]));

        for (int index = 0; index < revisions; index++)
        {
            await coordinator.EvaluateAsync(new(
                ProcessObservationBatchKind.StartDelta,
                UserSid,
                SessionId));
        }

        Assert.Equal(revisions - 1, store.Current!.PolicyLedger.HighestRevision);
        Assert.InRange(
            store.Current.PolicyLedger.PayloadByEvaluationIdentity.Count,
            1,
            64);
        Assert.Equal(revisions, store.SaveCount);
    }

    [Fact]
    public async Task FullJournalFailsOpenUntilTrustedAuthoritativeAbsencePrunesTerminalEntries()
    {
        ProcessObservation other = Root(
            44,
            Cutoff.AddTicks(1),
            @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 7)),
                PolicyEvidence(3, "policy-3", At(7, 0, 8)),
                PolicyEvidence(4, "policy-4", At(7, 0, 13))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, other),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.AuthoritativeSnapshot, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        store.Seed(WithTerminalJournal(store.Current!, 256));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        Assert.Equal(256, store.Current!.ActionJournal.Count);
        Assert.Equal(0, actions.CloseCalls);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));
        Assert.Single(store.Current!.ActionJournal);
        Assert.Equal(1, actions.CloseCalls);
        await EventuallyAsync(() => actions.TerminationCalls == 1);
    }

    [Fact]
    public async Task TrustedAuthoritativeAbsencePrunesCrashClaimsAndRecoversJournalCapacity()
    {
        ProcessObservation other = Root(
            44,
            Cutoff.AddTicks(1),
            @"C:\Other\tool.exe");
        MemoryEnvelopeStore store = new([]);
        RecordingActions actions = new([]);
        await using ProcessGateCoordinator coordinator = new(
            store,
            new QueuePolicySource(
                PolicyEvidence(1, "policy-1", At(7, 0, 6)),
                PolicyEvidence(2, "policy-2", At(7, 0, 7)),
                PolicyEvidence(3, "policy-3", At(7, 0, 8)),
                PolicyEvidence(4, "policy-4", At(7, 0, 13))),
            new ScriptedObservationSource(
                Batch(ProcessObservationBatchKind.StartDelta, other),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot),
                Batch(ProcessObservationBatchKind.AuthoritativeSnapshot, NewRoot),
                Batch(ProcessObservationBatchKind.StartDelta, NewRoot)),
            actions,
            new RecordingMonotonicDelay([]));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        store.Seed(WithCrashClaimJournal(store.Current!, 256));

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.StartDelta,
            UserSid,
            SessionId));
        Assert.Equal(256, store.Current!.ActionJournal.Count);
        Assert.Equal(0, actions.CloseCalls);

        await coordinator.EvaluateAsync(new(
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            UserSid,
            SessionId));

        Assert.Single(store.Current!.ActionJournal);
        Assert.Equal(1, actions.CloseCalls);
        await EventuallyAsync(() => actions.TerminationCalls == 1);
        Assert.Equal(1, actions.TerminationCalls);
    }

    private static ValidatedProcessPolicy PolicyEvidence(
        long revision,
        string identity,
        DateTimeOffset evaluatedAt,
        DesktopPolicySnapshotDto? policy = null,
        string payloadFingerprint = "same-payload-shape") =>
        new(
            revision,
            identity,
            payloadFingerprint,
            new DesktopPolicyResult(
                true,
                false,
                null,
                new DesktopServiceRuntimeStatusDto(
                    true,
                    false,
                    null,
                    policy ?? Policy(evaluatedAt))));

    private static ValidatedProcessPolicy NonExecutablePolicyEvidence(
        long revision,
        string identity,
        DesktopPolicySnapshotDto snapshot) =>
        new(
            revision,
            identity,
            $"payload-{identity}",
            new DesktopPolicyResult(
                false,
                true,
                "outer-runtime-degraded",
                new DesktopServiceRuntimeStatusDto(
                    false,
                    true,
                    "outer-runtime-degraded",
                    snapshot)),
            snapshot);

    private static DesktopPolicySnapshotDto Policy(
        DateTimeOffset evaluatedAt,
        DesktopNightPhase phase = DesktopNightPhase.LastStart,
        IReadOnlyList<DesktopAppRuleDto>? rules = null,
        DesktopActiveOverrideDto? activeOverride = null) =>
        new(
            evaluatedAt,
            phase,
            Window(),
            rules ?? [GameRule()],
            [],
            true,
            false,
            activeOverride);

    private static DesktopAppRuleDto GameRule(
        string path = @"C:\Games\game.exe",
        int sessionMinutes = 35,
        string ruleId = "game") =>
        new(
            ruleId,
            path,
            [],
            DesktopAppRuleCategory.Game,
            sessionMinutes,
            true);

    private static DesktopActiveOverrideDto ActiveOverride(
        DesktopOverrideKind kind,
        params string[] allowedRuleIds) =>
        new(
            kind,
            At(7, 0, 10),
            At(7, 0, 10),
            kind == DesktopOverrideKind.Emergency
                ? At(7, 0, 40)
                : At(7, 0, 30),
            allowedRuleIds);

    private static DesktopOverrideRequest OverrideRequest(DesktopOverrideKind kind) => new(
        kind,
        kind == DesktopOverrideKind.Emergency
            ? DesktopEmergencyReason.Health
            : null);

    private static DesktopNightWindowDto Window() =>
        new(
            NightDate,
            At(6, 21, 0),
            Cutoff,
            At(7, 0, 40),
            At(7, 1, 0),
            At(7, 9, 0));

    private static ProcessObservationBatchEvidence Batch(
        ProcessObservationBatchKind kind,
        params ProcessObservation[] values) =>
        Batch(
            kind,
            "epoch-a",
            isComplete: true,
            isAllProcessCatalog: kind == ProcessObservationBatchKind.AuthoritativeSnapshot,
            creationTimelineTrusted: true,
            continuityLost: false,
            clockSample: null,
            values);

    private static ProcessObservationBatchEvidence Batch(
        ProcessObservationBatchKind kind,
        string epoch,
        bool isComplete = true,
        bool? isAllProcessCatalog = null,
        bool creationTimelineTrusted = true,
        bool continuityLost = false,
        ProcessObservationClockSample? clockSample = null,
        params ProcessObservation[] values) =>
        new(
            ProcessGateSourceStatus.Available,
            kind,
            values,
            epoch,
            isComplete,
            isAllProcessCatalog
                ?? kind == ProcessObservationBatchKind.AuthoritativeSnapshot,
            creationTimelineTrusted,
            continuityLost,
            null,
            clockSample);

    private static ProcessObservationClockSample Sample(
        DateTimeOffset startedAtUtc,
        long startedMonotonic) =>
        new(
            startedAtUtc,
            TimeSpan.FromTicks(startedMonotonic),
            startedAtUtc.AddMilliseconds(1),
            TimeSpan.FromTicks(startedMonotonic) + TimeSpan.FromMilliseconds(1));

    private static ProcessObservationClockSample SampleTime(
        DateTimeOffset startedAtUtc,
        TimeSpan startedMonotonic) =>
        new(
            startedAtUtc,
            startedMonotonic,
            startedAtUtc.AddMilliseconds(1),
            startedMonotonic + TimeSpan.FromMilliseconds(1));

    private static ProcessObservation Root(
        int pid,
        DateTimeOffset createdAt,
        string path = @"C:\Games\game.exe")
    {
        DateTimeOffset utc = createdAt.ToUniversalTime();
        return new(
            pid,
            new ObservedProcessIdentity(
                new ProcessInstanceKey(pid, utc.UtcTicks),
                utc,
                path,
                UserSid,
                SessionId),
            ParentLink.None);
    }

    private static ProcessObservation WithIdentity(
        ProcessObservation source,
        DateTimeOffset? creationInstantUtc = null,
        string? executablePath = null,
        string? userSid = null,
        int? sessionId = null)
    {
        ObservedProcessIdentity identity = source.Identity!;
        return source with
        {
            Identity = identity with
            {
                CreationInstantUtc = creationInstantUtc ?? identity.CreationInstantUtc,
                ExecutablePath = executablePath ?? identity.ExecutablePath,
                UserSid = userSid ?? identity.UserSid,
                SessionId = sessionId ?? identity.SessionId,
            },
        };
    }

    private static ProcessGateEnvelope WithTerminalJournal(
        ProcessGateEnvelope envelope,
        int count)
    {
        ImmutableDictionary<ProcessActionKey, ProcessActionJournalEntry>.Builder journal =
            ImmutableDictionary.CreateBuilder<
                ProcessActionKey,
                ProcessActionJournalEntry>();
        for (int index = 0; index < count; index++)
        {
            DateTimeOffset created = At(6, 22, 0).AddTicks(index);
            ProcessExactTarget target = new(
                new ProcessInstanceKey(1000 + index, created.UtcTicks),
                created,
                @"C:\Games\game.exe",
                UserSid,
                SessionId,
                "game",
                Cutoff,
                NightDate,
                envelope.ReducerState.RuleFingerprint!,
                1,
                "seed-policy",
                "seed-payload",
                At(6, 22, 0));
            journal.Add(
                target.ActionKey,
                new(
                    target,
                    index + 1,
                    ProcessCloseOutcome.Requested,
                    $"recheck-{index}",
                    true,
                    ProcessTerminationOutcome.Terminated,
                    ProcessActionTerminalReason.TerminationCompleted));
        }

        return envelope with
        {
            Revision = checked(envelope.Revision + 1),
            ActionJournal = journal.ToImmutable(),
            NextJournalSequence = count + 1,
        };
    }

    private static ProcessGateEnvelope WithCrashClaimJournal(
        ProcessGateEnvelope envelope,
        int count)
    {
        ProcessGateEnvelope terminal = WithTerminalJournal(envelope, count);
        return terminal with
        {
            ActionJournal = terminal.ActionJournal.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value with
                {
                    CloseCompletion = null,
                    RecheckClaimIdentity = null,
                    TerminationClaimed = false,
                    TerminationCompletion = null,
                    TerminalReason = null,
                }),
        };
    }

    private static ProcessGateEnvelope CloneEnvelopeCollections(ProcessGateEnvelope source)
    {
        ProcessGateState state = source.ReducerState;
        ProcessGateState clonedState = state with
        {
            RuleStates = state.RuleStates.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase),
            KnownInstances = state.KnownInstances.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value),
            EligibleInstances = state.EligibleInstances.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value),
            TemporaryInstances = state.TemporaryInstances.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value),
            TaintedInstances = state.TaintedInstances.ToImmutableHashSet(),
            RetiredOverrideIdentities = state.RetiredOverrideIdentities.ToImmutableHashSet(),
        };
        ProcessPolicyLedger ledger = source.PolicyLedger with
        {
            PayloadByEvaluationIdentity = source.PolicyLedger.PayloadByEvaluationIdentity
                .ToImmutableDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal),
        };
        ProcessObservationContinuityState continuity = new(
            source.ObservationContinuity.IsLost,
            source.ObservationContinuity.TrustSeverPersisted,
            source.ObservationContinuity.LastTrustedEpoch,
            source.ObservationContinuity.LossEpoch,
            source.ObservationContinuity.ClockEpoch,
            source.ObservationContinuity.SampleUtcHighWater,
            source.ObservationContinuity.SampleMonotonicHighWater,
            source.ObservationContinuity.AcknowledgementCheckpoint is { } checkpoint
                ? checkpoint with { }
                : null);
        return source with
        {
            ReducerState = clonedState,
            ActionJournal = source.ActionJournal.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value),
            PolicyLedger = ledger,
            ObservationContinuity = continuity,
        };
    }

    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }

        Assert.True(condition(), "The asynchronous continuation did not reach the expected state.");
    }

    private sealed class MemoryEnvelopeStore(List<string> order) : IProcessGateEnvelopeStore
    {
        private readonly object _sync = new();

        public ProcessGateEnvelope? Current { get; private set; }

        public Queue<ProcessGateStoreSaveStatus> ForcedSaveStatuses { get; } = [];

        public Queue<ProcessGateStoreLoadStatus> ForcedLoadStatuses { get; } = [];

        public Func<ProcessGateEnvelope, ProcessGateEnvelope>? AcceptedEnvelopeProjection
        {
            get;
            set;
        }

        public Action<ProcessGateEnvelope>? AfterSaved { get; set; }

        public Func<
            ProcessGateEnvelope,
            ProcessGateEnvelope,
            ProcessGateEnvelope>? ForcedConflictWinnerProjection { get; init; }

        public int LoadCount { get; private set; }

        public int SaveCount { get; private set; }

        public void Seed(ProcessGateEnvelope envelope)
        {
            lock (_sync)
            {
                Current = envelope;
            }
        }

        public ValueTask<ProcessGateEnvelopeLoadResult> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                LoadCount++;
                if (ForcedLoadStatuses.Count > 0)
                {
                    ProcessGateStoreLoadStatus forced = ForcedLoadStatuses.Dequeue();
                    return ValueTask.FromResult(new ProcessGateEnvelopeLoadResult(
                        forced,
                        forced == ProcessGateStoreLoadStatus.Found ? Current : null));
                }

                return ValueTask.FromResult(Current is null
                    ? new ProcessGateEnvelopeLoadResult(
                        ProcessGateStoreLoadStatus.NotFound,
                        null)
                    : new(
                        ProcessGateStoreLoadStatus.Found,
                        Current));
            }
        }

        public ValueTask<ProcessGateEnvelopeSaveResult> CompareExchangeAsync(
            long expectedRevision,
            ProcessGateEnvelope replacement,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                SaveCount++;
                long currentRevision = Current?.Revision ?? 0;
                if (currentRevision != expectedRevision)
                {
                    return ValueTask.FromResult(new ProcessGateEnvelopeSaveResult(
                        ProcessGateStoreSaveStatus.Conflict,
                        Current));
                }

                ProcessGateStoreSaveStatus forced = ForcedSaveStatuses.Count > 0
                    ? ForcedSaveStatuses.Dequeue()
                    : ProcessGateStoreSaveStatus.Saved;
                if (forced != ProcessGateStoreSaveStatus.Saved)
                {
                    if (forced == ProcessGateStoreSaveStatus.Conflict
                        && Current is not null
                        && ForcedConflictWinnerProjection is not null)
                    {
                        Current = ForcedConflictWinnerProjection(Current, replacement);
                    }

                    return ValueTask.FromResult(new ProcessGateEnvelopeSaveResult(
                        forced,
                        Current));
                }

                ProcessGateEnvelope accepted = AcceptedEnvelopeProjection?.Invoke(replacement)
                    ?? replacement;
                Current = accepted;
                ProcessActionJournalEntry? changed = accepted.ActionJournal.Values
                    .OrderByDescending(entry => entry.Sequence)
                    .FirstOrDefault();
                string label = changed switch
                {
                    { TerminationClaimed: true, TerminationCompletion: null } =>
                        "save:terminate-claim",
                    { CloseCompletion: null } => "save:close-claim",
                    _ => "save:state",
                };
                order.Add(label);
                AfterSaved?.Invoke(accepted);
                return ValueTask.FromResult(new ProcessGateEnvelopeSaveResult(
                    ProcessGateStoreSaveStatus.Saved,
                    accepted));
            }
        }
    }

    private sealed class QueuePolicySource(params ValidatedProcessPolicy[] values) :
        IProcessGatePolicySource
    {
        private readonly Queue<ValidatedProcessPolicy> _values = new(values);
        private ValidatedProcessPolicy? _last;

        public int ReadCount { get; private set; }

        public ValueTask<ProcessGatePolicySourceResult> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            _last = _values.Count > 0 ? _values.Dequeue() : _last;
            return ValueTask.FromResult(new ProcessGatePolicySourceResult(
                ProcessGateSourceStatus.Available,
                _last,
                null));
        }
    }

    private sealed class CadenceClock(DateTimeOffset utcNow) :
        IProcessGateMonotonicDelay
    {
        private readonly object _sync = new();
        private readonly TaskCompletionSource _releaseGrace = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTimeOffset _utcNow = utcNow;
        private TimeSpan _monotonicNow;

        public TaskCompletionSource GraceStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_sync)
                {
                    return _utcNow;
                }
            }
        }

        public TimeSpan MonotonicNow
        {
            get
            {
                lock (_sync)
                {
                    return _monotonicNow;
                }
            }
        }

        public long GetTimestamp() => MonotonicNow.Ticks;

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            MonotonicNow - TimeSpan.FromTicks(startingTimestamp);

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(TimeSpan.FromSeconds(5), delay);
            GraceStarted.TrySetResult();
            await _releaseGrace.Task.WaitAsync(cancellationToken);
        }

        public void Advance(TimeSpan amount)
        {
            lock (_sync)
            {
                _utcNow += amount;
                _monotonicNow += amount;
            }
        }

        public void ReleaseGrace() => _releaseGrace.TrySetResult();
    }

    private sealed class CadencePolicySource(CadenceClock clock) :
        IProcessGatePolicySource
    {
        private long _revision;

        public ValueTask<ProcessGatePolicySourceResult> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long revision = Interlocked.Increment(ref _revision);
            DateTimeOffset evaluatedAt = clock.UtcNow;
            ValidatedProcessPolicy policy = PolicyEvidence(
                revision,
                $"cadence-policy-{revision}",
                evaluatedAt,
                payloadFingerprint: $"cadence-payload-{revision}");
            return ValueTask.FromResult(new ProcessGatePolicySourceResult(
                ProcessGateSourceStatus.Available,
                policy,
                null));
        }
    }

    private sealed class CadenceObservationSource(
        CadenceClock clock,
        ProcessObservation root) : IProcessGateObservationSource
    {
        private static readonly TimeSpan MaximumContinuityGap = TimeSpan.FromSeconds(2);
        private readonly object _sync = new();
        private DateTimeOffset _lastAuthoritativeAt = clock.UtcNow;
        private DateTimeOffset? _lastBatchAt;
        private bool _initialDelivered;

        public int BatchReadCount { get; private set; }

        public int ScansDuringGrace { get; private set; }

        public int ExactReadCount { get; private set; }

        public bool ContinuityLostAtExactRead { get; private set; }

        public ValueTask<ProcessObservationBatchEvidence> ReadBatchAsync(
            ProcessCatalogReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                DateTimeOffset now = clock.UtcNow;
                TimeSpan monotonic = clock.MonotonicNow;
                BatchReadCount++;
                bool initial = !_initialDelivered;
                if (initial)
                {
                    _initialDelivered = true;
                }
                else
                {
                    ScansDuringGrace++;
                }

                bool authoritative = now - _lastAuthoritativeAt >= MaximumContinuityGap;
                if (authoritative)
                {
                    _lastAuthoritativeAt = now;
                }

                _lastBatchAt = now;
                ProcessObservation[] observations = initial || authoritative
                    ? [root]
                    : [];
                return ValueTask.FromResult(Evidence(
                    authoritative
                        ? ProcessObservationBatchKind.AuthoritativeSnapshot
                        : ProcessObservationBatchKind.StartDelta,
                    observations,
                    now,
                    monotonic,
                    authoritative,
                    creationTimelineTrusted: true,
                    continuityLost: false));
            }
        }

        public ValueTask<ProcessObservationBatchEvidence> ReadExactAsync(
            ProcessExactTarget target,
            ProcessCatalogPolicyBinding policyBinding,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ExactReadCount++;
                DateTimeOffset now = clock.UtcNow;
                TimeSpan monotonic = clock.MonotonicNow;
                ContinuityLostAtExactRead = _lastBatchAt is not { } last
                    || now - last > MaximumContinuityGap;
                return ValueTask.FromResult(Evidence(
                    ProcessObservationBatchKind.StartDelta,
                    [root],
                    now,
                    monotonic,
                    authoritative: false,
                    creationTimelineTrusted: !ContinuityLostAtExactRead,
                    continuityLost: ContinuityLostAtExactRead));
            }
        }

        public ValueTask AcknowledgeAsync(
            ProcessObservationAcknowledgement acknowledgement,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        private static ProcessObservationBatchEvidence Evidence(
            ProcessObservationBatchKind kind,
            IReadOnlyList<ProcessObservation> observations,
            DateTimeOffset utc,
            TimeSpan monotonic,
            bool authoritative,
            bool creationTimelineTrusted,
            bool continuityLost) => new(
            ProcessGateSourceStatus.Available,
            kind,
            observations,
            "cadence-epoch",
            true,
            authoritative,
            creationTimelineTrusted,
            continuityLost,
            continuityLost ? "catalog-continuity-break" : null,
            new(utc, monotonic, utc, monotonic));
    }

    private sealed class SignalledActions(List<string> order) :
        IExactProcessActionAdapter
    {
        private int _closeCalls;
        private int _terminationCalls;

        public int CloseCalls => Volatile.Read(ref _closeCalls);

        public int TerminationCalls => Volatile.Read(ref _terminationCalls);

        public TaskCompletionSource TerminationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ProcessCloseOutcome> RequestCloseAsync(
            ProcessExactTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _closeCalls);
            order.Add("action:close");
            return ValueTask.FromResult(ProcessCloseOutcome.Requested);
        }

        public ValueTask<ProcessTerminationOutcome> RequestTerminationAsync(
            ProcessExactTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _terminationCalls);
            order.Add("action:terminate");
            TerminationObserved.TrySetResult();
            return ValueTask.FromResult(ProcessTerminationOutcome.Terminated);
        }
    }

    private sealed class SignallingRaceBarrier : ICutoffPipelineBarrier
    {
        private readonly CutoffPipelineBarrier _inner = new();
        private int _entries;

        public TaskCompletionSource FirstLeaseAcquired { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondEntryRequested { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IDisposable> EnterAsync(
            CancellationToken cancellationToken = default)
        {
            int ordinal = Interlocked.Increment(ref _entries);
            if (ordinal == 2)
            {
                SecondEntryRequested.TrySetResult();
            }

            IDisposable lease = await _inner.EnterAsync(cancellationToken);
            if (ordinal == 1)
            {
                FirstLeaseAcquired.TrySetResult();
            }

            return lease;
        }
    }

    private sealed class OverrideRacePolicySource : IProcessGatePolicySource
    {
        private readonly object _sync = new();
        private DesktopOverrideKind? _acceptedOverride;
        private long _reads;

        public void Accept(DesktopOverrideKind kind)
        {
            lock (_sync)
            {
                _acceptedOverride = kind;
            }
        }

        public ValueTask<ProcessGatePolicySourceResult> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long revision = Interlocked.Increment(ref _reads);
            DesktopOverrideKind? accepted;
            lock (_sync)
            {
                accepted = _acceptedOverride;
            }

            DateTimeOffset evaluatedAt = revision == 1
                ? At(7, 0, 6)
                : At(7, 0, 11);
            DesktopPolicySnapshotDto snapshot = accepted is { } kind
                ? Policy(
                    evaluatedAt,
                    DesktopNightPhase.OverrideActive,
                    activeOverride: kind == DesktopOverrideKind.TeamRescue
                        ? ActiveOverride(kind, "game")
                        : ActiveOverride(kind))
                : Policy(evaluatedAt);
            ValidatedProcessPolicy policy = PolicyEvidence(
                revision,
                $"race-policy-{revision}",
                evaluatedAt,
                snapshot,
                $"race-payload-{revision}");
            return ValueTask.FromResult(new ProcessGatePolicySourceResult(
                ProcessGateSourceStatus.Available,
                policy,
                null));
        }
    }

    private sealed class OverrideRaceRequestIds : IProtocolRequestIdSource
    {
        private int _next;

        public string NextRequestId() => Interlocked.Increment(ref _next) switch
        {
            1 => "override-1",
            2 => "policy-2",
            _ => throw new InvalidOperationException("Unexpected request."),
        };
    }

    private sealed class CoordinatedOverrideTransport(
        DesktopOverrideKind kind,
        OverrideRacePolicySource policies,
        RaceSequence sequence) : INightGatePipeTransport
    {
        private readonly TaskCompletionSource _allowAcceptance = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public TaskCompletionSource OverrideRequestStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Accepted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int AcceptedOrder { get; private set; }

        public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
            ReadOnlyMemory<byte> requestUtf8,
            CancellationToken cancellationToken = default)
        {
            int request = Interlocked.Increment(ref _requests);
            if (request != 1)
            {
                return Encoding.UTF8.GetBytes("{}");
            }

            OverrideRequestStarted.TrySetResult();
            await _allowAcceptance.Task.WaitAsync(cancellationToken);
            policies.Accept(kind);
            AcceptedOrder = sequence.Next();
            Accepted.TrySetResult();
            string kindToken = kind switch
            {
                DesktopOverrideKind.TeamRescue => "teamRescue",
                DesktopOverrideKind.Emergency => "emergency",
                DesktopOverrideKind.Entertainment => "entertainment",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            string minutes = kind == DesktopOverrideKind.Emergency ? "30" : "20";
            string response =
                """
                {"version":1,"type":"requestOverrideResult","requestId":"override-1","payload":{"status":"success","data":{"accepted":true,"kind":"__KIND__","startsAtUtc":"2026-07-07T00:00:00+00:00","endsAtUtc":"2026-07-07T00:__MINUTES__:00+00:00"}}}
                """
                .Replace("__KIND__", kindToken, StringComparison.Ordinal)
                .Replace("__MINUTES__", minutes, StringComparison.Ordinal);
            return Encoding.UTF8.GetBytes(response);
        }

        public void AllowAcceptance() => _allowAcceptance.TrySetResult();
    }

    private sealed class RaceSequence
    {
        private int _next;

        public int Next() => Interlocked.Increment(ref _next);
    }

    private sealed class BlockingTerminationActions(RaceSequence sequence) :
        IExactProcessActionAdapter
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource TerminationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CompletedOrder { get; private set; }

        public ValueTask<ProcessCloseOutcome> RequestCloseAsync(
            ProcessExactTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ProcessCloseOutcome.Requested);
        }

        public async ValueTask<ProcessTerminationOutcome> RequestTerminationAsync(
            ProcessExactTarget target,
            CancellationToken cancellationToken = default)
        {
            TerminationStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            CompletedOrder = sequence.Next();
            return ProcessTerminationOutcome.Terminated;
        }

        public void ReleaseTermination() => _release.TrySetResult();
    }

    private sealed class GateThenUnavailablePolicySource(ValidatedProcessPolicy first) :
        IProcessGatePolicySource
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount { get; private set; }

        public async ValueTask<ProcessGatePolicySourceResult> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (ReadCount == 1)
            {
                FirstReadStarted.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                return new(
                    ProcessGateSourceStatus.Available,
                    first,
                    null);
            }

            return new(
                ProcessGateSourceStatus.Unavailable,
                null,
                "policy-unavailable");
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class UnavailablePolicySource : IProcessGatePolicySource
    {
        public ValueTask<ProcessGatePolicySourceResult> ReadAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProcessGatePolicySourceResult(
                ProcessGateSourceStatus.Unavailable,
                null,
                "policy-unavailable"));
    }

    private sealed class ScriptedObservationSource(params ProcessObservationBatchEvidence[] values) :
        IProcessGateObservationSource
    {
        private readonly Queue<ProcessObservationBatchEvidence> _values = new(values);
        private int _reads;

        public int ExactReadCount { get; private set; }

        public int BatchReadCount { get; private set; }

        public List<ProcessCatalogReadRequest> BatchRequests { get; } = [];

        public List<(ProcessExactTarget Target, ProcessCatalogPolicyBinding Binding)>
            ExactRequests { get; } = [];

        public List<ProcessObservationAcknowledgement> Acknowledgements { get; } = [];

        public List<ProcessObservationAcknowledgement> AcknowledgementAttempts { get; } = [];

        public Queue<bool> AcknowledgementFailures { get; } = [];

        public Func<
            ProcessObservationAcknowledgement,
            CancellationToken,
            ValueTask>? AcknowledgementHook { get; init; }

        public Action? BeforeExactRead { get; init; }

        public ValueTask<ProcessObservationBatchEvidence> ReadBatchAsync(
            ProcessCatalogReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BatchReadCount++;
            BatchRequests.Add(request);
            return ValueTask.FromResult(Normalize(_values.Dequeue()));
        }

        public ValueTask<ProcessObservationBatchEvidence> ReadExactAsync(
            ProcessExactTarget target,
            ProcessCatalogPolicyBinding policyBinding,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeExactRead?.Invoke();
            ExactReadCount++;
            ExactRequests.Add((target, policyBinding));
            return ValueTask.FromResult(Normalize(_values.Dequeue()));
        }

        public async ValueTask AcknowledgeAsync(
            ProcessObservationAcknowledgement acknowledgement,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcknowledgementAttempts.Add(acknowledgement);
            if (AcknowledgementHook is not null)
            {
                await AcknowledgementHook(acknowledgement, cancellationToken);
            }

            if (AcknowledgementFailures.Count > 0
                && AcknowledgementFailures.Dequeue())
            {
                throw new InvalidOperationException("acknowledgement failed");
            }

            Acknowledgements.Add(acknowledgement);
        }

        private ProcessObservationBatchEvidence Normalize(
            ProcessObservationBatchEvidence evidence)
        {
            int read = ++_reads;
            return evidence.ClockSample is not null
                ? evidence
                : evidence with
                {
                    ClockSample = Sample(
                        At(7, 0, 6).AddSeconds(read),
                        checked(read * TimeSpan.TicksPerSecond)),
                };
        }
    }

    private sealed class RecordingActions(
        List<string> order,
        ProcessCloseOutcome closeOutcome = ProcessCloseOutcome.Requested,
        ProcessTerminationOutcome terminationOutcome = ProcessTerminationOutcome.Terminated,
        bool throwOnClose = false,
        bool throwOnTermination = false) : IExactProcessActionAdapter
    {
        public int CloseCalls { get; private set; }

        public int TerminationCalls { get; private set; }

        public ValueTask<ProcessCloseOutcome> RequestCloseAsync(
            ProcessExactTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloseCalls++;
            order.Add("action:close");
            if (throwOnClose)
            {
                throw new InvalidOperationException("close failed");
            }

            return ValueTask.FromResult(closeOutcome);
        }

        public ValueTask<ProcessTerminationOutcome> RequestTerminationAsync(
            ProcessExactTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerminationCalls++;
            order.Add("action:terminate");
            if (throwOnTermination)
            {
                throw new InvalidOperationException("termination failed");
            }

            return ValueTask.FromResult(terminationOutcome);
        }
    }

    private sealed class BlockingCloseActions : IExactProcessActionAdapter
    {
        private readonly TaskCompletionSource<ProcessCloseOutcome> _close = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CloseStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CloseCalls { get; private set; }

        public int TerminationCalls { get; private set; }

        public async ValueTask<ProcessCloseOutcome> RequestCloseAsync(
            ProcessExactTarget target,
            CancellationToken cancellationToken = default)
        {
            CloseCalls++;
            CloseStarted.TrySetResult();
            return await _close.Task;
        }

        public ValueTask<ProcessTerminationOutcome> RequestTerminationAsync(
            ProcessExactTarget target,
            CancellationToken cancellationToken = default)
        {
            TerminationCalls++;
            return ValueTask.FromResult(ProcessTerminationOutcome.Terminated);
        }

        public void ReleaseClose() => _close.TrySetResult(ProcessCloseOutcome.Requested);
    }

    private sealed class ThrowingObservationSource : IProcessGateObservationSource
    {
        public ValueTask<ProcessObservationBatchEvidence> ReadBatchAsync(
            ProcessCatalogReadRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("catalog failed");

        public ValueTask<ProcessObservationBatchEvidence> ReadExactAsync(
            ProcessExactTarget target,
            ProcessCatalogPolicyBinding policyBinding,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("lookup failed");

        public ValueTask AcknowledgeAsync(
            ProcessObservationAcknowledgement acknowledgement,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingMonotonicDelay(List<string> order) :
        IProcessGateMonotonicDelay
    {
        private long _ticks;

        public List<TimeSpan> Requests { get; } = [];

        public long GetTimestamp() => _ticks;

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            TimeSpan.FromTicks(_ticks - startingTimestamp);

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(delay);
            order.Add("delay");
            _ticks += delay.Ticks;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingDelay(CancellationTokenSource cancellation) :
        IProcessGateMonotonicDelay
    {
        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class BlockingDelay : IProcessGateMonotonicDelay
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private long _ticks;

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public long GetTimestamp() => _ticks;

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            TimeSpan.FromTicks(_ticks - startingTimestamp);

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            _ticks += delay.Ticks;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ThrowingCancellationDelay : IProcessGateMonotonicDelay
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public long GetTimestamp() => 0;

        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;

        public async ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(
                static () => throw new InvalidOperationException("cancellation callback failed"));
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class EarlyReturningDelay : IProcessGateMonotonicDelay
    {
        private long _ticks;
        private bool _returnedEarly;

        public TimeSpan Elapsed => TimeSpan.FromTicks(_ticks);

        public List<TimeSpan> Requests { get; } = [];

        public long GetTimestamp() => _ticks;

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            TimeSpan.FromTicks(_ticks - startingTimestamp);

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(delay);
            TimeSpan actual = !_returnedEarly
                ? TimeSpan.FromSeconds(4)
                : delay;
            _returnedEarly = true;
            _ticks += actual.Ticks;
            return ValueTask.CompletedTask;
        }
    }
}
