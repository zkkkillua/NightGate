using System.Collections.Immutable;
using System.IO;

namespace NightGate.Desktop;

internal enum GameDiscoverySource
{
    Epic,
    XboxGamingServices,
    Steam,
    UninstallRegistry,
    FixedDirectory,
}

internal enum GameDiscoveryConfidence
{
    Low,
    Medium,
    High,
}

internal enum GameDiscoverySourceState
{
    Succeeded,
    Unavailable,
    Degraded,
}

internal sealed record DiscoveredGame(
    string DisplayName,
    string ExecutablePath,
    GameDiscoverySource Source,
    GameDiscoveryConfidence Confidence);

internal sealed record GameDiscoverySourceStatus(
    GameDiscoverySource Source,
    GameDiscoverySourceState State,
    int CandidateCount);

internal sealed record GameDiscoverySnapshot(
    ImmutableArray<DiscoveredGame> Games,
    ImmutableArray<GameDiscoverySourceStatus> Sources);

internal interface IGameDiscovery
{
    ValueTask<GameDiscoverySnapshot> DiscoverAsync(
        CancellationToken cancellationToken = default);
}

internal interface IGameDiscoverySourceAdapter
{
    GameDiscoverySource Source { get; }

    ValueTask<GameDiscoverySourceBatch> DiscoverAsync(
        CancellationToken cancellationToken);
}

internal sealed record GameDiscoverySourceBatch(
    GameDiscoverySource Source,
    GameDiscoverySourceState State,
    IReadOnlyList<DiscoveredGame> Games);

internal sealed class WindowsGameDiscovery : IGameDiscovery
{
    private const int MaximumDisplayNameLength = 256;
    private readonly ImmutableArray<IGameDiscoverySourceAdapter> _sources;

    internal WindowsGameDiscovery()
        : this(
        [
            new EpicGameDiscoverySource(),
            new XboxGamingServicesGameDiscoverySource(),
            new SteamGameDiscoverySource(),
            new UninstallRegistryGameDiscoverySource(),
            new FixedDirectoryGameDiscoverySource(),
        ])
    {
    }

    internal WindowsGameDiscovery(
        IEnumerable<IGameDiscoverySourceAdapter> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToImmutableArray();
        if (_sources.IsDefaultOrEmpty
            || _sources.Any(static source => source is null)
            || _sources.Select(static source => source.Source).Distinct().Count()
                != _sources.Length)
        {
            throw new ArgumentException(
                "Discovery sources must be nonempty, initialized, and unique.",
                nameof(sources));
        }
    }

    public async ValueTask<GameDiscoverySnapshot> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<GameDiscoverySourceBatch>[] reads = _sources
            .Select(source => ReadSafelyAsync(source, cancellationToken))
            .ToArray();
        GameDiscoverySourceBatch[] batches = await Task.WhenAll(reads)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<string, DiscoveredGame> byPath = new(
            StringComparer.OrdinalIgnoreCase);
        var statuses = ImmutableArray.CreateBuilder<GameDiscoverySourceStatus>(
            batches.Length);
        foreach (GameDiscoverySourceBatch batch in batches)
        {
            int accepted = 0;
            IReadOnlyList<DiscoveredGame> candidates = batch.Games ?? [];
            foreach (DiscoveredGame? candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryNormalize(candidate, batch.Source, out DiscoveredGame? normalized))
                {
                    continue;
                }

                accepted++;
                if (!byPath.TryGetValue(normalized!.ExecutablePath, out DiscoveredGame? prior)
                    || IsPreferred(normalized, prior))
                {
                    byPath[normalized.ExecutablePath] = normalized;
                }
            }

            statuses.Add(new(batch.Source, batch.State, accepted));
        }

        ImmutableArray<DiscoveredGame> games = byPath.Values
            .OrderByDescending(static game => game.Confidence)
            .ThenBy(static game => game.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static game => game.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new(games, statuses.ToImmutable());
    }

    private static async Task<GameDiscoverySourceBatch> ReadSafelyAsync(
        IGameDiscoverySourceAdapter source,
        CancellationToken cancellationToken)
    {
        try
        {
            GameDiscoverySourceBatch batch = await source
                .DiscoverAsync(cancellationToken)
                .ConfigureAwait(false);
            return batch is not null && batch.Source == source.Source
                ? batch
                : new(source.Source, GameDiscoverySourceState.Degraded, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Discovery is optional assistance. A broken launcher manifest or an unreadable
            // installation must never affect the protection runtime or the settings window.
            return new(source.Source, GameDiscoverySourceState.Degraded, []);
        }
    }

    private static bool TryNormalize(
        DiscoveredGame? candidate,
        GameDiscoverySource expectedSource,
        out DiscoveredGame? normalized)
    {
        normalized = null;
        if (candidate is null
            || candidate.Source != expectedSource
            || !Enum.IsDefined(candidate.Source)
            || !Enum.IsDefined(candidate.Confidence)
            || string.IsNullOrWhiteSpace(candidate.DisplayName))
        {
            return false;
        }

        string displayName = candidate.DisplayName.Trim();
        if (displayName.Length > MaximumDisplayNameLength
            || displayName.Any(static character => char.IsControl(character)))
        {
            return false;
        }

        if (!NightGate.Core.Win32ExecutablePathCanonicalizer.TryCanonicalize(
                candidate.ExecutablePath,
                out string canonicalPath)
            || !File.Exists(canonicalPath))
        {
            return false;
        }

        normalized = candidate with
        {
            DisplayName = displayName,
            ExecutablePath = canonicalPath,
        };
        return true;
    }

    private static bool IsPreferred(DiscoveredGame candidate, DiscoveredGame prior)
    {
        int confidence = candidate.Confidence.CompareTo(prior.Confidence);
        if (confidence != 0)
        {
            return confidence > 0;
        }

        int source = SourcePriority(candidate.Source).CompareTo(SourcePriority(prior.Source));
        if (source != 0)
        {
            return source > 0;
        }

        bool candidateFriendly = HasFriendlyName(candidate);
        bool priorFriendly = HasFriendlyName(prior);
        if (candidateFriendly != priorFriendly)
        {
            return candidateFriendly;
        }

        int name = string.Compare(
            candidate.DisplayName,
            prior.DisplayName,
            StringComparison.OrdinalIgnoreCase);
        return name < 0;
    }

    private static bool HasFriendlyName(DiscoveredGame game) =>
        !string.Equals(
            GameDiscoveryText.NormalizeForMatching(game.DisplayName),
            GameDiscoveryText.NormalizeForMatching(
                Path.GetFileNameWithoutExtension(game.ExecutablePath)),
            StringComparison.Ordinal);

    private static int SourcePriority(GameDiscoverySource source) => source switch
    {
        GameDiscoverySource.Epic => 5,
        GameDiscoverySource.XboxGamingServices => 5,
        GameDiscoverySource.Steam => 4,
        GameDiscoverySource.UninstallRegistry => 2,
        GameDiscoverySource.FixedDirectory => 1,
        _ => 0,
    };
}

internal abstract class BackgroundGameDiscoverySource : IGameDiscoverySourceAdapter
{
    public abstract GameDiscoverySource Source { get; }

    public ValueTask<GameDiscoverySourceBatch> DiscoverAsync(
        CancellationToken cancellationToken) => new(Task.Run(
        () => DiscoverCore(cancellationToken),
        cancellationToken));

    protected abstract GameDiscoverySourceBatch DiscoverCore(
        CancellationToken cancellationToken);
}
