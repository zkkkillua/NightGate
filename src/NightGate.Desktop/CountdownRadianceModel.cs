namespace NightGate.Desktop;

internal readonly record struct CountdownRadianceLayout(
    double CardWidth,
    double CardHeight,
    double Halo,
    bool IsGame)
{
    public double Width => CardWidth + 2 * Halo;
    public double Height => CardHeight + 2 * Halo;
}

internal enum CountdownParticleShape
{
    Dot,
    Diamond,
    Streak,
}

internal readonly record struct CountdownRadianceWave(
    double Inflation,
    double Opacity,
    double Thickness);

internal readonly record struct CountdownRadianceParticle(
    double X,
    double Y,
    double Radius,
    double Opacity,
    double TrailX,
    double TrailY,
    CountdownParticleShape Shape = CountdownParticleShape.Dot);

internal sealed record CountdownRadianceFrame(
    IReadOnlyList<CountdownRadianceWave> Waves,
    IReadOnlyList<CountdownRadianceParticle> Particles,
    double GlowOpacity);

/// <summary>
/// Visual-only, stateless sampling. A window supplies one random seed for its
/// lifetime and monotonic elapsed time; presentation refreshes never restart it.
/// </summary>
internal static class CountdownRadianceModel
{
    private const double EpochSeconds = 10;
    private const double StartJitterSeconds = 4;

    public static CountdownRadianceLayout LayoutFor(CommitmentCountdownKind kind) =>
        kind switch
        {
            CommitmentCountdownKind.GameGraceToLock => new(560, 240, 64, true),
            CommitmentCountdownKind.GraceToLock
                or CommitmentCountdownKind.EntertainmentCoolingOff
                or CommitmentCountdownKind.TeamRescue
                or CommitmentCountdownKind.Emergency
                or CommitmentCountdownKind.EntertainmentActive => new(380, 148, 48, false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    public static CountdownRadianceFrame Sample(
        CountdownRadianceLayout layout,
        TimeSpan elapsed,
        bool reduceMotion = false,
        int seed = 0)
    {
        if (layout.CardWidth < 40 || layout.CardHeight < 40 || layout.Halo < 24
            || !double.IsFinite(layout.Width) || !double.IsFinite(layout.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }

        if (reduceMotion)
        {
            return new([new(5, 0.45, 2)], [], layout.IsGame ? 0.24 : 0.18);
        }

        double seconds = Math.Max(0, elapsed.TotalSeconds);
        long epoch = (long)Math.Floor(seconds / EpochSeconds);
        List<CountdownRadianceWave> waves = new(6);
        List<CountdownRadianceParticle> particles = new(96);
        double glow = layout.IsGame ? 0.21 : 0.16;
        // Jittering starts gives 6–14-second intervals. Only the current and prior
        // epochs can still be visible; no unbounded history or per-frame randomness.
        for (long burst = Math.Max(0, epoch - 1); burst <= epoch; burst++)
        {
            double start = burst == 0
                ? 0
                : burst * EpochSeconds + RandomUnit(seed, burst, 0) * StartJitterSeconds;
            double duration = 5.2 + RandomUnit(seed, burst, 1) * 2;
            double progress = (seconds - start) / duration;
            if (progress <= 0 || progress >= 1)
            {
                continue;
            }

            int variant = (int)(RandomUnit(seed, burst, 2) * 3);
            double strength = (layout.IsGame ? 0.48 : 0.36)
                + RandomUnit(seed, burst, 3) * 0.22;
            glow += Math.Sin(Math.PI * progress) * strength * 0.14;
            AddWaves(waves, layout, seed, burst, progress, variant, strength);
            AddParticles(particles, layout, seed, burst, progress, variant, strength);
        }

        return new(waves, particles, Math.Min(0.42, glow));
    }

    private static void AddWaves(
        List<CountdownRadianceWave> waves,
        CountdownRadianceLayout layout,
        int seed,
        long burst,
        double progress,
        int variant,
        double strength)
    {
        int count = variant == 1 ? 1 : 2 + (int)(RandomUnit(seed, burst, 4) * 2);
        for (int index = 0; index < count; index++)
        {
            double delay = index * (0.11 + RandomUnit(seed, burst, 5) * 0.08);
            double waveProgress = (progress - delay) / (1 - delay);
            if (waveProgress <= 0 || waveProgress >= 1)
            {
                continue;
            }

            double inflation = 3 + (layout.Halo - 10) * EaseOut(waveProgress);
            double opacity = Math.Sin(Math.PI * waveProgress) * strength * 0.68
                * (1 - index * 0.14);
            double thickness = 1.1 + RandomUnit(seed, burst, 10 + index) * 1.25;
            waves.Add(new(inflation, opacity, thickness));
        }
    }

    private static void AddParticles(
        List<CountdownRadianceParticle> particles,
        CountdownRadianceLayout layout,
        int seed,
        long burst,
        double progress,
        int variant,
        double strength)
    {
        int count = 16 + 4 * (int)(RandomUnit(seed, burst, 20) * 5)
            + (layout.IsGame ? 8 : 0);
        int perEdge = count / 4;
        int edgeRotation = (int)(RandomUnit(seed, burst, 21) * 4);
        for (int index = 0; index < count; index++)
        {
            int channel = 100 + index * 12;
            double delay = RandomUnit(seed, burst, channel) * 0.18;
            double particleProgress = (progress - delay) / (1 - delay);
            if (particleProgress <= 0 || particleProgress >= 1)
            {
                continue;
            }

            int edge = (index + edgeRotation) % 4;
            double along = (index / 4 + 0.2 + RandomUnit(seed, burst, channel + 1) * 0.6)
                / perEdge;
            double angle = (edge - 1) * Math.PI / 2
                + (RandomUnit(seed, burst, channel + 2) - 0.5) * 1.05;
            double directionX = Math.Cos(angle);
            double directionY = Math.Sin(angle);
            double originX = edge switch
            {
                1 => layout.Halo + layout.CardWidth + 2,
                3 => layout.Halo - 2,
                _ => layout.Halo + 12 + along * (layout.CardWidth - 24),
            };
            double originY = edge switch
            {
                0 => layout.Halo - 2,
                2 => layout.Halo + layout.CardHeight + 2,
                _ => layout.Halo + 12 + along * (layout.CardHeight - 24),
            };
            double distance = (layout.Halo - 10)
                * (0.12 + 0.88 * EaseOut(particleProgress))
                * (0.76 + RandomUnit(seed, burst, channel + 3) * 0.24);
            double x = originX + directionX * distance;
            double y = originY + directionY * distance;
            double radius = (1.35 + RandomUnit(seed, burst, channel + 4) * 1.6)
                * (layout.IsGame ? 1.18 : 1);
            double opacity = Math.Sin(Math.PI * particleProgress) * strength
                * (0.72 + RandomUnit(seed, burst, channel + 5) * 0.28);
            double tail = (3 + RandomUnit(seed, burst, channel + 6) * 8)
                * (0.6 + particleProgress * 0.4);
            CountdownParticleShape shape = (CountdownParticleShape)(
                (variant + (int)(RandomUnit(seed, burst, channel + 7) * 3)) % 3);
            particles.Add(new(x, y, radius, opacity,
                x - directionX * tail, y - directionY * tail, shape));
        }
    }

    private static double EaseOut(double progress) => 1 - Math.Pow(1 - progress, 2);

    private static double RandomUnit(int seed, long epoch, int channel)
    {
        // Stable per-burst mixing lets tests sample any frame, and avoids a
        // repeating animation storyboard while keeping a fixed allocation bound.
        ulong value = unchecked((ulong)(long)seed)
            ^ unchecked((ulong)epoch * 0x9E3779B97F4A7C15UL)
            ^ unchecked((ulong)(channel + 1) * 0xD1B54A32D192ED03UL);
        value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
        value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
        value ^= value >> 31;
        return (value >> 11) * (1.0 / (1UL << 53));
    }
}
