namespace NightGate.Desktop.Tests;

public sealed class CountdownRadiancePlacementTests
{
    [Theory]
    [InlineData(false, 1.0)]
    [InlineData(false, 1.5)]
    [InlineData(false, 2.0)]
    [InlineData(true, 1.0)]
    [InlineData(true, 1.5)]
    [InlineData(true, 2.0)]
    public void RandomMovesKeepTheWholeScatterAreaInsideTheWorkArea(bool game, double dpiScale)
    {
        CountdownRadianceLayout layout = CountdownRadianceModel.LayoutFor(
            game ? CommitmentCountdownKind.GameGraceToLock : CommitmentCountdownKind.Emergency);
        CountdownRadianceFrame frame = CountdownRadianceModel.Sample(layout, TimeSpan.FromSeconds(3), seed: 23);
        MonitorPixelBounds area = new(-1840, -1080, 1840, 1080);
        MonitorDescriptor monitor = new("left", new(-1920, -1080, 1920, 1080), true, area);
        int choice = 0;
        CommitmentCountdownMovementModel movement = new(count => choice++ % count);
        for (int index = 0; index < 20; index++)
        {
            MonitorPixelBounds window = movement.Update([monitor],
                (int)Math.Ceiling(layout.Width * dpiScale),
                (int)Math.Ceiling(layout.Height * dpiScale),
                24, TimeSpan.FromSeconds(index * 12), "left").PixelBounds;
            Assert.InRange(window.X, area.X + 24, area.X + area.Width - window.Width - 24);
            Assert.InRange(window.Y, area.Y + 24, area.Y + area.Height - window.Height - 24);
            foreach (CountdownRadianceParticle particle in frame.Particles)
            {
                Assert.InRange(window.X + (particle.X - particle.Radius) * dpiScale,
                    area.X, area.X + area.Width);
                Assert.InRange(window.X + (particle.X + particle.Radius) * dpiScale,
                    area.X, area.X + area.Width);
                Assert.InRange(window.Y + (particle.Y - particle.Radius) * dpiScale,
                    area.Y, area.Y + area.Height);
                Assert.InRange(window.Y + (particle.Y + particle.Radius) * dpiScale,
                    area.Y, area.Y + area.Height);
            }
        }
    }
}
