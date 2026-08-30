using NightGate.Core;

namespace NightGate.Service.Tests;

public sealed class ProgressionEngineTests
{
    [Fact]
    public void Advance_ThreeOfLatestFourEligibleQualify_UnlocksExactlyOnePendingStep()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7)),
            Outcome(new(2026, 7, 8), missedLock: true),
        ];

        ProgressState result = ProgressionEngine.Advance(
            ProgressState.Initial with { CurrentStep = 2 }, outcomes);

        Assert.Equal(2, result.CurrentStep);
        Assert.Equal(3, result.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 8), result.PendingStepUnlockedByNightDate);
    }

    [Fact]
    public void Advance_TwoOfLatestFourEligibleQualify_DoesNotAdvance()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7), missedLock: true),
            Outcome(new(2026, 7, 8), deliberateBypass: true),
        ];

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.Equal(1, result.CurrentStep);
        Assert.Null(result.PendingStep);
    }

    [Fact]
    public void Advance_FewerThanFourEligibleOutcomes_DoesNotAdvance()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7)),
        ];

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.Equal(1, result.CurrentStep);
        Assert.Null(result.PendingStep);
    }

    [Fact]
    public void Advance_EmergencyNightsAreExcludedRatherThanMarkedNonqualifying()
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

    [Theory]
    [InlineData("teamRescue")]
    [InlineData("entertainment")]
    [InlineData("bypass")]
    [InlineData("lateEntertainment")]
    [InlineData("missedLock")]
    public void Advance_DisqualifyingOutcomeFlagsAreEligibleButNonqualifying(string flag)
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7), flag: flag),
            Outcome(new(2026, 7, 8), flag: flag),
        ];

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.Equal(1, result.CurrentStep);
        Assert.Null(result.PendingStep);
    }

    [Fact]
    public void Advance_FiltersFridayAndSaturdayBeforeChoosingLatestFour()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7)),
            Outcome(new(2026, 7, 8), missedLock: true),
            Outcome(new(2026, 7, 10), missedLock: true),
            Outcome(new(2026, 7, 11), missedLock: true),
        ];

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.Equal(1, result.CurrentStep);
        Assert.Equal(2, result.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 8), result.PendingStepUnlockedByNightDate);
    }

    [Fact]
    public void Advance_InspectsOnlyLatestFourEligibleOutcomes()
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
    }

    [Fact]
    public void Advance_NeverRegressesAndCapsAtStepFour()
    {
        NightOutcome[] twoQualifying =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7), missedLock: true),
            Outcome(new(2026, 7, 8), missedLock: true),
        ];
        NightOutcome[] allQualifying =
        [
            Outcome(new(2026, 7, 12)),
            Outcome(new(2026, 7, 13)),
            Outcome(new(2026, 7, 14)),
            Outcome(new(2026, 7, 15)),
        ];

        ProgressState stepThree = ProgressionEngine.Advance(
            ProgressState.Initial with { CurrentStep = 3 },
            twoQualifying);
        ProgressState stepFour = ProgressionEngine.Advance(
            ProgressState.Initial with { CurrentStep = 4 },
            allQualifying);

        Assert.Equal(3, stepThree.CurrentStep);
        Assert.Null(stepThree.PendingStep);
        Assert.Equal(4, stepFour.CurrentStep);
        Assert.Null(stepFour.PendingStep);
    }

    [Fact]
    public void Advance_DoesNotReuseSameOutcomeWindow()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new(2026, 7, 5)),
            Outcome(new(2026, 7, 6)),
            Outcome(new(2026, 7, 7)),
            Outcome(new(2026, 7, 8)),
        ];

        ProgressState first = ProgressionEngine.Advance(ProgressState.Initial, outcomes);
        ProgressState second = ProgressionEngine.Advance(first, outcomes);

        Assert.Equal(1, first.CurrentStep);
        Assert.Equal(2, first.PendingStep);
        Assert.Equal(first, second);
    }

    private static NightOutcome Outcome(
        DateOnly date,
        bool emergencyUsed = false,
        bool teamRescueUsed = false,
        bool entertainmentUsed = false,
        bool deliberateBypass = false,
        bool lateNewEntertainment = false,
        bool missedLock = false,
        string? flag = null)
    {
        teamRescueUsed |= flag == "teamRescue";
        entertainmentUsed |= flag == "entertainment";
        deliberateBypass |= flag == "bypass";
        lateNewEntertainment |= flag == "lateEntertainment";
        missedLock |= flag == "missedLock";

        DateTimeOffset scheduledLock = new(
            date.AddDays(1).ToDateTime(new TimeOnly(0, 40)),
            TimeSpan.Zero);

        return new(
            Guid.NewGuid(),
            date,
            new DateTimeOffset(
                date.AddDays(1).ToDateTime(new TimeOnly(9, 0)),
                TimeSpan.Zero),
            emergencyUsed,
            teamRescueUsed,
            entertainmentUsed,
            deliberateBypass,
            lateNewEntertainment,
            missedLock,
            FirstLockObservedAtUtc: scheduledLock,
            ScheduledLockAtUtc: scheduledLock);
    }
}
