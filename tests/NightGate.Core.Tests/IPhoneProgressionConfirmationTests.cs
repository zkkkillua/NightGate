using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class IPhoneProgressionConfirmationTests
{
    private static readonly TimeZoneInfo ChinaTime = TimeZoneInfo.CreateCustomTimeZone(
        "NightGate-Test-China",
        TimeSpan.FromHours(8),
        "NightGate Test China",
        "NightGate Test China");

    [Fact]
    public void ConfirmPendingStep_RequiresEveryChecklistItem()
    {
        IPhoneStepConfirmation complete = CompleteChecklist();
        IPhoneStepConfirmation[] incomplete =
        [
            complete with { HealthSleepScheduleConfigured = false },
            complete with { SleepFocusConfigured = false },
            complete with { DowntimeConfigured = false },
            complete with { BlockAtDowntimeEnabled = false },
            complete with { EntertainmentCategoriesRestricted = false },
            complete with { RequiredAppsAllowed = false },
            complete with { SafariNotAllowlisted = false },
            complete with { DistinctRecoverableScreenTimePasscodeAcknowledged = false },
            complete with { OldAlarmsChecked = false },
            complete with { PhonePlacementPlanned = false },
        ];

        foreach (IPhoneStepConfirmation checklist in incomplete)
        {
            Assert.Throws<InvalidOperationException>(() => ProgressionEngine.ConfirmPendingStep(
                UnconfirmedPending(),
                2,
                checklist,
                AtChinaLocal(2026, 7, 8, 21, 0, 0),
                ChinaTime));
        }
    }

    [Fact]
    public void LegacyNineItemConstructionRemainsCompatibleButRequiresNewConfirmation()
    {
        IPhoneStepConfirmation legacy = new(
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true,
            true);

        Assert.False(legacy.EntertainmentCategoriesRestricted);
        Assert.False(legacy.IsComplete);
    }

    [Fact]
    public void ConfirmPendingStep_RejectsWrongStepOrMissingPendingStep()
    {
        Assert.Throws<InvalidOperationException>(() => ProgressionEngine.ConfirmPendingStep(
            UnconfirmedPending(),
            3,
            CompleteChecklist(),
            AtChinaLocal(2026, 7, 8, 21, 0, 0),
            ChinaTime));
        Assert.Throws<InvalidOperationException>(() => ProgressionEngine.ConfirmPendingStep(
            ProgressState.Initial,
            2,
            CompleteChecklist(),
            AtChinaLocal(2026, 7, 8, 21, 0, 0),
            ChinaTime));
    }

    [Fact]
    public void ConfirmPendingStep_AtTwentyTwoTwentyNineFiftyNineUsesCurrentLocalNight()
    {
        DateTimeOffset observedAtUtc = AtChinaLocal(2026, 7, 8, 22, 29, 59);

        ProgressState result = ProgressionEngine.ConfirmPendingStep(
            UnconfirmedPending(),
            2,
            CompleteChecklist(),
            observedAtUtc,
            ChinaTime);

        Assert.Equal(observedAtUtc, result.PendingStepConfirmedAtUtc);
        Assert.Equal(TimeSpan.Zero, result.PendingStepConfirmedAtUtc!.Value.Offset);
        Assert.Equal(new DateOnly(2026, 7, 8), result.PendingStepEffectiveNightDate);
        Assert.Equal(1, result.CurrentStep);
    }

    [Fact]
    public void ConfirmPendingStep_AtExactlyTwentyTwoThirtyUsesNextLocalNight()
    {
        ProgressState result = ProgressionEngine.ConfirmPendingStep(
            UnconfirmedPending(),
            2,
            CompleteChecklist(),
            AtChinaLocal(2026, 7, 8, 22, 30, 0),
            ChinaTime);

        Assert.Equal(new DateOnly(2026, 7, 9), result.PendingStepEffectiveNightDate);
        Assert.Equal(1, result.CurrentStep);
    }

    [Theory]
    [InlineData(10, 0, 0, 10)]
    [InlineData(10, 22, 30, 11)]
    [InlineData(11, 22, 30, 12)]
    public void ConfirmPendingStep_UsesCalendarNightAcrossMidnightFridayAndSaturday(
        int localDay,
        int localHour,
        int localMinute,
        int expectedEffectiveDay)
    {
        ProgressState result = ProgressionEngine.ConfirmPendingStep(
            UnconfirmedPending(),
            2,
            CompleteChecklist(),
            AtChinaLocal(2026, 7, localDay, localHour, localMinute, 0),
            ChinaTime);

        Assert.Equal(new DateOnly(2026, 7, expectedEffectiveDay), result.PendingStepEffectiveNightDate);
    }

    [Fact]
    public void ConfirmPendingStep_ConvertsAbsoluteInstantsDeterministicallyAcrossDstAmbiguity()
    {
        TimeZoneInfo timeZone = CreateDstTimeZone();
        DateTimeOffset firstAmbiguousOccurrence = new(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        DateTimeOffset secondAmbiguousOccurrence = new(2026, 11, 1, 6, 30, 0, TimeSpan.Zero);

        ProgressState first = ProgressionEngine.ConfirmPendingStep(
            UnconfirmedPending(new(2026, 10, 29)),
            2,
            CompleteChecklist(),
            firstAmbiguousOccurrence,
            timeZone);
        ProgressState second = ProgressionEngine.ConfirmPendingStep(
            UnconfirmedPending(new(2026, 10, 29)),
            2,
            CompleteChecklist(),
            secondAmbiguousOccurrence,
            timeZone);

        Assert.Equal(new DateOnly(2026, 11, 1), first.PendingStepEffectiveNightDate);
        Assert.Equal(first.PendingStepEffectiveNightDate, second.PendingStepEffectiveNightDate);
        Assert.NotEqual(first.PendingStepConfirmedAtUtc, second.PendingStepConfirmedAtUtc);
    }

    [Fact]
    public void ConfirmPendingStep_RepeatedAfterClockRollbackKeepsFirstConfirmationAndEffectiveNight()
    {
        ProgressState first = ProgressionEngine.ConfirmPendingStep(
            UnconfirmedPending(),
            2,
            CompleteChecklist(),
            AtChinaLocal(2026, 7, 8, 22, 30, 0),
            ChinaTime);

        ProgressState repeated = ProgressionEngine.ConfirmPendingStep(
            first,
            2,
            CompleteChecklist(),
            AtChinaLocal(2026, 7, 7, 20, 0, 0),
            ChinaTime);

        Assert.Equal(first, repeated);
    }

    [Fact]
    public void ConfirmPendingStep_RejectsNonUtcServiceObservation()
    {
        DateTimeOffset nonUtc = new(2026, 7, 8, 22, 0, 0, TimeSpan.FromHours(8));

        Assert.Throws<ArgumentException>(() => ProgressionEngine.ConfirmPendingStep(
            UnconfirmedPending(),
            2,
            CompleteChecklist(),
            nonUtc,
            ChinaTime));
    }

    [Fact]
    public void ActivatePendingStep_BeforeTargetDoesNothing()
    {
        ProgressState confirmed = ConfirmedPending(new(2026, 7, 9));

        ProgressState result = ProgressionEngine.ActivatePendingStep(
            confirmed,
            new DateOnly(2026, 7, 8));

        Assert.Equal(confirmed, result);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void ActivatePendingStep_OnOrAfterTargetActivatesAndClearsEveryPendingField(int day)
    {
        ProgressState result = ProgressionEngine.ActivatePendingStep(
            ConfirmedPending(new(2026, 7, 9)),
            new DateOnly(2026, 7, day));

        Assert.Equal(2, result.CurrentStep);
        Assert.Null(result.PendingStep);
        Assert.Null(result.PendingStepUnlockedByNightDate);
        Assert.Null(result.PendingStepConfirmedAtUtc);
        Assert.Null(result.PendingStepEffectiveNightDate);
    }

    [Fact]
    public void ActivatePendingStep_UnconfirmedPendingDoesNothing()
    {
        ProgressState pending = UnconfirmedPending();

        Assert.Equal(
            pending,
            ProgressionEngine.ActivatePendingStep(pending, new DateOnly(2026, 7, 20)));
    }

    private static ProgressState UnconfirmedPending(DateOnly? unlockNight = null)
    {
        DateOnly unlocked = unlockNight ?? new DateOnly(2026, 7, 6);
        return ProgressState.Initial with
        {
            PendingStep = 2,
            PendingStepUnlockedByNightDate = unlocked,
            LastProgressionNightDate = unlocked,
        };
    }

    private static ProgressState ConfirmedPending(DateOnly effectiveNight) =>
        UnconfirmedPending() with
        {
            PendingStepConfirmedAtUtc = new DateTimeOffset(2026, 7, 8, 14, 30, 0, TimeSpan.Zero),
            PendingStepEffectiveNightDate = effectiveNight,
        };

    private static IPhoneStepConfirmation CompleteChecklist() => new(
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        true);

    private static DateTimeOffset AtChinaLocal(
        int year,
        int month,
        int day,
        int hour,
        int minute,
        int second) => new DateTimeOffset(
            year,
            month,
            day,
            hour,
            minute,
            second,
            TimeSpan.FromHours(8)).ToUniversalTime();

    private static TimeZoneInfo CreateDstTimeZone()
    {
        TimeZoneInfo.TransitionTime daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            3,
            2,
            DayOfWeek.Sunday);
        TimeZoneInfo.TransitionTime daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            11,
            1,
            DayOfWeek.Sunday);
        TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Test-DST",
            TimeSpan.FromHours(-5),
            "NightGate Test DST",
            "NightGate Test Standard",
            "NightGate Test Daylight",
            [rule]);
    }
}
