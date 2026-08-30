using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class ProcessSourceContinuityReducerTests
{
    [Fact]
    public void MatchingTrustSeverRotatesLostEpochExactlyOnce()
    {
        ProcessSourceContinuityCheckpoint current = new(
            7,
            ProcessSourceContinuityPhase.Lost,
            "epoch-a",
            0,
            null);
        ProcessObservationAcknowledgement acknowledgement = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);

        ProcessSourceContinuityReduction result =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                current,
                acknowledgement,
                "epoch-b");

        Assert.Equal(ProcessSourceContinuityReductionKind.Applied, result.Kind);
        Assert.Equal(8, result.Checkpoint.Version);
        Assert.Equal(ProcessSourceContinuityPhase.Recovering, result.Checkpoint.Phase);
        Assert.Equal("epoch-b", result.Checkpoint.ObserverEpoch);
        Assert.Equal(10, result.Checkpoint.HighestAcceptedTransitionRevision);
        Assert.Equal(
            new ProcessSourceAcknowledgementTuple(
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                "epoch-a",
                10),
            result.Checkpoint.LastAcceptedAcknowledgement);
    }

    [Fact]
    public void ExactTupleRetrySucceedsWithoutMutationAfterEpochRotation()
    {
        ProcessSourceAcknowledgementTuple accepted = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);
        ProcessSourceContinuityCheckpoint current = new(
            8,
            ProcessSourceContinuityPhase.Recovering,
            "epoch-b",
            10,
            accepted);

        ProcessSourceContinuityReduction result =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                current,
                new(accepted.Kind, accepted.ObserverEpoch, accepted.TransitionRevision),
                "epoch-c");

        Assert.Equal(ProcessSourceContinuityReductionKind.Idempotent, result.Kind);
        Assert.Same(current, result.Checkpoint);
    }

    [Fact]
    public void FreshLostAdoptsOneForeignTrustSeverWithoutClaimingTrust()
    {
        ProcessSourceContinuityCheckpoint current = new(
            1,
            ProcessSourceContinuityPhase.FreshLost,
            "fresh-epoch",
            0,
            null);
        ProcessObservationAcknowledgement foreign = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "old-desktop-epoch",
            17);

        ProcessSourceContinuityReduction adopted =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                current,
                foreign,
                "recovery-epoch");
        ProcessSourceContinuityReduction secondForeign =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                adopted.Checkpoint,
                new(
                    ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                    "another-old-epoch",
                    18),
                "must-not-be-used");

        Assert.Equal(ProcessSourceContinuityReductionKind.Applied, adopted.Kind);
        Assert.Equal(ProcessSourceContinuityPhase.Recovering, adopted.Checkpoint.Phase);
        Assert.Equal("recovery-epoch", adopted.Checkpoint.ObserverEpoch);
        Assert.Equal(foreign.EnvelopeRevision,
            adopted.Checkpoint.HighestAcceptedTransitionRevision);
        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored, secondForeign.Kind);
        Assert.Same(adopted.Checkpoint, secondForeign.Checkpoint);
    }

    [Fact]
    public void RecoveryAcknowledgementIsAcceptedOnlyForPublishedCandidate()
    {
        ProcessSourceAcknowledgementTuple sever = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);
        ProcessObservationAcknowledgement recovery = new(
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            "epoch-b",
            20);
        ProcessSourceContinuityCheckpoint recovering = new(
            8,
            ProcessSourceContinuityPhase.Recovering,
            "epoch-b",
            10,
            sever);
        ProcessSourceContinuityCheckpoint candidate = recovering with
        {
            Version = 9,
            Phase = ProcessSourceContinuityPhase.RecoveryCandidate,
        };

        ProcessSourceContinuityReduction tooEarly =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                recovering,
                recovery,
                null);
        ProcessSourceContinuityReduction accepted =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                candidate,
                recovery,
                null);

        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored, tooEarly.Kind);
        Assert.Same(recovering, tooEarly.Checkpoint);
        Assert.Equal(ProcessSourceContinuityReductionKind.Applied, accepted.Kind);
        Assert.Equal(ProcessSourceContinuityPhase.Trusted, accepted.Checkpoint.Phase);
        Assert.Equal("epoch-b", accepted.Checkpoint.ObserverEpoch);
        Assert.Equal(20, accepted.Checkpoint.HighestAcceptedTransitionRevision);
        Assert.Equal(
            new ProcessSourceAcknowledgementTuple(
                recovery.Kind,
                recovery.ObserverEpoch,
                recovery.EnvelopeRevision),
            accepted.Checkpoint.LastAcceptedAcknowledgement);
    }

    [Theory]
    [InlineData(9, "epoch-b")]
    [InlineData(10, "different-epoch")]
    [InlineData(30, "different-epoch")]
    public void LowerSameRevisionVariantAndHigherConflictAreIgnored(
        long revision,
        string epoch)
    {
        ProcessSourceAcknowledgementTuple sever = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);
        ProcessSourceContinuityCheckpoint current = new(
            8,
            ProcessSourceContinuityPhase.Recovering,
            "epoch-b",
            10,
            sever);

        ProcessSourceContinuityReduction result =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                current,
                new(
                    ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                    epoch,
                    revision),
                "unused-epoch");

        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored, result.Kind);
        Assert.Same(current, result.Checkpoint);
        Assert.Equal(10, result.Checkpoint.HighestAcceptedTransitionRevision);
        Assert.Equal(sever, result.Checkpoint.LastAcceptedAcknowledgement);
    }

    [Fact]
    public void IgnoredHigherRevisionDoesNotBlockLaterLegalLowerRevision()
    {
        ProcessSourceAcknowledgementTuple sever = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);
        ProcessSourceContinuityCheckpoint candidate = new(
            9,
            ProcessSourceContinuityPhase.RecoveryCandidate,
            "epoch-b",
            10,
            sever);
        ProcessSourceContinuityReduction conflict =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                candidate,
                new(
                    ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                    "epoch-b",
                    30),
                "unused-epoch");

        ProcessSourceContinuityReduction legal =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                conflict.Checkpoint,
                new(
                    ProcessObservationAcknowledgementKind
                        .AuthoritativeRecoveryPersisted,
                    "epoch-b",
                    20),
                null);

        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored, conflict.Kind);
        Assert.Equal(10, conflict.Checkpoint.HighestAcceptedTransitionRevision);
        Assert.Equal(ProcessSourceContinuityReductionKind.Applied, legal.Kind);
        Assert.Equal(20, legal.Checkpoint.HighestAcceptedTransitionRevision);
        Assert.Equal(ProcessSourceContinuityPhase.Trusted, legal.Checkpoint.Phase);
    }

    [Fact]
    public void InvalidAcknowledgementNeverMutatesState()
    {
        ProcessSourceContinuityCheckpoint current = new(
            7,
            ProcessSourceContinuityPhase.Lost,
            "epoch-a",
            0,
            null);

        ProcessObservationAcknowledgement[] invalid =
        [
            new((ProcessObservationAcknowledgementKind)999, "epoch-a", 1),
            new(ProcessObservationAcknowledgementKind.TrustSeverPersisted, " ", 1),
            new(ProcessObservationAcknowledgementKind.TrustSeverPersisted, " epoch-a", 1),
            new(ProcessObservationAcknowledgementKind.TrustSeverPersisted, "epoch-a ", 1),
            new(ProcessObservationAcknowledgementKind.TrustSeverPersisted, "epoch-\u0001", 1),
            new(ProcessObservationAcknowledgementKind.TrustSeverPersisted, "epoch-a", 0),
        ];

        foreach (ProcessObservationAcknowledgement acknowledgement in invalid)
        {
            ProcessSourceContinuityReduction result =
                ProcessSourceContinuityReducer.ReduceAcknowledgement(
                    current,
                    acknowledgement,
                    "epoch-b");

            Assert.Equal(ProcessSourceContinuityReductionKind.Ignored, result.Kind);
            Assert.Same(current, result.Checkpoint);
        }
    }

    [Fact]
    public void ReachableCheckpointsPassSemanticValidation()
    {
        ProcessSourceAcknowledgementTuple sever = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);
        ProcessSourceAcknowledgementTuple recovery = new(
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            "epoch-b",
            20);
        ProcessSourceContinuityCheckpoint[] valid =
        [
            new(1, ProcessSourceContinuityPhase.FreshLost, "fresh", 0, null),
            new(2, ProcessSourceContinuityPhase.Lost, "epoch-a", 0, null),
            new(3, ProcessSourceContinuityPhase.Recovering, "epoch-b", 10, sever),
            new(4, ProcessSourceContinuityPhase.RecoveryCandidate, "epoch-b", 10, sever),
            new(5, ProcessSourceContinuityPhase.Trusted, "epoch-b", 20, recovery),
            new(6, ProcessSourceContinuityPhase.Lost, "epoch-b", 20, recovery),
            new(7, ProcessSourceContinuityPhase.Dormant, "epoch-b", 20, recovery),
        ];

        Assert.All(valid, value =>
            Assert.True(ProcessSourceContinuityReducer.IsValidCheckpoint(value)));
    }

    [Fact]
    public void ImpossibleCheckpointShapesAreRejected()
    {
        ProcessSourceAcknowledgementTuple sever = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);
        ProcessSourceAcknowledgementTuple recovery = new(
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            "epoch-b",
            20);
        ProcessSourceContinuityCheckpoint[] invalid =
        [
            new(0, ProcessSourceContinuityPhase.Lost, "epoch-a", 0, null),
            new(long.MaxValue, ProcessSourceContinuityPhase.Lost, "epoch-a", 0, null),
            new(1, (ProcessSourceContinuityPhase)999, "epoch-a", 0, null),
            new(1, ProcessSourceContinuityPhase.Lost, " ", 0, null),
            new(1, ProcessSourceContinuityPhase.Lost, "epoch-a", -1, null),
            new(1, ProcessSourceContinuityPhase.Lost, "epoch-a", 10, null),
            new(1, ProcessSourceContinuityPhase.Lost, "epoch-a", 0, sever),
            new(1, ProcessSourceContinuityPhase.Lost, "epoch-a", 11, sever),
            new(1, ProcessSourceContinuityPhase.FreshLost, "fresh", 10, sever),
            new(1, ProcessSourceContinuityPhase.Recovering, "epoch-a", 10, sever),
            new(1, ProcessSourceContinuityPhase.Recovering, "epoch-b", 20, recovery),
            new(1, ProcessSourceContinuityPhase.RecoveryCandidate, "epoch-a", 10, sever),
            new(1, ProcessSourceContinuityPhase.Trusted, "epoch-b", 10, sever),
            new(1, ProcessSourceContinuityPhase.Trusted, "epoch-c", 20, recovery),
            new(
                1,
                ProcessSourceContinuityPhase.Lost,
                "epoch-a",
                1,
                new((ProcessObservationAcknowledgementKind)999, "epoch-a", 1)),
            new(
                1,
                ProcessSourceContinuityPhase.Lost,
                "epoch-a",
                1,
                new(
                    ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                    " ",
                    1)),
        ];

        Assert.All(invalid, value =>
            Assert.False(ProcessSourceContinuityReducer.IsValidCheckpoint(value)));
    }

    [Fact]
    public void LossIsStickyAndRetainsAcknowledgementHighWater()
    {
        ProcessSourceAcknowledgementTuple recovery = new(
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            "epoch-b",
            20);
        ProcessSourceContinuityCheckpoint trusted = new(
            5,
            ProcessSourceContinuityPhase.Trusted,
            "epoch-b",
            20,
            recovery);

        ProcessSourceContinuityReduction first =
            ProcessSourceContinuityReducer.ReduceLoss(trusted);
        ProcessSourceContinuityReduction repeated =
            ProcessSourceContinuityReducer.ReduceLoss(first.Checkpoint);

        Assert.Equal(ProcessSourceContinuityReductionKind.Applied, first.Kind);
        Assert.Equal(ProcessSourceContinuityPhase.Lost, first.Checkpoint.Phase);
        Assert.Equal("epoch-b", first.Checkpoint.ObserverEpoch);
        Assert.Equal(20, first.Checkpoint.HighestAcceptedTransitionRevision);
        Assert.Equal(recovery, first.Checkpoint.LastAcceptedAcknowledgement);
        Assert.Equal(ProcessSourceContinuityReductionKind.Idempotent, repeated.Kind);
        Assert.Same(first.Checkpoint, repeated.Checkpoint);
    }

    [Fact]
    public void OnlyMatchingRecoveringEpochCanPublishCandidate()
    {
        ProcessSourceAcknowledgementTuple sever = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);
        ProcessSourceContinuityCheckpoint recovering = new(
            8,
            ProcessSourceContinuityPhase.Recovering,
            "epoch-b",
            10,
            sever);

        ProcessSourceContinuityReduction wrong =
            ProcessSourceContinuityReducer.ReduceRecoveryCandidate(
                recovering,
                "epoch-c");
        ProcessSourceContinuityReduction published =
            ProcessSourceContinuityReducer.ReduceRecoveryCandidate(
                recovering,
                "epoch-b");
        ProcessSourceContinuityReduction repeated =
            ProcessSourceContinuityReducer.ReduceRecoveryCandidate(
                published.Checkpoint,
                "epoch-b");

        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored, wrong.Kind);
        Assert.Equal(ProcessSourceContinuityReductionKind.Applied, published.Kind);
        Assert.Equal(ProcessSourceContinuityPhase.RecoveryCandidate,
            published.Checkpoint.Phase);
        Assert.Equal(ProcessSourceContinuityReductionKind.Idempotent, repeated.Kind);
    }

    [Fact]
    public void LaterLossMakesPublishedRecoveryAcknowledgementInapplicable()
    {
        ProcessSourceAcknowledgementTuple sever = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);
        ProcessSourceContinuityCheckpoint candidate = new(
            9,
            ProcessSourceContinuityPhase.RecoveryCandidate,
            "epoch-b",
            10,
            sever);
        ProcessSourceContinuityReduction lost =
            ProcessSourceContinuityReducer.ReduceLoss(candidate);

        ProcessSourceContinuityReduction late =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                lost.Checkpoint,
                new(
                    ProcessObservationAcknowledgementKind
                        .AuthoritativeRecoveryPersisted,
                    "epoch-b",
                    20),
                null);

        Assert.Equal(ProcessSourceContinuityPhase.Lost, lost.Checkpoint.Phase);
        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored, late.Kind);
        Assert.Equal(10, late.Checkpoint.HighestAcceptedTransitionRevision);
    }

    [Fact]
    public void DormancyDoesNotEraseOutstandingFreshOrOrdinaryLoss()
    {
        ProcessSourceContinuityCheckpoint fresh = new(
            1,
            ProcessSourceContinuityPhase.FreshLost,
            "fresh",
            0,
            null);
        ProcessSourceContinuityCheckpoint lost = fresh with
        {
            Version = 2,
            Phase = ProcessSourceContinuityPhase.Lost,
        };
        ProcessSourceContinuityCheckpoint trusted = fresh with
        {
            Version = 3,
            Phase = ProcessSourceContinuityPhase.Dormant,
        };

        ProcessSourceContinuityReduction freshResult =
            ProcessSourceContinuityReducer.ReduceDormant(fresh);
        ProcessSourceContinuityReduction lostResult =
            ProcessSourceContinuityReducer.ReduceDormant(lost);
        ProcessSourceContinuityReduction dormantResult =
            ProcessSourceContinuityReducer.ReduceDormant(trusted);

        Assert.Equal(ProcessSourceContinuityReductionKind.Idempotent, freshResult.Kind);
        Assert.Same(fresh, freshResult.Checkpoint);
        Assert.Equal(ProcessSourceContinuityReductionKind.Idempotent, lostResult.Kind);
        Assert.Same(lost, lostResult.Checkpoint);
        Assert.Equal(ProcessSourceContinuityReductionKind.Idempotent, dormantResult.Kind);
        Assert.Same(trusted, dormantResult.Checkpoint);
    }

    [Fact]
    public void SeverCannotPersistMalformedRotatedEpoch()
    {
        ProcessSourceContinuityCheckpoint current = new(
            1,
            ProcessSourceContinuityPhase.Lost,
            "epoch-a",
            0,
            null);
        ProcessObservationAcknowledgement acknowledgement = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);

        ProcessSourceContinuityReduction result =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                current,
                acknowledgement,
                new string('x', 257));

        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored, result.Kind);
        Assert.Same(current, result.Checkpoint);
    }

    [Fact]
    public void SeverCannotReuseLastAcceptedEpochDuringAnotherRotation()
    {
        ProcessSourceAcknowledgementTuple priorSever = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-a",
            10);
        ProcessSourceContinuityCheckpoint current = new(
            9,
            ProcessSourceContinuityPhase.Lost,
            "epoch-b",
            10,
            priorSever);
        ProcessObservationAcknowledgement acknowledgement = new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-b",
            20);

        ProcessSourceContinuityReduction result =
            ProcessSourceContinuityReducer.ReduceAcknowledgement(
                current,
                acknowledgement,
                "epoch-a");

        Assert.Equal(ProcessSourceContinuityReductionKind.Ignored, result.Kind);
        Assert.Same(current, result.Checkpoint);
        Assert.Equal(10, result.Checkpoint.HighestAcceptedTransitionRevision);
    }
}
