using System.Runtime.InteropServices;

namespace NightGate.Desktop;

internal static class Win32MonitorDpiNative
{
    private static readonly nint PerMonitorAwareV2 = new(-4);
    private static readonly nint TopmostWindow = new(-1);
    private const int ExtendedStyleIndex = -20;
    private const long ExtendedTransparent = 0x00000020L;
    private const long ExtendedToolWindow = 0x00000080L;
    private const long ExtendedLayered = 0x00080000L;
    private const long ExtendedNoActivate = 0x08000000L;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoActivate = 0x0010;
    private const uint FrameChanged = 0x0020;
    private const uint NoOwnerZOrder = 0x0200;

    public static bool TryEnablePerMonitorV2()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (!IsCurrentThreadPerMonitorV2())
            {
                _ = SetProcessDpiAwarenessContext(PerMonitorAwareV2);
            }

            return IsCurrentThreadPerMonitorV2();
        }
        catch (Exception exception) when (exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            SEHException)
        {
            return false;
        }
    }

    public static bool IsCurrentThreadPerMonitorV2()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            nint context = GetThreadDpiAwarenessContext();
            return context != nint.Zero
                && AreDpiAwarenessContextsEqual(context, PerMonitorAwareV2);
        }
        catch (Exception exception) when (exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            SEHException)
        {
            return false;
        }
    }

    public static bool TryPlaceTopmostWindow(
        nint window,
        MonitorPixelBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        if (!OperatingSystem.IsWindows()
            || window == nint.Zero
            || bounds.Width <= 0
            || bounds.Height <= 0)
        {
            return false;
        }

        try
        {
            _ = checked(bounds.X + bounds.Width);
            _ = checked(bounds.Y + bounds.Height);
            if (!IsWindowPerMonitorV2(window)
                || !SetWindowPos(
                    window,
                    TopmostWindow,
                    bounds.X,
                    bounds.Y,
                    bounds.Width,
                    bounds.Height,
                    NoActivate | NoOwnerZOrder))
            {
                return false;
            }

            return GetWindowRect(window, out NativeRect actual)
                && actual.Left == bounds.X
                && actual.Top == bounds.Y
                && actual.Right == bounds.X + bounds.Width
                && actual.Bottom == bounds.Y + bounds.Height;
        }
        catch (Exception exception) when (exception is ArithmeticException or
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            SEHException)
        {
            return false;
        }
    }

    public static bool TryConfigurePassiveWindow(nint window)
    {
        if (!OperatingSystem.IsWindows() || window == nint.Zero)
        {
            return false;
        }

        try
        {
            Marshal.SetLastPInvokeError(0);
            nint current = GetWindowLongPtr(window, ExtendedStyleIndex);
            if (current == nint.Zero && Marshal.GetLastPInvokeError() != 0)
            {
                return false;
            }

            long passiveStyle = current.ToInt64()
                | ExtendedNoActivate
                | ExtendedToolWindow
                | ExtendedLayered
                | ExtendedTransparent;
            Marshal.SetLastPInvokeError(0);
            nint previous = SetWindowLongPtr(
                window,
                ExtendedStyleIndex,
                new nint(passiveStyle));
            if (previous == nint.Zero && Marshal.GetLastPInvokeError() != 0)
            {
                return false;
            }

            return SetWindowPos(
                window,
                TopmostWindow,
                0,
                0,
                0,
                0,
                NoSize | NoMove | NoActivate | FrameChanged | NoOwnerZOrder);
        }
        catch (Exception exception) when (exception is OverflowException or
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            SEHException)
        {
            return false;
        }
    }

    internal static bool IsPassiveWindow(nint window)
    {
        if (!OperatingSystem.IsWindows() || window == nint.Zero)
        {
            return false;
        }

        try
        {
            Marshal.SetLastPInvokeError(0);
            nint style = GetWindowLongPtr(window, ExtendedStyleIndex);
            if (style == nint.Zero && Marshal.GetLastPInvokeError() != 0)
            {
                return false;
            }

            long required = ExtendedNoActivate
                | ExtendedToolWindow
                | ExtendedLayered
                | ExtendedTransparent;
            return (style.ToInt64() & required) == required;
        }
        catch (Exception exception) when (exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            SEHException)
        {
            return false;
        }
    }

    internal static nint GetForegroundWindowHandle() =>
        OperatingSystem.IsWindows() ? GetForegroundWindow() : nint.Zero;

    internal static bool ReturnsTransparentHitTest(nint window) =>
        OperatingSystem.IsWindows()
        && window != nint.Zero
        && SendMessage(window, 0x0084, nint.Zero, nint.Zero) == new nint(-1);

    internal static nint FindWindowByCaption(string caption) =>
        OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(caption)
            ? FindWindow(null, caption)
            : nint.Zero;

    internal static nint GetWindowAtPoint(int x, int y) =>
        OperatingSystem.IsWindows()
            ? WindowFromPoint(new NativePoint(x, y))
            : nint.Zero;

    internal static bool TryGetWindowProcessId(nint window, out uint processId)
    {
        processId = 0;
        return OperatingSystem.IsWindows()
            && window != nint.Zero
            && GetWindowThreadProcessId(window, out processId) != 0;
    }

    internal static bool TryGetWindowPixelBounds(
        nint window,
        out MonitorPixelBounds bounds)
    {
        bounds = new(0, 0, 0, 0);
        if (!OperatingSystem.IsWindows()
            || window == nint.Zero
            || !GetWindowRect(window, out NativeRect rectangle))
        {
            return false;
        }

        try
        {
            int width = checked(rectangle.Right - rectangle.Left);
            int height = checked(rectangle.Bottom - rectangle.Top);
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            bounds = new(rectangle.Left, rectangle.Top, width, height);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static bool IsWindowPerMonitorV2(nint window)
    {
        nint context = GetWindowDpiAwarenessContext(window);
        return context != nint.Zero
            && AreDpiAwarenessContextsEqual(context, PerMonitorAwareV2);
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct NativeRect
    {
        [FieldOffset(0)] public int Left;
        [FieldOffset(4)] public int Top;
        [FieldOffset(8)] public int Right;
        [FieldOffset(12)] public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDpiAwarenessContext(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(
        nint window,
        int index,
        nint newValue);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(
        nint window,
        int message,
        nint wordParameter,
        nint longParameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint window,
        out uint processId);
}
