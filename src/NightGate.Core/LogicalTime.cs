namespace NightGate.Core;

public readonly record struct LogicalTimeResult(
    DateTimeOffset UtcNow,
    TimeSpan? Uptime,
    Guid? BootSessionId,
    bool BootSessionReset,
    bool IsStaleObservation = false);

public static class LogicalTime
{
    public static LogicalTimeResult Advance(
        NightState state,
        ClockObservation current)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (current.Uptime is { } currentUptime && currentUptime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(current), "System uptime cannot be negative.");
        }

        if (current.BootSessionId == Guid.Empty)
        {
            throw new ArgumentException("Boot session ID cannot be empty.", nameof(current));
        }

        if (current.BootSessionId is not null && current.Uptime is null)
        {
            throw new ArgumentException(
                "A boot session ID requires a system uptime observation.",
                nameof(current));
        }

        DateTimeOffset logicalUtc = Later(state.LastObservedUtc, current.UtcNow);
        bool hasAnyMonotonicAnchor = state.LastObservedUptime is not null
            || state.LastObservedBootSessionId is not null
            || current.Uptime is not null
            || current.BootSessionId is not null;
        bool matchingBootSession = state.LastObservedBootSessionId is { } persistedBootSessionId
            && current.BootSessionId is { } observedBootSessionId
            && persistedBootSessionId == observedBootSessionId;
        bool staleSameBoot = matchingBootSession
            && state.LastObservedUptime is { } stalePersistedUptime
            && current.Uptime is { } staleObservedUptime
            && staleObservedUptime < stalePersistedUptime;
        if (staleSameBoot)
        {
            return new(
                state.LastObservedUtc,
                state.LastObservedUptime,
                state.LastObservedBootSessionId,
                false,
                true);
        }

        bool trustedSameBoot = matchingBootSession
            && state.LastObservedUptime is { } persistedUptime
            && current.Uptime is { } observedUptime
            && observedUptime >= persistedUptime;
        bool bootSessionReset = hasAnyMonotonicAnchor && !trustedSameBoot;
        if (trustedSameBoot)
        {
            TimeSpan currentElapsed = current.Uptime!.Value;
            TimeSpan persistedElapsed = state.LastObservedUptime!.Value;
            logicalUtc = Later(
                logicalUtc,
                AddElapsed(state.LastObservedUtc, currentElapsed - persistedElapsed));
        }

        if (bootSessionReset
            && current.UtcNow < state.LastObservedUtc
            && state.ActiveOverride is { } activeOverride)
        {
            logicalUtc = Later(logicalUtc, activeOverride.EndsAtUtc);
        }

        return new(
            logicalUtc,
            current.Uptime,
            current.BootSessionId,
            bootSessionReset);
    }

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static DateTimeOffset AddElapsed(DateTimeOffset value, TimeSpan elapsed)
    {
        try
        {
            return value.Add(elapsed);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue;
        }
    }
}
