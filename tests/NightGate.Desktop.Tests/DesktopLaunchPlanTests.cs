using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DesktopLaunchPlanTests
{
    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, false)]
    public void Create_DistinguishesInteractiveLaunchFromBackgroundLogon(
        bool background,
        bool isPrimary,
        bool expectedSignal,
        bool expectedOpen)
    {
        string[] arguments = background
            ? [DesktopLaunchPlan.BackgroundFlag]
            : [];

        DesktopLaunchPlan plan = DesktopLaunchPlan.Create(arguments, isPrimary);

        Assert.Equal(background, plan.IsBackground);
        Assert.Equal(expectedSignal, plan.ShouldSignalExistingInstance);
        Assert.Equal(expectedOpen, plan.ShouldOpenDashboard);
    }

    [Fact]
    public void App_UsesTheLaunchPlanForExistingAndNewInstances()
    {
        string app = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "App.xaml.cs"));

        Assert.Contains("DesktopLaunchPlan.Create(e.Args", app, StringComparison.Ordinal);
        Assert.Contains("launch.ShouldSignalExistingInstance", app, StringComparison.Ordinal);
        Assert.Contains("launch.ShouldOpenDashboard", app, StringComparison.Ordinal);
    }

    [Fact]
    public void LogonRunCommands_ExplicitlyRequestBackgroundMode()
    {
        string msiFinalize = File.ReadAllText(Repo(
            "installer", "Finalize-NightGateMsi.ps1"));
        string zipInstaller = File.ReadAllText(Repo(
            "installer", "Install-NightGate.ps1"));

        Assert.Contains(
            "$desktopCommand = \"`\"$desktopExe`\" --background\"",
            msiFinalize,
            StringComparison.Ordinal);
        Assert.Contains(
            "`\"$desktopExe`\" --background",
            zipInstaller,
            StringComparison.Ordinal);
    }

    private static string Repo(params string[] segments)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "NightGate.slnx")))
        {
            current = current.Parent;
        }

        return Path.Combine(
            current?.FullName
                ?? throw new DirectoryNotFoundException(
                    "Could not locate NightGate.slnx."),
            Path.Combine(segments));
    }
}
