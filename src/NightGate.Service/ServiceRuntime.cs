using NightGate.Core;

namespace NightGate.Service;

public sealed record ServiceRuntimeStatus(
    bool EnforcementEnabled,
    bool IsDegraded,
    string? DegradationCode,
    PolicySnapshot? Policy = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset? NextProtectedStartAtUtc { get; init; }

    public static ServiceRuntimeStatus Starting { get; } = new(
        false,
        true,
        "service-starting");

    public static ServiceRuntimeStatus Degraded(string code) => new(false, true, code);
}

public interface IServiceStatusPublisher
{
    ValueTask PublishAsync(
        ServiceRuntimeStatus status,
        CancellationToken cancellationToken = default);
}

public interface IServiceStatusReader
{
    ServiceRuntimeStatus Current { get; }
}

public readonly record struct ServiceRuntimeStatusSnapshot(
    long Revision,
    ServiceRuntimeStatus Status);

public interface IServiceStatusRecovery
{
    ServiceRuntimeStatusSnapshot ReadSnapshot();

    ValueTask<bool> TryRecoverAsync(
        long expectedRevision,
        ServiceRuntimeStatus status,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryServiceStatus :
    IServiceStatusPublisher,
    IServiceStatusReader,
    IServiceStatusRecovery
{
    private readonly object _sync = new();
    private ServiceRuntimeStatus _current = ServiceRuntimeStatus.Starting;
    private long _revision;

    public ServiceRuntimeStatus Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public ValueTask PublishAsync(
        ServiceRuntimeStatus status,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(status);
        lock (_sync)
        {
            status = StabilizePolicyRevision(_current, status);
            if (_current.IsDegraded
                && !status.IsDegraded
                && !IsSuccessfulMaintenanceStatus(status))
            {
                _current = _current with { Policy = status.Policy };
            }
            else
            {
                _current = status;
            }

            _revision = checked(_revision + 1);
        }

        return ValueTask.CompletedTask;
    }

    public ServiceRuntimeStatusSnapshot ReadSnapshot()
    {
        lock (_sync)
        {
            return new(_revision, _current);
        }
    }

    public ValueTask<bool> TryRecoverAsync(
        long expectedRevision,
        ServiceRuntimeStatus status,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(status);
        if (!IsSuccessfulMaintenanceStatus(status))
        {
            return ValueTask.FromResult(false);
        }

        lock (_sync)
        {
            if (_revision != expectedRevision)
            {
                return ValueTask.FromResult(false);
            }

            _current = StabilizePolicyRevision(_current, status);
            _revision = checked(_revision + 1);
            return ValueTask.FromResult(true);
        }
    }

    private static bool IsSuccessfulMaintenanceStatus(ServiceRuntimeStatus status) =>
        status.EnforcementEnabled
        && !status.IsDegraded
        && status.DegradationCode is null
        && status.Policy is
        {
            EnforcementEnabled: true,
            IsDegraded: false,
        };

    private static ServiceRuntimeStatus StabilizePolicyRevision(
        ServiceRuntimeStatus current,
        ServiceRuntimeStatus candidate)
    {
        if (candidate.Policy is not { } nextPolicy)
        {
            return candidate;
        }

        PolicySnapshot? currentPolicy = current.Policy;
        if (currentPolicy is null)
        {
            return candidate;
        }

        long revision = currentPolicy.HasEquivalentEnforcementTo(nextPolicy)
            ? currentPolicy.Revision
            : Math.Max(nextPolicy.Revision, checked(currentPolicy.Revision + 1));
        return candidate with
        {
            Policy = nextPolicy with { Revision = revision },
        };
    }
}

public interface ISystemUptimeSource
{
    TimeSpan GetUptime();
}

public sealed class EnvironmentSystemUptimeSource : ISystemUptimeSource
{
    public TimeSpan GetUptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);
}

public sealed class SystemClock(
    IBootSessionIdProvider bootSessionIdProvider,
    ISystemUptimeSource uptimeSource) : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public ClockObservation Observe()
    {
        Guid? bootSessionId = bootSessionIdProvider.Current;
        if (bootSessionId is null || bootSessionId == Guid.Empty)
        {
            throw new IOException("boot-session-unavailable");
        }

        return new(DateTimeOffset.UtcNow, uptimeSource.GetUptime(), bootSessionId);
    }
}
