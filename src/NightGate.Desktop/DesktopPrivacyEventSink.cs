namespace NightGate.Desktop;

public sealed class DesktopPrivacyEventSink :
    ILockWorkflowEventSink,
    IProcessGateOutcomeSink,
    IAsyncDisposable
{
    private readonly IDesktopPolicyClient _client;
    private readonly object _sync = new();
    private readonly HashSet<Task> _pending = [];
    private bool _disposed;

    public DesktopPrivacyEventSink(IDesktopPolicyClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public void ReportMissedLock(LockAttemptKind attemptKind)
    {
        if (!Enum.IsDefined(attemptKind))
        {
            return;
        }

        _ = StartTracked(PrivacySafeEventKind.MissedLock, CancellationToken.None);
    }

    public void ReportWorkstationLocked()
    {
        _ = StartTracked(PrivacySafeEventKind.WorkstationLocked, CancellationToken.None);
    }

    public ValueTask ReportDeliberateBypassAsync(
        CancellationToken cancellationToken = default) => new(
            StartTracked(PrivacySafeEventKind.DeliberateBypass, cancellationToken));

    public ValueTask PublishAsync(
        ProcessGateOrchestrationOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Kind is not (ProcessGateOutcomeKind.CloseRequested
            or ProcessGateOutcomeKind.NoEligibleWindow))
        {
            return ValueTask.CompletedTask;
        }

        return new(StartTracked(
            PrivacySafeEventKind.LateNewEntertainment,
            cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _pending.ToArray();
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private Task StartTracked(
        PrivacySafeEventKind kind,
        CancellationToken cancellationToken)
    {
        Task task;
        lock (_sync)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            task = RecordSafelyAsync(kind, cancellationToken);
            _pending.Add(task);
        }

        _ = RemoveWhenCompletedAsync(task);
        return task;
    }

    private async Task RecordSafelyAsync(
        PrivacySafeEventKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await _client.RecordEventAsync(kind, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Privacy-safe outcome reporting is observational and always fail-open.
        }
    }

    private async Task RemoveWhenCompletedAsync(Task task)
    {
        await task.ConfigureAwait(false);
        lock (_sync)
        {
            _pending.Remove(task);
        }
    }
}
