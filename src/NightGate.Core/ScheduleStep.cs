namespace NightGate.Core;

public sealed record ScheduleStep(
    int Number,
    TimeOnly LastStart,
    TimeOnly Lock,
    TimeOnly LightsOut,
    TimeOnly Wake);
