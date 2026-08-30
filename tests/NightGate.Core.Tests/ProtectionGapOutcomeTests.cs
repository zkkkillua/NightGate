using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class ProtectionGapOutcomeTests
{
    [Fact]
    public void ProtectionGap_RemainsEligibleButCannotQualify()
    {
        NightOutcome outcome = Outcome(
            new DateOnly(2026, 7, 6),
            protectionGapObserved: true);

        Assert.True(outcome.ProtectionGapObserved);
        Assert.True(outcome.IsEligible);
        Assert.False(outcome.Qualifies);
    }

    [Fact]
    public void ConstructorWithoutProtectionGap_DefaultsToNoGap()
    {
        NightOutcome outcome = Outcome(new DateOnly(2026, 7, 6));

        Assert.False(outcome.ProtectionGapObserved);
        Assert.True(outcome.Qualifies);
    }

    [Fact]
    public void Progression_ProtectionGapConsumesEligibleSlotWithoutQualifying()
    {
        NightOutcome[] outcomes =
        [
            Outcome(new DateOnly(2026, 7, 5)),
            Outcome(new DateOnly(2026, 7, 6)),
            Outcome(new DateOnly(2026, 7, 7), missedLock: true),
            Outcome(new DateOnly(2026, 7, 8), protectionGapObserved: true),
        ];

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.True(outcomes[^1].IsEligible);
        Assert.False(outcomes[^1].Qualifies);
        Assert.Null(result.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 8), result.LastProgressionNightDate);
    }

    private static NightOutcome Outcome(
        DateOnly nightDate,
        bool missedLock = false,
        bool protectionGapObserved = false)
    {
        DateTimeOffset scheduledLock = new(
            nightDate.AddDays(1).ToDateTime(new TimeOnly(0, 40)),
            TimeSpan.Zero);
        return new NightOutcome(
            Guid.NewGuid(),
            nightDate,
            new DateTimeOffset(
                nightDate.AddDays(1).ToDateTime(new TimeOnly(9, 0)),
                TimeSpan.Zero),
            EmergencyUsed: false,
            TeamRescueUsed: false,
            EntertainmentUsed: false,
            DeliberateBypass: false,
            LateNewEntertainment: false,
            MissedLock: missedLock,
            FirstLockObservedAtUtc: scheduledLock,
            ScheduledLockAtUtc: scheduledLock,
            ProtectionGapObserved: protectionGapObserved);
    }
}
