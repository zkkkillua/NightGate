using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class NightWindowTests
{
    private static readonly TimeZoneInfo TestZone = TimeZoneInfo.CreateCustomTimeZone(
        "NightGate-Test-UTC+08",
        TimeSpan.FromHours(8),
        "NightGate Test UTC+08",
        "NightGate Test UTC+08");

    private static readonly TimeZoneInfo DaylightZone = CreateDaylightZone();

    [Fact]
    public void CreateWindow_MapsWorkNightTimesAcrossMidnight()
    {
        DateOnly nightDate = new(2026, 7, 9); // Thursday night.
        ScheduleStep step = ScheduleProfile.Default.Steps[0];

        NightWindow actual = ScheduleEvaluator.CreateWindow(nightDate, step, TestZone);

        var offset = TimeSpan.FromHours(8);
        Assert.Equal(nightDate, actual.NightDate);
        Assert.Equal(new(2026, 7, 9, 21, 0, 0, offset), actual.ProtectedStart);
        Assert.Equal(new(2026, 7, 10, 0, 5, 0, offset), actual.LastStart);
        Assert.Equal(new(2026, 7, 10, 0, 40, 0, offset), actual.Lock);
        Assert.Equal(new(2026, 7, 10, 1, 0, 0, offset), actual.LightsOut);
        Assert.Equal(new(2026, 7, 10, 9, 0, 0, offset), actual.Wake);
    }

    [Theory]
    [InlineData(2026, 7, 10)] // Friday night.
    [InlineData(2026, 7, 11)] // Saturday night.
    public void CreateWindow_AddsExactlyOneHourOnWeekendNights(
        int year,
        int month,
        int day)
    {
        DateOnly nightDate = new(year, month, day);
        ScheduleStep step = ScheduleProfile.Default.Steps[3];

        NightWindow actual = ScheduleEvaluator.CreateWindow(nightDate, step, TestZone);

        DateOnly nextDate = nightDate.AddDays(1);
        var offset = TimeSpan.FromHours(8);
        Assert.Equal(new(nightDate, new(22, 0), offset), actual.ProtectedStart);
        Assert.Equal(new(nextDate, new(0, 20), offset), actual.LastStart);
        Assert.Equal(new(nextDate, new(0, 55), offset), actual.Lock);
        Assert.Equal(new(nextDate, new(1, 15), offset), actual.LightsOut);
        Assert.Equal(new(nextDate, new(9, 15), offset), actual.Wake);
    }

    [Theory]
    [InlineData("2026-07-10T21:59:59+08:00", NightPhase.Morning)]
    [InlineData("2026-07-10T22:00:00+08:00", NightPhase.Free)]
    [InlineData("2026-07-11T00:19:59+08:00", NightPhase.Free)]
    [InlineData("2026-07-11T21:59:59+08:00", NightPhase.Morning)]
    [InlineData("2026-07-11T22:00:00+08:00", NightPhase.Free)]
    [InlineData("2026-07-12T21:00:00+08:00", NightPhase.Free)]
    public void EvaluatePhase_ShiftsFridayFreeStartAndRestoresSunday(
        string instantText,
        NightPhase expected)
    {
        ScheduleStep step = ScheduleProfile.Default.Steps[3];

        NightPhase actual = ScheduleEvaluator.EvaluatePhase(
            DateTimeOffset.Parse(instantText),
            step,
            TestZone);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CreateWindow_ResetsToWorkNightTimesOnSunday()
    {
        DateOnly nightDate = new(2026, 7, 12); // Sunday night.
        ScheduleStep step = ScheduleProfile.Default.Steps[3];

        NightWindow actual = ScheduleEvaluator.CreateWindow(nightDate, step, TestZone);

        var offset = TimeSpan.FromHours(8);
        Assert.Equal(new(nightDate, new(21, 0), offset), actual.ProtectedStart);
        Assert.Equal(new(nightDate, new(23, 20), offset), actual.LastStart);
        Assert.Equal(new(nightDate, new(23, 55), offset), actual.Lock);
        Assert.Equal(new(nightDate.AddDays(1), new(0, 15), offset), actual.LightsOut);
        Assert.Equal(new(nightDate.AddDays(1), new(8, 15), offset), actual.Wake);
    }

    [Fact]
    public void CreateWindow_NormalizesWeekendBoundaryAcrossSpringForwardGap()
    {
        DateOnly nightDate = new(2026, 3, 7); // Saturday before daylight time starts.
        ScheduleStep step = ScheduleProfile.Default.Steps[0];

        NightWindow actual = ScheduleEvaluator.CreateWindow(nightDate, step, DaylightZone);

        Assert.Equal(new(2026, 3, 8, 3, 0, 0), actual.LightsOut.DateTime);
        Assert.Equal(TimeSpan.FromHours(-7), actual.LightsOut.Offset);
    }

    [Fact]
    public void EvaluatePhase_UsesLaterOccurrenceForAmbiguousWeekendLock()
    {
        ScheduleStep step = ScheduleProfile.Default.Steps[1];
        var firstOccurrence = new DateTimeOffset(
            2026,
            11,
            1,
            1,
            25,
            0,
            TimeSpan.FromHours(-7));
        var secondOccurrence = new DateTimeOffset(
            2026,
            11,
            1,
            1,
            25,
            0,
            TimeSpan.FromHours(-8));

        NightPhase firstPhase = ScheduleEvaluator.EvaluatePhase(
            firstOccurrence,
            step,
            DaylightZone);
        NightPhase secondPhase = ScheduleEvaluator.EvaluatePhase(
            secondOccurrence,
            step,
            DaylightZone);

        Assert.Equal(NightPhase.Grace, firstPhase);
        Assert.Equal(NightPhase.LandingLocked, secondPhase);
    }

    private static TimeZoneInfo CreateDaylightZone()
    {
        TimeZoneInfo.TransitionTime daylightStart =
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new(1, 1, 1, 2, 0, 0),
                3,
                2,
                DayOfWeek.Sunday);
        TimeZoneInfo.TransitionTime daylightEnd =
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new(1, 1, 1, 2, 0, 0),
                11,
                1,
                DayOfWeek.Sunday);
        TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new(2026, 1, 1),
            new(2026, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);

        return TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Test-Daylight",
            TimeSpan.FromHours(-8),
            "NightGate Test Daylight",
            "NightGate Test Standard",
            "NightGate Test Daylight",
            [rule]);
    }
}
