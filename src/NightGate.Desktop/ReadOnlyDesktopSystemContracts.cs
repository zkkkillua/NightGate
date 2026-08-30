namespace NightGate.Desktop;

public sealed class CurrentSessionChangedEventArgs : EventArgs
{
    public CurrentSessionChangedEventArgs(
        CurrentSessionEventKind kind,
        TimeSpan monotonicTimestamp)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (monotonicTimestamp < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(monotonicTimestamp));
        }

        Kind = kind;
        MonotonicTimestamp = monotonicTimestamp;
    }

    public CurrentSessionEventKind Kind { get; }

    public TimeSpan MonotonicTimestamp { get; }
}

public interface ICurrentSessionEventSource
{
    event EventHandler<CurrentSessionChangedEventArgs>? SessionChanged;
}

public enum DesktopPowerSource
{
    Unknown,
    Ac,
    Battery,
}

public sealed record SleepTimeoutSnapshot
{
    public SleepTimeoutSnapshot(
        DesktopPowerSource activeSource,
        TimeSpan acTimeout,
        TimeSpan batteryTimeout)
    {
        if (!Enum.IsDefined(activeSource))
        {
            throw new ArgumentOutOfRangeException(nameof(activeSource));
        }

        if (acTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(acTimeout));
        }

        if (batteryTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(batteryTimeout));
        }

        ActiveSource = activeSource;
        AcTimeout = acTimeout;
        BatteryTimeout = batteryTimeout;
    }

    public DesktopPowerSource ActiveSource { get; }

    public TimeSpan AcTimeout { get; }

    public TimeSpan BatteryTimeout { get; }
}

public interface ISleepTimeoutReader
{
    SleepTimeoutSnapshot? Read();
}
