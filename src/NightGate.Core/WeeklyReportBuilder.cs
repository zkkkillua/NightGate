namespace NightGate.Core;

public static class WeeklyReportBuilder
{
    private const int MinutesPerDay = 24 * 60;
    private const int NoonMinutes = 12 * 60;

    public static WeeklyReportSummary Build(
        IEnumerable<NightOutcome> outcomes,
        DateOnly periodEnd,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (periodEnd == default || periodEnd.DayNumber < 13)
        {
            throw new ArgumentOutOfRangeException(nameof(periodEnd));
        }

        DateOnly periodStart = periodEnd.AddDays(-6);
        DateOnly previousStart = periodStart.AddDays(-7);
        NightOutcome[] materialized = outcomes.ToArray();
        if (materialized.Any(outcome => outcome is null))
        {
            throw new ArgumentException("Weekly report outcomes cannot contain null.", nameof(outcomes));
        }

        NightOutcome[] unique = materialized
            .Where(outcome => outcome.NightDate >= previousStart && outcome.NightDate <= periodEnd)
            .GroupBy(outcome => outcome.NightDate)
            .Select(group => group
                .OrderByDescending(outcome => outcome.ClosedAtUtc)
                .ThenByDescending(outcome => outcome.NightId)
                .First())
            .ToArray();
        NightOutcome[] current = unique
            .Where(outcome => outcome.NightDate >= periodStart)
            .ToArray();
        NightOutcome[] currentWorkNights = current
            .Where(outcome => outcome.IsWorkNight)
            .ToArray();
        NightOutcome[] previousWorkNights = unique
            .Where(outcome => outcome.NightDate < periodStart && outcome.IsWorkNight)
            .ToArray();

        int? currentMedian = MedianNormalizedLockMinute(currentWorkNights, timeZone);
        int? previousMedian = MedianNormalizedLockMinute(previousWorkNights, timeZone);
        TimeOnly? medianTime = currentMedian is { } median
            ? TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(median % MinutesPerDay))
            : null;
        int? change = currentMedian is { } currentValue && previousMedian is { } previousValue
            ? currentValue - previousValue
            : null;

        return new(
            periodStart,
            periodEnd,
            currentWorkNights.Length,
            currentWorkNights.Count(outcome => outcome.IsEligible),
            currentWorkNights.Count(outcome => outcome.Qualifies),
            currentWorkNights.Count(outcome => outcome.FirstLockObservedAtUtc is not null),
            medianTime,
            change,
            AggregateReasons(current));
    }

    private static int? MedianNormalizedLockMinute(
        IEnumerable<NightOutcome> outcomes,
        TimeZoneInfo timeZone)
    {
        int[] values = outcomes
            .Where(outcome => outcome.FirstLockObservedAtUtc is not null)
            .Select(outcome => NormalizeLockMinute(
                outcome.FirstLockObservedAtUtc!.Value,
                ResolveHistoricalTimeZone(outcome, timeZone)))
            .Order()
            .ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        int middle = values.Length / 2;
        if ((values.Length & 1) == 1)
        {
            return values[middle];
        }

        return (int)Math.Round(
            (values[middle - 1] + values[middle]) / 2d,
            MidpointRounding.AwayFromZero);
    }

    private static int NormalizeLockMinute(
        DateTimeOffset lockTimeUtc,
        TimeZoneInfo timeZone)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(lockTimeUtc, timeZone);
        int minute = local.Hour * 60 + local.Minute;
        return minute < NoonMinutes ? minute + MinutesPerDay : minute;
    }

    private static TimeZoneInfo ResolveHistoricalTimeZone(
        NightOutcome outcome,
        TimeZoneInfo fallbackTimeZone) =>
        outcome.ScheduleTimeZoneSerialized is { } serialized
            ? NightScheduleTimeZone.Restore(serialized)
            : fallbackTimeZone;

    private static OverrideReasonSummary AggregateReasons(
        IEnumerable<NightOutcome> outcomes)
    {
        int teamRescue = 0;
        int entertainment = 0;
        int health = 0;
        int safety = 0;
        int urgentWork = 0;
        int other = 0;
        foreach (NightOutcome outcome in outcomes)
        {
            OverrideReasonSummary reasons = outcome.OverrideReasons;
            teamRescue = AddSaturated(teamRescue, reasons.TeamRescueCount);
            entertainment = AddSaturated(entertainment, reasons.EntertainmentCount);
            health = AddSaturated(health, reasons.EmergencyHealthCount);
            safety = AddSaturated(safety, reasons.EmergencySafetyCount);
            urgentWork = AddSaturated(urgentWork, reasons.EmergencyUrgentWorkCount);
            other = AddSaturated(other, reasons.EmergencyOtherCount);
        }

        return new(teamRescue, entertainment, health, safety, urgentWork, other);
    }

    private static int AddSaturated(int left, int right) => (int)Math.Min(
        OverrideReasonSummary.MaximumCount,
        (long)left + right);
}
