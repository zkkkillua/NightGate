namespace NightGate.Desktop.Tests;

public sealed class CommitmentCountdownLayoutTests
{
    [Fact]
    public void Planner_UsesThePrimaryMonitorWorkAreaTopRight()
    {
        MonitorDescriptor[] monitors =
        [
            new(
                "primary",
                new MonitorPixelBounds(0, 0, 1920, 1080),
                true,
                new MonitorPixelBounds(0, 0, 1920, 1040)),
            new(
                "secondary",
                new MonitorPixelBounds(1920, 0, 2560, 1440),
                false,
                new MonitorPixelBounds(1920, 0, 2560, 1400)),
        ];

        CommitmentCountdownPlacement placement =
            CommitmentCountdownLayoutPlanner.Plan(
                monitors,
                windowWidth: 360,
                windowHeight: 128,
                margin: 24);

        Assert.Equal("primary", placement.MonitorId);
        Assert.Equal(new MonitorPixelBounds(1536, 24, 360, 128), placement.PixelBounds);
    }

    [Fact]
    public void Planner_HandlesNegativeCoordinatesAndVerticalTaskbars()
    {
        MonitorDescriptor[] monitors =
        [
            new(
                "primary-left",
                new MonitorPixelBounds(-1920, -1080, 1920, 1080),
                true,
                new MonitorPixelBounds(-1840, -1080, 1840, 1080)),
        ];

        CommitmentCountdownPlacement placement =
            CommitmentCountdownLayoutPlanner.Plan(monitors, 400, 160, 20);

        Assert.Equal(
            new MonitorPixelBounds(-420, -1060, 400, 160),
            placement.PixelBounds);
    }

    [Fact]
    public void Planner_ClampsAnOversizedWindowInsideTheWorkArea()
    {
        MonitorDescriptor[] monitors =
        [
            new(
                "small",
                new MonitorPixelBounds(0, 0, 320, 240),
                true,
                new MonitorPixelBounds(0, 0, 300, 200)),
        ];

        CommitmentCountdownPlacement placement =
            CommitmentCountdownLayoutPlanner.Plan(monitors, 500, 400, 24);

        Assert.Equal(new MonitorPixelBounds(0, 0, 300, 200), placement.PixelBounds);
    }

    [Fact]
    public void Planner_RejectsAmbiguousOrInvalidTopology()
    {
        Assert.Throws<ArgumentException>(() =>
            CommitmentCountdownLayoutPlanner.Plan([], 360, 128, 24));
        Assert.Throws<ArgumentException>(() =>
            CommitmentCountdownLayoutPlanner.Plan(
                [
                    new MonitorDescriptor(
                        "a",
                        new MonitorPixelBounds(0, 0, 100, 100),
                        true),
                    new MonitorDescriptor(
                        "b",
                        new MonitorPixelBounds(100, 0, 100, 100),
                        true),
                ],
                360,
                128,
                24));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CommitmentCountdownLayoutPlanner.Plan(
                [new MonitorDescriptor(
                    "a",
                    new MonitorPixelBounds(0, 0, 100, 100),
                    true)],
                0,
                128,
                24));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Planner_RejectsMissingOrOutOfBoundsWorkingArea(
        bool useOutOfBoundsArea)
    {
        MonitorPixelBounds? workArea = useOutOfBoundsArea
            ? new MonitorPixelBounds(0, 0, 1921, 1080)
            : null;

        Assert.Throws<ArgumentException>(() =>
            CommitmentCountdownLayoutPlanner.Plan(
                [new MonitorDescriptor(
                    "primary",
                    new MonitorPixelBounds(0, 0, 1920, 1080),
                    true,
                    workArea)],
                360,
                128,
                24));
    }
}
