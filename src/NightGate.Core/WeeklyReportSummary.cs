namespace NightGate.Core;

public sealed record WeeklyReportSummary
{
    public WeeklyReportSummary(
        DateOnly PeriodStart,
        DateOnly PeriodEnd,
        int ObservedWorkNights,
        int EligibleWorkNights,
        int QualifyingWorkNights,
        int LockObservations,
        TimeOnly? MedianLockTime,
        int? MedianLockChangeMinutes,
        OverrideReasonSummary OverrideReasons)
    {
        if (PeriodStart == default
            || PeriodEnd == default
            || PeriodEnd.DayNumber - PeriodStart.DayNumber != 6)
        {
            throw new ArgumentException("A weekly report must cover exactly seven dates.");
        }

        if (ObservedWorkNights is < 0 or > 5
            || EligibleWorkNights is < 0 or > 5
            || QualifyingWorkNights is < 0 or > 5
            || EligibleWorkNights > ObservedWorkNights
            || QualifyingWorkNights > EligibleWorkNights
            || LockObservations < 0
            || LockObservations > ObservedWorkNights
            || (LockObservations == 0) != (MedianLockTime is null)
            || MedianLockChangeMinutes is not null && MedianLockTime is null)
        {
            throw new ArgumentException("Weekly report counts or lock facts are inconsistent.");
        }

        ArgumentNullException.ThrowIfNull(OverrideReasons);
        this.PeriodStart = PeriodStart;
        this.PeriodEnd = PeriodEnd;
        this.ObservedWorkNights = ObservedWorkNights;
        this.EligibleWorkNights = EligibleWorkNights;
        this.QualifyingWorkNights = QualifyingWorkNights;
        this.LockObservations = LockObservations;
        this.MedianLockTime = MedianLockTime;
        this.MedianLockChangeMinutes = MedianLockChangeMinutes;
        this.OverrideReasons = OverrideReasons;
    }

    public DateOnly PeriodStart { get; }

    public DateOnly PeriodEnd { get; }

    public int ObservedWorkNights { get; }

    public int EligibleWorkNights { get; }

    public int QualifyingWorkNights { get; }

    public int LockObservations { get; }

    public TimeOnly? MedianLockTime { get; }

    public int? MedianLockChangeMinutes { get; }

    public OverrideReasonSummary OverrideReasons { get; }
}
