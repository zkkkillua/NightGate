using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class PendingProgressionTests
{
    [Fact]
    public void Advance_ThreeOfFourQualifyingNightsUnlocksPendingStepOnly()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7)),
            Outcome(new(2026, 7, 8), missedLock: true),
        ];

        ProgressState result = ProgressionEngine.Advance(
            ProgressState.Initial with { CurrentStep = 2 },
            outcomes);

        Assert.Equal(2, result.CurrentStep);
        Assert.Equal(3, result.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 8), result.PendingStepUnlockedByNightDate);
        Assert.Equal(new DateOnly(2026, 7, 8), result.LastProgressionNightDate);
        Assert.Null(result.PendingStepConfirmedAtUtc);
        Assert.Null(result.PendingStepEffectiveNightDate);
    }

    [Fact]
    public void Advance_TwoOfFourHoldsStepAndConsumesWindowOnce()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7), missedLock: true),
            Outcome(new(2026, 7, 8), missedLock: true),
        ];

        ProgressState first = ProgressionEngine.Advance(ProgressState.Initial, outcomes);
        ProgressState repeated = ProgressionEngine.Advance(first, outcomes);

        Assert.Equal(1, first.CurrentStep);
        Assert.Null(first.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 8), first.LastProgressionNightDate);
        Assert.Equal(first, repeated);
    }

    [Fact]
    public void Advance_RequiresFourEligibleWorkNights()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7)),
            Outcome(new(2026, 7, 10)),
            Outcome(new(2026, 7, 11)),
        ];

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.Equal(ProgressState.Initial, result);
    }

    [Fact]
    public void Advance_ExcludesEmergencyNightsBeforeTakingLatestFour()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7)),
            Outcome(new(2026, 7, 8), missedLock: true),
            Outcome(new(2026, 7, 9), emergencyUsed: true),
        ];

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.Equal(1, result.CurrentStep);
        Assert.Equal(2, result.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 8), result.PendingStepUnlockedByNightDate);
    }

    [Fact]
    public void Advance_UsesOnlyLatestFourEligibleNights()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7)),
            Outcome(new(2026, 7, 8), missedLock: true),
            Outcome(new(2026, 7, 9), missedLock: true),
        ];

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.Equal(1, result.CurrentStep);
        Assert.Null(result.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 9), result.LastProgressionNightDate);
    }

    [Fact]
    public void Advance_LateActualLocksRemainEligibleButDoNotQualify()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7), lockOffsetMinutes: 1),
            Outcome(new(2026, 7, 8), lockOffsetMinutes: 1),
        ];

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.All(outcomes, outcome => Assert.True(outcome.IsEligible));
        Assert.Null(result.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 8), result.LastProgressionNightDate);
    }

    [Fact]
    public void Advance_SubminuteSessionEventObservationLatencyStillQualifies()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5), lockOffsetSeconds: 3),
            Outcome(new(2026, 7, 6), lockOffsetSeconds: 3),
            Outcome(new(2026, 7, 7), lockOffsetSeconds: 3),
            Outcome(new(2026, 7, 8), lockOffsetSeconds: 3),
        ];

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.All(outcomes, outcome => Assert.True(outcome.Qualifies));
        Assert.Equal(2, result.PendingStep);
    }

    [Fact]
    public void Advance_FiveSecondLateLockIsNotHiddenByObservationTolerance()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5), lockOffsetSeconds: 5),
            Outcome(new(2026, 7, 6), lockOffsetSeconds: 5),
            Outcome(new(2026, 7, 7), lockOffsetSeconds: 5),
            Outcome(new(2026, 7, 8), lockOffsetSeconds: 5),
        ];

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.All(outcomes, outcome => Assert.False(outcome.Qualifies));
        Assert.Null(result.PendingStep);
    }

    [Fact]
    public void Advance_ExistingPendingStepDoesNotStackAndConsumesNewWindow()
    {
        ProgressState pending = ProgressState.Initial with
        {
            PendingStep = 2,
            PendingStepUnlockedByNightDate = new(2026, 7, 8),
            LastProgressionNightDate = new(2026, 7, 8),
        };
        NightOutcome[] newerOutcomes =
        [
            Outcome(new(2026, 7, 12)),
            Outcome(new(2026, 7, 13)),
            Outcome(new(2026, 7, 14)),
            Outcome(new(2026, 7, 15)),
        ];

        ProgressState result = ProgressionEngine.Advance(pending, newerOutcomes);

        Assert.Equal(1, result.CurrentStep);
        Assert.Equal(2, result.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 8), result.PendingStepUnlockedByNightDate);
        Assert.Equal(new DateOnly(2026, 7, 15), result.LastProgressionNightDate);
    }

    [Fact]
    public void Advance_StepFourNeverCreatesPendingStepOrRegresses()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 12)),
            Outcome(new(2026, 7, 13)),
            Outcome(new(2026, 7, 14)),
            Outcome(new(2026, 7, 15)),
        ];

        ProgressState result = ProgressionEngine.Advance(
            ProgressState.Initial with { CurrentStep = 4 },
            outcomes);

        Assert.Equal(4, result.CurrentStep);
        Assert.Null(result.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 15), result.LastProgressionNightDate);
    }

    [Fact]
    public void Advance_RejectsPartialOrImpossiblePendingState()
    {
        DateOnly unlockNight = new(2026, 7, 8);
        DateTimeOffset confirmedAt = new(2026, 7, 9, 14, 0, 0, TimeSpan.Zero);
        ProgressState[] corruptStates =
        [
            ProgressState.Initial with { PendingStep = 2 },
            ProgressState.Initial with { PendingStepUnlockedByNightDate = unlockNight },
            ProgressState.Initial with
            {
                PendingStep = 3,
                PendingStepUnlockedByNightDate = unlockNight,
                LastProgressionNightDate = unlockNight,
            },
            ProgressState.Initial with
            {
                PendingStep = 2,
                PendingStepUnlockedByNightDate = unlockNight,
                LastProgressionNightDate = unlockNight.AddDays(-1),
            },
            ProgressState.Initial with
            {
                PendingStep = 2,
                PendingStepUnlockedByNightDate = unlockNight,
                LastProgressionNightDate = unlockNight,
                PendingStepConfirmedAtUtc = confirmedAt,
            },
            ProgressState.Initial with
            {
                PendingStep = 2,
                PendingStepUnlockedByNightDate = unlockNight,
                LastProgressionNightDate = unlockNight,
                PendingStepEffectiveNightDate = unlockNight.AddDays(1),
            },
            ProgressState.Initial with
            {
                CurrentStep = 4,
                PendingStep = 4,
                PendingStepUnlockedByNightDate = unlockNight,
                LastProgressionNightDate = unlockNight,
            },
        ];

        foreach (ProgressState corrupt in corruptStates)
        {
            Assert.Throws<InvalidDataException>(() => ProgressionEngine.Advance(corrupt, []));
        }
    }

    private static NightOutcome Outcome(
        DateOnly nightDate,
        bool emergencyUsed = false,
        bool missedLock = false,
        int lockOffsetMinutes = 0,
        int lockOffsetSeconds = 0)
    {
        DateTimeOffset scheduledLock = new(
            nightDate.AddDays(1).ToDateTime(new TimeOnly(0, 40)),
            TimeSpan.Zero);
        return new(
            Guid.NewGuid(),
            nightDate,
            new DateTimeOffset(
                nightDate.AddDays(1).ToDateTime(new TimeOnly(9, 0)),
                TimeSpan.Zero),
            emergencyUsed,
            false,
            false,
            false,
            false,
            missedLock,
            FirstLockObservedAtUtc: scheduledLock
                .AddMinutes(lockOffsetMinutes)
                .AddSeconds(lockOffsetSeconds),
            ScheduledLockAtUtc: scheduledLock);
    }
}
