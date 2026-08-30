using System.Diagnostics;

namespace NightGate.Desktop;

public interface IDesktopRuntimeClock
{
    DateTimeOffset Now { get; }

    TimeSpan MonotonicNow { get; }

    ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default);
}

public sealed class StopwatchDesktopRuntimeClock :
    IDesktopRuntimeClock,
    IProcessGateMonotonicDelay
{
    public DateTimeOffset Now => DateTimeOffset.Now;

    public TimeSpan MonotonicNow =>
        Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) =>
        Stopwatch.GetElapsedTime(startingTimestamp);

    public ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        return new(Task.Delay(delay, cancellationToken));
    }
}

internal sealed class GuidProcessObserverEpochFactory : IProcessObserverEpochFactory
{
    public string CreateEpoch() => Guid.NewGuid().ToString("N");
}
