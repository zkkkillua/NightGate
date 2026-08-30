using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace NightGate.Desktop;

internal sealed class EpicGameDiscoverySource : BackgroundGameDiscoverySource
{
    private const int MaximumManifestFiles = 512;
    private const int MaximumManifestBytes = 1024 * 1024;
    private readonly ImmutableArray<string>? _manifestDirectories;

    internal EpicGameDiscoverySource(IEnumerable<string>? manifestDirectories = null)
    {
        _manifestDirectories = manifestDirectories?.ToImmutableArray();
    }

    public override GameDiscoverySource Source => GameDiscoverySource.Epic;

    protected override GameDiscoverySourceBatch DiscoverCore(
        CancellationToken cancellationToken)
    {
        bool available = false;
        bool degraded = false;
        List<DiscoveredGame> games = [];
        foreach (string manifestDirectory in ManifestDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(manifestDirectory))
            {
                continue;
            }

            available = true;
            IReadOnlyList<string> manifests = GameDiscoveryIo.EnumerateFilesBounded(
                manifestDirectory,
                "*.item",
                MaximumManifestFiles,
                out bool enumerationDegraded);
            degraded |= enumerationDegraded;
            foreach (string manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadManifest(manifest, out DiscoveredGame? game))
                {
                    degraded = true;
                    continue;
                }

                if (game is not null)
                {
                    games.Add(game);
                }
            }
        }

        return new(
            Source,
            GameDiscoveryIo.State(available, degraded),
            games);
    }

    private IEnumerable<string> ManifestDirectories()
    {
        if (_manifestDirectories is { } configured)
        {
            return configured;
        }

        string programData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        return string.IsNullOrWhiteSpace(programData)
            ? []
            : [Path.Combine(
                programData,
                "Epic",
                "EpicGamesLauncher",
                "Data",
                "Manifests")];
    }

    private static bool TryReadManifest(
        string path,
        out DiscoveredGame? game)
    {
        game = null;
        if (!GameDiscoveryIo.TryReadText(path, MaximumManifestBytes, out string json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 32,
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || IsTrue(root, "bIsIncompleteInstall")
                || IsFalse(root, "bIsApplication")
                || !TryString(root, "DisplayName", out string displayName)
                || !TryString(root, "InstallLocation", out string installLocation)
                || !TryString(root, "LaunchExecutable", out string launchExecutable))
            {
                return true;
            }

            if (!GameDiscoveryIo.TryResolveUnderRoot(
                    installLocation,
                    launchExecutable,
                    out string executablePath)
                || !File.Exists(executablePath))
            {
                return true;
            }

            game = new(
                displayName,
                executablePath,
                GameDiscoverySource.Epic,
                GameDiscoveryConfidence.High);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString() ?? string.Empty);
    }

    private static bool IsTrue(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.True;

    private static bool IsFalse(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.False;
}

internal sealed class XboxGamingServicesGameDiscoverySource
    : BackgroundGameDiscoverySource
{
    private const int MaximumGamingRootBytes = 4096;
    private const int MaximumConfigBytes = 1024 * 1024;
    private const int MaximumGamesPerRoot = 256;
    private readonly ImmutableArray<string>? _driveRoots;

    internal XboxGamingServicesGameDiscoverySource(
        IEnumerable<string>? driveRoots = null)
    {
        _driveRoots = driveRoots?.ToImmutableArray();
    }

    public override GameDiscoverySource Source =>
        GameDiscoverySource.XboxGamingServices;

    protected override GameDiscoverySourceBatch DiscoverCore(
        CancellationToken cancellationToken)
    {
        bool available = false;
        bool degraded = false;
        List<DiscoveredGame> games = [];
        foreach (string driveRoot in DriveRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string gamingRoot in GamingRoots(driveRoot, ref degraded))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(gamingRoot))
                {
                    continue;
                }

                available = true;
                IReadOnlyList<string> installDirectories =
                    GameDiscoveryIo.EnumerateDirectoriesBounded(
                        gamingRoot,
                        MaximumGamesPerRoot,
                        out bool enumerationDegraded);
                degraded |= enumerationDegraded;
                foreach (string installDirectory in installDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string contentRoot = Path.Combine(installDirectory, "Content");
                    string config = Path.Combine(contentRoot, "MicrosoftGame.config");
                    if (!File.Exists(config))
                    {
                        continue;
                    }

                    if (!TryReadConfig(
                            config,
                            contentRoot,
                            Path.GetFileName(installDirectory),
                            out IReadOnlyList<DiscoveredGame> configured))
                    {
                        degraded = true;
                        continue;
                    }

                    games.AddRange(configured);
                }
            }
        }

        return new(
            Source,
            GameDiscoveryIo.State(available, degraded),
            games);
    }

    private IEnumerable<string> DriveRoots()
    {
        if (_driveRoots is { } configured)
        {
            return configured;
        }

        List<string> roots = [];
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    {
                        roots.Add(drive.RootDirectory.FullName);
                    }
                }
                catch (Exception exception) when (GameDiscoveryIo.IsExpectedIo(exception))
                {
                }
            }
        }
        catch (Exception exception) when (GameDiscoveryIo.IsExpectedIo(exception))
        {
        }

        return roots;
    }

    private static IReadOnlyList<string> GamingRoots(
        string driveRoot,
        ref bool degraded)
    {
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            string fullDriveRoot = Path.GetFullPath(driveRoot);
            string fixedRoot = Path.Combine(fullDriveRoot, "XboxGames");
            _ = roots.Add(fixedRoot);

            string marker = Path.Combine(fullDriveRoot, ".GamingRoot");
            if (File.Exists(marker))
            {
                if (TryReadGamingRoot(marker, fullDriveRoot, out string discovered))
                {
                    _ = roots.Add(discovered);
                }
                else
                {
                    degraded = true;
                }
            }
        }
        catch (Exception exception) when (GameDiscoveryIo.IsExpectedIo(exception))
        {
            degraded = true;
        }

        return roots.ToArray();
    }

    private static bool TryReadGamingRoot(
        string marker,
        string driveRoot,
        out string gamingRoot)
    {
        gamingRoot = string.Empty;
        if (!GameDiscoveryIo.TryReadBytes(
                marker,
                MaximumGamingRootBytes,
                out byte[] bytes)
            || bytes.Length < 10
            || bytes[0] != (byte)'R'
            || bytes[1] != (byte)'G'
            || bytes[2] != (byte)'B'
            || bytes[3] != (byte)'X'
            || BitConverter.ToInt32(bytes, 4) != 1
            || (bytes.Length - 8) % 2 != 0)
        {
            return false;
        }

        string relative = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8)
            .TrimEnd('\0')
            .Trim();
        return GameDiscoveryIo.TryResolveDirectoryUnderRoot(
            driveRoot,
            relative,
            out gamingRoot);
    }

    private static bool TryReadConfig(
        string configPath,
        string contentRoot,
        string fallbackName,
        out IReadOnlyList<DiscoveredGame> games)
    {
        games = [];
        if (!GameDiscoveryIo.TryReadText(
                configPath,
                MaximumConfigBytes,
                out string xml))
        {
            return false;
        }

        try
        {
            XmlReaderSettings settings = new()
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumConfigBytes,
            };
            using StringReader text = new(xml);
            using XmlReader reader = XmlReader.Create(text, settings);
            XDocument document = XDocument.Load(reader, LoadOptions.None);
            XElement? shellVisuals = document
                .Descendants()
                .FirstOrDefault(static element =>
                    element.Name.LocalName == "ShellVisuals");
            string displayName = shellVisuals?.Attributes()
                .FirstOrDefault(static attribute =>
                    attribute.Name.LocalName == "DefaultDisplayName")
                ?.Value
                ?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(displayName)
                || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                displayName = fallbackName;
            }

            List<DiscoveredGame> configured = [];
            foreach (XElement executable in document.Descendants().Where(
                         static element => element.Name.LocalName == "Executable"))
            {
                XAttribute? targetFamily = executable.Attributes().FirstOrDefault(
                    static attribute => attribute.Name.LocalName == "TargetDeviceFamily");
                if (targetFamily is not null
                    && !string.Equals(
                        targetFamily.Value,
                        "PC",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? relative = executable.Attributes().FirstOrDefault(
                    static attribute => attribute.Name.LocalName == "Name")?.Value;
                if (!GameDiscoveryIo.TryResolveUnderRoot(
                        contentRoot,
                        relative,
                        out string executablePath)
                    || !File.Exists(executablePath))
                {
                    continue;
                }

                configured.Add(new(
                    displayName,
                    executablePath,
                    GameDiscoverySource.XboxGamingServices,
                    GameDiscoveryConfidence.High));
            }

            games = configured;
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}

internal sealed class SteamGameDiscoverySource : BackgroundGameDiscoverySource
{
    private const int MaximumTextBytes = 2 * 1024 * 1024;
    private const int MaximumLibraries = 64;
    private const int MaximumManifestsPerLibrary = 1024;
    private readonly ImmutableArray<string>? _steamRoots;

    internal SteamGameDiscoverySource(IEnumerable<string>? steamRoots = null)
    {
        _steamRoots = steamRoots?.ToImmutableArray();
    }

    public override GameDiscoverySource Source => GameDiscoverySource.Steam;

    protected override GameDiscoverySourceBatch DiscoverCore(
        CancellationToken cancellationToken)
    {
        bool available = false;
        bool degraded = false;
        List<DiscoveredGame> games = [];
        HashSet<string> libraries = new(StringComparer.OrdinalIgnoreCase);
        foreach (string steamRoot in SteamRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string steamApps;
            try
            {
                steamApps = Path.Combine(Path.GetFullPath(steamRoot), "steamapps");
            }
            catch (Exception exception) when (GameDiscoveryIo.IsExpectedIo(exception))
            {
                degraded = true;
                continue;
            }

            if (!Directory.Exists(steamApps))
            {
                continue;
            }

            available = true;
            _ = libraries.Add(steamApps);
            string libraryFile = Path.Combine(steamApps, "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
            {
                continue;
            }

            if (!GameDiscoveryIo.TryReadText(
                    libraryFile,
                    MaximumTextBytes,
                    out string vdf))
            {
                degraded = true;
                continue;
            }

            IReadOnlyList<string> paths = ValveKeyValueReader.Values(vdf, "path");
            if (paths.Count > MaximumLibraries)
            {
                degraded = true;
            }

            foreach (string path in paths.Take(MaximumLibraries))
            {
                try
                {
                    if (GameDiscoveryIo.IsLocalAbsolutePath(path))
                    {
                        _ = libraries.Add(Path.Combine(Path.GetFullPath(path), "steamapps"));
                    }
                }
                catch (Exception exception) when (GameDiscoveryIo.IsExpectedIo(exception))
                {
                    degraded = true;
                }
            }
        }

        foreach (string steamApps in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(steamApps))
            {
                degraded = true;
                continue;
            }

            IReadOnlyList<string> manifests = GameDiscoveryIo.EnumerateFilesBounded(
                steamApps,
                "appmanifest_*.acf",
                MaximumManifestsPerLibrary,
                out bool enumerationDegraded);
            degraded |= enumerationDegraded;
            foreach (string manifest in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadManifest(
                        manifest,
                        steamApps,
                        cancellationToken,
                        out DiscoveredGame? game,
                        out bool manifestDegraded))
                {
                    degraded = true;
                    continue;
                }

                degraded |= manifestDegraded;
                if (game is not null)
                {
                    games.Add(game);
                }
            }
        }

        return new(
            Source,
            GameDiscoveryIo.State(available, degraded),
            games);
    }

    private IEnumerable<string> SteamRoots()
    {
        if (_steamRoots is { } configured)
        {
            return configured;
        }

        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        foreach ((RegistryHive hive, RegistryView view, string key, string value) in new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.CurrentUser, RegistryView.Registry32, @"Software\Valve\Steam", "SteamPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\WOW6432Node\Valve\Steam", "InstallPath"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Valve\Steam", "InstallPath"),
        })
        {
            if (GameDiscoveryRegistryReader.TryReadString(
                    hive,
                    view,
                    key,
                    value,
                    out string root))
            {
                _ = roots.Add(root);
            }
        }

        string programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            _ = roots.Add(Path.Combine(programFilesX86, "Steam"));
        }

        return roots;
    }

    private static bool TryReadManifest(
        string manifest,
        string steamApps,
        CancellationToken cancellationToken,
        out DiscoveredGame? game,
        out bool degraded)
    {
        game = null;
        degraded = false;
        if (!GameDiscoveryIo.TryReadText(
                manifest,
                MaximumTextBytes,
                out string acf))
        {
            return false;
        }

        if (!ValveKeyValueReader.TryValue(acf, "name", out string name)
            || !ValveKeyValueReader.TryValue(acf, "installdir", out string installDirectory)
            || string.IsNullOrWhiteSpace(name)
            || IsNonGameManifest(name))
        {
            return true;
        }

        string common = Path.Combine(steamApps, "common");
        if (!GameDiscoveryIo.TryResolveDirectoryUnderRoot(
                common,
                installDirectory,
                out string installRoot)
            || !Directory.Exists(installRoot))
        {
            return true;
        }

        BoundedExecutableWalk walk = GameDiscoveryIo.FindExecutables(
            installRoot,
            maximumDepth: 5,
            maximumDirectories: 512,
            maximumExecutables: 2048,
            cancellationToken);
        degraded |= walk.Degraded;
        RankedExecutable? best = GameExecutableSelector.SelectBest(
            name,
            installRoot,
            walk.Paths);
        if (best is null)
        {
            return true;
        }

        game = new(
            name,
            best.Path,
            GameDiscoverySource.Steam,
            best.Score >= 60
                ? GameDiscoveryConfidence.Medium
                : GameDiscoveryConfidence.Low);
        return true;
    }

    private static bool IsNonGameManifest(string name) =>
        name.Contains("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(" Dedicated Server", StringComparison.OrdinalIgnoreCase);
}

internal sealed record UninstallRegistryEntry(
    string KeyName,
    string DisplayName,
    string? DisplayIcon,
    string? InstallLocation,
    string? Publisher,
    bool IsSystemComponent,
    string? ReleaseType);

internal sealed class UninstallRegistryGameDiscoverySource
    : BackgroundGameDiscoverySource
{
    private const int MaximumEntries = 4096;
    private readonly ImmutableArray<UninstallRegistryEntry>? _configuredEntries;

    internal UninstallRegistryGameDiscoverySource(
        IEnumerable<UninstallRegistryEntry>? entries = null)
    {
        _configuredEntries = entries?.ToImmutableArray();
    }

    public override GameDiscoverySource Source =>
        GameDiscoverySource.UninstallRegistry;

    protected override GameDiscoverySourceBatch DiscoverCore(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<UninstallRegistryEntry> entries;
        bool available;
        bool degraded;
        if (_configuredEntries is { } configured)
        {
            entries = configured;
            available = true;
            degraded = false;
        }
        else
        {
            entries = GameDiscoveryRegistryReader.ReadUninstallEntries(
                MaximumEntries,
                cancellationToken,
                out available,
                out degraded);
        }

        List<DiscoveredGame> games = [];
        foreach (UninstallRegistryEntry entry in entries.Take(MaximumEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsGameLike(entry))
            {
                continue;
            }

            string? executable = TryDisplayIconPath(entry.DisplayIcon);
            if (executable is not null
                && !GameExecutableSelector.IsClearlyNonGame(executable)
                && File.Exists(executable))
            {
                games.Add(new(
                    entry.DisplayName,
                    executable,
                    Source,
                    GameDiscoveryConfidence.Low));
                continue;
            }

            string? installLocation = entry.InstallLocation;
            if (string.IsNullOrWhiteSpace(installLocation)
                || !GameDiscoveryIo.IsLocalAbsolutePath(installLocation)
                || !Directory.Exists(installLocation))
            {
                continue;
            }

            BoundedExecutableWalk walk = GameDiscoveryIo.FindExecutables(
                installLocation,
                maximumDepth: 4,
                maximumDirectories: 256,
                maximumExecutables: 1024,
                cancellationToken);
            degraded |= walk.Degraded;
            RankedExecutable? best = GameExecutableSelector.SelectBest(
                entry.DisplayName,
                installLocation,
                walk.Paths);
            if (best is not null)
            {
                games.Add(new(
                    entry.DisplayName,
                    best.Path,
                    Source,
                    GameDiscoveryConfidence.Low));
            }
        }

        if (entries.Count > MaximumEntries)
        {
            degraded = true;
        }

        return new(Source, GameDiscoveryIo.State(available, degraded), games);
    }

    private static string? TryDisplayIconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon) || displayIcon.Length > 4096)
        {
            return null;
        }

        string expanded;
        try
        {
            expanded = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        }
        catch (ArgumentException)
        {
            return null;
        }

        Match match = Regex.Match(
            expanded,
            "^\\s*\\\"?(?<path>[^\\\"]+?\\.exe)\\\"?(?:\\s*,\\s*-?\\d+)?\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        return match.Success
            && GameDiscoveryIo.IsLocalAbsolutePath(match.Groups["path"].Value)
                ? match.Groups["path"].Value
                : null;
    }

    private static bool IsGameLike(UninstallRegistryEntry entry)
    {
        if (entry.IsSystemComponent
            || !string.IsNullOrWhiteSpace(entry.ReleaseType)
            || string.IsNullOrWhiteSpace(entry.DisplayName)
            || IsLauncherProduct(entry.DisplayName))
        {
            return false;
        }

        if (entry.KeyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string evidence = string.Join(
            '|',
            entry.Publisher,
            entry.InstallLocation,
            entry.DisplayIcon);
        string[] markers =
        [
            @"\steamapps\common\",
            @"\epic games\",
            @"\xboxgames\",
            @"\gog games\",
            @"\riot games\",
            @"\ea games\",
            @"\ubisoft game launcher\games\",
            "electronic arts",
            "ubisoft",
            "riot games",
            "blizzard entertainment",
            "gog.com",
            "rockstar games",
            "cd projekt",
            "bandai namco",
            "capcom",
            "square enix",
            "bethesda",
            "activision",
        ];
        return markers.Any(marker =>
            evidence.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLauncherProduct(string displayName)
    {
        string normalized = GameDiscoveryText.NormalizeForMatching(displayName);
        string[] launcherProducts =
        [
            "steam",
            "epicgameslauncher",
            "eaapp",
            "ubisoftconnect",
            "goggalaxy",
            "battlenet",
            "riotclient",
            "rockstargameslauncher",
            "gamingservices",
            "xbox",
        ];
        return launcherProducts.Contains(normalized, StringComparer.Ordinal);
    }
}

internal sealed class FixedDirectoryGameDiscoverySource
    : BackgroundGameDiscoverySource
{
    private const int MaximumInstallDirectories = 256;
    private readonly ImmutableArray<string>? _roots;

    internal FixedDirectoryGameDiscoverySource(IEnumerable<string>? roots = null)
    {
        _roots = roots?.ToImmutableArray();
    }

    public override GameDiscoverySource Source =>
        GameDiscoverySource.FixedDirectory;

    protected override GameDiscoverySourceBatch DiscoverCore(
        CancellationToken cancellationToken)
    {
        bool available = false;
        bool degraded = false;
        List<DiscoveredGame> games = [];
        foreach (string root in Roots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            available = true;
            IReadOnlyList<string> installs = GameDiscoveryIo.EnumerateDirectoriesBounded(
                root,
                MaximumInstallDirectories,
                out bool enumerationDegraded);
            degraded |= enumerationDegraded;
            foreach (string install in installs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BoundedExecutableWalk walk = GameDiscoveryIo.FindExecutables(
                    install,
                    maximumDepth: 4,
                    maximumDirectories: 256,
                    maximumExecutables: 1024,
                    cancellationToken);
                degraded |= walk.Degraded;
                string displayName = Path.GetFileName(install);
                RankedExecutable? best = GameExecutableSelector.SelectBest(
                    displayName,
                    install,
                    walk.Paths);
                if (best is not null)
                {
                    games.Add(new(
                        displayName,
                        best.Path,
                        Source,
                        GameDiscoveryConfidence.Low));
                }
            }
        }

        return new(Source, GameDiscoveryIo.State(available, degraded), games);
    }

    private IEnumerable<string> Roots()
    {
        if (_roots is { } configured)
        {
            return configured;
        }

        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        string programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            _ = roots.Add(Path.Combine(programFiles, "EA Games"));
        }

        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            _ = roots.Add(Path.Combine(
                programFilesX86,
                "Ubisoft",
                "Ubisoft Game Launcher",
                "games"));
            _ = roots.Add(Path.Combine(programFilesX86, "GOG Galaxy", "Games"));
        }

        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        _ = roots.Add(Path.Combine(systemDrive, "GOG Games"));
        _ = roots.Add(Path.Combine(systemDrive, "Riot Games"));
        return roots;
    }
}

internal static class GameDiscoveryRegistryReader
{
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    internal static bool TryReadString(
        RegistryHive hive,
        RegistryView view,
        string keyPath,
        string valueName,
        out string value)
    {
        value = string.Empty;
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(keyPath, writable: false);
            value = key?.GetValue(
                valueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception exception) when (GameDiscoveryIo.IsExpectedIo(exception))
        {
            return false;
        }
    }

    internal static IReadOnlyList<UninstallRegistryEntry> ReadUninstallEntries(
        int maximumEntries,
        CancellationToken cancellationToken,
        out bool available,
        out bool degraded)
    {
        available = false;
        degraded = false;
        List<UninstallRegistryEntry> entries = [];
        foreach (RegistryHive hive in new[]
                 {
                     RegistryHive.CurrentUser,
                     RegistryHive.LocalMachine,
                 })
        {
            foreach (RegistryView view in new[]
                     {
                         RegistryView.Registry64,
                         RegistryView.Registry32,
                     })
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? uninstall = baseKey.OpenSubKey(
                        UninstallKey,
                        writable: false);
                    if (uninstall is null)
                    {
                        continue;
                    }

                    available = true;
                    foreach (string subKeyName in uninstall.GetSubKeyNames())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entries.Count >= maximumEntries)
                        {
                            degraded = true;
                            return entries;
                        }

                        try
                        {
                            using RegistryKey? entry = uninstall.OpenSubKey(
                                subKeyName,
                                writable: false);
                            string displayName = StringValue(entry, "DisplayName");
                            if (entry is null || string.IsNullOrWhiteSpace(displayName))
                            {
                                continue;
                            }

                            entries.Add(new(
                                subKeyName,
                                displayName,
                                NullIfEmpty(StringValue(entry, "DisplayIcon")),
                                NullIfEmpty(StringValue(entry, "InstallLocation")),
                                NullIfEmpty(StringValue(entry, "Publisher")),
                                IntValue(entry, "SystemComponent") == 1,
                                NullIfEmpty(StringValue(entry, "ReleaseType"))));
                        }
                        catch (Exception exception) when (GameDiscoveryIo.IsExpectedIo(exception))
                        {
                            degraded = true;
                        }
                    }
                }
                catch (Exception exception) when (GameDiscoveryIo.IsExpectedIo(exception))
                {
                    degraded = true;
                }
            }
        }

        return entries;
    }

    private static string StringValue(RegistryKey? key, string name) =>
        key?.GetValue(
            name,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;

    private static int IntValue(RegistryKey key, string name)
    {
        object? value = key.GetValue(name, 0, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            int integer => integer,
            string text when int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed) => parsed,
            _ => 0,
        };
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

internal static class ValveKeyValueReader
{
    internal static bool TryValue(string text, string key, out string value)
    {
        value = Values(text, key).FirstOrDefault() ?? string.Empty;
        return value.Length > 0;
    }

    internal static IReadOnlyList<string> Values(string text, string key)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(key))
        {
            return [];
        }

        MatchCollection matches = Regex.Matches(
            text,
            "\\\"" + Regex.Escape(key)
                + "\\\"\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));
        List<string> values = new(Math.Min(matches.Count, 1024));
        foreach (Match match in matches.Cast<Match>().Take(1024))
        {
            string value = Unescape(match.Groups["value"].Value).Trim();
            if (value.Length > 0)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string Unescape(string value)
    {
        StringBuilder builder = new(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current == '\\' && index + 1 < value.Length)
            {
                char next = value[index + 1];
                if (next is '\\' or '"')
                {
                    builder.Append(next);
                    index++;
                    continue;
                }
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}

internal sealed record BoundedExecutableWalk(
    IReadOnlyList<string> Paths,
    bool Degraded);

internal sealed record RankedExecutable(string Path, int Score, long Length);

internal static class GameExecutableSelector
{
    private static readonly string[] ExcludedTokens =
    [
        "unins",
        "uninstall",
        "setup",
        "installer",
        "crashreport",
        "errorreport",
        "reportclient",
        "vcredist",
        "vc_redist",
        "dxsetup",
        "easyanticheat",
        "battleye",
        "epicwebhelper",
        "webhelper",
        "updater",
    ];

    internal static RankedExecutable? SelectBest(
        string displayName,
        string installRoot,
        IEnumerable<string> executablePaths)
    {
        string normalizedName = GameDiscoveryText.NormalizeForMatching(displayName);
        List<RankedExecutable> ranked = [];
        foreach (string path in executablePaths)
        {
            if (IsClearlyNonGame(path))
            {
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(path);
            string normalizedStem = GameDiscoveryText.NormalizeForMatching(stem);
            string relative;
            try
            {
                relative = Path.GetRelativePath(installRoot, path);
            }
            catch (Exception exception) when (GameDiscoveryIo.IsExpectedIo(exception))
            {
                continue;
            }

            int score = 0;
            if (normalizedName.Length > 0 && normalizedStem == normalizedName)
            {
                score += 100;
            }
            else if (normalizedName.Length >= 4
                && normalizedStem.Length >= 4
                && (normalizedStem.Contains(normalizedName, StringComparison.Ordinal)
                    || normalizedName.Contains(normalizedStem, StringComparison.Ordinal)))
            {
                score += 70;
            }

            if (normalizedStem.Contains("win64shipping", StringComparison.Ordinal))
            {
                score += 35;
            }

            if (relative.Contains(
                    $"Binaries{Path.DirectorySeparatorChar}Win64",
                    StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
            }

            if (!relative.Contains(Path.DirectorySeparatorChar))
            {
                score += 5;
            }

            if (normalizedStem.Contains("launcher", StringComparison.Ordinal)
                || normalizedStem.Contains("prelauncher", StringComparison.Ordinal))
            {
                score -= 35;
            }

            if (normalizedStem.StartsWith("play", StringComparison.Ordinal))
            {
                score -= 20;
            }

            long length = GameDiscoveryIo.TryGetFileLength(path);
            score += length switch
            {
                >= 10 * 1024 * 1024 => 30,
                >= 1024 * 1024 => 15,
                >= 128 * 1024 => 5,
                _ => 0,
            };
            ranked.Add(new(path, score, length));
        }

        return ranked
            .OrderByDescending(static candidate => candidate.Score)
            .ThenByDescending(static candidate => candidate.Length)
            .ThenBy(static candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    internal static bool IsClearlyNonGame(string path)
    {
        string fileName = Path.GetFileName(path);
        string relative = path.Replace('/', '\\');
        if (ExcludedTokens.Any(token =>
                fileName.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string[] excludedDirectories =
        [
            @"\_CommonRedist\",
            @"\Redistributables\",
            @"\Redist\",
            @"\Installers\",
        ];
        return excludedDirectories.Any(directory =>
            relative.Contains(directory, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class GameDiscoveryText
{
    internal static string NormalizeForMatching(string? value) => new(
        (value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
}

internal static class GameDiscoveryIo
{
    internal static GameDiscoverySourceState State(bool available, bool degraded) =>
        !available
            ? GameDiscoverySourceState.Unavailable
            : degraded
                ? GameDiscoverySourceState.Degraded
                : GameDiscoverySourceState.Succeeded;

    internal static bool TryReadText(
        string path,
        int maximumBytes,
        out string text)
    {
        text = string.Empty;
        try
        {
            FileInfo info = new(path);
            if (!info.Exists || info.Length < 0 || info.Length > maximumBytes)
            {
                return false;
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            return Encoding.UTF8.GetByteCount(text) <= maximumBytes * 2;
        }
        catch (Exception exception) when (IsExpectedIo(exception))
        {
            return false;
        }
    }

    internal static bool TryReadBytes(
        string path,
        int maximumBytes,
        out byte[] bytes)
    {
        bytes = [];
        try
        {
            FileInfo info = new(path);
            if (!info.Exists || info.Length < 0 || info.Length > maximumBytes)
            {
                return false;
            }

            bytes = File.ReadAllBytes(path);
            return bytes.Length <= maximumBytes;
        }
        catch (Exception exception) when (IsExpectedIo(exception))
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> EnumerateFilesBounded(
        string directory,
        string pattern,
        int maximumFiles,
        out bool degraded)
    {
        degraded = false;
        List<string> files = [];
        try
        {
            foreach (string file in Directory.EnumerateFiles(
                         directory,
                         pattern,
                         SearchOption.TopDirectoryOnly))
            {
                if (files.Count >= maximumFiles)
                {
                    degraded = true;
                    break;
                }

                files.Add(file);
            }
        }
        catch (Exception exception) when (IsExpectedIo(exception))
        {
            degraded = true;
        }

        return files;
    }

    internal static IReadOnlyList<string> EnumerateDirectoriesBounded(
        string directory,
        int maximumDirectories,
        out bool degraded)
    {
        degraded = false;
        List<string> directories = [];
        try
        {
            foreach (string child in Directory.EnumerateDirectories(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (directories.Count >= maximumDirectories)
                {
                    degraded = true;
                    break;
                }

                if (IsReparsePoint(child, out bool attributeFailure))
                {
                    degraded = true;
                    continue;
                }

                directories.Add(child);
            }
        }
        catch (Exception exception) when (IsExpectedIo(exception))
        {
            degraded = true;
        }

        return directories;
    }

    internal static BoundedExecutableWalk FindExecutables(
        string root,
        int maximumDepth,
        int maximumDirectories,
        int maximumExecutables,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return new([], false);
        }

        if (IsReparsePoint(root, out bool rootAttributeFailure))
        {
            return new([], true);
        }

        Queue<(string Path, int Depth)> pending = new();
        pending.Enqueue((root, 0));
        List<string> executables = [];
        int directories = 0;
        bool degraded = false;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string current, int depth) = pending.Dequeue();
            if (++directories > maximumDirectories)
            {
                degraded = true;
                break;
            }

            try
            {
                foreach (string executable in Directory.EnumerateFiles(
                             current,
                             "*.exe",
                             SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (executables.Count >= maximumExecutables)
                    {
                        degraded = true;
                        return new(executables, degraded);
                    }

                    if (TryResolveUnderRoot(root, executable, out string contained))
                    {
                        executables.Add(contained);
                    }
                }
            }
            catch (Exception exception) when (IsExpectedIo(exception))
            {
                degraded = true;
            }

            if (depth >= maximumDepth)
            {
                continue;
            }

            try
            {
                foreach (string child in Directory.EnumerateDirectories(
                             current,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsReparsePoint(child, out bool attributeFailure))
                    {
                        degraded = true;
                        continue;
                    }

                    pending.Enqueue((child, depth + 1));
                    if (pending.Count + directories > maximumDirectories)
                    {
                        degraded = true;
                        break;
                    }
                }
            }
            catch (Exception exception) when (IsExpectedIo(exception))
            {
                degraded = true;
            }
        }

        return new(executables, degraded);
    }

    internal static bool TryResolveUnderRoot(
        string root,
        string? candidate,
        out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(root)
            || string.IsNullOrWhiteSpace(candidate)
            || !IsLocalAbsolutePath(root))
        {
            return false;
        }

        try
        {
            string fullRoot = Path.GetFullPath(root);
            string fullCandidate = Path.IsPathFullyQualified(candidate)
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(Path.Combine(fullRoot, candidate));
            string prefix = Path.EndsInDirectorySeparator(fullRoot)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;
            if (!fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            resolved = fullCandidate;
            return true;
        }
        catch (Exception exception) when (IsExpectedIo(exception))
        {
            return false;
        }
    }

    internal static bool TryResolveDirectoryUnderRoot(
        string root,
        string? candidate,
        out string resolved) => TryResolveUnderRoot(root, candidate, out resolved)
        && !string.Equals(
            Path.GetExtension(resolved),
            ".exe",
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsLocalAbsolutePath(string? path) =>
        path is { Length: >= 3 }
        && !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        && !path.StartsWith("\\\\", StringComparison.Ordinal)
        && path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && (path[2] == '\\' || path[2] == '/');

    internal static long TryGetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (IsExpectedIo(exception))
        {
            return 0;
        }
    }

    internal static bool IsExpectedIo(Exception exception) => exception is
        IOException
        or UnauthorizedAccessException
        or SecurityException
        or ArgumentException
        or NotSupportedException;

    private static bool IsReparsePoint(string path, out bool degraded)
    {
        degraded = false;
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (IsExpectedIo(exception))
        {
            degraded = true;
            return true;
        }
    }
}
