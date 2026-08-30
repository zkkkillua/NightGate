using System.Text;
using System.Text.Json;

namespace NightGate.Desktop.Tests;

public sealed class GameDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_DeduplicatesCanonicalPathsAndPrefersStrongEvidence()
    {
        using GameFixture fixture = new();
        string executable = fixture.Executable("Game", "Game.exe", 1024);
        WindowsGameDiscovery discovery = new(
        [
            FakeSource.Returning(
                GameDiscoverySource.Steam,
                new DiscoveredGame(
                    "game",
                    executable.ToUpperInvariant(),
                    GameDiscoverySource.Steam,
                    GameDiscoveryConfidence.Medium)),
            FakeSource.Returning(
                GameDiscoverySource.Epic,
                new DiscoveredGame(
                    "Friendly Game",
                    executable,
                    GameDiscoverySource.Epic,
                    GameDiscoveryConfidence.High)),
        ]);

        GameDiscoverySnapshot snapshot = await discovery.DiscoverAsync();

        DiscoveredGame game = Assert.Single(snapshot.Games);
        Assert.Equal("Friendly Game", game.DisplayName);
        Assert.Equal(GameDiscoverySource.Epic, game.Source);
        Assert.Equal(GameDiscoveryConfidence.High, game.Confidence);
        Assert.Equal(2, snapshot.Sources.Length);
        Assert.All(snapshot.Sources, status => Assert.Equal(1, status.CandidateCount));
    }

    [Fact]
    public async Task DiscoverAsync_ContainsSourceFailureAndReturnsOtherCandidates()
    {
        using GameFixture fixture = new();
        string executable = fixture.Executable("Game", "Game.exe", 1024);
        WindowsGameDiscovery discovery = new(
        [
            FakeSource.Throwing(GameDiscoverySource.Steam),
            FakeSource.Returning(
                GameDiscoverySource.Epic,
                new DiscoveredGame(
                    "Still Available",
                    executable,
                    GameDiscoverySource.Epic,
                    GameDiscoveryConfidence.High)),
        ]);

        GameDiscoverySnapshot snapshot = await discovery.DiscoverAsync();

        Assert.Single(snapshot.Games);
        GameDiscoverySourceStatus steam = Assert.Single(
            snapshot.Sources,
            status => status.Source == GameDiscoverySource.Steam);
        Assert.Equal(GameDiscoverySourceState.Degraded, steam.State);
        Assert.Equal(0, steam.CandidateCount);
    }

    [Fact]
    public async Task DiscoverAsync_PropagatesCallerCancellation()
    {
        using CancellationTokenSource stopping = new();
        stopping.Cancel();
        WindowsGameDiscovery discovery = new(
        [
            FakeSource.Returning(GameDiscoverySource.Steam),
        ]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await discovery.DiscoverAsync(stopping.Token));
    }

    [Fact]
    public async Task EpicManifest_ProducesHighConfidenceExecutableAndRejectsEscape()
    {
        using GameFixture fixture = new();
        string manifests = fixture.Directory("EpicManifests");
        string install = fixture.Directory("EpicGames", "Example");
        string executable = fixture.Executable(
            Path.Combine("EpicGames", "Example"),
            "Binaries\\Example.exe",
            1024);
        fixture.Text(
            Path.Combine("EpicManifests", "valid.item"),
            JsonSerializer.Serialize(new
            {
                DisplayName = "Epic Example",
                InstallLocation = install,
                LaunchExecutable = "Binaries/Example.exe",
                bIsApplication = true,
            }));
        fixture.Text(
            Path.Combine("EpicManifests", "escape.item"),
            JsonSerializer.Serialize(new
            {
                DisplayName = "Escaped",
                InstallLocation = install,
                LaunchExecutable = "../../outside.exe",
                bIsApplication = true,
            }));
        WindowsGameDiscovery discovery = new(
        [
            new EpicGameDiscoverySource([manifests]),
        ]);

        GameDiscoverySnapshot snapshot = await discovery.DiscoverAsync();

        DiscoveredGame game = Assert.Single(snapshot.Games);
        Assert.Equal("Epic Example", game.DisplayName);
        Assert.Equal(executable, game.ExecutablePath, ignoreCase: true);
        Assert.Equal(GameDiscoveryConfidence.High, game.Confidence);
    }

    [Fact]
    public async Task XboxGamingRootAndConfig_ProduceDeclaredPcExecutable()
    {
        using GameFixture fixture = new();
        string driveRoot = fixture.Directory("Drive");
        byte[] marker =
        [
            (byte)'R', (byte)'G', (byte)'B', (byte)'X',
            1, 0, 0, 0,
            .. Encoding.Unicode.GetBytes("XboxGames\0"),
        ];
        fixture.Bytes(Path.Combine("Drive", ".GamingRoot"), marker);
        string content = fixture.Directory(
            "Drive",
            "XboxGames",
            "Example Game",
            "Content");
        string executable = fixture.Executable(
            Path.Combine("Drive", "XboxGames", "Example Game", "Content"),
            "Example.exe",
            1024);
        fixture.Text(
            Path.Combine(
                "Drive",
                "XboxGames",
                "Example Game",
                "Content",
                "MicrosoftGame.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Game xmlns="urn:test">
              <ExecutableList>
                <Executable Name="Example.exe" Id="Game" TargetDeviceFamily="PC" />
                <Executable Name="Console.exe" Id="Console" TargetDeviceFamily="XboxOne" />
              </ExecutableList>
              <ShellVisuals DefaultDisplayName="Xbox Example" />
            </Game>
            """);
        WindowsGameDiscovery discovery = new(
        [
            new XboxGamingServicesGameDiscoverySource([driveRoot]),
        ]);

        GameDiscoverySnapshot snapshot = await discovery.DiscoverAsync();

        DiscoveredGame game = Assert.Single(snapshot.Games);
        Assert.Equal("Xbox Example", game.DisplayName);
        Assert.Equal(executable, game.ExecutablePath, ignoreCase: true);
        Assert.Equal(GameDiscoveryConfidence.High, game.Confidence);
        Assert.True(Directory.Exists(content));
    }

    [Fact]
    public async Task SteamManifest_RanksGameBinaryAboveCrashAndLauncherExecutables()
    {
        using GameFixture fixture = new();
        string steam = fixture.Directory("Steam");
        fixture.Directory("Steam", "steamapps", "common", "Great Game");
        fixture.Text(
            Path.Combine("Steam", "steamapps", "libraryfolders.vdf"),
            $$"""
            "libraryfolders"
            {
              "0" { "path" "{{steam.Replace("\\", "\\\\", StringComparison.Ordinal)}}" }
            }
            """);
        fixture.Text(
            Path.Combine("Steam", "steamapps", "appmanifest_1.acf"),
            """
            "AppState"
            {
              "appid" "1"
              "name" "Great Game"
              "installdir" "Great Game"
            }
            """);
        string game = fixture.Executable(
            Path.Combine("Steam", "steamapps", "common", "Great Game"),
            "Game\\Binaries\\Win64\\GreatGame-Win64-Shipping.exe",
            12 * 1024 * 1024);
        _ = fixture.Executable(
            Path.Combine("Steam", "steamapps", "common", "Great Game"),
            "CrashReportClient.exe",
            20 * 1024 * 1024);
        _ = fixture.Executable(
            Path.Combine("Steam", "steamapps", "common", "Great Game"),
            "GreatGameLauncher.exe",
            1024);
        WindowsGameDiscovery discovery = new(
        [
            new SteamGameDiscoverySource([steam]),
        ]);

        GameDiscoverySnapshot snapshot = await discovery.DiscoverAsync();

        DiscoveredGame discovered = Assert.Single(snapshot.Games);
        Assert.Equal("Great Game", discovered.DisplayName);
        Assert.Equal(game, discovered.ExecutablePath, ignoreCase: true);
        Assert.Equal(GameDiscoveryConfidence.Medium, discovered.Confidence);
    }

    [Fact]
    public async Task ConstrainedUninstallEntry_UsesOnlyAnAbsoluteDisplayIconExecutable()
    {
        using GameFixture fixture = new();
        string executable = fixture.Executable("EA Games\\Example", "Example.exe", 1024);
        UninstallRegistryEntry entry = new(
            "Example",
            "Registry Example",
            $"\"{executable}\",0",
            fixture.PathOf("EA Games", "Example"),
            "Electronic Arts",
            false,
            null);
        WindowsGameDiscovery discovery = new(
        [
            new UninstallRegistryGameDiscoverySource([entry]),
        ]);

        GameDiscoverySnapshot snapshot = await discovery.DiscoverAsync();

        DiscoveredGame game = Assert.Single(snapshot.Games);
        Assert.Equal("Registry Example", game.DisplayName);
        Assert.Equal(GameDiscoveryConfidence.Low, game.Confidence);
    }

    [Fact]
    public async Task FixedDirectoryScan_IsBoundedToKnownChildInstallDirectories()
    {
        using GameFixture fixture = new();
        string root = fixture.Directory("KnownGames");
        string executable = fixture.Executable(
            Path.Combine("KnownGames", "Fixed Example"),
            "Binaries\\FixedExample.exe",
            2 * 1024 * 1024);
        _ = fixture.Executable(
            Path.Combine("KnownGames", "Fixed Example"),
            "uninstall.exe",
            20 * 1024 * 1024);
        WindowsGameDiscovery discovery = new(
        [
            new FixedDirectoryGameDiscoverySource([root]),
        ]);

        GameDiscoverySnapshot snapshot = await discovery.DiscoverAsync();

        DiscoveredGame game = Assert.Single(snapshot.Games);
        Assert.Equal("Fixed Example", game.DisplayName);
        Assert.Equal(executable, game.ExecutablePath, ignoreCase: true);
    }

    [Fact]
    public void PathContainment_PreservesDriveRootSemantics()
    {
        Assert.True(GameDiscoveryIo.TryResolveUnderRoot(
            @"C:\",
            @"XboxGames\Example\Game.exe",
            out string resolved));
        Assert.Equal(
            @"C:\XboxGames\Example\Game.exe",
            resolved,
            ignoreCase: true);
    }

    private sealed class FakeSource(
        GameDiscoverySource source,
        Func<CancellationToken, ValueTask<GameDiscoverySourceBatch>> read)
        : IGameDiscoverySourceAdapter
    {
        public GameDiscoverySource Source { get; } = source;

        public ValueTask<GameDiscoverySourceBatch> DiscoverAsync(
            CancellationToken cancellationToken) => read(cancellationToken);

        internal static FakeSource Returning(
            GameDiscoverySource source,
            params DiscoveredGame[] games) => new(
                source,
                _ => ValueTask.FromResult(new GameDiscoverySourceBatch(
                    source,
                    GameDiscoverySourceState.Succeeded,
                    games)));

        internal static FakeSource Throwing(GameDiscoverySource source) => new(
            source,
            _ => ValueTask.FromException<GameDiscoverySourceBatch>(
                new IOException("fixture failure")));
    }

    private sealed class GameFixture : IDisposable
    {
        internal GameFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "NightGate.GameDiscovery.Tests",
                Guid.NewGuid().ToString("N"));
            _ = System.IO.Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string Directory(params string[] segments)
        {
            string path = PathOf(segments);
            _ = System.IO.Directory.CreateDirectory(path);
            return path;
        }

        internal string Executable(string directory, string name, long length)
        {
            string parent = Directory(directory);
            string path = Path.Combine(parent, name);
            _ = System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write);
            stream.SetLength(length);
            return path;
        }

        internal void Text(string relativePath, string value)
        {
            string path = PathOf(relativePath);
            _ = System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        internal void Bytes(string relativePath, byte[] value)
        {
            string path = PathOf(relativePath);
            _ = System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, value);
        }

        internal string PathOf(params string[] segments) => segments.Aggregate(
            Root,
            Path.Combine);

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

public sealed class GameDiscoveryNativeSmokeTests
{
    [Fact]
    [Trait("Category", "WindowsSmoke")]
    public async Task ProductionDiscovery_IsReadOnlyAndReturnsCanonicalExistingPaths()
    {
        if (!OperatingSystem.IsWindows()
            || !string.Equals(
                Environment.GetEnvironmentVariable("NIGHTGATE_GAME_DISCOVERY_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        GameDiscoverySnapshot snapshot = await new WindowsGameDiscovery().DiscoverAsync();
        Assert.Equal(
            snapshot.Games.Length,
            snapshot.Games.Select(game => game.ExecutablePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.All(snapshot.Games, game => Assert.True(File.Exists(game.ExecutablePath)));
        Console.WriteLine($"DISCOVERED_TOTAL={snapshot.Games.Length}");
        foreach (IGrouping<GameDiscoverySource, DiscoveredGame> source in
                 snapshot.Games.GroupBy(game => game.Source))
        {
            Console.WriteLine($"DISCOVERED_{source.Key}={source.Count()}");
        }

        foreach (GameDiscoverySourceStatus source in snapshot.Sources)
        {
            Console.WriteLine(
                $"SOURCE_{source.Source}={source.State};ACCEPTED={source.CandidateCount}");
        }
    }
}
