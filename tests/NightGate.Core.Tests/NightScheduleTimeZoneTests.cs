using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class NightScheduleTimeZoneTests
{
    [Fact]
    public void CaptureAndRestore_PreserveDstBoundariesForThePinnedNight()
    {
        TimeZoneInfo original = CreateDstTimeZone();

        TimeZoneInfo restored = NightScheduleTimeZone.Restore(
            NightScheduleTimeZone.Capture(original));
        ScheduleStep step = ScheduleProfile.Default.Steps.Single(candidate => candidate.Number == 1);
        NightWindow expected = ScheduleEvaluator.CreateWindow(
            new DateOnly(2026, 3, 7),
            step,
            original);
        NightWindow actual = ScheduleEvaluator.CreateWindow(
            new DateOnly(2026, 3, 7),
            step,
            restored);

        Assert.Equal(expected.ProtectedStart.ToUniversalTime(), actual.ProtectedStart.ToUniversalTime());
        Assert.Equal(expected.LastStart.ToUniversalTime(), actual.LastStart.ToUniversalTime());
        Assert.Equal(expected.Lock.ToUniversalTime(), actual.Lock.ToUniversalTime());
        Assert.Equal(expected.LightsOut.ToUniversalTime(), actual.LightsOut.ToUniversalTime());
        Assert.Equal(expected.Wake.ToUniversalTime(), actual.Wake.ToUniversalTime());
        Assert.NotEqual(
            expected.Lock.Offset,
            expected.Wake.Offset);
    }

    [Fact]
    public void Restore_MalformedSnapshot_IsRejected()
    {
        Assert.Throws<System.Runtime.Serialization.SerializationException>(
            () => NightScheduleTimeZone.Restore("not-a-time-zone"));
    }

    [Fact]
    public void ResolveForActiveNight_ClosedOrLegacyStateUsesCurrentTimeZone()
    {
        TimeZoneInfo pinned = TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-Pinned-Offset",
            TimeSpan.FromHours(8),
            "NightGate Pinned Offset",
            "NightGate Pinned Offset");
        NightState active = new(
            Guid.NewGuid(),
            new DateOnly(2026, 7, 14),
            new DateTimeOffset(2026, 7, 14, 13, 1, 0, TimeSpan.Zero),
            NightPhase.Free,
            null,
            false,
            false,
            false,
            false,
            false,
            false,
            ScheduleTimeZoneSerialized: NightScheduleTimeZone.Capture(pinned));

        Assert.Equal(
            pinned.Id,
            NightScheduleTimeZone.ResolveForActiveNight(active, TimeZoneInfo.Utc).Id);
        Assert.Same(
            TimeZoneInfo.Utc,
            NightScheduleTimeZone.ResolveForActiveNight(
                active with { IsClosed = true },
                TimeZoneInfo.Utc));
        Assert.Same(
            TimeZoneInfo.Utc,
            NightScheduleTimeZone.ResolveForActiveNight(
                active with { ScheduleTimeZoneSerialized = null },
                TimeZoneInfo.Utc));
    }

    private static TimeZoneInfo CreateDstTimeZone()
    {
        TimeZoneInfo.TransitionTime daylightStart = TimeZoneInfo.TransitionTime
            .CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0),
                3,
                2,
                DayOfWeek.Sunday);
        TimeZoneInfo.TransitionTime daylightEnd = TimeZoneInfo.TransitionTime
            .CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0),
                11,
                1,
                DayOfWeek.Sunday);
        TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2020, 1, 1),
            new DateTime(2030, 12, 31),
            TimeSpan.FromHours(1),
            daylightStart,
            daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone(
            "NightGate-DST-Snapshot",
            TimeSpan.FromHours(-5),
            "NightGate DST Snapshot",
            "NightGate Standard",
            "NightGate Daylight",
            [rule]);
    }
}
