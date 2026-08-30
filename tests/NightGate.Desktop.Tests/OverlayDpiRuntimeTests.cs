using System.Diagnostics;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class OverlayDpiRuntimeTests
{
    [Theory]
    [InlineData("--internal-dpi-awareness-probe")]
    [InlineData("--internal-overlay-position-probe")]
    [InlineData("--internal-countdown-passive-probe")]
    public void DesktopIsolatedProcess_VerifiesRuntimeDpiContractWithoutShowingUi(
        string probeArgument)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string executable = Path.ChangeExtension(
            typeof(App).Assembly.Location,
            ".exe");
        Assert.True(File.Exists(executable), $"Desktop apphost was not found: {executable}");
        ProcessStartInfo start = new(executable, probeArgument)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The isolated DPI probe did not start.");

        Assert.True(process.WaitForExit(10_000), "The isolated DPI probe did not exit.");
        Assert.Equal(0, process.ExitCode);
    }
}
