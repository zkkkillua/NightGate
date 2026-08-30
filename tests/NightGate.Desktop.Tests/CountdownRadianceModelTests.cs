namespace NightGate.Desktop.Tests;

public sealed class CountdownRadianceModelTests
{
    [Theory]
    [InlineData(CommitmentCountdownKind.GraceToLock)]
    [InlineData(CommitmentCountdownKind.EntertainmentCoolingOff)]
    [InlineData(CommitmentCountdownKind.TeamRescue)]
    [InlineData(CommitmentCountdownKind.Emergency)]
    [InlineData(CommitmentCountdownKind.EntertainmentActive)]
    public void ExistingCardsKeepTheirReadableSizeWithAnExternalEffectsGutter(CommitmentCountdownKind kind)
    {
        CountdownRadianceLayout layout = CountdownRadianceModel.LayoutFor(kind);
        Assert.Equal(380, layout.CardWidth);
        Assert.Equal(148, layout.CardHeight);
        Assert.Equal(476, layout.Width);
        Assert.Equal(244, layout.Height);
        Assert.False(layout.IsGame);
    }

    [Fact]
    public void GameCardAndItsEffectsOccupyMoreSpace()
    {
        CountdownRadianceLayout layout = CountdownRadianceModel.LayoutFor(CommitmentCountdownKind.GameGraceToLock);
        Assert.Equal(560, layout.CardWidth);
        Assert.Equal(240, layout.CardHeight);
        Assert.Equal(688, layout.Width);
        Assert.Equal(368, layout.Height);
        Assert.True(layout.IsGame);
    }

    [Fact]
    public void AllCountdownKindsEmitIntoAllFourOutsideEdges()
    {
        foreach (CommitmentCountdownKind kind in Enum.GetValues<CommitmentCountdownKind>())
        {
            CountdownRadianceLayout layout = CountdownRadianceModel.LayoutFor(kind);
            CountdownRadianceFrame frame = CountdownRadianceModel.Sample(layout, TimeSpan.FromSeconds(3));
            Assert.NotEmpty(frame.Waves);
            Assert.Contains(frame.Particles, particle => particle.Y < layout.Halo);
            Assert.Contains(frame.Particles, particle => particle.X > layout.Halo + layout.CardWidth);
            Assert.Contains(frame.Particles, particle => particle.Y > layout.Halo + layout.CardHeight);
            Assert.Contains(frame.Particles, particle => particle.X < layout.Halo);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FramesAreBoundedAndInsideTheTransparentWindowAtAnyTime(bool game)
    {
        CountdownRadianceLayout layout = CountdownRadianceModel.LayoutFor(
            game ? CommitmentCountdownKind.GameGraceToLock : CommitmentCountdownKind.TeamRescue);
        for (int seed = -3; seed < 5; seed++)
        {
            for (int step = 0; step < 500; step++)
            {
                CountdownRadianceFrame frame = CountdownRadianceModel.Sample(layout,
                    TimeSpan.FromSeconds(step * 0.17), seed: seed);
                Assert.InRange(frame.GlowOpacity, 0, 0.42);
                Assert.InRange(frame.Particles.Count, 0, 80);
                Assert.InRange(frame.Waves.Count, 0, 6);
                foreach (CountdownRadianceWave wave in frame.Waves)
                {
                    Assert.InRange(wave.Inflation + wave.Thickness, 0, layout.Halo);
                    Assert.InRange(wave.Opacity, 0, 0.5);
                }
                foreach (CountdownRadianceParticle particle in frame.Particles)
                {
                    Assert.InRange(particle.X - particle.Radius, 0, layout.Width);
                    Assert.InRange(particle.X + particle.Radius, 0, layout.Width);
                    Assert.InRange(particle.Y - particle.Radius, 0, layout.Height);
                    Assert.InRange(particle.Y + particle.Radius, 0, layout.Height);
                    Assert.InRange(particle.TrailX, 0, layout.Width);
                    Assert.InRange(particle.TrailY, 0, layout.Height);
                    Assert.InRange(particle.Opacity, 0, 0.71);
                }
            }
        }
    }

    [Fact]
    public void FrameSamplingIsStableButDifferentSessionsAndBurstsVary()
    {
        CountdownRadianceLayout layout = CountdownRadianceModel.LayoutFor(CommitmentCountdownKind.GameGraceToLock);
        CountdownRadianceFrame first = CountdownRadianceModel.Sample(layout, TimeSpan.FromSeconds(3), seed: 42);
        CountdownRadianceFrame repeat = CountdownRadianceModel.Sample(layout, TimeSpan.FromSeconds(3), seed: 42);
        Assert.Equal(first.Particles, repeat.Particles);
        Assert.Equal(first.Waves, repeat.Waves);
        Assert.Equal(first.GlowOpacity, repeat.GlowOpacity);
        Assert.NotEqual(first.Particles.ToArray(),
            CountdownRadianceModel.Sample(layout, TimeSpan.FromSeconds(3), seed: 43).Particles.ToArray());
        HashSet<int> counts = [];
        HashSet<string> waveShapes = [];
        HashSet<CountdownParticleShape> particleShapes = [];
        for (int burst = 0; burst < 50; burst++)
        {
            CountdownRadianceFrame frame = CountdownRadianceModel.Sample(layout,
                TimeSpan.FromSeconds(burst * 10 + 4.9), seed: 42);
            counts.Add(frame.Particles.Count);
            waveShapes.Add(string.Join(',', frame.Waves.Select(wave => wave.Inflation.ToString("F2"))));
            particleShapes.UnionWith(frame.Particles.Select(particle => particle.Shape));
        }
        Assert.True(counts.Count >= 3);
        Assert.True(waveShapes.Count >= 40);
        Assert.Equal(3, particleShapes.Count);
    }

    [Fact]
    public void ParticlesTravelOutwardSmoothlyInsteadOfFlickeringPerFrame()
    {
        CountdownRadianceLayout layout = CountdownRadianceModel.LayoutFor(CommitmentCountdownKind.GameGraceToLock);
        CountdownRadianceFrame before = CountdownRadianceModel.Sample(layout, TimeSpan.FromSeconds(2.5), seed: 17);
        CountdownRadianceFrame after = CountdownRadianceModel.Sample(layout, TimeSpan.FromSeconds(2.55), seed: 17);
        Assert.Equal(before.Particles.Count, after.Particles.Count);
        for (int index = 0; index < before.Particles.Count; index++)
        {
            CountdownRadianceParticle a = before.Particles[index];
            CountdownRadianceParticle b = after.Particles[index];
            Assert.Equal(a.Shape, b.Shape);
            Assert.InRange(Math.Abs(a.Opacity - b.Opacity), 0, 0.03);
            double movement = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
            Assert.InRange(movement, 0.01, 2);
            Assert.True(OutsideDistance(b, layout) > OutsideDistance(a, layout));
        }
    }

    [Fact]
    public void ReducedMotionIsStaticHasNoParticlesAndIgnoresRandomSeed()
    {
        CountdownRadianceLayout layout = CountdownRadianceModel.LayoutFor(CommitmentCountdownKind.TeamRescue);
        CountdownRadianceFrame first = CountdownRadianceModel.Sample(layout, TimeSpan.Zero, true, 1);
        CountdownRadianceFrame later = CountdownRadianceModel.Sample(layout, TimeSpan.FromDays(10), true, 2);
        Assert.Empty(first.Particles);
        Assert.Single(first.Waves);
        Assert.Equal(first.Waves, later.Waves);
        Assert.Equal(first.GlowOpacity, later.GlowOpacity);
    }

    [Fact]
    public void NegativeOrExtremeElapsedTimeNeverEscapesTheEffectBounds()
    {
        CountdownRadianceLayout layout = CountdownRadianceModel.LayoutFor(CommitmentCountdownKind.GraceToLock);
        Assert.Empty(CountdownRadianceModel.Sample(layout, TimeSpan.MinValue).Particles);
        CountdownRadianceFrame farFuture = CountdownRadianceModel.Sample(layout, TimeSpan.MaxValue, seed: int.MinValue);
        Assert.InRange(farFuture.GlowOpacity, 0, 0.42);
        Assert.InRange(farFuture.Particles.Count, 0, 80);
        Assert.Throws<ArgumentOutOfRangeException>(() => CountdownRadianceModel.Sample(default, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => CountdownRadianceModel.LayoutFor((CommitmentCountdownKind)99));
    }

    private static double OutsideDistance(CountdownRadianceParticle particle, CountdownRadianceLayout layout) =>
        Math.Max(Math.Max(layout.Halo - particle.X, particle.X - layout.Halo - layout.CardWidth),
            Math.Max(layout.Halo - particle.Y, particle.Y - layout.Halo - layout.CardHeight));
}
