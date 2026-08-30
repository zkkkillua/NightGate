namespace NightGate.Core;

public readonly record struct ClockObservation(
    DateTimeOffset UtcNow,
    TimeSpan? Uptime = null,
    Guid? BootSessionId = null);

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    ClockObservation Observe() => new(UtcNow);
}
