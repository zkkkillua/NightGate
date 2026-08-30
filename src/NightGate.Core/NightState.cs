namespace NightGate.Core;

public sealed record NightState
{
    private DateTimeOffset? _firstLockObservedAtUtc;
    private DateTimeOffset? _scheduledLockAtUtc;

    public NightState(
        Guid NightId,
        DateOnly NightDate,
        DateTimeOffset LastObservedUtc,
        NightPhase HighestBasePhaseReached,
        ActiveOverride? ActiveOverride,
        bool EmergencyUsed,
        bool TeamRescueUsed,
        bool EntertainmentUsed,
        bool DeliberateBypass,
        bool LateNewEntertainment,
        bool MissedLock,
        bool IsClosed = false,
        TimeSpan? LastObservedUptime = null,
        Guid? LastObservedBootSessionId = null,
        OverrideReasonSummary? OverrideReasons = null,
        DateTimeOffset? FirstLockObservedAtUtc = null,
        DateTimeOffset? ScheduledLockAtUtc = null,
        bool ProtectionGapObserved = false,
        string? ScheduleTimeZoneSerialized = null)
    {
        this.NightId = NightId;
        this.NightDate = NightDate;
        this.LastObservedUtc = LastObservedUtc;
        this.HighestBasePhaseReached = HighestBasePhaseReached;
        this.ActiveOverride = ActiveOverride;
        this.EmergencyUsed = EmergencyUsed;
        this.TeamRescueUsed = TeamRescueUsed;
        this.EntertainmentUsed = EntertainmentUsed;
        this.DeliberateBypass = DeliberateBypass;
        this.LateNewEntertainment = LateNewEntertainment;
        this.MissedLock = MissedLock;
        this.IsClosed = IsClosed;
        this.LastObservedUptime = LastObservedUptime;
        this.LastObservedBootSessionId = LastObservedBootSessionId;
        this.OverrideReasons = OverrideReasons ?? OverrideReasonSummary.Empty;
        this.FirstLockObservedAtUtc = FirstLockObservedAtUtc;
        this.ScheduledLockAtUtc = ScheduledLockAtUtc;
        this.ProtectionGapObserved = ProtectionGapObserved;
        this.ScheduleTimeZoneSerialized = ScheduleTimeZoneSerialized;
    }

    public Guid NightId { get; init; }

    public DateOnly NightDate { get; init; }

    public DateTimeOffset LastObservedUtc { get; init; }

    public NightPhase HighestBasePhaseReached { get; init; }

    public ActiveOverride? ActiveOverride { get; init; }

    public bool EmergencyUsed { get; init; }

    public bool TeamRescueUsed { get; init; }

    public bool EntertainmentUsed { get; init; }

    public bool DeliberateBypass { get; init; }

    public bool LateNewEntertainment { get; init; }

    public bool MissedLock { get; init; }

    public bool IsClosed { get; init; }

    public TimeSpan? LastObservedUptime { get; init; }

    public Guid? LastObservedBootSessionId { get; init; }

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
}
