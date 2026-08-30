using System.Windows;

namespace NightGate.Desktop;

public partial class App : System.Windows.Application
{
    private readonly bool _isPerMonitorV2;
    private DesktopProductionComposition? _composition;
    private DesktopSingleInstanceCoordinator? _singleInstance;

    public App()
    {
        // This runs before InitializeComponent/Run create any WPF HWND.
        _isPerMonitorV2 = Win32MonitorDpiNative.TryEnablePerMonitorV2();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        const string countdownTargetPrefix = "--internal-countdown-hit-target=";
        string? countdownTarget = e.Args.FirstOrDefault(argument =>
            argument.StartsWith(countdownTargetPrefix, StringComparison.Ordinal));
        if (countdownTarget is not null)
        {
            string token = countdownTarget[countdownTargetPrefix.Length..];
            Shutdown(CommitmentCountdownRuntimeProbe.RunCrossProcessTarget(token) ? 0 : 1);
            return;
        }
        if (e.Args.Contains("--internal-dpi-awareness-probe", StringComparer.Ordinal))
        {
            Shutdown(_isPerMonitorV2 ? 0 : 1);
            return;
        }
        if (e.Args.Contains("--internal-overlay-position-probe", StringComparer.Ordinal))
        {
            Shutdown(_isPerMonitorV2 && OverlayDpiRuntimeProbe.Run() ? 0 : 1);
            return;
        }
        if (e.Args.Contains("--internal-countdown-passive-probe", StringComparer.Ordinal))
        {
            Shutdown(_isPerMonitorV2 && CommitmentCountdownRuntimeProbe.Run() ? 0 : 1);
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (LegacyTaskElevationEntryPoint.TryRun(e.Args, out int helperExitCode))
        {
            Shutdown(helperExitCode);
            return;
        }

        bool isPrimary = true;
        try
        {
            _singleInstance = DesktopSingleInstanceCoordinator.CreateForCurrentUser();
            isPrimary = _singleInstance.IsPrimary;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            // If the OS single-instance primitives are unavailable, stay fail-open.
            _singleInstance = null;
        }

        DesktopLaunchPlan launch = DesktopLaunchPlan.Create(e.Args, isPrimary);
        if (!isPrimary)
        {
            if (launch.ShouldSignalExistingInstance)
            {
                _singleInstance?.SignalExistingInstance();
            }
            _singleInstance?.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        _composition = DesktopProductionComposition.Create(this);
        MainWindow = _composition.Dashboard;
        try
        {
            _singleInstance?.StartListening(() => _composition?.Tray.OpenDashboard());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            // The primary instance remains usable even if activation forwarding fails.
        }
        if (launch.ShouldOpenDashboard)
        {
            _composition.Tray.OpenDashboard();
        }
        _ = StartAsync(_composition);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DesktopProductionComposition? composition = _composition;
        _composition = null;
        DesktopSingleInstanceCoordinator? singleInstance = _singleInstance;
        _singleInstance = null;
        if (composition is not null)
        {
            try
            {
                composition.CompleteApplicationExit();
            }
            catch (Exception)
            {
                // Final UI resource release cannot prevent application exit.
            }
        }

        singleInstance?.Dispose();

        base.OnExit(e);
    }

    private static async Task StartAsync(DesktopProductionComposition composition)
    {
        try
        {
            await composition.StartAsync();
        }
        catch (Exception)
        {
            // The tray remains usable while all enforcement stays fail-open.
        }
    }
}
