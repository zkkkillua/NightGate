using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class WeeklyReportBuilderTests
{
    private static readonly DateOnly PeriodEnd = new(2026, 7, 12);

    [Fact]
    public void EmptyPeriod_ProducesCalmEmptyFactsWithoutAStreak()
    {
        WeeklyReportSummary report = WeeklyReportBuilder.Build(
            [],
            PeriodEnd,
            TimeZoneInfo.Utc);

        Assert.Equal(new DateOnly(2026, 7, 6), report.PeriodStart);
        Assert.Equal(PeriodEnd, report.PeriodEnd);
        Assert.Equal(0, report.ObservedWorkNights);
        Assert.Equal(0, report.EligibleWorkNights);
        Assert.Equal(0, report.QualifyingWorkNights);
        Assert.Equal(0, report.LockObservations);
        Assert.Null(report.MedianLockTime);
        Assert.Null(report.MedianLockChangeMinutes);
        Assert.Equal(OverrideReasonSummary.Empty, report.OverrideReasons);
        Assert.DoesNotContain(
            typeof(WeeklyReportSummary).GetProperties(),
            property => property.Name.Contains("Streak", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CurrentWeek_AggregatesWorkNightCountsAndAllOverrideReasons()
    {
        NightOutcome qualified = Outcome(new(2026, 7, 6), 0, 20);
        NightOutcome failed = Outcome(
            new(2026, 7, 7),
            0,
            40,
            lateEntertainment: true,
            reasons: new(TeamRescueCount: 1));
        NightOutcome emergencyExcluded = Outcome(
            new(2026, 7, 8),
            0,
            30,
            emergency: true,
            reasons: new(EmergencySafetyCount: 2));
        NightOutcome weekend = Outcome(
            new(2026, 7, 10),
            1,
            10,
            reasons: new(EntertainmentCount: 1));

        WeeklyReportSummary report = WeeklyReportBuilder.Build(
            [qualified, failed, emergencyExcluded, weekend],
            PeriodEnd,
            TimeZoneInfo.Utc);

        Assert.Equal(3, report.ObservedWorkNights);
        Assert.Equal(2, report.EligibleWorkNights);
        Assert.Equal(1, report.QualifyingWorkNights);
        Assert.Equal(3, report.LockObservations);
        Assert.Equal(new TimeOnly(0, 30), report.MedianLockTime);
        Assert.Equal(1, report.OverrideReasons.TeamRescueCount);
        Assert.Equal(1, report.OverrideReasons.EntertainmentCount);
        Assert.Equal(2, report.OverrideReasons.EmergencySafetyCount);
    }

    [Fact]
    public void LegacyMedianLockTime_NormalizesAcrossMidnightAndFallsBackToConfiguredTimeZone()
    {
        TimeZoneInfo utcPlusEight = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Weekly-UTC+8",
            TimeSpan.FromHours(8),
            "NightGate Weekly UTC+8",
            "NightGate Weekly UTC+8");
        NightOutcome beforeMidnight = OutcomeFromUtcLock(
            new(2026, 7, 6),
            new(2026, 7, 6, 15, 50, 0, TimeSpan.Zero));
        NightOutcome afterMidnight = OutcomeFromUtcLock(
            new(2026, 7, 7),
            new(2026, 7, 7, 16, 10, 0, TimeSpan.Zero));

        WeeklyReportSummary report = WeeklyReportBuilder.Build(
            [beforeMidnight, afterMidnight],
            PeriodEnd,
            utcPlusEight);

        Assert.Equal(new TimeOnly(0, 0), report.MedianLockTime);
    }

    [Fact]
    public void HistoricalMedian_RemainsStableAfterCurrentTimeZoneChanges()
    {
        TimeZoneInfo originalTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Weekly-History-UTC+8",
            TimeSpan.FromHours(8),
            "NightGate Weekly History UTC+8",
            "NightGate Weekly History UTC+8");
        string serializedOriginalTimeZone = NightScheduleTimeZone.Capture(originalTimeZone);
        NightOutcome first = OutcomeFromUtcLock(
            new(2026, 7, 6),
            new(2026, 7, 6, 16, 10, 0, TimeSpan.Zero)) with
        {
            ScheduleTimeZoneSerialized = serializedOriginalTimeZone,
        };
        NightOutcome second = OutcomeFromUtcLock(
            new(2026, 7, 7),
            new(2026, 7, 7, 16, 30, 0, TimeSpan.Zero)) with
        {
            ScheduleTimeZoneSerialized = serializedOriginalTimeZone,
        };
        TimeZoneInfo changedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Weekly-History-UTC-12",
            TimeSpan.FromHours(-12),
            "NightGate Weekly History UTC-12",
            "NightGate Weekly History UTC-12");

        WeeklyReportSummary beforeChange = WeeklyReportBuilder.Build(
            [first, second],
            PeriodEnd,
            TimeZoneInfo.Utc);
        WeeklyReportSummary afterChange = WeeklyReportBuilder.Build(
            [first, second],
            PeriodEnd,
            changedTimeZone);

        Assert.Equal(new TimeOnly(0, 20), beforeChange.MedianLockTime);
        Assert.Equal(beforeChange.MedianLockTime, afterChange.MedianLockTime);
    }

    [Fact]
    public void HistoricalMedian_RestoresDaylightSavingRuleFromNightSnapshot()
    {
        TimeZoneInfo.TransitionTime daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 2, 0, 0),
            3,
            5,
            DayOfWeek.Sunday);
        TimeZoneInfo.TransitionTime daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
            new DateTime(1, 1, 1, 3, 0, 0),
            10,
            5,
            DayOfWeek.Sunday);
        TimeZoneInfo.AdjustmentRule daylightRule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);
        TimeZoneInfo daylightTimeZone = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Weekly-DST-History",
            TimeSpan.FromHours(8),
            "NightGate Weekly DST History",
            "NightGate Weekly Standard",
            "NightGate Weekly Daylight",
            [daylightRule]);
        NightOutcome outcome = OutcomeFromUtcLock(
            new(2026, 7, 6),
            new(2026, 7, 6, 15, 20, 0, TimeSpan.Zero)) with
        {
            ScheduleTimeZoneSerialized = NightScheduleTimeZone.Capture(daylightTimeZone),
        };

        WeeklyReportSummary report = WeeklyReportBuilder.Build(
            [outcome],
            PeriodEnd,
            TimeZoneInfo.Utc);

        Assert.Equal(new TimeOnly(0, 20), report.MedianLockTime);
    }

    [Fact]
    public void Trend_ComparesCurrentMedianWithPreviousSevenNights()
    {
        NightOutcome previous1 = Outcome(new(2026, 6, 29), 0, 45);
        NightOutcome previous2 = Outcome(new(2026, 6, 30), 0, 35);
        NightOutcome current1 = Outcome(new(2026, 7, 6), 0, 20);
        NightOutcome current2 = Outcome(new(2026, 7, 7), 0, 30);

        WeeklyReportSummary report = WeeklyReportBuilder.Build(
            [previous1, previous2, current1, current2],
            PeriodEnd,
            TimeZoneInfo.Utc);

        Assert.Equal(new TimeOnly(0, 25), report.MedianLockTime);
        Assert.Equal(-15, report.MedianLockChangeMinutes);
    }

    [Fact]
    public void DuplicateNightDate_UsesLatestClosedOutcomeOnce()
    {
        NightOutcome earlier = Outcome(new(2026, 7, 6), 0, 50) with
        {
            ClosedAtUtc = new(2026, 7, 7, 8, 0, 0, TimeSpan.Zero),
        };
        NightOutcome latest = Outcome(new(2026, 7, 6), 0, 10) with
        {
            ClosedAtUtc = new(2026, 7, 7, 9, 0, 0, TimeSpan.Zero),
        };

        WeeklyReportSummary report = WeeklyReportBuilder.Build(
            [earlier, latest],
            PeriodEnd,
            TimeZoneInfo.Utc);

        Assert.Equal(1, report.ObservedWorkNights);
        Assert.Equal(1, report.LockObservations);
        Assert.Equal(new TimeOnly(0, 10), report.MedianLockTime);
    }

    [Fact]
    public void AggregateCounters_SaturateInsteadOfOverflowing()
    {
        OverrideReasonSummary maximum = new(
            TeamRescueCount: OverrideReasonSummary.MaximumCount);
        NightOutcome first = Outcome(new(2026, 7, 6), 0, 10, reasons: maximum);
        NightOutcome second = Outcome(
            new(2026, 7, 7),
            0,
            20,
            reasons: new(TeamRescueCount: 1));

        WeeklyReportSummary report = WeeklyReportBuilder.Build(
            [first, second],
            PeriodEnd,
            TimeZoneInfo.Utc);

        Assert.Equal(
            OverrideReasonSummary.MaximumCount,
            report.OverrideReasons.TeamRescueCount);
    }

    private static NightOutcome Outcome(
        DateOnly nightDate,
        int hour,
        int minute,
        bool lateEntertainment = false,
        bool emergency = false,
        OverrideReasonSummary? reasons = null)
    {
        DateOnly observedDate = hour >= 21 ? nightDate : nightDate.AddDays(1);
        return OutcomeFromUtcLock(
            nightDate,
            new(observedDate.ToDateTime(new(hour, minute)), TimeSpan.Zero),
            lateEntertainment,
            emergency,
            reasons);
    }

    private static NightOutcome OutcomeFromUtcLock(
        DateOnly nightDate,
        DateTimeOffset lockTimeUtc,
        bool lateEntertainment = false,
        bool emergency = false,
        OverrideReasonSummary? reasons = null) => new(
        Guid.NewGuid(),
        nightDate,
        new(nightDate.AddDays(1).ToDateTime(new(9, 0)), TimeSpan.Zero),
        emergency,
        false,
        false,
        false,
        lateEntertainment,
        false,
        reasons,
        lockTimeUtc,
        lockTimeUtc);
}
