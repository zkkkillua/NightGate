using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class ConfiguredRuleProviderTests
{
    [Fact]
    public void Provider_ParsesStrictBoundedConfiguredRules()
    {
        string root = Path.Combine(Path.GetTempPath(), "NightGate", "game.exe");
        string helper = Path.Combine(Path.GetTempPath(), "NightGate", "helper.exe");
        ConfigurationManager configuration = Configuration(JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "game-primary",
                rootExecutablePath = root,
                helperExecutablePaths = new[] { helper },
                category = "game",
                sessionMinutes = 45,
            },
        }));

        ConfiguredRuleProviderResult result =
            new ConfigurationConfiguredRuleProvider(configuration).GetRules();

        Assert.False(result.IsDegraded);
        AppRule rule = Assert.Single(result.Rules);
        Assert.Equal("game-primary", rule.Id);
        Assert.Equal(Path.GetFullPath(root), rule.RootExecutablePath);
        Assert.Equal([Path.GetFullPath(helper)], rule.HelperExecutablePaths.ToArray());
        Assert.Equal(AppRuleCategory.Game, rule.Category);
        Assert.Equal(45, rule.SessionMinutes);
    }

    [Fact]
    public void Provider_ExplicitEmptyArrayIsValid()
    {
        ConfiguredRuleProviderResult result =
            new ConfigurationConfiguredRuleProvider(Configuration("[]")).GetRules();

        Assert.False(result.IsDegraded);
        Assert.Empty(result.Rules);
    }

    [Theory]
    [InlineData(@"\\?\C:\Games\game.exe", @"C:\Games\game.exe")]
    [InlineData(@"\\?\UNC\server\share\game.exe", @"\\server\share\game.exe")]
    public void Provider_NormalizesSupportedExtendedPrefixesBeforePublishingRules(
        string configured,
        string expected)
    {
        string json = JsonSerializer.Serialize(new[] { Rule("game", configured) });

        ConfiguredRuleProviderResult result =
            new ConfigurationConfiguredRuleProvider(Configuration(json)).GetRules();

        Assert.False(result.IsDegraded);
        Assert.Equal(expected, Assert.Single(result.Rules).RootExecutablePath, ignoreCase: true);
    }

    [Theory]
    [InlineData(@"C:\Games\folder.\..\game.exe")]
    [InlineData(@"C:\Games\NUL\..\game.exe")]
    [InlineData(@"C:\Games\bad:stream\..\game.exe")]
    [InlineData(@"C:\Games\game.exe.")]
    [InlineData(@"C:\Games\game.exe ")]
    public void Provider_RejectsAmbiguousRawSegmentsBeforeTheyCanBeNormalized(string path)
    {
        string json = JsonSerializer.Serialize(new[] { Rule("game", path) });

        ConfiguredRuleProviderResult result =
            new ConfigurationConfiguredRuleProvider(Configuration(json)).GetRules();

        Assert.True(result.IsDegraded);
        Assert.Empty(result.Rules);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("[{\"id\":\"game\",\"unknown\":true}]")]
    public void Provider_UnavailableOrMalformedConfigurationIsDegraded(string? json)
    {
        ConfiguredRuleProviderResult result =
            new ConfigurationConfiguredRuleProvider(Configuration(json)).GetRules();

        Assert.True(result.IsDegraded);
        Assert.False(result.EnforcementEnabled);
        Assert.Empty(result.Rules);
        Assert.Equal("configured-rules-invalid", result.DegradationCode);
    }

    [Fact]
    public void Provider_OversizeConfigurationIsDegradedBeforeParsing()
    {
        string json = new(' ', ConfigurationConfiguredRuleProvider.MaximumConfigurationCharacters + 1);

        ConfiguredRuleProviderResult result =
            new ConfigurationConfiguredRuleProvider(Configuration(json)).GetRules();

        Assert.True(result.IsDegraded);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public void Provider_ConfigurationAccessFailureIsExplicitlyDegraded()
    {
        ConfiguredRuleProviderResult result =
            new ConfigurationConfiguredRuleProvider(new ThrowingConfiguration()).GetRules();

        Assert.True(result.IsDegraded);
        Assert.Equal("configured-rules-invalid", result.DegradationCode);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public void Provider_DuplicateStableIdsAreDegradedIgnoringCase()
    {
        string first = Path.Combine(Path.GetTempPath(), "NightGate", "first.exe");
        string second = Path.Combine(Path.GetTempPath(), "NightGate", "second.exe");
        string json = JsonSerializer.Serialize(new[]
        {
            Rule("game", first),
            Rule("GAME", second),
        });

        ConfiguredRuleProviderResult result =
            new ConfigurationConfiguredRuleProvider(Configuration(json)).GetRules();

        Assert.True(result.IsDegraded);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public void Provider_DuplicateCanonicalRootsAreDegradedIgnoringCase()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NightGate");
        string first = Path.Combine(directory, "bin", "..", "game.exe");
        string second = Path.Combine(directory, "GAME.EXE");
        string json = JsonSerializer.Serialize(new[]
        {
            Rule("first", first),
            Rule("second", second),
        });

        ConfiguredRuleProviderResult result =
            new ConfigurationConfiguredRuleProvider(Configuration(json)).GetRules();

        Assert.True(result.IsDegraded);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public void Provider_CrossRuleRootAndHelperAmbiguityIsDegradedIgnoringCase()
    {
        string json = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = "first",
                rootExecutablePath = @"C:\Games\first.exe",
                helperExecutablePaths = new[] { @"C:\Games\shared.exe" },
                category = "game",
                sessionMinutes = 35,
            },
            new
            {
                id = "second",
                rootExecutablePath = @"C:\Games\second.exe",
                helperExecutablePaths = new[] { @"c:\games\FIRST.exe" },
                category = "game",
                sessionMinutes = 35,
            },
        });

        ConfiguredRuleProviderResult result =
            new ConfigurationConfiguredRuleProvider(Configuration(json)).GetRules();

        Assert.True(result.IsDegraded);
        Assert.Empty(result.Rules);
    }

    [Fact]
    public void TeamRescueSnapshot_ContainsOnlyValidatedStableRuleIds()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NightGate");
        AppRule game = Configured("game-id", Path.Combine(directory, "game.exe"));
        AppRule voice = Configured("voice-id", Path.Combine(directory, "voice.exe"), AppRuleCategory.Voice);
        var provider = new FixedConfiguredRuleProvider(
            ConfiguredRuleProviderResult.Success([game, voice]));
        var snapshotProvider = new ConfiguredRuleIdSnapshotProvider(provider);

        ImmutableArray<string> snapshot = snapshotProvider.GetSnapshot();

        Assert.Equal(["game-id", "voice-id"], snapshot.ToArray());
        Assert.DoesNotContain(game.RootExecutablePath!, snapshot);
    }

    [Fact]
    public void TeamRescueSnapshot_GrantsNothingWhenConfigurationIsDegraded()
    {
        var provider = new FixedConfiguredRuleProvider(
            ConfiguredRuleProviderResult.Degraded("configured-rules-invalid"));

        ImmutableArray<string> snapshot =
            new ConfiguredRuleIdSnapshotProvider(provider).GetSnapshot();

        Assert.Empty(snapshot);
    }

    [Fact]
    public void TeamRescueSnapshot_DistinguishesExplicitEmptyConfigurationFromDegradation()
    {
        var validEmpty = new ConfiguredRuleIdSnapshotProvider(
            new FixedConfiguredRuleProvider(ConfiguredRuleProviderResult.Success([])));
        var degraded = new ConfiguredRuleIdSnapshotProvider(
            new FixedConfiguredRuleProvider(
                ConfiguredRuleProviderResult.Degraded("configured-rules-invalid")));

        AllowedProcessSnapshotResult validResult = validEmpty.GetSnapshotResult();
        AllowedProcessSnapshotResult degradedResult = degraded.GetSnapshotResult();

        Assert.True(validResult.IsAvailable);
        Assert.Empty(validResult.Identifiers);
        Assert.False(degradedResult.IsAvailable);
        Assert.Empty(degradedResult.Identifiers);
        Assert.Equal("configured-rules-invalid", degradedResult.DegradationCode);
    }

    [Fact]
    public void TeamRescue_DegradedConfigurationIsRejectedWithoutConsumingTheToken()
    {
        DateTimeOffset now = new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);
        NightState state = new(
            Guid.NewGuid(),
            new DateOnly(2026, 7, 6),
            now,
            NightPhase.LandingLocked,
            null,
            false,
            false,
            false,
            false,
            false,
            false);
        ProgressState progress = ProgressState.Initial;
        var snapshotProvider = new ConfiguredRuleIdSnapshotProvider(
            new FixedConfiguredRuleProvider(
                ConfiguredRuleProviderResult.Degraded("configured-rules-invalid")));

        OverrideDecision decision = new OverridePolicy(snapshotProvider).Request(
            state,
            progress,
            new(OverrideKind.TeamRescue, null),
            now);

        Assert.False(decision.Accepted);
        Assert.Equal(OverrideError.TeamRescueUnavailable, decision.Error);
        Assert.Equal(state, decision.State);
        Assert.Equal(progress, decision.Progress);
        Assert.Null(decision.State.ActiveOverride);
        Assert.Null(decision.Progress.LastTeamRescueAtUtc);
    }

    private static ConfigurationManager Configuration(string? json)
    {
        ConfigurationManager configuration = new();
        if (json is not null)
        {
            configuration[ConfigurationConfiguredRuleProvider.ConfigurationKey] = json;
        }

        return configuration;
    }

    private static object Rule(string id, string rootExecutablePath) => new
    {
        id,
        rootExecutablePath,
        helperExecutablePaths = Array.Empty<string>(),
        category = "game",
        sessionMinutes = 35,
    };

    private static AppRule Configured(
        string id,
        string root,
        AppRuleCategory category = AppRuleCategory.Game) =>
        new(id, root, [], category);

    private sealed class FixedConfiguredRuleProvider(ConfiguredRuleProviderResult result) :
        IConfiguredRuleProvider
    {
        public ConfiguredRuleProviderResult GetRules() => result;
    }

    private sealed class ThrowingConfiguration : IConfiguration
    {
        public string? this[string key]
        {
            get => throw new IOException("configuration-unavailable");
            set => throw new NotSupportedException();
        }

        public IEnumerable<IConfigurationSection> GetChildren() => [];

        public IChangeToken GetReloadToken() =>
            new CancellationChangeToken(CancellationToken.None);

        public IConfigurationSection GetSection(string key) =>
            throw new NotSupportedException();
    }
}
