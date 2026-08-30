using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class OverlayLayoutPlannerTests
{
    [Fact]
    public void ProductionProvider_NativeSmokeReturnsValidCurrentTopology()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IReadOnlyList<MonitorDescriptor> monitors =
            new WpfMonitorLayoutProvider().ReadMonitors();

        Assert.NotEmpty(monitors);
        Assert.Single(monitors, monitor => monitor.IsPrimary);
        Assert.All(monitors, monitor => Assert.True(
            monitor.PixelBounds.Width > 0 && monitor.PixelBounds.Height > 0));
        Assert.Equal(monitors.Count, OverlayLayoutPlanner.Plan(monitors).Count);
    }

    [Fact]
    public void ProductionProvider_PreservesPhysicalScreenBounds()
    {
        WpfScreenSnapshot[] screens =
        [
            new(
                "primary",
                new MonitorPixelBounds(0, 0, 2560, 1440),
                true),
            new(
                "secondary",
                new MonitorPixelBounds(2560, 0, 1920, 1080),
                false),
        ];
        WpfMonitorLayoutProvider provider = new(() => screens);

        IReadOnlyList<MonitorDescriptor> monitors = provider.ReadMonitors();

        Assert.Equal(screens.Select(screen => screen.PixelBounds),
            monitors.Select(monitor => monitor.PixelBounds));
    }

    [Fact]
    public void Plan_PreservesRightUpperAndNegativePhysicalCoordinates()
    {
        MonitorDescriptor[] monitors =
        [
            new("primary", new MonitorPixelBounds(0, 0, 2560, 1440), true),
            new("right", new MonitorPixelBounds(2560, 180, 1920, 1080), false),
            new("upper", new MonitorPixelBounds(320, -2160, 3840, 2160), false),
            new("left", new MonitorPixelBounds(-1920, -120, 1920, 1080), false),
        ];

        IReadOnlyList<OverlayWindowPlacement> placements = OverlayLayoutPlanner.Plan(monitors);

        Assert.Equal(monitors.Select(monitor => monitor.PixelBounds),
            placements.Select(placement => placement.PixelBounds));
        Assert.True(placements[0].ShowsExceptionControls);
        Assert.All(placements.Skip(1), placement =>
            Assert.False(placement.ShowsExceptionControls));
    }

    [Fact]
    public void Plan_DoesNotCreateGapsBetweenAdjacentPhysicalBounds()
    {
        MonitorDescriptor[] monitors =
        [
            new(
                "primary",
                new MonitorPixelBounds(0, 0, 2560, 1440),
                true),
            new(
                "right",
                new MonitorPixelBounds(2560, 0, 1920, 1080),
                false),
        ];

        IReadOnlyList<OverlayWindowPlacement> placements = OverlayLayoutPlanner.Plan(monitors);

        Assert.Collection(
            placements,
            primary =>
            {
                Assert.Equal("primary", primary.MonitorId);
                Assert.Equal(new MonitorPixelBounds(0, 0, 2560, 1440), primary.PixelBounds);
                Assert.True(primary.ShowsExceptionControls);
            },
            right =>
            {
                Assert.Equal("right", right.MonitorId);
                Assert.Equal(placements[0].PixelBounds.X + placements[0].PixelBounds.Width,
                    right.PixelBounds.X);
                Assert.False(right.ShowsExceptionControls);
            });
    }

    [Fact]
    public void Plan_RejectsTopologyWithoutExactlyOnePrimaryMonitor()
    {
        MonitorDescriptor secondary = new(
            "secondary",
            new MonitorPixelBounds(0, 0, 1920, 1080),
            false);
        MonitorDescriptor primary = secondary with { Id = "primary", IsPrimary = true };

        Assert.Throws<ArgumentException>(() => OverlayLayoutPlanner.Plan([]));
        Assert.Throws<ArgumentException>(() => OverlayLayoutPlanner.Plan([secondary]));
        Assert.Throws<ArgumentException>(() => OverlayLayoutPlanner.Plan([primary, primary with { Id = "other" }]));
    }

    [Fact]
    public void Plan_RejectsInvalidOrDuplicateMonitorDescriptors()
    {
        MonitorDescriptor primary = new(
            "primary",
            new MonitorPixelBounds(0, 0, 1920, 1080),
            true);

        Assert.Throws<ArgumentException>(() => OverlayLayoutPlanner.Plan([primary with { Id = " " }]));
        Assert.Throws<ArgumentException>(() => OverlayLayoutPlanner.Plan(
            [primary with { PixelBounds = new MonitorPixelBounds(0, 0, 0, 1080) }]));
        Assert.Throws<ArgumentException>(() => OverlayLayoutPlanner.Plan(
            [primary, primary with { Id = "PRIMARY", IsPrimary = false }]));
        Assert.Throws<ArgumentException>(() => OverlayLayoutPlanner.Plan([null!, primary]));
        Assert.Throws<ArgumentException>(() => OverlayLayoutPlanner.Plan(
            [primary with { PixelBounds = null! }]));
    }
}
