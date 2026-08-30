using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DurableProcessSourceContinuityTests
{
    [Fact]
    public async Task MissingCheckpointIsDurablyInitializedAsFreshLost()
    {
        MemoryContinuityStore store = new();
        SequenceEpochFactory epochs = new("fresh-epoch");
        DurableProcessSourceContinuity continuity = new(store, epochs);

        ProcessSourceContinuityAccessResult first =
            await continuity.GetCurrentAsync();
        ProcessSourceContinuityAccessResult second =
            await continuity.GetCurrentAsync();

        Assert.Equal(ProcessSourceContinuityAccessStatus.Available, first.Status);
        Assert.NotNull(first.Checkpoint);
        Assert.Equal(1, first.Checkpoint.Version);
        Assert.Equal(ProcessSourceContinuityPhase.FreshLost, first.Checkpoint.Phase);
        Assert.Equal("fresh-epoch", first.Checkpoint.ObserverEpoch);
        Assert.Equal(0, first.Checkpoint.HighestAcceptedTransitionRevision);
        Assert.Null(first.Checkpoint.LastAcceptedAcknowledgement);
        Assert.Equal(first.Checkpoint, store.Current);
        Assert.Equal(first.Checkpoint, second.Checkpoint);
        Assert.Single(store.ExpectedVersions);
        Assert.Null(store.ExpectedVersions[0]);
        Assert.Equal(1, epochs.CallCount);
    }

    [Fact]
    public async Task RestartBeforeExactSeverRetryAddsLossButDoesNotRotateAgain()
    {
        ProcessSourceAcknowledgementTuple sever = Sever("epoch-a", 10);
        MemoryContinuityStore store = new(new(
            4,
            ProcessSourceContinuityPhase.Recovering,
            "epoch-b",
            10,
            sever));
        SequenceEpochFactory epochs = new("epoch-c");
        DurableProcessSourceContinuity continuity = new(store, epochs);
        ProcessObservationAcknowledgement retry = Ack(sever);

        ProcessSourceAcknowledgementResult result =
            await continuity.TryAcknowledgeAsync(retry);

        Assert.Equal(ProcessSourceContinuityAccessStatus.Available, result.Status);
        Assert.Equal(ProcessSourceContinuityReductionKind.Idempotent,
            result.Disposition);
        Assert.Equal(ProcessSourceContinuityPhase.Lost, store.Current!.Phase);
        Assert.Equal("epoch-b", store.Current.ObserverEpoch);
        Assert.Equal(10, store.Current.HighestAcceptedTransitionRevision);
        Assert.Equal(sever, store.Current.LastAcceptedAcknowledgement);
        Assert.Equal(5, store.Current.Version);
        Assert.Single(store.ExpectedVersions);
        Assert.Equal(4, store.ExpectedVersions[0]);
        Assert.Equal(0, epochs.CallCount);

        ProcessSourceAcknowledgementResult next =
            await continuity.TryAcknowledgeAsync(new(
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                "epoch-b",
                11));

        Assert.Equal(ProcessSourceContinuityReductionKind.Applied, next.Disposition);
        Assert.Equal(ProcessSourceContinuityPhase.Recovering, store.Current.Phase);
        Assert.Equal("epoch-c", store.Current.ObserverEpoch);
        Assert.Equal(11, store.Current.HighestAcceptedTransitionRevision);
        Assert.Equal(1, epochs.CallCount);
    }

    [Fact]
    public async Task RestartAckFirstCannotTrustRestoredRecoveryCandidate()
    {
        ProcessSourceAcknowledgementTuple sever = Sever("epoch-a", 10);
        MemoryContinuityStore store = new(new(
            9,
            ProcessSourceContinuityPhase.RecoveryCandidate,
            "epoch-b",
            10,
            sever));
        DurableProcessSourceContinuity continuity = new(
            store,
            new SequenceEpochFactory());

        ProcessSourceAcknowledgementResult result =
            await continuity.TryAcknowledgeAsync(new(
                ProcessObservationAcknowledgementKind
                    .AuthoritativeRecoveryPersisted,
                "epoch-b",
                20));

        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored,
            result.Disposition);
        Assert.Equal(ProcessSourceContinuityPhase.Lost, store.Current!.Phase);
        Assert.Equal(10, store.Current.HighestAcceptedTransitionRevision);
        Assert.Equal(sever, store.Current.LastAcceptedAcknowledgement);
        Assert.Single(store.ExpectedVersions);
        Assert.Equal(9, store.ExpectedVersions[0]);
    }

    [Fact]
    public async Task MissingFreshLostAdoptsOneForeignPendingSever()
    {
        MemoryContinuityStore store = new();
        SequenceEpochFactory epochs = new("fresh-epoch", "recovery-epoch");
        DurableProcessSourceContinuity continuity = new(store, epochs);
        await continuity.GetCurrentAsync();

        ProcessSourceAcknowledgementResult result =
            await continuity.TryAcknowledgeAsync(new(
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                "old-desktop-epoch",
                17));

        Assert.Equal(ProcessSourceContinuityReductionKind.Applied, result.Disposition);
        Assert.Equal(ProcessSourceContinuityPhase.Recovering, store.Current!.Phase);
        Assert.Equal("recovery-epoch", store.Current.ObserverEpoch);
        Assert.Equal(17, store.Current.HighestAcceptedTransitionRevision);
        Assert.Equal(2, store.Current.Version);
        Assert.Equal(2, epochs.CallCount);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("corrupt")]
    public async Task LoadFailureFailsOpenWithoutMutation(string scenario)
    {
        ProcessSourceContinuityStoreLoadStatus storeStatus = scenario == "unavailable"
            ? ProcessSourceContinuityStoreLoadStatus.Unavailable
            : ProcessSourceContinuityStoreLoadStatus.Corrupt;
        ProcessSourceContinuityAccessStatus expectedStatus = scenario == "unavailable"
            ? ProcessSourceContinuityAccessStatus.Unavailable
            : ProcessSourceContinuityAccessStatus.Corrupt;
        MemoryContinuityStore store = new()
        {
            ForcedLoadStatus = storeStatus,
        };
        DurableProcessSourceContinuity continuity = new(
            store,
            new SequenceEpochFactory("unused"));

        ProcessSourceContinuityAccessResult result =
            await continuity.GetCurrentAsync();

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Checkpoint);
        Assert.Empty(store.ExpectedVersions);
        Assert.Null(store.Current);
    }

    [Fact]
    public async Task SemanticallyCorruptFoundCheckpointIsNeverCleared()
    {
        ProcessSourceContinuityCheckpoint corrupt = new(
            5,
            ProcessSourceContinuityPhase.Trusted,
            "epoch-b",
            0,
            null);
        MemoryContinuityStore store = new(corrupt);
        DurableProcessSourceContinuity continuity = new(
            store,
            new SequenceEpochFactory("unused"));

        ProcessSourceContinuityAccessResult result =
            await continuity.GetCurrentAsync();

        Assert.Equal(ProcessSourceContinuityAccessStatus.Corrupt, result.Status);
        Assert.Null(result.Checkpoint);
        Assert.Same(corrupt, store.Current);
        Assert.Empty(store.ExpectedVersions);
    }

    [Fact]
    public async Task ConflictWinnerWithExactTupleMakesRetryIdempotent()
    {
        ProcessSourceContinuityCheckpoint lost = new(
            1,
            ProcessSourceContinuityPhase.Lost,
            "epoch-a",
            0,
            null);
        ProcessSourceAcknowledgementTuple sever = Sever("epoch-a", 10);
        ProcessSourceContinuityCheckpoint winner = new(
            2,
            ProcessSourceContinuityPhase.Recovering,
            "winner-epoch",
            10,
            sever);
        MemoryContinuityStore store = new(lost);
        store.SaveHook = (_, _) =>
        {
            store.Current = winner;
            return new(
                ProcessSourceContinuityStoreSaveStatus.Conflict,
                winner);
        };
        SequenceEpochFactory epochs = new("losing-epoch");
        DurableProcessSourceContinuity continuity = new(store, epochs);

        ProcessSourceAcknowledgementResult result =
            await continuity.TryAcknowledgeAsync(Ack(sever));

        Assert.Equal(ProcessSourceContinuityAccessStatus.Available, result.Status);
        Assert.Equal(ProcessSourceContinuityReductionKind.Idempotent,
            result.Disposition);
        Assert.Equal(winner, result.Checkpoint);
        Assert.Equal(winner, store.Current);
        Assert.Single(store.ExpectedVersions);
        Assert.Equal(1, epochs.CallCount);
    }

    [Fact]
    public async Task ConflictWinnerWithSameRevisionDifferentTupleIsNotOverwritten()
    {
        ProcessSourceContinuityCheckpoint lost = new(
            1,
            ProcessSourceContinuityPhase.Lost,
            "epoch-a",
            0,
            null);
        ProcessSourceAcknowledgementTuple other = Sever("other-loss", 10);
        ProcessSourceContinuityCheckpoint winner = new(
            2,
            ProcessSourceContinuityPhase.Recovering,
            "winner-epoch",
            10,
            other);
        MemoryContinuityStore store = new(lost);
        store.SaveHook = (_, _) =>
        {
            store.Current = winner;
            return new(
                ProcessSourceContinuityStoreSaveStatus.Conflict,
                winner);
        };
        DurableProcessSourceContinuity continuity = new(
            store,
            new SequenceEpochFactory("losing-epoch"));

        ProcessSourceAcknowledgementResult result =
            await continuity.TryAcknowledgeAsync(new(
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                "epoch-a",
                10));

        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored,
            result.Disposition);
        Assert.Equal(winner, result.Checkpoint);
        Assert.Equal(winner, store.Current);
        Assert.Single(store.ExpectedVersions);
    }

    [Fact]
    public async Task TamperedSavedEchoIsCorruptAndNeverClaimsRecovery()
    {
        MemoryContinuityStore store = new()
        {
            SavedProjection = replacement => replacement with
            {
                ObserverEpoch = "tampered-epoch",
            },
        };
        DurableProcessSourceContinuity continuity = new(
            store,
            new SequenceEpochFactory("fresh-epoch"));

        ProcessSourceContinuityAccessResult result =
            await continuity.GetCurrentAsync();

        Assert.Equal(ProcessSourceContinuityAccessStatus.Corrupt, result.Status);
        Assert.Null(result.Checkpoint);
        Assert.Single(store.ExpectedVersions);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("corrupt")]
    public async Task SaveFailureKeepsCoordinatorAcknowledgementPending(
        string scenario)
    {
        ProcessSourceContinuityStoreSaveStatus saveStatus = scenario == "unavailable"
            ? ProcessSourceContinuityStoreSaveStatus.Unavailable
            : ProcessSourceContinuityStoreSaveStatus.Corrupt;
        ProcessSourceContinuityAccessStatus expectedStatus = scenario == "unavailable"
            ? ProcessSourceContinuityAccessStatus.Unavailable
            : ProcessSourceContinuityAccessStatus.Corrupt;
        MemoryContinuityStore store = new(new(
            1,
            ProcessSourceContinuityPhase.Lost,
            "epoch-a",
            0,
            null))
        {
            ForcedSaveStatus = saveStatus,
        };
        DurableProcessSourceContinuity continuity = new(
            store,
            new SequenceEpochFactory("epoch-b", "epoch-c"));
        ProcessObservationAcknowledgement acknowledgement = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);

        ProcessSourceAcknowledgementResult result =
            await continuity.TryAcknowledgeAsync(acknowledgement);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Checkpoint);
        await Assert.ThrowsAsync<ProcessSourceContinuityPersistenceException>(
            async () => await continuity.AcknowledgeAsync(acknowledgement));
        Assert.Equal(ProcessSourceContinuityPhase.Lost, store.Current!.Phase);
        Assert.Equal(0, store.Current.HighestAcceptedTransitionRevision);
    }

    [Fact]
    public async Task CandidateAndLaterLossAreEachPersistedBeforeTheyAreReported()
    {
        MemoryContinuityStore store = new();
        DurableProcessSourceContinuity continuity = new(
            store,
            new SequenceEpochFactory("fresh", "recovering"));
        await continuity.GetCurrentAsync();
        await continuity.TryAcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "old",
            10));

        ProcessSourceContinuityAccessResult candidate =
            await continuity.PublishRecoveryCandidateAsync("recovering");
        ProcessSourceContinuityAccessResult lost =
            await continuity.MarkLossAsync();
        ProcessSourceAcknowledgementResult late =
            await continuity.TryAcknowledgeAsync(new(
                ProcessObservationAcknowledgementKind
                    .AuthoritativeRecoveryPersisted,
                "recovering",
                20));

        Assert.Equal(ProcessSourceContinuityPhase.RecoveryCandidate,
            candidate.Checkpoint!.Phase);
        Assert.Equal(ProcessSourceContinuityPhase.Lost, lost.Checkpoint!.Phase);
        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored, late.Disposition);
        Assert.Equal(ProcessSourceContinuityPhase.Lost, store.Current!.Phase);
        Assert.Equal(10, store.Current.HighestAcceptedTransitionRevision);
    }

    [Fact]
    public async Task InvalidFreshLostAcknowledgementDoesNotConsumeAnEpoch()
    {
        MemoryContinuityStore store = new();
        SequenceEpochFactory epochs = new("fresh");
        DurableProcessSourceContinuity continuity = new(store, epochs);
        await continuity.GetCurrentAsync();

        ProcessSourceAcknowledgementResult result =
            await continuity.TryAcknowledgeAsync(new(
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                " ",
                10));

        Assert.Equal(ProcessSourceContinuityAccessStatus.Available, result.Status);
        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored,
            result.Disposition);
        Assert.Equal(1, epochs.CallCount);
        Assert.Equal(ProcessSourceContinuityPhase.FreshLost, store.Current!.Phase);
    }

    [Fact]
    public async Task IgnoredAcknowledgementWrapperCompletesWithoutClaimingTransition()
    {
        MemoryContinuityStore store = new();
        DurableProcessSourceContinuity continuity = new(
            store,
            new SequenceEpochFactory("fresh", "recovering"));
        await continuity.GetCurrentAsync();
        await continuity.TryAcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "old",
            10));
        int savesBefore = store.ExpectedVersions.Count;

        await continuity.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            "recovering",
            20));

        Assert.Equal(savesBefore, store.ExpectedVersions.Count);
        Assert.Equal(ProcessSourceContinuityPhase.Recovering, store.Current!.Phase);
        Assert.Equal(10, store.Current.HighestAcceptedTransitionRevision);
    }

    [Fact]
    public async Task RestoredStableStatesBecomeLostWithoutForgettingHighWater()
    {
        ProcessSourceAcknowledgementTuple recovery = new(
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            "epoch-b",
            20);
        foreach (ProcessSourceContinuityPhase phase in new[]
        {
            ProcessSourceContinuityPhase.Trusted,
            ProcessSourceContinuityPhase.Dormant,
        })
        {
            MemoryContinuityStore store = new(new(
                5,
                phase,
                "epoch-b",
                20,
                recovery));
            DurableProcessSourceContinuity continuity = new(
                store,
                new SequenceEpochFactory());

            ProcessSourceContinuityAccessResult result =
                await continuity.GetCurrentAsync();

            Assert.Equal(ProcessSourceContinuityPhase.Lost,
                result.Checkpoint!.Phase);
            Assert.Equal("epoch-b", result.Checkpoint.ObserverEpoch);
            Assert.Equal(20, result.Checkpoint.HighestAcceptedTransitionRevision);
            Assert.Equal(recovery, result.Checkpoint.LastAcceptedAcknowledgement);
            Assert.Equal(6, result.Checkpoint.Version);
        }
    }

    [Fact]
    public async Task RestoredFreshLostRetainsForeignAdoptionCapability()
    {
        MemoryContinuityStore store = new(new(
            1,
            ProcessSourceContinuityPhase.FreshLost,
            "fresh",
            0,
            null));
        DurableProcessSourceContinuity continuity = new(
            store,
            new SequenceEpochFactory("recovering"));

        ProcessSourceContinuityAccessResult restored =
            await continuity.GetCurrentAsync();
        ProcessSourceAcknowledgementResult adopted =
            await continuity.TryAcknowledgeAsync(new(
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                "foreign-old",
                10));

        Assert.Equal(ProcessSourceContinuityPhase.FreshLost,
            restored.Checkpoint!.Phase);
        Assert.Equal(ProcessSourceContinuityReductionKind.Applied,
            adopted.Disposition);
        Assert.Equal(ProcessSourceContinuityPhase.Recovering,
            adopted.Checkpoint!.Phase);
        Assert.Equal("recovering", adopted.Checkpoint.ObserverEpoch);
    }

    [Fact]
    public async Task NonAdvancingConflictWinnerIsCorruptNotAStateRollback()
    {
        ProcessSourceContinuityCheckpoint lost = new(
            1,
            ProcessSourceContinuityPhase.Lost,
            "epoch-a",
            0,
            null);
        MemoryContinuityStore store = new(lost)
        {
            SaveHook = (_, _) => new(
                ProcessSourceContinuityStoreSaveStatus.Conflict,
                lost),
        };
        DurableProcessSourceContinuity continuity = new(
            store,
            new SequenceEpochFactory("epoch-b"));

        ProcessSourceAcknowledgementResult result =
            await continuity.TryAcknowledgeAsync(new(
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                "epoch-a",
                10));

        Assert.Equal(ProcessSourceContinuityAccessStatus.Corrupt, result.Status);
        Assert.Null(result.Checkpoint);
        Assert.Equal(lost, store.Current);
    }

    private static ProcessSourceAcknowledgementTuple Sever(
        string epoch,
        long revision) =>
        new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            epoch,
            revision);

    private static ProcessObservationAcknowledgement Ack(
        ProcessSourceAcknowledgementTuple value) =>
        new(value.Kind, value.ObserverEpoch, value.TransitionRevision);

    private sealed class SequenceEpochFactory(params string[] values) :
        IProcessObserverEpochFactory
    {
        private readonly Queue<string> _values = new(values);

        public int CallCount { get; private set; }

        public string CreateEpoch()
        {
            CallCount++;
            return _values.Dequeue();
        }
    }

    private sealed class MemoryContinuityStore : IProcessSourceContinuityStore
    {
        public MemoryContinuityStore(ProcessSourceContinuityCheckpoint? seed = null)
        {
            Current = seed;
        }

        public ProcessSourceContinuityCheckpoint? Current { get; set; }

        public ProcessSourceContinuityStoreLoadStatus? ForcedLoadStatus { get; init; }

        public ProcessSourceContinuityStoreSaveStatus? ForcedSaveStatus { get; init; }

        public Func<
            long?,
            ProcessSourceContinuityCheckpoint,
            ProcessSourceContinuityStoreSaveResult>? SaveHook { get; set; }

        public Func<
            ProcessSourceContinuityCheckpoint,
            ProcessSourceContinuityCheckpoint>? SavedProjection { get; init; }

        public List<long?> ExpectedVersions { get; } = [];

        public ValueTask<ProcessSourceContinuityStoreLoadResult> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessSourceContinuityStoreLoadStatus status = ForcedLoadStatus
                ?? (Current is null
                    ? ProcessSourceContinuityStoreLoadStatus.Missing
                    : ProcessSourceContinuityStoreLoadStatus.Found);
            return ValueTask.FromResult(new ProcessSourceContinuityStoreLoadResult(
                status,
                status == ProcessSourceContinuityStoreLoadStatus.Found
                    ? Current
                    : null));
        }

        public ValueTask<ProcessSourceContinuityStoreSaveResult> CompareExchangeAsync(
            long? expectedVersion,
            ProcessSourceContinuityCheckpoint replacement,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExpectedVersions.Add(expectedVersion);
            if (SaveHook is not null)
            {
                return ValueTask.FromResult(SaveHook(expectedVersion, replacement));
            }

            if (ForcedSaveStatus is { } forced)
            {
                return ValueTask.FromResult(new ProcessSourceContinuityStoreSaveResult(
                    forced,
                    forced == ProcessSourceContinuityStoreSaveStatus.Conflict
                        ? Current
                        : null));
            }

            if (Current?.Version != expectedVersion
                || (Current is null) != (expectedVersion is null))
            {
                return ValueTask.FromResult(new ProcessSourceContinuityStoreSaveResult(
                    ProcessSourceContinuityStoreSaveStatus.Conflict,
                    Current));
            }

            Current = replacement;
            ProcessSourceContinuityCheckpoint echo = SavedProjection?.Invoke(replacement)
                ?? replacement;
            return ValueTask.FromResult(new ProcessSourceContinuityStoreSaveResult(
                ProcessSourceContinuityStoreSaveStatus.Saved,
                echo));
        }
    }
}
