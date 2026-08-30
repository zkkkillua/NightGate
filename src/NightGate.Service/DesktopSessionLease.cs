namespace NightGate.Service;

public enum DesktopSessionLeaseState
{
    Active,
    Missing,
    Expired,
    Retired,
    Invalid,
}

public readonly record struct DesktopSessionLeaseObservation(
    DesktopSessionLeaseState State,
    string? SessionId)
{
    public bool IsActive => State == DesktopSessionLeaseState.Active;
}

public sealed class DesktopSessionLease(TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(80);

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    // Retired capabilities remain invalid for this service process's lifetime. Evicting
    // one would let a delayed request from an older desktop process become authoritative.
    private readonly HashSet<string> _retiredSessionIds = new(StringComparer.Ordinal);
    private string? _activeSessionId;
    private long _renewedAtTimestamp;

    public DesktopSessionLeaseObservation Renew(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
        {
            return new(DesktopSessionLeaseState.Invalid, sessionId);
        }

        lock (_sync)
        {
            if (_retiredSessionIds.Contains(sessionId))
            {
                return new(DesktopSessionLeaseState.Retired, sessionId);
            }

            if (_activeSessionId is not null
                && !string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
            {
                Retire(_activeSessionId);
            }

            _activeSessionId = sessionId;
            _renewedAtTimestamp = _timeProvider.GetTimestamp();
            return new(DesktopSessionLeaseState.Active, sessionId);
        }
    }

    public bool End(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
        {
            return false;
        }

        lock (_sync)
        {
            if (!string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
            {
                return false;
            }

            Retire(sessionId);
            _activeSessionId = null;
            return true;
        }
    }

    public DesktopSessionLeaseObservation Observe(string? expectedSessionId = null)
    {
        if (expectedSessionId is not null && !IsValidSessionId(expectedSessionId))
        {
            return new(DesktopSessionLeaseState.Invalid, expectedSessionId);
        }

        lock (_sync)
        {
            if (expectedSessionId is not null
                && _retiredSessionIds.Contains(expectedSessionId))
            {
                return new(DesktopSessionLeaseState.Retired, expectedSessionId);
            }

            if (_activeSessionId is null)
            {
                return new(DesktopSessionLeaseState.Missing, null);
            }

            if (expectedSessionId is not null
                && !string.Equals(_activeSessionId, expectedSessionId, StringComparison.Ordinal))
            {
                return new(DesktopSessionLeaseState.Retired, expectedSessionId);
            }

            long observedTimestamp = _timeProvider.GetTimestamp();
            TimeSpan age = _timeProvider.GetElapsedTime(
                _renewedAtTimestamp,
                observedTimestamp);
            DesktopSessionLeaseState state = age >= TimeSpan.Zero && age < Lifetime
                ? DesktopSessionLeaseState.Active
                : DesktopSessionLeaseState.Expired;
            return new(state, _activeSessionId);
        }
    }

    public static bool IsValidSessionId(string? value) =>
        value is { Length: 32 }
        && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private void Retire(string sessionId)
    {
        _retiredSessionIds.Add(sessionId);
    }
}
