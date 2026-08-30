using System.Windows.Interop;

namespace NightGate.Desktop;

internal static class OverlayDpiRuntimeProbe
{
    private const int PopupWindow = unchecked((int)0x80000000);
    private const int NoActivate = 0x08000000;

    public static bool Run()
    {
        if (!OperatingSystem.IsWindows()
            || !Win32MonitorDpiNative.IsCurrentThreadPerMonitorV2())
        {
            return false;
        }

        try
        {
            HwndSourceParameters parameters = new("NightGateOverlayDpiProbe")
            {
                WindowStyle = PopupWindow,
                ExtendedWindowStyle = NoActivate,
                PositionX = -32000,
                PositionY = -32000,
                Width = 64,
                Height = 64,
            };
            using HwndSource source = new(parameters);
            MonitorPixelBounds target = new(-31000, -30000, 211, 127);
            return Win32MonitorDpiNative.IsWindowPerMonitorV2(source.Handle)
                && Win32MonitorDpiNative.TryPlaceTopmostWindow(source.Handle, target);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            return false;
        }
    }
}
