using System.Diagnostics;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class ChromeExtensionOptionsLauncherTests
{
    [Fact]
    public void Open_UsesChromeWithTheFixedExtensionOptionsAddress()
    {
        RecordingProcessStarter processStarter = new();
        ChromeExtensionOptionsLauncher launcher = new(processStarter);

        bool opened = launcher.TryOpen();

        Assert.True(opened);
        ProcessStartInfo startInfo = Assert.IsType<ProcessStartInfo>(
            processStarter.StartInfo);
        Assert.Equal("chrome.exe", startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal(
            "chrome-extension://eefgemhlhbdodhlgjmicnoifhclhdgmm/options.html",
            Assert.Single(startInfo.ArgumentList));
    }

    [Fact]
    public void Open_WhenChromeCannotBeStarted_FailsOpen()
    {
        ChromeExtensionOptionsLauncher launcher = new(
            new ThrowingProcessStarter());

        bool opened = launcher.TryOpen();

        Assert.False(opened);
    }

    private sealed class RecordingProcessStarter : IExternalProcessStarter
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public void Start(ProcessStartInfo startInfo) => StartInfo = startInfo;
    }

    private sealed class ThrowingProcessStarter : IExternalProcessStarter
    {
        public void Start(ProcessStartInfo startInfo) =>
            throw new System.ComponentModel.Win32Exception("chrome-missing");
    }
}
