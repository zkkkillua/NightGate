namespace NightGate.Desktop;

public sealed class DesktopPolicyPollModel
{
    private DateOnly? _nightDate;
    private DesktopNightPhase? _observedPhase;
    private bool _immediateRefreshPending;
    private BoundaryRefreshKey? _lastBoundaryRefresh;

    public DesktopNightPhase Observe(DesktopPolicySnapshotDto policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        DateOnly incomingNight = policy.Window.NightDate;
        if (_nightDate is null || incomingNight > _nightDate)
        {
            _nightDate = incomingNight;
            _observedPhase = policy.Phase;
            _lastBoundaryRefresh = null;
            return policy.Phase;
        }

        if (incomingNight < _nightDate)
        {
            return _observedPhase ?? policy.Phase;
        }

        DesktopNightPhase current = _observedPhase ?? policy.Phase;
        if (ProgressRank(policy.Phase) < ProgressRank(current))
        {
            return current;
        }

        _observedPhase = policy.Phase;
        return policy.Phase;
    }

    public void MarkOverrideAccepted() => _immediateRefreshPending = true;

    public TimeSpan GetNextDelay(
        DesktopPolicySnapshotDto policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(policy);
        DesktopNightPhase phase = Observe(policy);
        PolicyBoundary? staleBoundary = GetStaleBoundary(phase, policy.Window, now);
        if (_immediateRefreshPending)
        {
            _immediateRefreshPending = false;
            if (staleBoundary is { } boundary)
            {
                _lastBoundaryRefresh = new(policy.Window.NightDate, boundary);
            }

            return TimeSpan.Zero;
        }

        if (staleBoundary is { } behindBoundary)
        {
            BoundaryRefreshKey refreshKey = new(policy.Window.NightDate, behindBoundary);
            if (_lastBoundaryRefresh != refreshKey)
            {
                _lastBoundaryRefresh = refreshKey;
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(1);
        }

        TimeSpan cadence = IsRestricted(phase)
            ? TimeSpan.FromSeconds(1)
            : TimeSpan.FromSeconds(30);
        foreach (DateTimeOffset boundary in Boundaries(policy.Window))
        {
            TimeSpan untilBoundary = boundary - now;
            if (untilBoundary > TimeSpan.Zero && untilBoundary < cadence)
            {
                cadence = untilBoundary;
            }
        }

        return cadence;
    }

    private static PolicyBoundary? GetStaleBoundary(
        DesktopNightPhase phase,
        DesktopNightWindowDto window,
        DateTimeOffset now)
    {
        PolicyBoundary? passedBoundary = now >= window.Wake
            ? PolicyBoundary.Wake
            : now >= window.Lock
                ? PolicyBoundary.Lock
                : now >= window.LastStart
                    ? PolicyBoundary.LastStart
                    : null;
        if (passedBoundary is not { } boundary)
        {
            return null;
        }

        int expectedRank = boundary switch
        {
            PolicyBoundary.LastStart => ProgressRank(DesktopNightPhase.LastStart),
            PolicyBoundary.Lock => ProgressRank(DesktopNightPhase.LandingLocked),
            PolicyBoundary.Wake => ProgressRank(DesktopNightPhase.Morning),
            _ => throw new ArgumentOutOfRangeException(nameof(window)),
        };
        return ProgressRank(phase) < expectedRank ? boundary : null;
    }

    private static IEnumerable<DateTimeOffset> Boundaries(DesktopNightWindowDto window)
    {
        yield return window.LastStart;
        yield return window.Lock;
        yield return window.Wake;
    }

    private static bool IsRestricted(DesktopNightPhase phase) => phase is
        DesktopNightPhase.LastStart or
        DesktopNightPhase.Grace or
        DesktopNightPhase.LandingLocked or
        DesktopNightPhase.CoolingOff or
        DesktopNightPhase.OverrideActive;

    private static int ProgressRank(DesktopNightPhase phase) => phase switch
    {
        DesktopNightPhase.Free => 0,
        DesktopNightPhase.LastStart => 1,
        DesktopNightPhase.Grace => 2,
        DesktopNightPhase.LandingLocked or
        DesktopNightPhase.CoolingOff or
        DesktopNightPhase.OverrideActive => 3,
        DesktopNightPhase.Morning => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private enum PolicyBoundary
    {
        LastStart,
        Lock,
        Wake,
    }

    private readonly record struct BoundaryRefreshKey(
        DateOnly NightDate,
        PolicyBoundary Boundary);
}
