using System.Xml.Linq;

namespace NightGate.Desktop.Tests;

public sealed class DesktopArchitectureTests
{
    [Fact]
    public void Desktop_IsX64WpfWinExeAndReferencesOnlyCoreAndProtocolProjects()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(
            root,
            "src",
            "NightGate.Desktop",
            "NightGate.Desktop.csproj");
        XDocument project = XDocument.Load(projectPath);
        string[] references = project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")!.Value.Replace('\\', '/'))
            .ToArray();

        Assert.Equal("net10.0-windows", Property(project, "TargetFramework"));
        Assert.Equal("WinExe", Property(project, "OutputType"));
        Assert.Equal("x64", Property(project, "PlatformTarget"));
        Assert.Equal("true", Property(project, "UseWPF"));
        Assert.Equal(
            ["../NightGate.Core/NightGate.Core.csproj", "../NightGate.Protocol/NightGate.Protocol.csproj"],
            references);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.DoesNotContain(references, reference => reference.Contains("Service", StringComparison.Ordinal));
    }

    [Fact]
    public void Desktop_DeclaresPerMonitorV2DpiAwareness()
    {
        string root = FindRepositoryRoot();
        string projectPath = Path.Combine(
            root,
            "src",
            "NightGate.Desktop",
            "NightGate.Desktop.csproj");
        XDocument project = XDocument.Load(projectPath);
        Assert.Equal("app.manifest", Property(project, "ApplicationManifest"));
        Assert.Equal("PerMonitorV2", Property(project, "ApplicationHighDpiMode"));
        string manifest = File.ReadAllText(Path.Combine(
            root,
            "src",
            "NightGate.Desktop",
            "app.manifest"));
        Assert.Contains("PerMonitorV2,PerMonitor", manifest, StringComparison.Ordinal);
        string startup = File.ReadAllText(Path.Combine(
            root,
            "src",
            "NightGate.Desktop",
            "App.xaml.cs"));
        Assert.Contains("TryEnablePerMonitorV2", startup, StringComparison.Ordinal);
    }

    [Fact]
    public void PureProcessPolicyAndOrchestration_RemainFreeOfNativeExecution()
    {
        string root = FindRepositoryRoot();
        string desktop = Path.Combine(root, "src", "NightGate.Desktop");
        string source = string.Join(
            "\n",
            new[]
            {
                "ProcessGateModel.cs",
                "ProcessGateReducer.cs",
                "ProcessGateCoordinatorModel.cs",
                "ProcessGateCoordinator.cs",
                "ProcessCatalogModel.cs",
                "ProcessSourceContinuity.cs",
            }
                .Where(name => File.Exists(Path.Combine(desktop, name)))
                .Select(name => Path.Combine(desktop, name))
                .Select(File.ReadAllText));
        string[] forbidden =
        [
            "NightGate.Service",
            "LockWorkStation",
            "TerminateProcess",
            ".Kill(",
            "System.Diagnostics.Process",
            "CreateToolhelp32Snapshot",
            "Process32First",
            "EnumProcesses",
            "ManagementObjectSearcher",
            "EventLogWatcher",
            "DllImport",
            "LibraryImport",
            "WM_CLOSE",
            "WaitForSingleObject",
            "PowerWrite",
            "PowerSetActiveScheme",
            "SetActiveScheme",
            "netsh",
            "shutdown.exe",
            "CurrentVersion\\Run",
            "TaskScheduler",
            "WTSRegisterSessionNotification",
            "NativeMessaging",
            "chrome.exe",
        ];

        foreach (string token in forbidden)
        {
            Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ProcessNativeSurface_IsRestrictedToReviewedBoundaryFiles()
    {
        string root = FindRepositoryRoot();
        string desktop = Path.Combine(root, "src", "NightGate.Desktop");
        Dictionary<string, string> sources = Directory
            .EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .ToDictionary(
                path => Path.GetRelativePath(desktop, path).Replace('\\', '/'),
                File.ReadAllText,
                StringComparer.OrdinalIgnoreCase);

        AssertTokenOnlyIn(
            sources,
             "DllImport",
             "Win32ProcessNative.cs",
             "Win32ExactProcessActionNative.cs",
             "Win32ProcessCatalogNative.cs",
             "Win32MonitorDpiNative.cs",
             "Win32WorkstationLockNative.cs",
             "WindowsCurrentInteractiveIdentityProvider.cs",
             "WindowsCurrentSessionEventSource.cs",
             "WindowsSleepTimeoutReader.cs");
        AssertTokenOnlyIn(
            sources,
            "LockWorkStation",
            "Win32WorkstationLockNative.cs");
        AssertTokenOnlyIn(
            sources,
            "CreateToolhelp32Snapshot",
            "Win32ProcessCatalogNative.cs");
        AssertTokenOnlyIn(
            sources,
            "Process32FirstW",
            "Win32ProcessCatalogNative.cs");
        AssertTokenOnlyIn(
            sources,
            "Process32NextW",
            "Win32ProcessCatalogNative.cs");
        AssertTokenOnlyIn(
            sources,
            "PostMessageW",
            "Win32ExactProcessActionNative.cs");
        AssertTokenOnlyIn(
            sources,
            "TerminateProcess",
            "Win32ExactProcessActionNative.cs");
        AssertTokenOnlyIn(
            sources,
            "WaitForSingleObject",
            "Win32ProcessNative.cs",
            "DesktopSingleInstanceCoordinator.cs");
        AssertTokenOnlyIn(
            sources,
            "ProcessIdToSessionId",
            "WindowsCurrentInteractiveIdentityProvider.cs");
        AssertTokenOnlyIn(
            sources,
            "WTSRegisterSessionNotification",
            "WindowsCurrentSessionEventSource.cs");
        AssertTokenOnlyIn(
            sources,
            "PowerReadACValueIndex",
            "WindowsSleepTimeoutReader.cs");
        AssertTokenOnlyIn(
            sources,
            "PowerReadDCValueIndex",
            "WindowsSleepTimeoutReader.cs");
        AssertTokenOnlyIn(
            sources,
            "shutdown.exe",
            "LegacyShutdownTaskAdapter.cs");
        AssertTokenOnlyIn(
            sources,
            "Registry.",
            "ChromeNativeHostRegistration.cs");

        string allSource = string.Join("\n", sources.Values);
        string[] forbidden =
        [
            "PROCESS_ALL_ACCESS",
            "AdjustTokenPrivileges",
            "SeDebugPrivilege",
            "System.Diagnostics.Process",
            ".Kill(",
            "taskkill",
            "ManagementObjectSearcher",
            "CreateRemoteThread",
            "WriteProcessMemory",
            "SendInput",
            "keybd_event",
            "PowerWrite",
            "PowerSetActiveScheme",
            "SetActiveScheme",
            "SetSuspendState",
            "ExitWindows",
            "netsh",
            "CurrentVersion\\Run",
        ];

        foreach (string token in forbidden)
        {
            Assert.DoesNotContain(token, allSource, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ServiceProject_RemainsNonWpfAndDoesNotReferenceDesktop()
    {
        string root = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(
            root,
            "src",
            "NightGate.Service",
            "NightGate.Service.csproj"));

        Assert.Null(project.Descendants("UseWPF").SingleOrDefault());
        Assert.DoesNotContain(
            project.Descendants("ProjectReference"),
            element => element.Attribute("Include")!.Value.Contains(
                "Desktop",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PureProcessReducer_LineageResolutionDoesNotUseRecursiveSelfCalls()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "NightGate.Desktop",
            "ProcessGateReducer.cs"));

        Assert.DoesNotContain("return Cache(ResolveHelper(", source, StringComparison.Ordinal);
    }

    private static string Property(XDocument document, string name) =>
        Assert.Single(document.Descendants(name)).Value;

    private static bool IsBuildOutput(string path) =>
        path.Contains(
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
        || path.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

    private static void AssertTokenOnlyIn(
        IReadOnlyDictionary<string, string> sources,
        string token,
        params string[] allowedFiles)
    {
        HashSet<string> allowed = new(allowedFiles, StringComparer.OrdinalIgnoreCase);
        string[] unexpected = sources
            .Where(pair => pair.Value.Contains(token, StringComparison.OrdinalIgnoreCase)
                && !allowed.Contains(pair.Key))
            .Select(pair => pair.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            unexpected.Length == 0,
            $"'{token}' occurred outside its reviewed boundary: {string.Join(", ", unexpected)}");
    }

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
