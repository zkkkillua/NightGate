using System.Collections.Immutable;

namespace NightGate.Core;

public sealed record ScheduleProfile(ImmutableArray<ScheduleStep> Steps)
{
    public static ScheduleProfile Default { get; } = new(
        [
            new(1, new(0, 5), new(0, 40), new(1, 0), new(9, 0)),
            new(2, new(23, 50), new(0, 25), new(0, 45), new(8, 45)),
            new(3, new(23, 35), new(0, 10), new(0, 30), new(8, 30)),
            new(4, new(23, 20), new(23, 55), new(0, 15), new(8, 15)),
        ]);
}
