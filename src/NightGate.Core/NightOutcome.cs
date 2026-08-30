namespace NightGate.Core;

public sealed record NightOutcome
{
    // Windows reports the session lock asynchronously. Treat the first five
    // seconds as an explicit technical grace period for qualification; this does
    // not move the enforced lock boundary or extend an entertainment window.
    private static readonly TimeSpan LockObservationTolerance = TimeSpan.FromSeconds(5);
    private DateTimeOffset? _firstLockObservedAtUtc;
    private DateTimeOffset? _scheduledLockAtUtc;

    public NightOutcome(
        Guid NightId,
        DateOnly NightDate,
        DateTimeOffset ClosedAtUtc,
        bool EmergencyUsed,
        bool TeamRescueUsed,
        bool EntertainmentUsed,
        bool DeliberateBypass,
        bool LateNewEntertainment,
        bool MissedLock,
        OverrideReasonSummary? OverrideReasons = null,
        DateTimeOffset? FirstLockObservedAtUtc = null,
        DateTimeOffset? ScheduledLockAtUtc = null,
        bool ProtectionGapObserved = false,
        string? ScheduleTimeZoneSerialized = null)
    {
        this.NightId = NightId;
        this.NightDate = NightDate;
        this.ClosedAtUtc = ClosedAtUtc;
        this.EmergencyUsed = EmergencyUsed;
        this.TeamRescueUsed = TeamRescueUsed;
        this.EntertainmentUsed = EntertainmentUsed;
        this.DeliberateBypass = DeliberateBypass;
        this.LateNewEntertainment = LateNewEntertainment;
        this.MissedLock = MissedLock;
        this.OverrideReasons = OverrideReasons ?? OverrideReasonSummary.Empty;
        this.FirstLockObservedAtUtc = FirstLockObservedAtUtc;
        this.ScheduledLockAtUtc = ScheduledLockAtUtc;
        this.ProtectionGapObserved = ProtectionGapObserved;
        this.ScheduleTimeZoneSerialized = ScheduleTimeZoneSerialized;
    }

    public Guid NightId { get; init; }

    public DateOnly NightDate { get; init; }

    public DateTimeOffset ClosedAtUtc { get; init; }

    public bool EmergencyUsed { get; init; }

    public bool TeamRescueUsed { get; init; }

    public bool EntertainmentUsed { get; init; }

    public bool DeliberateBypass { get; init; }

    public bool LateNewEntertainment { get; init; }

    public bool MissedLock { get; init; }

    public OverrideReasonSummary OverrideReasons { get; init; }

    public bool ProtectionGapObserved { get; init; }

    public string? ScheduleTimeZoneSerialized { get; init; }

    public DateTimeOffset? FirstLockObservedAtUtc
    {
        get => _firstLockObservedAtUtc;
        init
        {
            if (value is { } observedAt
                && (observedAt == default || observedAt.Offset != TimeSpan.Zero))
            {
                throw new ArgumentException(
                    "The first lock observation must be a nondefault UTC timestamp.",
                    nameof(FirstLockObservedAtUtc));
            }

            _firstLockObservedAtUtc = value;
        }
    }

    public DateTimeOffset? ScheduledLockAtUtc
    {
        get => _scheduledLockAtUtc;
        init
        {
            if (value is { } scheduledAt
                && (scheduledAt == default || scheduledAt.Offset != TimeSpan.Zero))
            {
                throw new ArgumentException(
                    "The scheduled lock deadline must be a nondefault UTC timestamp.",
                    nameof(ScheduledLockAtUtc));
            }

            _scheduledLockAtUtc = value;
        }
    }

    public bool IsWorkNight => NightDate.DayOfWeek is >= DayOfWeek.Sunday and <= DayOfWeek.Thursday;

    public bool IsEligible => IsWorkNight && !EmergencyUsed;

    public bool Qualifies => IsEligible
        && !TeamRescueUsed
        && !EntertainmentUsed
        && !DeliberateBypass
        && !LateNewEntertainment
        && !MissedLock
        && !ProtectionGapObserved
        && FirstLockObservedAtUtc is { } firstLockObservedAtUtc
        && ScheduledLockAtUtc is { } scheduledLockAtUtc
        && firstLockObservedAtUtc - scheduledLockAtUtc < LockObservationTolerance;
}
