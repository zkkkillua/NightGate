namespace NightGate.Desktop.Tests;

public sealed class InstalledChromeBridgeProbeContractTests
{
    [Fact]
    public void ProbeChecksBothCurrentUserRegistryViewsAndLiveHeartbeatWithoutElevation()
    {
        string scriptPath = Path.Combine(
            FindRepositoryRoot(),
            "scripts",
            "Test-InstalledChromeBridge.ps1");
        string script = File.ReadAllText(scriptPath);

        Assert.Contains("RegistryHive]::CurrentUser", script, StringComparison.Ordinal);
        Assert.Contains("RegistryView]::Registry32", script, StringComparison.Ordinal);
        Assert.Contains("RegistryView]::Registry64", script, StringComparison.Ordinal);
        Assert.Contains("com.nightgate.host", script, StringComparison.Ordinal);
        Assert.Contains("NightGateService", script, StringComparison.Ordinal);
        Assert.Contains("getUserState", script, StringComparison.Ordinal);
        Assert.Contains("chromeProtection", script, StringComparison.Ordinal);
        Assert.Contains("IsInRole", script, StringComparison.Ordinal);
        Assert.DoesNotContain("RunAs", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SetValue", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CreateSubKey", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-", script, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "NightGate.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate NightGate.slnx.");
    }
}
