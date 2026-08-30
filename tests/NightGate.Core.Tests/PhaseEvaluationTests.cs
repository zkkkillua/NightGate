using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class PhaseEvaluationTests
{
    private static readonly TimeZoneInfo TestZone = TimeZoneInfo.CreateCustomTimeZone(
        "NightGate-Phase-UTC+08",
        TimeSpan.FromHours(8),
        "NightGate Phase UTC+08",
        "NightGate Phase UTC+08");

    public static TheoryData<int, DateTimeOffset, NightPhase> AllBoundaryMinutes
    {
        get
        {
            var data = new TheoryData<int, DateTimeOffset, NightPhase>();
            DateOnly nightDate = new(2026, 7, 12); // Sunday night, no weekend offset.

            foreach (ScheduleStep step in ScheduleProfile.Default.Steps)
            {
                NightWindow window = ScheduleEvaluator.CreateWindow(nightDate, step, TestZone);

                data.Add(step.Number, window.ProtectedStart.AddMinutes(-1), NightPhase.Morning);
                data.Add(step.Number, window.ProtectedStart, NightPhase.Free);
                data.Add(step.Number, window.LastStart.AddMinutes(-1), NightPhase.Free);
                data.Add(step.Number, window.LastStart, NightPhase.LastStart);
                data.Add(step.Number, window.LastStart.AddMinutes(1), NightPhase.Grace);
                data.Add(step.Number, window.Lock.AddMinutes(-1), NightPhase.Grace);
                data.Add(step.Number, window.Lock, NightPhase.LandingLocked);
                data.Add(step.Number, window.LightsOut.AddMinutes(-1), NightPhase.LandingLocked);
                data.Add(step.Number, window.LightsOut, NightPhase.LandingLocked);
                data.Add(step.Number, window.Wake.AddMinutes(-1), NightPhase.LandingLocked);
                data.Add(step.Number, window.Wake, NightPhase.Morning);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllBoundaryMinutes))]
    public void EvaluatePhase_UsesHalfOpenIntervalsAtEveryBoundaryMinute(
        int stepNumber,
        DateTimeOffset instant,
        NightPhase expected)
    {
        ScheduleStep step = ScheduleProfile.Default.Steps[stepNumber - 1];

        NightPhase actual = ScheduleEvaluator.EvaluatePhase(instant, step, TestZone);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EvaluatePhase_UsesExplicitTimeZoneDeterministically()
    {
        var instant = new DateTimeOffset(2026, 7, 12, 16, 5, 0, TimeSpan.Zero);
        ScheduleStep step = ScheduleProfile.Default.Steps[0];

        NightPhase first = ScheduleEvaluator.EvaluatePhase(instant, step, TestZone);
        NightPhase second = ScheduleEvaluator.EvaluatePhase(instant, step, TestZone);
        NightPhase utcPhase = ScheduleEvaluator.EvaluatePhase(instant, step, TimeZoneInfo.Utc);

        Assert.Equal(NightPhase.LastStart, first);
        Assert.Equal(first, second);
        Assert.Equal(NightPhase.Morning, utcPhase);
    }
}
