namespace NightGate.Core;

public sealed record OverrideReasonSummary
{
    public const int MaximumCount = 1_000_000;

    public OverrideReasonSummary(
        int TeamRescueCount = 0,
        int EntertainmentCount = 0,
        int EmergencyHealthCount = 0,
        int EmergencySafetyCount = 0,
        int EmergencyUrgentWorkCount = 0,
        int EmergencyOtherCount = 0)
    {
        this.TeamRescueCount = ValidateCount(TeamRescueCount, nameof(TeamRescueCount));
        this.EntertainmentCount = ValidateCount(EntertainmentCount, nameof(EntertainmentCount));
        this.EmergencyHealthCount = ValidateCount(
            EmergencyHealthCount,
            nameof(EmergencyHealthCount));
        this.EmergencySafetyCount = ValidateCount(
            EmergencySafetyCount,
            nameof(EmergencySafetyCount));
        this.EmergencyUrgentWorkCount = ValidateCount(
            EmergencyUrgentWorkCount,
            nameof(EmergencyUrgentWorkCount));
        this.EmergencyOtherCount = ValidateCount(
            EmergencyOtherCount,
            nameof(EmergencyOtherCount));
    }

    public static OverrideReasonSummary Empty { get; } = new();

    public int TeamRescueCount { get; }

    public int EntertainmentCount { get; }

    public int EmergencyHealthCount { get; }

    public int EmergencySafetyCount { get; }

    public int EmergencyUrgentWorkCount { get; }

    public int EmergencyOtherCount { get; }

    public OverrideReasonSummary Increment(
        OverrideKind kind,
        EmergencyReason? emergencyReason = null) => kind switch
        {
            OverrideKind.TeamRescue => new(
                IncrementBounded(TeamRescueCount),
                EntertainmentCount,
                EmergencyHealthCount,
                EmergencySafetyCount,
                EmergencyUrgentWorkCount,
                EmergencyOtherCount),
            OverrideKind.Entertainment => new(
                TeamRescueCount,
                IncrementBounded(EntertainmentCount),
                EmergencyHealthCount,
                EmergencySafetyCount,
                EmergencyUrgentWorkCount,
                EmergencyOtherCount),
            OverrideKind.Emergency => IncrementEmergency(
                emergencyReason ?? throw new ArgumentException(
                    "An emergency reason is required.",
                    nameof(emergencyReason))),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private OverrideReasonSummary IncrementEmergency(EmergencyReason reason) => reason switch
    {
        EmergencyReason.Health => new(
            TeamRescueCount,
            EntertainmentCount,
            IncrementBounded(EmergencyHealthCount),
            EmergencySafetyCount,
            EmergencyUrgentWorkCount,
            EmergencyOtherCount),
        EmergencyReason.Safety => new(
            TeamRescueCount,
            EntertainmentCount,
            EmergencyHealthCount,
            IncrementBounded(EmergencySafetyCount),
            EmergencyUrgentWorkCount,
            EmergencyOtherCount),
        EmergencyReason.UrgentWork => new(
            TeamRescueCount,
            EntertainmentCount,
            EmergencyHealthCount,
            EmergencySafetyCount,
            IncrementBounded(EmergencyUrgentWorkCount),
            EmergencyOtherCount),
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static int ValidateCount(int value, string parameterName)
    {
        if (value is < 0 or > MaximumCount)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    private static int IncrementBounded(int value) =>
        value == MaximumCount ? MaximumCount : value + 1;
}
