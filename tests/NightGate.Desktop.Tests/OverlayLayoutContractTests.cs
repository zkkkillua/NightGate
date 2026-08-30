using System.Reflection;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class OverlayLayoutContractTests
{
    [Fact]
    public void DesktopAssembly_ExposesPureMonitorLayoutContracts()
    {
        Assembly desktop = typeof(DesktopPolicyResult).Assembly;

        Assert.NotNull(desktop.GetType("NightGate.Desktop.MonitorDescriptor"));
        Assert.NotNull(desktop.GetType("NightGate.Desktop.MonitorPixelBounds"));
        Assert.NotNull(desktop.GetType("NightGate.Desktop.OverlayWindowPlacement"));
        Assert.NotNull(desktop.GetType("NightGate.Desktop.OverlayLayoutPlanner"));
        Assert.NotNull(desktop.GetType("NightGate.Desktop.IMonitorLayoutProvider"));
        Assert.NotNull(desktop.GetType("NightGate.Desktop.ICurrentSessionEventSource"));
        Assert.NotNull(desktop.GetType("NightGate.Desktop.CurrentSessionChangedEventArgs"));
        Assert.NotNull(desktop.GetType("NightGate.Desktop.ISleepTimeoutReader"));
        Assert.NotNull(desktop.GetType("NightGate.Desktop.SleepTimeoutSnapshot"));
        Assert.NotNull(desktop.GetType("NightGate.Desktop.DesktopPowerSource"));
    }
}
