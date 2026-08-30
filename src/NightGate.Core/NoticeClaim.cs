namespace NightGate.Core;

public enum NightNoticeKind
{
    IfThenPlan,
    LastStart,
    Grace10,
    Grace2,
}

public sealed record NoticeClaim
{
    public NoticeClaim(DateOnly NightDate, NightNoticeKind Kind, DateTimeOffset ClaimedAtUtc)
    {
        if (NightDate == default)
        {
            throw new ArgumentOutOfRangeException(nameof(NightDate));
        }

        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }

        if (ClaimedAtUtc == default || ClaimedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The notice claim time must be nondefault UTC.",
                nameof(ClaimedAtUtc));
        }

        this.NightDate = NightDate;
        this.Kind = Kind;
        this.ClaimedAtUtc = ClaimedAtUtc;
    }

    public DateOnly NightDate { get; }

    public NightNoticeKind Kind { get; }

    public DateTimeOffset ClaimedAtUtc { get; }
}
