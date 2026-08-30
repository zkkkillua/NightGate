using System.Diagnostics;
using System.IO;
using System.Windows.Interop;

namespace NightGate.Desktop;

internal static class CommitmentCountdownRuntimeProbe
{
    private const int PopupWindow = unchecked((int)0x80000000);
    private const int VisibleWindow = 0x10000000;
    private const int NoActivate = 0x08000000;
    private const int ToolWindow = 0x00000080;

    public static bool Run()
    {
        if (!OperatingSystem.IsWindows()
            || !Win32MonitorDpiNative.IsCurrentThreadPerMonitorV2())
        {
            return false;
        }

        try
        {
            nint foregroundBefore = Win32MonitorDpiNative.GetForegroundWindowHandle();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool reduceMotion = false;
            CommitmentCountdownWindow window = new(() => (reduceMotion, false));
            try
            {
                if (window.IsRadianceAnimating || window.HasRadiancePreferenceSubscription)
                {
                    return false;
                }

                foreach (CommitmentCountdownKind kind in new[]
                    { CommitmentCountdownKind.EntertainmentActive, CommitmentCountdownKind.GameGraceToLock })
                {
                    window.UpdatePresentation(new(
                        kind,
                        TimeSpan.FromMinutes(20),
                        now.AddMinutes(20),
                        false), now);
                    if (!window.IsVisible)
                    {
                        window.Show();
                    }

                    if (!window.IsRadianceAnimating || !window.HasRadiancePreferenceSubscription)
                    {
                        return false;
                    }

                    TimeSpan start = kind == CommitmentCountdownKind.GameGraceToLock
                        ? TimeSpan.FromSeconds(24) : TimeSpan.Zero;
                    if (!window.TryConfigureAndPlace(new ProbeMonitorLayoutProvider(), start, "probe"))
                    {
                        return false;
                    }

                    nint handle = new WindowInteropHelper(window).Handle;
                    if (!Win32MonitorDpiNative.TryGetWindowPixelBounds(handle, out MonitorPixelBounds first)
                        || !window.TryConfigureAndPlace(new ProbeMonitorLayoutProvider(), start.Add(TimeSpan.FromSeconds(11)), "probe")
                        || !Win32MonitorDpiNative.TryGetWindowPixelBounds(handle, out MonitorPixelBounds stable)
                        || first != stable
                        || !window.TryConfigureAndPlace(new ProbeMonitorLayoutProvider(), start.Add(TimeSpan.FromSeconds(12)), "probe")
                        || !Win32MonitorDpiNative.TryGetWindowPixelBounds(handle, out MonitorPixelBounds moved)
                        || first == moved
                        || !Win32MonitorDpiNative.IsPassiveWindow(handle)
                        || !Win32MonitorDpiNative.ReturnsTransparentHitTest(handle)
                        || !VerifyCrossProcessHitTesting(handle)
                        || Win32MonitorDpiNative.GetForegroundWindowHandle() != foregroundBefore)
                    {
                        return false;
                    }
                }

                reduceMotion = true;
                window.RefreshRadiancePreferences();
                if (window.IsRadianceAnimating || window.RadianceFrameForTesting?.Particles.Count != 0)
                {
                    return false;
                }

                reduceMotion = false;
                window.RefreshRadiancePreferences();
                if (!window.IsRadianceAnimating)
                {
                    return false;
                }

                window.Hide();
                if (window.IsRadianceAnimating || window.HasRadiancePreferenceSubscription)
                {
                    return false;
                }

                window.Show();
                if (!window.IsRadianceAnimating || !window.HasRadiancePreferenceSubscription
                    || Win32MonitorDpiNative.GetForegroundWindowHandle() != foregroundBefore)
                {
                    return false;
                }

                window.Close();
                if (window.IsRadianceAnimating || window.HasRadiancePreferenceSubscription)
                {
                    return false;
                }

                return true;
            }
            finally
            {
                window.Close();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            return false;
        }
    }

    public static bool RunCrossProcessTarget(string token)
    {
        if (!OperatingSystem.IsWindows()
            || string.IsNullOrWhiteSpace(token)
            || token.Length > 64
            || token.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            return false;
        }

        try
        {
            using EventWaitHandle ready = EventWaitHandle.OpenExisting(
                ReadyEventName(token));
            using EventWaitHandle done = EventWaitHandle.OpenExisting(
                DoneEventName(token));
            HwndSourceParameters parameters = new(TargetWindowName(token))
            {
                WindowStyle = PopupWindow | VisibleWindow,
                ExtendedWindowStyle = NoActivate | ToolWindow,
                PositionX = -32000,
                PositionY = -32000,
                Width = 1920,
                Height = 1080,
            };
            using HwndSource source = new(parameters);
            ready.Set();
            return done.WaitOne(TimeSpan.FromSeconds(8));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            return false;
        }
    }

    private static bool VerifyCrossProcessHitTesting(nint overlayWindow)
    {
        string token = Guid.NewGuid().ToString("N");
        using EventWaitHandle ready = new(
            false,
            EventResetMode.ManualReset,
            ReadyEventName(token));
        using EventWaitHandle done = new(
            false,
            EventResetMode.ManualReset,
            DoneEventName(token));
        string executable = Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executable)
            || !File.Exists(executable))
        {
            return false;
        }

        if (!Win32MonitorDpiNative.TryGetWindowPixelBounds(
                overlayWindow,
                out MonitorPixelBounds overlayBounds))
        {
            return false;
        }

        ProcessStartInfo start = new(
            executable,
            $"--internal-countdown-hit-target={token}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using Process targetProcess = Process.Start(start)
            ?? throw new InvalidOperationException(
                "The cross-process countdown hit-test target did not start.");
        try
        {
            if (!ready.WaitOne(TimeSpan.FromSeconds(4)))
            {
                return false;
            }

            nint targetWindow = Win32MonitorDpiNative.FindWindowByCaption(
                TargetWindowName(token));
            if (targetWindow == nint.Zero)
            {
                return false;
            }

            int pointX = overlayBounds.X + overlayBounds.Width / 2;
            int pointY = overlayBounds.Y + overlayBounds.Height / 2;
            nint hitWindow = Win32MonitorDpiNative.GetWindowAtPoint(
                pointX,
                pointY);
            bool hasProcess = Win32MonitorDpiNative.TryGetWindowProcessId(
                hitWindow,
                out uint hitProcessId);
            return hitWindow == targetWindow
                && hasProcess
                && hitProcessId == (uint)targetProcess.Id;
        }
        finally
        {
            done.Set();
            _ = targetProcess.WaitForExit(2_000);
        }
    }

    private static string ReadyEventName(string token) =>
        $"Local\\NightGate.CountdownProbe.Ready.{token}";

    private static string DoneEventName(string token) =>
        $"Local\\NightGate.CountdownProbe.Done.{token}";

    private static string TargetWindowName(string token) =>
        $"NightGateCountdownTarget-{token}";

    private sealed class ProbeMonitorLayoutProvider : IMonitorLayoutProvider
    {
        public IReadOnlyList<MonitorDescriptor> ReadMonitors() =>
        [
            new(
                "probe",
                new MonitorPixelBounds(-32000, -32000, 1920, 1080),
                true,
                new MonitorPixelBounds(-32000, -32000, 1920, 1080)),
        ];
    }
}
