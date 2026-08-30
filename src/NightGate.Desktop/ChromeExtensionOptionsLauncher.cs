using System.Diagnostics;

namespace NightGate.Desktop;

internal interface IChromeExtensionOptionsLauncher
{
    bool TryOpen();
}

internal interface IExternalProcessStarter
{
    void Start(ProcessStartInfo startInfo);
}

internal sealed class WindowsExternalProcessStarter : IExternalProcessStarter
{
    public void Start(ProcessStartInfo startInfo)
    {
        using Process? process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Chrome did not start.");
        }
    }
}

internal sealed class ChromeExtensionOptionsLauncher :
    IChromeExtensionOptionsLauncher
{
    internal const string ExtensionId = "eefgemhlhbdodhlgjmicnoifhclhdgmm";
    internal const string OptionsAddress =
        $"chrome-extension://{ExtensionId}/options.html";

    private readonly IExternalProcessStarter _processStarter;

    public ChromeExtensionOptionsLauncher(IExternalProcessStarter processStarter)
    {
        ArgumentNullException.ThrowIfNull(processStarter);
        _processStarter = processStarter;
    }

    public bool TryOpen()
    {
        ProcessStartInfo startInfo = new("chrome.exe")
        {
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add(OptionsAddress);

        try
        {
            _processStarter.Start(startInfo);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            // Chrome protection is optional. A launch failure must leave the
            // desktop app usable and the manual setup path remains visible.
            return false;
        }
    }
}
