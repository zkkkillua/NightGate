namespace NightGate.Desktop.Tests;

public sealed class CommitmentCountdownMovementTests
{
    [Fact]
    public void FirstPositionUsesRandomChoiceInsteadOfAlwaysTopRight()
    {
        CommitmentCountdownMovementModel first = new(_ => 0);
        CommitmentCountdownMovementModel last = new(count => count - 1);

        CommitmentCountdownPlacement topLeft = first.Update(Monitors(), 380, 148, 24, TimeSpan.Zero);
        CommitmentCountdownPlacement bottomRight = last.Update(Monitors(), 380, 148, 24, TimeSpan.Zero);

        Assert.Equal(new MonitorPixelBounds(24, 24, 380, 148), topLeft.PixelBounds);
        Assert.Equal(new MonitorPixelBounds(1516, 868, 380, 148), bottomRight.PixelBounds);
    }

    [Fact]
    public void OneSecondRefreshesStayStillUntilTwelveSecondsThenMoveFarEnough()
    {
        int randomCalls = 0;
        CommitmentCountdownMovementModel model = new(_ => { randomCalls++; return 0; });
        CommitmentCountdownPlacement initial = model.Update(Monitors(), 380, 148, 24, TimeSpan.Zero);

        for (int second = 1; second < 12; second++)
        {
            Assert.Equal(initial, model.Update(Monitors(), 380, 148, 24, TimeSpan.FromSeconds(second)));
        }

        CommitmentCountdownPlacement moved = model.Update(Monitors(), 380, 148, 24, TimeSpan.FromSeconds(12));
        Assert.Equal(2, randomCalls);
        Assert.True(Math.Abs(initial.PixelBounds.X - moved.PixelBounds.X) >= 240
            || Math.Abs(initial.PixelBounds.Y - moved.PixelBounds.Y) >= 240);
        Assert.Equal(moved, model.Update(Monitors(), 380, 148, 24, TimeSpan.FromSeconds(23.99)));
        Assert.NotEqual(moved, model.Update(Monitors(), 380, 148, 24, TimeSpan.FromSeconds(24)));
    }

    [Fact]
    public void MonotonicRollbackCannotTriggerExtraMovesOrResetTheInterval()
    {
        CommitmentCountdownMovementModel model = new(_ => 0);
        CommitmentCountdownPlacement initial = model.Update(Monitors(), 380, 148, 24, TimeSpan.FromSeconds(100));

        Assert.Equal(initial, model.Update(Monitors(), 380, 148, 24, TimeSpan.FromSeconds(1)));
        Assert.Equal(initial, model.Update(Monitors(), 380, 148, 24, TimeSpan.FromSeconds(111.99)));
        Assert.NotEqual(initial, model.Update(Monitors(), 380, 148, 24, TimeSpan.FromSeconds(112)));
    }

    [Fact]
    public void ForegroundMonitorIsPreferredAndFocusChangesWaitForNextMove()
    {
        CommitmentCountdownMovementModel model = new(_ => 0);
        CommitmentCountdownPlacement initial = model.Update(Monitors(), 560, 240, 24, TimeSpan.Zero, "left");

        Assert.Equal("left", initial.MonitorId);
        Assert.Equal(initial, model.Update(Monitors(), 560, 240, 24, TimeSpan.FromSeconds(1), "primary"));
        Assert.Equal("primary", model.Update(Monitors(), 560, 240, 24, TimeSpan.FromSeconds(12), "primary").MonitorId);
    }

    [Fact]
    public void DisconnectedMonitorMovesToAvailableMonitorImmediately()
    {
        CommitmentCountdownMovementModel model = new(_ => 0);
        model.Update(Monitors(), 560, 240, 24, TimeSpan.Zero, "left");

        CommitmentCountdownPlacement moved = model.Update([Monitors()[0]], 560, 240, 24, TimeSpan.FromSeconds(1), "left");

        Assert.Equal("primary", moved.MonitorId);
        AssertInside(Monitors()[0].WorkingAreaPixelBounds!, moved.PixelBounds);
    }

    [Fact]
    public void WindowSizeChangesReplanImmediatelyAndRemainInsideWorkingArea()
    {
        CommitmentCountdownMovementModel model = new(count => count - 1);
        CommitmentCountdownPlacement small = model.Update(Monitors(), 380, 148, 24, TimeSpan.Zero);
        CommitmentCountdownPlacement large = model.Update(Monitors(), 560, 240, 24, TimeSpan.FromSeconds(1));

        Assert.Equal(560, large.PixelBounds.Width);
        Assert.Equal(240, large.PixelBounds.Height);
        Assert.NotEqual(small.PixelBounds, large.PixelBounds);
        AssertInside(Monitors()[0].WorkingAreaPixelBounds!, large.PixelBounds);
        Assert.Equal(large, model.Update(Monitors(), 560, 240, 24, TimeSpan.FromSeconds(12)));
        Assert.NotEqual(large, model.Update(Monitors(), 560, 240, 24, TimeSpan.FromSeconds(13)));
    }

    [Fact]
    public void NegativeCoordinatesVerticalTaskbarAndRepeatedMovesRemainInBounds()
    {
        int sequence = 0;
        CommitmentCountdownMovementModel model = new(count => sequence++ % count);
        for (int index = 0; index < 100; index++)
        {
            CommitmentCountdownPlacement placement = model.Update(Monitors(), 560, 240, 24, TimeSpan.FromSeconds(index * 12), "left");
            AssertInside(Monitors()[1].WorkingAreaPixelBounds!, placement.PixelBounds);
            Assert.True(placement.PixelBounds.X >= -1840 + 24);
            Assert.True(placement.PixelBounds.Y >= -1080 + 24);
        }
    }

    [Fact]
    public void TaskbarWorkAreaChangesReplanWithoutWaiting()
    {
        CommitmentCountdownMovementModel model = new(count => count - 1);
        model.Update(Monitors(), 560, 240, 24, TimeSpan.Zero);
        MonitorDescriptor movedTaskbar = Monitors()[0] with
        {
            WorkingAreaPixelBounds = new MonitorPixelBounds(200, 0, 1720, 1000),
        };

        CommitmentCountdownPlacement changed = model.Update([movedTaskbar], 560, 240, 24, TimeSpan.FromSeconds(1));

        AssertInside(movedTaskbar.WorkingAreaPixelBounds!, changed.PixelBounds);
    }

    [Fact]
    public void SmallWorkAreaClampsWindowWithoutThrowingOrGoingOffscreen()
    {
        MonitorDescriptor small = new("small", new(-300, -200, 300, 200), true, new(-300, -200, 280, 180));
        CommitmentCountdownMovementModel model = new(_ => 0);

        CommitmentCountdownPlacement first = model.Update([small], 560, 240, 24, TimeSpan.Zero);
        CommitmentCountdownPlacement next = model.Update([small], 560, 240, 24, TimeSpan.FromSeconds(12));

        Assert.Equal(new MonitorPixelBounds(-300, -200, 280, 180), first.PixelBounds);
        Assert.Equal(first, next);
    }

    [Fact]
    public void ResetClearsLocationAndMovementClockForNextVisibleLifecycle()
    {
        int count = 0;
        CommitmentCountdownMovementModel model = new(max => count++ % max);
        CommitmentCountdownPlacement before = model.Update(Monitors(), 380, 148, 24, TimeSpan.FromSeconds(100));
        model.Reset();
        CommitmentCountdownPlacement after = model.Update(Monitors(), 380, 148, 24, TimeSpan.Zero);

        Assert.NotEqual(before, after);
        Assert.Equal(after, model.Update(Monitors(), 380, 148, 24, TimeSpan.FromSeconds(11)));
        Assert.NotEqual(after, model.Update(Monitors(), 380, 148, 24, TimeSpan.FromSeconds(12)));
    }

    [Fact]
    public void InvalidRandomValueIsRejectedInsteadOfSelectingOutOfBounds()
    {
        CommitmentCountdownMovementModel model = new(count => count);

        Assert.Throws<InvalidOperationException>(() => model.Update(Monitors(), 380, 148, 24, TimeSpan.Zero));
    }

    private static MonitorDescriptor[] Monitors() =>
    [
        new("primary", new(0, 0, 1920, 1080), true, new(0, 0, 1920, 1040)),
        new("left", new(-1920, -1080, 1920, 1080), false, new(-1840, -1080, 1840, 1080)),
    ];

    private static void AssertInside(MonitorPixelBounds area, MonitorPixelBounds placement)
    {
        Assert.InRange(placement.X, area.X, area.X + area.Width - placement.Width);
        Assert.InRange(placement.Y, area.Y, area.Y + area.Height - placement.Height);
    }
}
