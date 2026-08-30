using System.Windows;
using System.Windows.Interop;

namespace NightGate.Desktop;

public partial class RestrictedOverlayWindow : Window
{
    public RestrictedOverlayWindow()
    {
        InitializeComponent();
    }

    internal void Configure(
        RestrictedOverlayPresentation presentation,
        OverlayWindowPlacement placement,
        DashboardViewModel? dashboard)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(placement);
        Opacity = 0;
        DataContext = dashboard;
        ExceptionPanel.Visibility = placement.ShowsExceptionControls
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdatePresentation(presentation);
    }

    internal bool TryPlaceInPhysicalPixels(OverlayWindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        nint handle = new WindowInteropHelper(this).Handle;
        if (!Win32MonitorDpiNative.TryPlaceTopmostWindow(
                handle,
                placement.PixelBounds))
        {
            return false;
        }

        Opacity = 1;
        return true;
    }

    internal void UpdatePresentation(RestrictedOverlayPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        int seconds = Math.Max(0, (int)Math.Ceiling(presentation.Remaining.TotalSeconds));
        RelockCountdown.Text = $"{seconds} 秒后重新锁屏";
        PhaseMessage.Text = presentation.Policy.Phase == DesktopNightPhase.CoolingOff
            ? "娱乐再用仍在冷静期，电脑会先保持锁屏保护。"
            : "电脑娱乐已到今晚的收尾时间。";
        ServiceDeadline.Text = FormatServiceDeadline(presentation.Policy);
    }

    private static string FormatServiceDeadline(DesktopPolicySnapshotDto policy)
    {
        if (policy.Phase == DesktopNightPhase.CoolingOff
            && policy.ActiveOverride is
            {
                Kind: DesktopOverrideKind.Entertainment,
            } active)
        {
            return $"娱乐窗口最早于 {active.StartsAtUtc.ToLocalTime():HH:mm} 开启（以本机服务为准）";
        }

        return $"今晚锁屏时间：{policy.Window.Lock.ToLocalTime():HH:mm}";
    }
}
