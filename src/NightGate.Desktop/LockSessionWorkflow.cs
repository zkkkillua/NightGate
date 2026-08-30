namespace NightGate.Desktop;

public interface IWorkstationLocker
{
    bool TryLock();
}

public interface IRestrictedOverlayPresenter
{
    void Show(RestrictedOverlayPresentation presentation);

    void Update(RestrictedOverlayPresentation presentation);

    void Hide();
}

public interface ILockWorkflowEventSink
{
    void ReportMissedLock(LockAttemptKind attemptKind);

    void ReportWorkstationLocked();
}

public enum LockAttemptKind
{
    Initial,
    Relock,
}

public enum CurrentSessionEventKind
{
    Locked,
    Unlocked,
    Logon,
    Logoff,
}

public sealed record RestrictedOverlayPresentation(
    TimeSpan Remaining,
    DesktopPolicySnapshotDto Policy);

public sealed class LockSessionController
{
    private const int MaximumLockAttemptsPerEpisode = 3;
    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromSeconds(30);
    private readonly IWorkstationLocker _locker;
    private readonly IRestrictedOverlayPresenter _overlayPresenter;
    private readonly ILockWorkflowEventSink _eventSink;
    private DateOnly? _nightDate;
    private bool _initialLockAttempted;
    private DesktopPolicySnapshotDto? _latestPolicy;
    private TimeSpan _monotonicHighWater;
    private TimeSpan? _relockDeadline;
    private bool _overrideRequestHoldingRelock;
    private TimeSpan? _lockRetryDeadline;
    private LockAttemptKind? _lockRetryKind;
    private int _lockAttemptCount;
    private bool _sessionAvailable;

    public LockSessionController(
        IWorkstationLocker locker,
        IRestrictedOverlayPresenter overlayPresenter,
        ILockWorkflowEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(locker);
        ArgumentNullException.ThrowIfNull(overlayPresenter);
        ArgumentNullException.ThrowIfNull(eventSink);
        _locker = locker;
        _overlayPresenter = overlayPresenter;
        _eventSink = eventSink;
    }

    public void ObservePolicy(DesktopPolicyResult policyResult, TimeSpan monotonicNow)
    {
        ArgumentNullException.ThrowIfNull(policyResult);
        TimeSpan effectiveNow = AdvanceMonotonic(monotonicNow);
        DesktopPolicySnapshotDto? policy = policyResult.ExecutablePolicy;
        if (policy is null)
        {
            _latestPolicy = null;
            _initialLockAttempted = false;
            ResetLockAttempts();
            CancelOverlay();
            return;
        }

        if (_nightDate != policy.Window.NightDate)
        {
            _nightDate = policy.Window.NightDate;
            _initialLockAttempted = false;
            ResetLockAttempts();
            CancelOverlay();
        }

        _latestPolicy = policy;

        if (!IsRestrictedLockPhase(policy.Phase))
        {
            if (policy.Phase == DesktopNightPhase.OverrideActive)
            {
                _initialLockAttempted = false;
            }

            ResetLockAttempts();
            CancelOverlay();
            return;
        }

        if (_relockDeadline is not null)
        {
            UpdateOverlay(effectiveNow);
        }

        if (_initialLockAttempted)
        {
            return;
        }

        _initialLockAttempted = true;
        ResetLockAttempts();
        TryLockWithRetry(LockAttemptKind.Initial, effectiveNow);
    }

    public void OnSessionChanged(
        CurrentSessionEventKind eventKind,
        TimeSpan monotonicNow)
    {
        TimeSpan effectiveNow = AdvanceMonotonic(monotonicNow);
        if (eventKind == CurrentSessionEventKind.Locked)
        {
            try
            {
                _eventSink.ReportWorkstationLocked();
            }
            catch (Exception)
            {
                // Lock reporting is observational and must never disrupt session handling.
            }
        }

        if (eventKind is CurrentSessionEventKind.Locked or CurrentSessionEventKind.Logoff)
        {
            _sessionAvailable = false;
            ResetLockAttempts();
            CancelOverlay();
            return;
        }

        if (eventKind is not (CurrentSessionEventKind.Unlocked
                or CurrentSessionEventKind.Logon)
            || _sessionAvailable
            || _latestPolicy is not { } policy
            || !IsRestrictedLockPhase(policy.Phase))
        {
            return;
        }

        _sessionAvailable = true;
        ResetLockAttempts();
        _relockDeadline = effectiveNow + TimeSpan.FromSeconds(10);
        try
        {
            _overlayPresenter.Show(new(TimeSpan.FromSeconds(10), policy));
        }
        catch (Exception)
        {
            // The overlay is visual-only. Its failure must not cancel the
            // authoritative relock deadline.
        }
    }

    public void OnOverrideRequestStarted(TimeSpan monotonicNow)
    {
        TimeSpan effectiveNow = AdvanceMonotonic(monotonicNow);
        if (_relockDeadline is null)
        {
            return;
        }

        _overrideRequestHoldingRelock = true;
        _relockDeadline = effectiveNow + TimeSpan.FromSeconds(25);
        UpdateOverlay(effectiveNow);
    }

    public void OnOverrideRequestCompleted(
        DesktopOverrideResult? result,
        TimeSpan monotonicNow)
    {
        TimeSpan effectiveNow = AdvanceMonotonic(monotonicNow);
        bool heldCurrentRelock = _overrideRequestHoldingRelock;
        _overrideRequestHoldingRelock = false;
        if (!heldCurrentRelock)
        {
            return;
        }

        if (result is not null)
        {
            ObservePolicy(result.PolicyAfterRequest, effectiveNow);
        }

        if (_latestPolicy is not { } policy
            || !IsRestrictedLockPhase(policy.Phase))
        {
            CancelOverlay();
            return;
        }

        _relockDeadline = effectiveNow + TimeSpan.FromSeconds(10);
        UpdateOverlay(effectiveNow);
    }

    public void Tick(TimeSpan monotonicNow)
    {
        TimeSpan effectiveNow = AdvanceMonotonic(monotonicNow);
        RetryFailedLockIfDue(effectiveNow);
        if (_relockDeadline is not { } deadline)
        {
            return;
        }

        if (_latestPolicy is not { } policy
            || !IsRestrictedLockPhase(policy.Phase))
        {
            CancelOverlay();
            return;
        }

        TimeSpan remaining = deadline - effectiveNow;
        if (remaining > TimeSpan.Zero)
        {
            try
            {
                _overlayPresenter.Update(new(remaining, policy));
            }
            catch (Exception)
            {
                // The overlay is visual-only. Keep the established deadline
                // so the workstation is still relocked on time.
            }

            return;
        }

        CancelOverlay();
        ResetLockAttempts();
        TryLockWithRetry(LockAttemptKind.Relock, effectiveNow);
    }

    private void TryLockWithRetry(
        LockAttemptKind attemptKind,
        TimeSpan monotonicNow)
    {
        _lockAttemptCount++;
        if (TryLockOnce(attemptKind)
            || _lockAttemptCount >= MaximumLockAttemptsPerEpisode)
        {
            _lockRetryDeadline = null;
            _lockRetryKind = null;
            return;
        }

        _lockRetryDeadline = monotonicNow + LockRetryInterval;
        _lockRetryKind = attemptKind;
    }

    private void RetryFailedLockIfDue(TimeSpan monotonicNow)
    {
        if (_lockRetryDeadline is not { } deadline
            || monotonicNow < deadline
            || _lockRetryKind is not { } attemptKind)
        {
            return;
        }

        if (_latestPolicy is not { } policy
            || !IsRestrictedLockPhase(policy.Phase))
        {
            ResetLockAttempts();
            return;
        }

        _lockRetryDeadline = null;
        _lockRetryKind = null;
        TryLockWithRetry(attemptKind, monotonicNow);
    }

    private bool TryLockOnce(LockAttemptKind attemptKind)
    {
        bool locked;
        try
        {
            locked = _locker.TryLock();
        }
        catch (Exception)
        {
            locked = false;
        }

        if (!locked)
        {
            try
            {
                _eventSink.ReportMissedLock(attemptKind);
            }
            catch (Exception)
            {
                // Event reporting is observational and must never turn fail-open into a crash.
            }
        }

        return locked;
    }

    private void ResetLockAttempts()
    {
        _lockRetryDeadline = null;
        _lockRetryKind = null;
        _lockAttemptCount = 0;
    }

    private static bool IsRestrictedLockPhase(DesktopNightPhase phase) => phase is
        DesktopNightPhase.LandingLocked or DesktopNightPhase.CoolingOff;

    private TimeSpan AdvanceMonotonic(TimeSpan candidate)
    {
        if (candidate > _monotonicHighWater)
        {
            _monotonicHighWater = candidate;
        }

        return _monotonicHighWater;
    }

    private void UpdateOverlay(TimeSpan effectiveNow)
    {
        if (_relockDeadline is not { } deadline || _latestPolicy is not { } policy)
        {
            return;
        }

        TimeSpan remaining = deadline - effectiveNow;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            _overlayPresenter.Update(new(remaining, policy));
        }
        catch (Exception)
        {
            // The overlay is visual-only. Keep the established deadline so
            // policy refreshes cannot disable the scheduled relock.
        }
    }

    private void CancelOverlay()
    {
        _overrideRequestHoldingRelock = false;
        if (_relockDeadline is null)
        {
            return;
        }

        _relockDeadline = null;
        try
        {
            _overlayPresenter.Hide();
        }
        catch (Exception)
        {
            // Overlay failures are visual-only and must remain fail-open.
        }
    }
}
