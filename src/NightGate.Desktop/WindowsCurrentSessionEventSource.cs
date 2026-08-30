using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NightGate.Desktop;

internal sealed class RawCurrentSessionEventArgs(
    int reason,
    int sessionId) : EventArgs
{
    internal int Reason { get; } = reason;

    internal int SessionId { get; } = sessionId;
}

internal interface ICurrentSessionNotificationNative : IDisposable
{
    event EventHandler<RawCurrentSessionEventArgs>? SessionChanged;
}

public sealed class WindowsCurrentSessionEventSource :
    ICurrentSessionEventSource,
    IDisposable
{
    private const int SessionLogon = 0x5;
    private const int SessionLogoff = 0x6;
    private const int SessionLock = 0x7;
    private const int SessionUnlock = 0x8;
    private readonly int _expectedSessionId;
    private readonly IDesktopRuntimeClock _clock;
    private readonly ICurrentSessionNotificationNative _native;
    private bool _disposed;

    public WindowsCurrentSessionEventSource(
        nint windowHandle,
        int expectedSessionId,
        IDesktopRuntimeClock clock)
        : this(
            expectedSessionId,
            clock,
            new WpfCurrentSessionNotificationNative(windowHandle))
    {
    }

    internal WindowsCurrentSessionEventSource(
        int expectedSessionId,
        IDesktopRuntimeClock clock,
        ICurrentSessionNotificationNative native)
    {
        if (expectedSessionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSessionId));
        }

        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(native);
        _expectedSessionId = expectedSessionId;
        _clock = clock;
        _native = native;
        _native.SessionChanged += OnNativeSessionChanged;
    }

    public event EventHandler<CurrentSessionChangedEventArgs>? SessionChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _native.SessionChanged -= OnNativeSessionChanged;
        _native.Dispose();
        SessionChanged = null;
    }

    private void OnNativeSessionChanged(
        object? sender,
        RawCurrentSessionEventArgs args)
    {
        if (_disposed
            || args.SessionId != _expectedSessionId
            || !TryMap(args.Reason, out CurrentSessionEventKind kind))
        {
            return;
        }

        CurrentSessionChangedEventArgs forwarded = new(kind, _clock.MonotonicNow);
        Delegate[] handlers = SessionChanged?.GetInvocationList() ?? [];
        foreach (EventHandler<CurrentSessionChangedEventArgs> handler in handlers.Cast<
                     EventHandler<CurrentSessionChangedEventArgs>>())
        {
            try
            {
                handler(this, forwarded);
            }
            catch (Exception)
            {
                // A consumer fault must not escape the native window-message callback.
            }
        }
    }

    private static bool TryMap(int reason, out CurrentSessionEventKind kind)
    {
        switch (reason)
        {
            case SessionLock:
                kind = CurrentSessionEventKind.Locked;
                return true;
            case SessionUnlock:
                kind = CurrentSessionEventKind.Unlocked;
                return true;
            case SessionLogon:
                kind = CurrentSessionEventKind.Logon;
                return true;
            case SessionLogoff:
                kind = CurrentSessionEventKind.Logoff;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}

internal interface IWpfCurrentSessionNotificationPlatform
{
    bool CheckAccess();

    void AddHook(HwndSourceHook hook);

    void RemoveHook(HwndSourceHook hook);

    bool Register(nint windowHandle);

    bool Unregister(nint windowHandle);
}

internal sealed class WpfCurrentSessionNotificationNative :
    ICurrentSessionNotificationNative
{
    private const int SessionChangeMessage = 0x02B1;
    private readonly nint _windowHandle;
    private readonly IWpfCurrentSessionNotificationPlatform _platform;
    private bool _registered;
    private bool _disposed;

    internal WpfCurrentSessionNotificationNative(nint windowHandle)
        : this(windowHandle, new WpfCurrentSessionNotificationPlatform(windowHandle))
    {
    }

    internal WpfCurrentSessionNotificationNative(
        nint windowHandle,
        IWpfCurrentSessionNotificationPlatform platform)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(windowHandle));
        }

        ArgumentNullException.ThrowIfNull(platform);
        if (!platform.CheckAccess())
        {
            throw new InvalidOperationException(
                "Session notifications must be created on the window dispatcher.");
        }

        _windowHandle = windowHandle;
        _platform = platform;
        _platform.AddHook(WindowProcedure);
        try
        {
            if (!_platform.Register(_windowHandle))
            {
                throw new InvalidOperationException(
                    "Current-session notification registration failed.");
            }

            _registered = true;
        }
        catch
        {
            _platform.RemoveHook(WindowProcedure);
            throw;
        }
    }

    public event EventHandler<RawCurrentSessionEventArgs>? SessionChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_platform.CheckAccess())
        {
            throw new InvalidOperationException(
                "Session notifications must be disposed on the window dispatcher.");
        }

        _disposed = true;
        try
        {
            _platform.RemoveHook(WindowProcedure);
        }
        finally
        {
            try
            {
                if (_registered)
                {
                    _ = _platform.Unregister(_windowHandle);
                    _registered = false;
                }
            }
            finally
            {
                SessionChanged = null;
            }
        }
    }

    private nint WindowProcedure(
        nint window,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (!_disposed
            && message == SessionChangeMessage
            && wParam.ToInt64() is >= int.MinValue and <= int.MaxValue
            && lParam.ToInt64() is >= 0 and <= int.MaxValue)
        {
            SessionChanged?.Invoke(
                this,
                new((int)wParam.ToInt64(), (int)lParam.ToInt64()));
        }

        return nint.Zero;
    }
}

internal sealed class WpfCurrentSessionNotificationPlatform :
    IWpfCurrentSessionNotificationPlatform
{
    private const uint NotifyForThisSession = 0;
    private readonly HwndSource _source;

    internal WpfCurrentSessionNotificationPlatform(nint windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero)
        {
            throw new PlatformNotSupportedException(
                "A Windows window handle is required for session notifications.");
        }

        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("The WPF window source is unavailable.");
    }

    public bool CheckAccess() => _source.Dispatcher.CheckAccess();

    public void AddHook(HwndSourceHook hook) => _source.AddHook(hook);

    public void RemoveHook(HwndSourceHook hook) => _source.RemoveHook(hook);

    public bool Register(nint windowHandle) =>
        NativeMethods.WTSRegisterSessionNotification(
            windowHandle,
            NotifyForThisSession);

    public bool Unregister(nint windowHandle) =>
        NativeMethods.WTSUnRegisterSessionNotification(windowHandle);

    private static class NativeMethods
    {
        [DllImport(
            "wtsapi32.dll",
            EntryPoint = "WTSRegisterSessionNotification",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSRegisterSessionNotification(
            nint window,
            uint flags);

        [DllImport(
            "wtsapi32.dll",
            EntryPoint = "WTSUnRegisterSessionNotification",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSUnRegisterSessionNotification(nint window);
    }
}
