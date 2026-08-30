namespace NightGate.Desktop;

public enum CommitmentCountdownKind
{
    GraceToLock,
    GameGraceToLock,
    EntertainmentCoolingOff,
    TeamRescue,
    Emergency,
    EntertainmentActive,
}

public sealed record CommitmentCountdownTarget(
    CommitmentCountdownKind Kind,
    string Identity,
    DateTimeOffset ServiceDeadline,
    TimeSpan InitialRemaining);

public sealed record CommitmentCountdownPresentation(
    CommitmentCountdownKind Kind,
    TimeSpan Remaining,
    DateTimeOffset ServiceDeadline,
    bool IsUrgent);

public interface ICommitmentCountdownPresenter
{
    void Apply(CommitmentCountdownPresentation? presentation);
}

public enum CommitmentCountdownVisualOperation
{
    Update,
    Hide,
}

public interface ICommitmentCountdownDiagnostics
{
    void RecordVisualFailure(
        CommitmentCountdownVisualOperation operation,
        Exception exception);
}

public static class CommitmentCountdownModel
{
    private static readonly TimeSpan FinalGraceWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UrgentWindow = TimeSpan.FromMinutes(2);

    public static CommitmentCountdownTarget? Resolve(
        DesktopPolicyResult policy,
        bool hasRunningGame = false,
        TimeSpan elapsedSincePolicy = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        DesktopPolicySnapshotDto? snapshot = policy.ExecutablePolicy;
        if (snapshot is null
            || policy.IsDegraded
            || snapshot.IsDegraded
            || !snapshot.EnforcementEnabled
            || snapshot.EvaluatedAt == default)
        {
            return null;
        }

        DateTimeOffset evaluatedAt = AdvanceEvaluation(snapshot.EvaluatedAt, elapsedSincePolicy);
        if (hasRunningGame && IsGameReminderWindow(snapshot, evaluatedAt))
        {
            return new(
                CommitmentCountdownKind.GameGraceToLock,
                $"grace:{snapshot.Window.NightDate:yyyy-MM-dd}",
                snapshot.Window.Lock,
                snapshot.Window.Lock - evaluatedAt);
        }

        if (snapshot.Phase == DesktopNightPhase.Grace)
        {
            TimeSpan remaining = snapshot.Window.Lock - evaluatedAt;
            return remaining > TimeSpan.Zero && remaining <= FinalGraceWindow
                ? new(
                    CommitmentCountdownKind.GraceToLock,
                    $"grace:{snapshot.Window.NightDate:yyyy-MM-dd}",
                    snapshot.Window.Lock,
                    remaining)
                : null;
        }

        DesktopActiveOverrideDto? active = snapshot.ActiveOverride;
        if (active is null
            || active.RequestedAtUtc == default
            || active.StartsAtUtc == default
            || active.EndsAtUtc == default
            || active.EndsAtUtc <= active.StartsAtUtc
            || active.RequestedAtUtc > evaluatedAt)
        {
            return null;
        }

        if (snapshot.Phase == DesktopNightPhase.CoolingOff)
        {
            TimeSpan remaining = active.StartsAtUtc - evaluatedAt;
            return active.Kind == DesktopOverrideKind.Entertainment
                && remaining > TimeSpan.Zero
                ? new(
                    CommitmentCountdownKind.EntertainmentCoolingOff,
                    $"cooling:{active.Kind}:{active.RequestedAtUtc.UtcTicks}",
                    active.StartsAtUtc,
                    remaining)
                : null;
        }

        if (snapshot.Phase != DesktopNightPhase.OverrideActive
            || evaluatedAt < active.StartsAtUtc
            || evaluatedAt >= active.EndsAtUtc)
        {
            return null;
        }

        CommitmentCountdownKind kind = active.Kind switch
        {
            DesktopOverrideKind.TeamRescue => CommitmentCountdownKind.TeamRescue,
            DesktopOverrideKind.Emergency => CommitmentCountdownKind.Emergency,
            DesktopOverrideKind.Entertainment =>
                CommitmentCountdownKind.EntertainmentActive,
            _ => throw new ArgumentOutOfRangeException(nameof(active.Kind)),
        };
        return new(
            kind,
            $"active:{active.Kind}:{active.RequestedAtUtc.UtcTicks}",
            active.EndsAtUtc,
            active.EndsAtUtc - evaluatedAt);
    }

    public static bool IsGameReminderWindow(
        DesktopPolicyResult policy,
        TimeSpan elapsedSincePolicy = default) =>
        policy is { IsDegraded: false, ExecutablePolicy:
            { IsDegraded: false, EnforcementEnabled: true } snapshot }
        && snapshot.EvaluatedAt != default
        && IsGameReminderWindow(
            snapshot,
            AdvanceEvaluation(snapshot.EvaluatedAt, elapsedSincePolicy));

    internal static bool HasSameGameObservationScope(
        DesktopPolicyResult? sampled,
        DesktopPolicyResult current)
    {
        if (sampled?.ExecutablePolicy is not { } prior
            || current.ExecutablePolicy is not { } next
            || prior.Window.NightDate != next.Window.NightDate)
        {
            return false;
        }

        static IEnumerable<string> Roots(DesktopPolicySnapshotDto snapshot) =>
            snapshot.AppRules
                .Where(rule => rule is { IsConfigured: true,
                    Category: DesktopAppRuleCategory.Game, RootExecutablePath: not null })
                .Select(rule => rule.RootExecutablePath!)
                .Order(StringComparer.OrdinalIgnoreCase);
        return Roots(prior).SequenceEqual(Roots(next), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGameReminderWindow(
        DesktopPolicySnapshotDto snapshot,
        DateTimeOffset evaluatedAt)
    {
        if (snapshot.Phase is not (DesktopNightPhase.Free
            or DesktopNightPhase.LastStart or DesktopNightPhase.Grace)
            || evaluatedAt >= snapshot.Window.Lock)
        {
            return false;
        }

        // Match the existing last-start notice, including a configured long game's
        // earlier cutoff. The earlier if-then plan card must not trigger this overlay.
        DateTimeOffset startsAt = snapshot.Window.LastStart;
        foreach (DesktopAppRuleDto rule in snapshot.AppRules)
        {
            if (rule is { IsConfigured: true, Category: DesktopAppRuleCategory.Game,
                    RootExecutablePath: not null, SessionMinutes: >= 15 and <= 90 })
            {
                DateTimeOffset cutoff = snapshot.Window.Lock.AddMinutes(-rule.SessionMinutes);
                if (cutoff < startsAt)
                {
                    startsAt = cutoff;
                }
            }
        }
        if (startsAt < snapshot.Window.ProtectedStart)
        {
            startsAt = snapshot.Window.ProtectedStart;
        }
        return evaluatedAt >= startsAt;
    }

    private static DateTimeOffset AdvanceEvaluation(
        DateTimeOffset evaluatedAt,
        TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            return evaluatedAt;
        }
        return elapsed.Ticks > DateTimeOffset.MaxValue.UtcTicks - evaluatedAt.UtcTicks
            ? DateTimeOffset.MaxValue
            : evaluatedAt.ToUniversalTime().Add(elapsed);
    }

    public static CommitmentCountdownPresentation? Project(
        CommitmentCountdownTarget target,
        TimeSpan remaining)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (remaining <= TimeSpan.Zero || !double.IsFinite(remaining.TotalSeconds))
        {
            return null;
        }

        double seconds = Math.Ceiling(remaining.TotalSeconds);
        if (seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            return null;
        }

        TimeSpan displayed = TimeSpan.FromSeconds(seconds);
        return new(
            target.Kind,
            displayed,
            target.ServiceDeadline,
            displayed <= UrgentWindow);
    }
}

public sealed class CommitmentCountdownController
{
    private readonly ICommitmentCountdownPresenter _presenter;
    private readonly ICommitmentCountdownDiagnostics _diagnostics;
    private DesktopPolicyResult? _observedPolicy;
    private TimeSpan _policyObservedAt;
    private bool _hasRunningGame;
    private CommitmentCountdownTarget? _target;
    private string? _guardedIdentity;
    private CommitmentCountdownPresentation? _lastApplied;
    private TimeSpan _monotonicDeadline;
    private TimeSpan _monotonicHighWater;
    private bool _hasMonotonicSample;
    private bool _lastApplySucceeded = true;
    private bool _presenterMayBeVisible;
    private bool _hideRetryPending;
    private bool _visualFailureActive;

    public CommitmentCountdownController(
        ICommitmentCountdownPresenter presenter,
        ICommitmentCountdownDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        _presenter = presenter;
        _diagnostics = diagnostics ?? NullCommitmentCountdownDiagnostics.Instance;
    }

    public void ObservePolicy(
        DesktopPolicyResult policy,
        TimeSpan monotonicNow)
    {
        try
        {
            TimeSpan safeNow = AdvanceMonotonicHighWater(monotonicNow);
            if (!CommitmentCountdownModel.HasSameGameObservationScope(_observedPolicy, policy)
                || policy.IsDegraded)
            {
                _hasRunningGame = false;
            }
            _observedPolicy = policy;
            _policyObservedAt = safeNow;
            RefreshTarget(safeNow);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // This controller is visual-only. A countdown failure must never change
            // lock, process, browser, network, or policy availability behavior.
        }
    }

    public void ObserveGamePresence(bool hasRunningGame, TimeSpan monotonicNow)
    {
        try
        {
            TimeSpan safeNow = AdvanceMonotonicHighWater(monotonicNow);
            _hasRunningGame = hasRunningGame;
            RefreshTarget(safeNow);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // A read-only game observation can affect presentation only.
        }
    }

    private void RefreshTarget(TimeSpan safeNow)
    {
        if (_observedPolicy is null)
        {
            RetryHideIfNeeded();
            return;
        }
        CommitmentCountdownTarget? candidate =
            CommitmentCountdownModel.Resolve(
                _observedPolicy,
                _hasRunningGame,
                safeNow - _policyObservedAt);
        if (candidate is null)
        {
            SetHidden();
            return;
        }

        TimeSpan candidateDeadline = AddSaturated(
            safeNow,
            candidate.InitialRemaining);
        if (!string.Equals(
                _guardedIdentity,
                candidate.Identity,
                StringComparison.Ordinal))
        {
            _guardedIdentity = candidate.Identity;
            _monotonicDeadline = candidateDeadline;
            _lastApplied = null;
            _lastApplySucceeded = false;
        }
        else
        {
            if (candidateDeadline < _monotonicDeadline)
            {
                _monotonicDeadline = candidateDeadline;
            }
        }

        _target = candidate;
        _hideRetryPending = false;
        PublishVisible(safeNow);
    }

    public void Tick(TimeSpan monotonicNow)
    {
        try
        {
            TimeSpan safeNow = AdvanceMonotonicHighWater(monotonicNow);
            RefreshTarget(safeNow);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Visual-only and retried on the next runtime tick.
        }
    }

    public void Clear()
    {
        try
        {
            _observedPolicy = null;
            _hasRunningGame = false;
            SetHidden();
            _guardedIdentity = null;
            _monotonicDeadline = TimeSpan.Zero;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Application shutdown and fail-open transitions remain best-effort.
        }
    }

    private void PublishVisible(TimeSpan safeNow)
    {
        CommitmentCountdownTarget? target = _target;
        if (target is null)
        {
            return;
        }

        CommitmentCountdownPresentation? presentation =
            CommitmentCountdownModel.Project(
                target,
                _monotonicDeadline - safeNow);
        if (presentation is null)
        {
            SetHidden();
            return;
        }

        if (_lastApplySucceeded && presentation == _lastApplied)
        {
            return;
        }

        _presenterMayBeVisible = true;
        try
        {
            _presenter.Apply(presentation);
            _lastApplied = presentation;
            _lastApplySucceeded = true;
            _visualFailureActive = false;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _lastApplySucceeded = false;
            ReportVisualFailureOnce(
                CommitmentCountdownVisualOperation.Update,
                exception);
        }
    }

    private void SetHidden()
    {
        bool shouldHide = _presenterMayBeVisible
            || _hideRetryPending;
        _target = null;
        _lastApplied = null;
        _lastApplySucceeded = true;
        if (!shouldHide)
        {
            return;
        }

        try
        {
            _presenter.Apply(null);
            _presenterMayBeVisible = false;
            _hideRetryPending = false;
            _visualFailureActive = false;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _presenterMayBeVisible = true;
            _hideRetryPending = true;
            ReportVisualFailureOnce(
                CommitmentCountdownVisualOperation.Hide,
                exception);
        }
    }

    private void RetryHideIfNeeded()
    {
        if (!_hideRetryPending)
        {
            return;
        }

        try
        {
            _presenter.Apply(null);
            _presenterMayBeVisible = false;
            _hideRetryPending = false;
            _visualFailureActive = false;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Retry on the next tick.
            ReportVisualFailureOnce(
                CommitmentCountdownVisualOperation.Hide,
                exception);
        }
    }

    private TimeSpan AdvanceMonotonicHighWater(TimeSpan monotonicNow)
    {
        if (monotonicNow < TimeSpan.Zero)
        {
            monotonicNow = TimeSpan.Zero;
        }

        if (!_hasMonotonicSample || monotonicNow > _monotonicHighWater)
        {
            _monotonicHighWater = monotonicNow;
            _hasMonotonicSample = true;
        }

        return _monotonicHighWater;
    }

    private static TimeSpan AddSaturated(TimeSpan left, TimeSpan right)
    {
        if (right <= TimeSpan.Zero)
        {
            return left;
        }

        long available = TimeSpan.MaxValue.Ticks - left.Ticks;
        return right.Ticks > available
            ? TimeSpan.MaxValue
            : left + right;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;

    private void ReportVisualFailureOnce(
        CommitmentCountdownVisualOperation operation,
        Exception exception)
    {
        if (_visualFailureActive)
        {
            return;
        }

        _visualFailureActive = true;
        try
        {
            _diagnostics.RecordVisualFailure(operation, exception);
        }
        catch (Exception diagnosticException) when (IsRecoverable(diagnosticException))
        {
            // Diagnostics are strictly observational and never affect protection.
        }
    }

    private sealed class NullCommitmentCountdownDiagnostics :
        ICommitmentCountdownDiagnostics
    {
        internal static NullCommitmentCountdownDiagnostics Instance { get; } = new();

        private NullCommitmentCountdownDiagnostics()
        {
        }

        public void RecordVisualFailure(
            CommitmentCountdownVisualOperation operation,
            Exception exception)
        {
        }
    }
}
