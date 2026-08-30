using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace NightGate.Desktop;

internal sealed record WpfScreenSnapshot(
    string Id,
    MonitorPixelBounds PixelBounds,
    bool IsPrimary,
    MonitorPixelBounds? WorkingAreaPixelBounds = null);

public sealed class WpfMonitorLayoutProvider : IMonitorLayoutProvider
{
    private readonly Func<IReadOnlyList<WpfScreenSnapshot>> _readScreens;

    public WpfMonitorLayoutProvider()
        : this(ReadScreens)
    {
    }

    internal WpfMonitorLayoutProvider(
        Func<IReadOnlyList<WpfScreenSnapshot>> readScreens)
    {
        ArgumentNullException.ThrowIfNull(readScreens);
        _readScreens = readScreens;
    }

    public IReadOnlyList<MonitorDescriptor> ReadMonitors()
    {
        IReadOnlyList<WpfScreenSnapshot> screens = _readScreens();
        if (screens.Count == 0)
        {
            return [];
        }

        return screens
            .Select(screen => new MonitorDescriptor(
                screen.Id,
                screen.PixelBounds,
                screen.IsPrimary,
                screen.WorkingAreaPixelBounds))
            .ToArray();
    }

    private static IReadOnlyList<WpfScreenSnapshot> ReadScreens() =>
        Forms.Screen.AllScreens
            .Select(screen => new WpfScreenSnapshot(
                screen.DeviceName,
                new MonitorPixelBounds(
                    screen.Bounds.X,
                    screen.Bounds.Y,
                    screen.Bounds.Width,
                    screen.Bounds.Height),
                screen.Primary,
                new MonitorPixelBounds(
                    screen.WorkingArea.X,
                    screen.WorkingArea.Y,
                    screen.WorkingArea.Width,
                    screen.WorkingArea.Height)))
            .ToArray();

}

public sealed class WpfRestrictedOverlayPresenter : IRestrictedOverlayPresenter
{
    private readonly IMonitorLayoutProvider _monitorLayoutProvider;
    private readonly DashboardViewModel? _dashboard;
    private readonly List<RestrictedOverlayWindow> _windows = [];

    public WpfRestrictedOverlayPresenter()
        : this(new WpfMonitorLayoutProvider(), null)
    {
    }

    public WpfRestrictedOverlayPresenter(DashboardViewModel dashboard)
        : this(new WpfMonitorLayoutProvider(), dashboard)
    {
    }

    public WpfRestrictedOverlayPresenter(
        IMonitorLayoutProvider monitorLayoutProvider,
        DashboardViewModel? dashboard = null)
    {
        ArgumentNullException.ThrowIfNull(monitorLayoutProvider);
        _monitorLayoutProvider = monitorLayoutProvider;
        _dashboard = dashboard;
    }

    public void Show(RestrictedOverlayPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        InvokeOnUiThread(() => ShowCore(presentation));
    }

    public void Update(RestrictedOverlayPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        InvokeOnUiThread(() =>
        {
            foreach (RestrictedOverlayWindow window in _windows.ToArray())
            {
                window.UpdatePresentation(presentation);
            }
        });
    }

    public void Hide() => InvokeOnUiThread(HideCore);

    private void ShowCore(RestrictedOverlayPresentation presentation)
    {
        HideCore();
        IReadOnlyList<OverlayWindowPlacement> placements = OverlayLayoutPlanner.Plan(
            _monitorLayoutProvider.ReadMonitors());
        List<RestrictedOverlayWindow> staged = [];
        try
        {
            foreach (OverlayWindowPlacement placement in placements)
            {
                RestrictedOverlayWindow window = new();
                window.Configure(presentation, placement, _dashboard);
                staged.Add(window);
                window.Show();
                if (!window.TryPlaceInPhysicalPixels(placement))
                {
                    throw new InvalidOperationException(
                        "The restricted overlay could not be placed in physical screen coordinates.");
                }
            }

            _windows.AddRange(staged);
        }
        catch
        {
            CloseAll(staged);
            throw;
        }
    }

    private void HideCore()
    {
        RestrictedOverlayWindow[] snapshot = _windows.ToArray();
        _windows.Clear();
        CloseAll(snapshot);
    }

    private static void CloseAll(IEnumerable<RestrictedOverlayWindow> windows)
    {
        foreach (RestrictedOverlayWindow window in windows)
        {
            try
            {
                window.Close();
            }
            catch (Exception)
            {
                // Overlay cleanup is visual-only and remains fail-open.
            }
        }
    }

    private static void InvokeOnUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null
            || dispatcher.HasShutdownStarted
            || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action, DispatcherPriority.Normal);
    }
}
