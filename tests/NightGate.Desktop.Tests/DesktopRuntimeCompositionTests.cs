using System.Reflection;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DesktopRuntimeCompositionTests
{
    [Fact]
    public void ProductionComposition_CreatesOneSharedTransportClientAndAllRuntimePieces()
    {
        string desktop = DesktopSourceDirectory();
        string composition = File.ReadAllText(Path.Combine(
            desktop,
            "DesktopProductionComposition.cs"));

        Assert.Equal(1, Count(composition, "new NamedPipeNightGateTransport("));
        Assert.Equal(1, Count(composition, "new NightGateDesktopClient("));
        string[] required =
        [
            "DesktopPolicyWitnessSource",
            "PipeProcessGateEnvelopeStore",
            "PipeProcessSourceContinuityStore",
            "DurableProcessSourceContinuity",
            "Win32ProcessCatalog",
            "Win32ExactProcessActionAdapter",
            "ProcessGateCoordinator",
            "Win32WorkstationLocker",
            "WpfRestrictedOverlayPresenter",
            "LockSessionController",
            "WindowsSleepTimeoutReader",
            "DesktopApplicationRuntime",
            "TrayApplicationShell",
            "LegacyTaskMigrationCoordinator",
            "LegacyShutdownTaskAdapter",
            "DesktopClientLegacyMigrationService",
            "LegacyShutdownTaskEvidenceRuntime",
            "ChromeNativeHostRegistration",
        ];
        foreach (string token in required)
        {
            Assert.Contains(token, composition, StringComparison.Ordinal);
        }

        Assert.Contains(
            "LegacyShutdownTaskEvidenceRuntime.CreateForCurrentUser(",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "await _legacyTaskEvidenceRuntime.StartAsync()",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "await _legacyTaskEvidenceRuntime.StopAsync()",
            composition,
            StringComparison.Ordinal);
        Assert.Contains("Guid.NewGuid().ToString(\"N\")", composition, StringComparison.Ordinal);
        Assert.Contains("desktopSessionId", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitPath_StopsPolicyRenewalBeforeEndingDesktopSessionAndUsesBoundedFallback()
    {
        string composition = File.ReadAllText(Path.Combine(
            DesktopSourceDirectory(),
            "DesktopProductionComposition.cs"));
        string normalized = composition.Replace("\r\n", "\n", StringComparison.Ordinal);

        int stopRuntime = normalized.IndexOf(
            "await _runtime.StopAsync()",
            StringComparison.Ordinal);
        int endSession = normalized.IndexOf(
            "await _client.EndDesktopSessionAsync",
            StringComparison.Ordinal);
        int stopExperience = normalized.IndexOf(
            "await _experienceRuntime.StopAsync()",
            StringComparison.Ordinal);

        Assert.True(stopRuntime >= 0, "Policy polling must be stopped during exit.");
        Assert.True(endSession > stopRuntime, "The final policy poll must finish before ending its lease.");
        Assert.True(stopExperience > endSession, "The desktop session must end before non-enforcement cleanup.");
        Assert.Contains("TimeSpan.FromSeconds(1)", normalized, StringComparison.Ordinal);
        Assert.Contains("CompleteApplicationExit", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait()", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionComposition_SharesOneRealCutoffBarrierAcrossOverridesAndTermination()
    {
        string composition = File.ReadAllText(Path.Combine(
            DesktopSourceDirectory(),
            "DesktopProductionComposition.cs"));

        Assert.Equal(1, Count(composition, "new CutoffPipelineBarrier("));
        Assert.Contains(
            "new DesktopClientOverrideGateway(client, cutoffPipelineBarrier)",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "eventSink,\n            cutoffPipelineBarrier",
            composition.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.DoesNotContain("NoOpCutoffPipelineBarrier", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void AppStartsHiddenCompositionAndMainWindowCannotCreateASecondClient()
    {
        string desktop = DesktopSourceDirectory();
        string app = File.ReadAllText(Path.Combine(desktop, "App.xaml.cs"));
        string mainWindow = File.ReadAllText(Path.Combine(desktop, "MainWindow.xaml.cs"));
        string composition = File.ReadAllText(Path.Combine(
            desktop,
            "DesktopProductionComposition.cs"));

        Assert.Contains("DesktopProductionComposition.Create", app, StringComparison.Ordinal);
        Assert.Contains("DesktopSingleInstanceCoordinator.CreateForCurrentUser", app, StringComparison.Ordinal);
        Assert.Contains("SignalExistingInstance", app, StringComparison.Ordinal);
        Assert.Contains("StartListening", app, StringComparison.Ordinal);
        Assert.Contains("StartAsync", app, StringComparison.Ordinal);
        Assert.Contains("CompleteApplicationExit", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".Show()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("NightGateDesktopClient", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("NamedPipeNightGateTransport", mainWindow, StringComparison.Ordinal);
        ConstructorInfo constructor = Assert.Single(typeof(MainWindow).GetConstructors());
        Assert.Equal(typeof(DashboardViewModel), Assert.Single(constructor.GetParameters()).ParameterType);
        Assert.Contains("IsOnboardingComplete", composition, StringComparison.Ordinal);
        Assert.Contains("HasPendingProgression", composition, StringComparison.Ordinal);
        Assert.Contains("Tray.OpenDashboard", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitPath_AwaitsAsyncStopBeforeShutdownAndOnExitNeverBlocksDispatcher()
    {
        string desktop = DesktopSourceDirectory();
        string app = File.ReadAllText(Path.Combine(desktop, "App.xaml.cs"));
        string tray = File.ReadAllText(Path.Combine(desktop, "TrayApplicationShell.cs"));

        Assert.DoesNotContain("GetAwaiter().GetResult", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait()", app, StringComparison.Ordinal);
        Assert.Contains("CompleteApplicationExit", app, StringComparison.Ordinal);
        int stop = tray.IndexOf("await _beforeExitAsync()", StringComparison.Ordinal);
        int shutdown = tray.IndexOf("_application.Shutdown()", StringComparison.Ordinal);
        Assert.True(stop >= 0, "The tray exit path must await asynchronous runtime cleanup.");
        Assert.True(
            shutdown > stop,
            "Application shutdown must happen only after asynchronous runtime cleanup.");
    }

    [Fact]
    public void RuntimeSliceContainsNoExpansiveOrStateChangingSystemSurface()
    {
        string desktop = DesktopSourceDirectory();
        string source = string.Join(
            "\n",
            new[]
            {
                "DesktopApplicationRuntime.cs",
                "DesktopProductionComposition.cs",
                "DesktopPolicyWitnessSource.cs",
                "DesktopPrivacyEventSink.cs",
                "DesktopRuntimeClock.cs",
                "WindowsCurrentInteractiveIdentityProvider.cs",
                "WindowsCurrentSessionEventSource.cs",
                "WindowsSleepTimeoutReader.cs",
            }.Select(name => File.ReadAllText(Path.Combine(desktop, name))));
        string[] forbidden =
        [
            "PowerWrite",
            "PowerSetActiveScheme",
            "SetActiveScheme",
            "SetSuspendState",
            "ExitWindows",
            "shutdown.exe",
            "taskkill",
            ".Kill(",
            "TerminateJobObject",
            "Registry.",
            "CurrentVersion\\Run",
            "Sqlite",
            "state.db",
            "HttpClient",
            "WebClient",
            "NetworkInterface",
            "netsh",
        ];

        foreach (string token in forbidden)
        {
            Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static int Count(string source, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string DesktopSourceDirectory() => Path.Combine(
        FindRepositoryRoot(),
        "src",
        "NightGate.Desktop");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "NightGate.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate NightGate.slnx.");
    }
}
