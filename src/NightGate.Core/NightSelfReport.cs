namespace NightGate.Core;

public sealed record NightSelfReport
{
    public NightSelfReport(
        DateOnly NightDate,
        bool? PhoneOutOfReach,
        bool? WakeWithinWindow,
        DateTimeOffset UpdatedAtUtc)
    {
        if (NightDate == default)
        {
            throw new ArgumentOutOfRangeException(nameof(NightDate));
        }

        if (UpdatedAtUtc == default || UpdatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The self-report update time must be nondefault UTC.",
                nameof(UpdatedAtUtc));
        }

        this.NightDate = NightDate;
        this.PhoneOutOfReach = PhoneOutOfReach;
        this.WakeWithinWindow = WakeWithinWindow;
        this.UpdatedAtUtc = UpdatedAtUtc;
    }

    public DateOnly NightDate { get; }

    public bool? PhoneOutOfReach { get; }

    public bool? WakeWithinWindow { get; }

    public DateTimeOffset UpdatedAtUtc { get; }
}
