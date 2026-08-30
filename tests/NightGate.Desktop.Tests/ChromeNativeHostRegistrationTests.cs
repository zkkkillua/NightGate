using System.Text.Json;
using Microsoft.Win32;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class ChromeNativeHostRegistrationTests
{
    [Fact]
    public void MissingCurrentUserRegistration_IsRepairedFromInstalledPayload()
    {
        using InstalledChromeBridgeFixture fixture = new();
        RecordingRegistry registry = new();
        ChromeNativeHostRegistration registration = new(
            fixture.DesktopDirectory,
            registry);

        bool registered = registration.TryEnsureRegistered();

        Assert.True(registered);
        Assert.Equal(
            fixture.ManifestPath,
            registry.ManifestPaths[RegistryView.Registry32]);
        Assert.Equal(
            fixture.ManifestPath,
            registry.ManifestPaths[RegistryView.Registry64]);
        Assert.Equal(2, registry.WriteCount);
    }

    [Fact]
    public void CorrectCurrentUserRegistration_IsLeftUntouched()
    {
        using InstalledChromeBridgeFixture fixture = new();
        RecordingRegistry registry = new();
        registry.ManifestPaths[RegistryView.Registry32] =
            fixture.ManifestPath.ToUpperInvariant();
        registry.ManifestPaths[RegistryView.Registry64] = fixture.ManifestPath;
        ChromeNativeHostRegistration registration = new(
            fixture.DesktopDirectory,
            registry);

        bool registered = registration.TryEnsureRegistered();

        Assert.True(registered);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public void StaleRegistry32Value_IsRepairedEvenWhenRegistry64IsCorrect()
    {
        using InstalledChromeBridgeFixture fixture = new();
        RecordingRegistry registry = new();
        registry.ManifestPaths[RegistryView.Registry32] = @"C:\old\host.json";
        registry.ManifestPaths[RegistryView.Registry64] = fixture.ManifestPath;
        ChromeNativeHostRegistration registration = new(
            fixture.DesktopDirectory,
            registry);

        bool registered = registration.TryEnsureRegistered();

        Assert.True(registered);
        Assert.Equal(fixture.ManifestPath, registry.ManifestPaths[RegistryView.Registry32]);
        Assert.Equal(1, registry.WriteCount);
    }

    [Fact]
    public void InvalidInstalledPayload_FailsOpenWithoutWritingCurrentUserRegistry()
    {
        using InstalledChromeBridgeFixture fixture = new();
        File.WriteAllText(
            fixture.ManifestPath,
            JsonSerializer.Serialize(new
            {
                name = ChromeNativeHostRegistration.HostName,
                path = Path.Combine(fixture.InstallDirectory, "wrong-host.exe"),
                type = "stdio",
                allowed_origins = new[] { ChromeNativeHostRegistration.ExtensionOrigin },
            }));
        RecordingRegistry registry = new();
        ChromeNativeHostRegistration registration = new(
            fixture.DesktopDirectory,
            registry);

        bool registered = registration.TryEnsureRegistered();

        Assert.False(registered);
        Assert.Empty(registry.ManifestPaths);
        Assert.Equal(0, registry.WriteCount);
    }

    [Fact]
    public void RegistryFailure_NeverPreventsDesktopStartup()
    {
        using InstalledChromeBridgeFixture fixture = new();
        RecordingRegistry registry = new()
        {
            FailingView = RegistryView.Registry32,
        };
        ChromeNativeHostRegistration registration = new(
            fixture.DesktopDirectory,
            registry);

        bool registered = registration.TryEnsureRegistered();

        Assert.False(registered);
        Assert.Equal(fixture.ManifestPath, registry.ManifestPaths[RegistryView.Registry64]);
    }

    private sealed class RecordingRegistry : ICurrentUserChromeNativeHostRegistry
    {
        public Dictionary<RegistryView, string> ManifestPaths { get; } = [];

        public int WriteCount { get; private set; }

        public RegistryView? FailingView { get; init; }

        public string? ReadManifestPath(RegistryView view) =>
            ManifestPaths.GetValueOrDefault(view);

        public void WriteManifestPath(RegistryView view, string manifestPath)
        {
            if (FailingView == view)
            {
                throw new UnauthorizedAccessException("simulated registry policy");
            }

            ManifestPaths[view] = manifestPath;
            WriteCount++;
        }
    }

    private sealed class InstalledChromeBridgeFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"nightgate-chrome-registration-{Guid.NewGuid():N}");

        public InstalledChromeBridgeFixture()
        {
            InstallDirectory = Path.Combine(_root, "NightGate");
            DesktopDirectory = Path.Combine(InstallDirectory, "apps", "Desktop");
            string nativeHostDirectory = Path.Combine(
                InstallDirectory,
                "apps",
                "NativeHost");
            string manifestDirectory = Path.Combine(InstallDirectory, "native-host");
            Directory.CreateDirectory(DesktopDirectory);
            Directory.CreateDirectory(nativeHostDirectory);
            Directory.CreateDirectory(manifestDirectory);
            string hostPath = Path.Combine(
                nativeHostDirectory,
                "NightGate.NativeHost.exe");
            File.WriteAllBytes(hostPath, [0x4d, 0x5a]);
            ManifestPath = Path.Combine(
                manifestDirectory,
                "com.nightgate.host.json");
            File.WriteAllText(
                ManifestPath,
                JsonSerializer.Serialize(new
                {
                    name = ChromeNativeHostRegistration.HostName,
                    description = "NightGate Chrome native bridge",
                    path = hostPath,
                    type = "stdio",
                    allowed_origins = new[]
                    {
                        ChromeNativeHostRegistration.ExtensionOrigin,
                    },
                }));
        }

        public string InstallDirectory { get; }

        public string DesktopDirectory { get; }

        public string ManifestPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
