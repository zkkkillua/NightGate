using System.Windows;
using System.Windows.Interop;

namespace NightGate.Desktop;

public sealed class DesktopProductionComposition : IAsyncDisposable
{
    private readonly NightGateDesktopClient _client;
    private readonly DesktopApplicationRuntime _runtime;
    private readonly DesktopUserExperienceRuntime _experienceRuntime;
    private readonly LegacyShutdownTaskEvidenceRuntime _legacyTaskEvidenceRuntime;
    private readonly DesktopPrivacyEventSink _eventSink;
    private readonly ChromeNativeHostRegistration _chromeNativeHostRegistration;
    private readonly UserExperienceViewModel _experience;
    private readonly object _lifecycle = new();
    private Task? _startTask;
    private Task? _stopTask;
    private int _applicationExitCompleted;

    private DesktopProductionComposition(
        System.Windows.Application application,
        MainWindow dashboard,
        NightGateDesktopClient client,
        DesktopApplicationRuntime runtime,
        DesktopUserExperienceRuntime experienceRuntime,
        LegacyShutdownTaskEvidenceRuntime legacyTaskEvidenceRuntime,
        UserExperienceViewModel experience,
        DesktopPrivacyEventSink eventSink,
        ChromeNativeHostRegistration chromeNativeHostRegistration)
    {
        Dashboard = dashboard;
        _client = client;
        _runtime = runtime;
        _experienceRuntime = experienceRuntime;
        _legacyTaskEvidenceRuntime = legacyTaskEvidenceRuntime;
        _experience = experience;
        _eventSink = eventSink;
        _chromeNativeHostRegistration = chromeNativeHostRegistration;
        Tray = new TrayApplicationShell(dashboard, application, BeforeUserExitAsync);
        _experience.NoticeRaised += ExperienceNoticeRaised;
        _experienceRuntime.FirstUseDashboardRequested +=
            ExperienceFirstUseDashboardRequested;
    }

    public MainWindow Dashboard { get; }

    public TrayApplicationShell Tray { get; }

    public static DesktopProductionComposition Create(
        System.Windows.Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        NamedPipeNightGateTransport transport = new NamedPipeNightGateTransport();
        string desktopSessionId = Guid.NewGuid().ToString("N");
        NightGateDesktopClient client = new NightGateDesktopClient(
            transport,
            desktopSessionId: desktopSessionId);
        // All three supported active-night overrides and process termination share this
        // in-process boundary. Direct same-SID pipe traffic is a deliberate bypass outside
        // the supported threat model. Ordinary rule edits can affect the current night only
        // before 22:30 while Free; restricted-time edits are scheduled for the next night.
        CutoffPipelineBarrier cutoffPipelineBarrier = new CutoffPipelineBarrier();
        LegacyShutdownTaskAdapter legacyTaskAdapter = new();
        DesktopClientLegacyMigrationService legacyMigrationService = new(client);
        UserExperienceViewModel experience = new(
            new DesktopClientUserExperienceGateway(client),
            new LegacyTaskMigrationCoordinator(
                legacyTaskAdapter,
                legacyMigrationService,
                new WindowsLegacyTaskElevationService()),
            new WindowsGameDiscovery(),
            new ChromeExtensionOptionsLauncher(
                new WindowsExternalProcessStarter()));
        DashboardViewModel dashboardModel = new(
            new DesktopClientOverrideGateway(client, cutoffPipelineBarrier),
            experience);
        MainWindow dashboard = new(dashboardModel);
        StopwatchDesktopRuntimeClock clock = new();
        WpfDesktopUiDispatcher dispatcher = new(dashboard.Dispatcher);
        WindowsCurrentInteractiveIdentityProvider identityProvider = new();
        (ICurrentSessionEventSource sessionEvents, IDisposable sessionLifetime) =
            CreateSessionSource(dashboard, identityProvider, clock);

        DesktopPrivacyEventSink eventSink = new(client);
        LockSessionController lockController = new(
            new Win32WorkstationLocker(),
            new WpfRestrictedOverlayPresenter(dashboardModel),
            eventSink);
        DesktopPolicyWitnessSource policySource = new(client);
        PipeProcessGateEnvelopeStore envelopeStore = new(transport);
        PipeProcessSourceContinuityStore continuityStore = new(transport);
        DurableProcessSourceContinuity continuity = new(
            continuityStore,
            new GuidProcessObserverEpochFactory());
        Win32ProcessCatalog catalog = new(continuity);
        ProcessGateCoordinator coordinator = new(
            envelopeStore,
            policySource,
            catalog,
            new Win32ExactProcessActionAdapter(),
            clock,
            eventSink,
            cutoffPipelineBarrier);
        ProcessGateCoordinatorRuntime processRuntime = new(coordinator);
        CommitmentCountdownController commitmentCountdown = new(
            new WpfCommitmentCountdownPresenter(
                new WpfMonitorLayoutProvider()),
            new TraceCommitmentCountdownDiagnostics());
        DesktopApplicationRuntime runtime = new(
            client,
            dashboardModel,
            lockController,
            dispatcher,
            new WindowsSleepTimeoutReader(),
            new DesktopPolicyPollModel(),
            clock,
            sessionEvents,
            identityProvider,
            processRuntime,
            sessionLifetime,
            commitmentCountdown,
            new WindowsRunningGameDetector());
        DesktopUserExperienceRuntime experienceRuntime = new(
            experience,
            dispatcher,
            clock);
        LegacyShutdownTaskEvidenceRuntime legacyTaskEvidenceRuntime =
            LegacyShutdownTaskEvidenceRuntime.CreateForCurrentUser(
                legacyMigrationService,
                legacyTaskAdapter,
                clock);
        return new(
            application,
            dashboard,
            client,
            runtime,
            experienceRuntime,
            legacyTaskEvidenceRuntime,
            experience,
            eventSink,
            new ChromeNativeHostRegistration(
                AppContext.BaseDirectory,
                new WindowsCurrentUserChromeNativeHostRegistry()));
    }

    public Task StartAsync()
    {
        lock (_lifecycle)
        {
            return _startTask ??= StartCoreAsync();
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycle)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _experienceRuntime.FirstUseDashboardRequested -=
            ExperienceFirstUseDashboardRequested;
        _experience.NoticeRaised -= ExperienceNoticeRaised;
        Tray.Dispose();
    }

    internal void CompleteApplicationExit()
    {
        if (Interlocked.Exchange(ref _applicationExitCompleted, 1) != 0)
        {
            return;
        }

        _ = StopAsync();
        Tray.Dispose();
    }

    private async Task StopCoreAsync()
    {
        try
        {
            await _runtime.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // A local shutdown fault cannot leave the service-side desktop lease active.
        }

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
            _ = await _client.EndDesktopSessionAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // The server lease also expires after a crash; exit must never wait indefinitely.
        }

        try
        {
            await _experienceRuntime.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Presentation cleanup cannot prevent the remaining fail-open shutdown path.
        }

        try
        {
            await _legacyTaskEvidenceRuntime.StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Read-only diagnostics cannot prevent event sinks from stopping.
        }

        await _eventSink.DisposeAsync().ConfigureAwait(false);
    }

    private async Task BeforeUserExitAsync()
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(1));
            await _eventSink.ReportDeliberateBypassAsync(timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Recording an intentional exit is best-effort and may never trap the user.
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    private async Task StartCoreAsync()
    {
        _chromeNativeHostRegistration.TryEnsureRegistered();
        await _runtime.StartAsync().ConfigureAwait(false);
        await _experienceRuntime.StartAsync().ConfigureAwait(false);
        try
        {
            await _legacyTaskEvidenceRuntime.StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            // Read-only diagnostics are not part of the protection availability gate.
        }
    }

    private void ExperienceFirstUseDashboardRequested(object? sender, EventArgs eventArgs)
    {
        if (_experience.IsAvailable
            && (!_experience.IsOnboardingComplete || _experience.HasPendingProgression))
        {
            Tray.OpenDashboard();
        }
    }

    private void ExperienceNoticeRaised(
        object? sender,
        DesktopNoticePresentation notice) => Tray.ShowNotice(notice);

    private static bool IsRecoverable(Exception exception) =>
        exception is not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;

    private static (ICurrentSessionEventSource Source, IDisposable Lifetime)
        CreateSessionSource(
            MainWindow dashboard,
            ICurrentInteractiveIdentityProvider identityProvider,
            IDesktopRuntimeClock clock)
    {
        try
        {
            CurrentInteractiveIdentity? identity = identityProvider.Read();
            if (identity is null)
            {
                return NullCurrentSessionEventSource.CreatePair();
            }

            nint handle = new WindowInteropHelper(dashboard).EnsureHandle();
            WindowsCurrentSessionEventSource source = new(
                handle,
                identity.SessionId,
                clock);
            return (source, source);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            return NullCurrentSessionEventSource.CreatePair();
        }
    }

    private sealed class NullCurrentSessionEventSource :
        ICurrentSessionEventSource,
        IDisposable
    {
        public event EventHandler<CurrentSessionChangedEventArgs>? SessionChanged
        {
            add { }
            remove { }
        }

        internal static (ICurrentSessionEventSource Source, IDisposable Lifetime)
            CreatePair()
        {
            NullCurrentSessionEventSource source = new();
            return (source, source);
        }

        public void Dispose()
        {
        }
    }
}
