using NightGate.Core;

namespace NightGate.Service;

public static class PolicyMaintenanceTiming
{
    public static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);

    public static TimeSpan GetNextDelay(
        ServiceRuntimeStatus status,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(status);
        EnsureUtc(nowUtc, nameof(nowUtc));
        if (!IsHealthyPolicy(status, out PolicySnapshot? policy))
        {
            return WatchdogInterval;
        }

        if (IsRefreshDue(status, nowUtc))
        {
            return TimeSpan.Zero;
        }

        TimeSpan delay = WatchdogInterval;
        foreach (DateTimeOffset boundary in Boundaries(status, policy!))
        {
            TimeSpan untilBoundary = boundary.ToUniversalTime() - nowUtc;
            if (untilBoundary > TimeSpan.Zero && untilBoundary < delay)
            {
                delay = untilBoundary;
            }
        }

        return delay;
    }

    public static bool IsRefreshDue(
        ServiceRuntimeStatus status,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(status);
        EnsureUtc(nowUtc, nameof(nowUtc));

        if (!IsHealthyPolicy(status, out PolicySnapshot? policy))
        {
            return true;
        }

        DateTimeOffset evaluatedAtUtc = policy!.EvaluatedAt.ToUniversalTime();
        if (nowUtc < evaluatedAtUtc)
        {
            // A wall-clock rollback must not reopen or rewind an already evaluated
            // policy. The regular watchdog will keep observing logical time.
            return false;
        }

        return Boundaries(status, policy).Any(boundary =>
        {
            DateTimeOffset boundaryUtc = boundary.ToUniversalTime();
            return boundaryUtc > evaluatedAtUtc && boundaryUtc <= nowUtc;
        });
    }

    private static IEnumerable<DateTimeOffset> Boundaries(
        ServiceRuntimeStatus status,
        PolicySnapshot policy)
    {
        if (status.NextProtectedStartAtUtc is { } nextProtectedStartAtUtc)
        {
            yield return nextProtectedStartAtUtc;
        }

        yield return policy.Window.ProtectedStart;
        foreach (AppRule rule in policy.AppRules)
        {
            if (rule is { IsConfigured: true, Category: AppRuleCategory.Game })
            {
                yield return ScheduleEvaluator.CalculateLastStart(
                    policy.Window.Lock,
                    rule);
            }
        }

        yield return policy.Window.LastStart;
        yield return policy.Window.LastStart.AddMinutes(1);
        yield return policy.Window.Lock;
        yield return policy.Window.Wake;
        if (policy.ActiveOverride is { } activeOverride)
        {
            yield return activeOverride.StartsAtUtc;
            yield return activeOverride.EndsAtUtc;
        }
    }

    private static bool IsHealthyPolicy(
        ServiceRuntimeStatus status,
        out PolicySnapshot? policy)
    {
        policy = status.Policy;
        return status is
        {
            EnforcementEnabled: true,
            IsDegraded: false,
            Policy: { EnforcementEnabled: true, IsDegraded: false },
        };
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Policy maintenance timing requires a nondefault UTC timestamp.",
                parameterName);
        }
    }
}

public interface IPolicyMaintenanceScheduler
{
    void MarkDirty();

    ValueTask RefreshAsync(
        bool force,
        CancellationToken cancellationToken = default);
}

public sealed class PolicyMaintenanceScheduler(
    IPolicyMaintenanceIteration iteration,
    IServiceStatusReader statusReader,
    IClock clock) : IPolicyMaintenanceScheduler, IDisposable
{
    private static readonly object InitialGeneration = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private object _dirtyGeneration = InitialGeneration;
    private object _completedDirtyGeneration = InitialGeneration;

    public void MarkDirty() =>
        Interlocked.Exchange(ref _dirtyGeneration, new object());

    public async ValueTask RefreshAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            object refreshGeneration = Volatile.Read(ref _dirtyGeneration);
            bool isDirty = !ReferenceEquals(
                refreshGeneration,
                Volatile.Read(ref _completedDirtyGeneration));
            if (!force
                && !isDirty
                && !PolicyMaintenanceTiming.IsRefreshDue(
                    statusReader.Current,
                    clock.UtcNow.ToUniversalTime()))
            {
                return;
            }

            await iteration.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _completedDirtyGeneration, refreshGeneration);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

public sealed class BoundaryAwarePolicyMaintenanceDelay(
    IServiceStatusReader statusReader,
    IClock clock) : IPolicyMaintenanceDelay
{
    public async ValueTask DelayAsync(
        CancellationToken cancellationToken = default)
    {
        TimeSpan delay = PolicyMaintenanceTiming.GetNextDelay(
            statusReader.Current,
            clock.UtcNow.ToUniversalTime());
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}
