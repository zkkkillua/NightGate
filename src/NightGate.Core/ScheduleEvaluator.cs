namespace NightGate.Core;

public static class ScheduleEvaluator
{
    private static readonly TimeOnly ProtectedStartTime = new(21, 0);

    public static DateTimeOffset CalculateLastStart(
        DateTimeOffset lockTime,
        AppRule rule) => lockTime.AddMinutes(-rule.SessionMinutes);

    public static NightPhase EvaluatePhase(
        DateTimeOffset instant,
        ScheduleStep step,
        TimeZoneInfo timeZone)
    {
        NightWindow window = CreateWindowForInstant(instant, step, timeZone);

        if (instant < window.ProtectedStart || instant >= window.Wake)
        {
            return NightPhase.Morning;
        }

        if (instant < window.LastStart)
        {
            return NightPhase.Free;
        }

        if (instant < window.LastStart.AddMinutes(1))
        {
            return NightPhase.LastStart;
        }

        return instant < window.Lock
            ? NightPhase.Grace
            : NightPhase.LandingLocked;
    }

    public static NightWindow CreateWindowForInstant(
        DateTimeOffset instant,
        ScheduleStep step,
        TimeZoneInfo timeZone)
    {
        DateTimeOffset localInstant = TimeZoneInfo.ConvertTime(instant, timeZone);
        DateOnly localDate = DateOnly.FromDateTime(localInstant.DateTime);
        TimeOnly localTime = TimeOnly.FromDateTime(localInstant.DateTime);
        DateOnly nightDate = localTime >= ProtectedStartTime
            ? localDate
            : localDate.AddDays(-1);
        return CreateWindow(nightDate, step, timeZone);
    }

    public static NightWindow CreateWindow(
        DateOnly nightDate,
        ScheduleStep step,
        TimeZoneInfo timeZone)
    {
        int offsetMinutes = nightDate.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday
            ? 60
            : 0;

        return new(
            nightDate,
            AtNightTime(nightDate, ProtectedStartTime, offsetMinutes, timeZone),
            AtNightTime(nightDate, step.LastStart, offsetMinutes, timeZone),
            AtNightTime(nightDate, step.Lock, offsetMinutes, timeZone),
            AtNightTime(nightDate, step.LightsOut, offsetMinutes, timeZone),
            AtNightTime(nightDate, step.Wake, offsetMinutes, timeZone));
    }

    private static DateTimeOffset AtNightTime(
        DateOnly nightDate,
        TimeOnly time,
        int offsetMinutes,
        TimeZoneInfo timeZone)
    {
        DateOnly boundaryDate = time >= ProtectedStartTime
            ? nightDate
            : nightDate.AddDays(1);
        DateTime localTime = boundaryDate.ToDateTime(time).AddMinutes(offsetMinutes);

        return AtLocalTime(localTime, timeZone);
    }

    private static DateTimeOffset AtLocalTime(
        DateOnly date,
        TimeOnly time,
        TimeZoneInfo timeZone) => AtLocalTime(date.ToDateTime(time), timeZone);

    private static DateTimeOffset AtLocalTime(
        DateTime localTime,
        TimeZoneInfo timeZone)
    {
        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(localTime))
        {
            DateTime beforeGap = localTime;
            DateTime afterGap = localTime;

            do
            {
                beforeGap = beforeGap.AddMinutes(-1);
            }
            while (timeZone.IsInvalidTime(beforeGap));

            do
            {
                afterGap = afterGap.AddMinutes(1);
            }
            while (timeZone.IsInvalidTime(afterGap));

            TimeSpan gap = timeZone.GetUtcOffset(afterGap) - timeZone.GetUtcOffset(beforeGap);
            localTime = localTime.Add(gap);
        }

        TimeSpan offset = timeZone.IsAmbiguousTime(localTime)
            ? timeZone.GetAmbiguousTimeOffsets(localTime).Min()
            : timeZone.GetUtcOffset(localTime);

        return new(localTime, offset);
    }
}
