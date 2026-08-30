using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class AppRuleTests
{
    [Fact]
    public void LegacyConstructor_RemainsNonEnforceable()
    {
        var rule = new AppRule(" game ");

        Assert.Equal("game", rule.Id);
        Assert.False(rule.IsConfigured);
        Assert.Null(rule.RootExecutablePath);
        Assert.Empty(rule.HelperExecutablePaths);
        Assert.Null(rule.Category);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RejectsBlankStableId(string id)
    {
        var error = Assert.Throws<ArgumentException>(() => new AppRule(id));

        Assert.Equal("id", error.ParamName);
    }

    [Fact]
    public void ConfiguredConstructor_CanonicalizesExecutablePathsAndCopiesHelpers()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NightGate", "Rules");
        string root = Path.Combine(directory, "Game", "..", "game.exe");
        List<string> helpers =
        [
            Path.Combine(directory, "bin", "..", "voice-helper.EXE"),
        ];

        var rule = new AppRule(
            "game-primary",
            root,
            helpers,
            AppRuleCategory.Game,
            45);
        helpers.Clear();

        Assert.True(rule.IsConfigured);
        Assert.Equal(Path.GetFullPath(root), rule.RootExecutablePath);
        Assert.Equal(
            [Path.GetFullPath(Path.Combine(directory, "voice-helper.EXE"))],
            rule.HelperExecutablePaths.ToArray());
        Assert.Equal(AppRuleCategory.Game, rule.Category);
        Assert.Equal(45, rule.SessionMinutes);
    }

    [Theory]
    [InlineData(@"\\?\C:\Games\game.exe", @"C:\Games\game.exe")]
    [InlineData(@"\\?\UNC\server\share\game.exe", @"\\server\share\game.exe")]
    public void ConfiguredConstructor_NormalizesSupportedExtendedPrefixes(
        string configured,
        string expected)
    {
        var rule = new AppRule(
            "game",
            configured,
            [],
            AppRuleCategory.Game);

        Assert.Equal(expected, rule.RootExecutablePath, ignoreCase: true);
    }

    [Theory]
    [InlineData(@"C:\Games\folder.\..\game.exe")]
    [InlineData(@"C:\Games\NUL\..\game.exe")]
    [InlineData(@"C:\Games\bad:stream\..\game.exe")]
    [InlineData(@"C:\Games\game.exe.")]
    [InlineData(@"C:\Games\game.exe ")]
    public void ConfiguredConstructor_RejectsAmbiguousSegmentsBeforeResolution(string path)
    {
        Assert.Throws<ArgumentException>(() => new AppRule(
            "game",
            path,
            [],
            AppRuleCategory.Game));
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData("relative.txt")]
    public void ConfiguredConstructor_RejectsNonAbsoluteRoot(string root)
    {
        Assert.Throws<ArgumentException>(() => new AppRule(
            "game",
            root,
            [],
            AppRuleCategory.Game));
    }

    [Fact]
    public void ConfiguredConstructor_RejectsRootWithoutExeExtension()
    {
        string root = Path.Combine(Path.GetTempPath(), "NightGate", "game.com");

        var error = Assert.Throws<ArgumentException>(() => new AppRule(
            "game",
            root,
            [],
            AppRuleCategory.Game));

        Assert.Equal("rootExecutablePath", error.ParamName);
    }

    [Fact]
    public void ConfiguredConstructor_RejectsHelperEqualToRootIgnoringCase()
    {
        string root = Path.Combine(Path.GetTempPath(), "NightGate", "game.exe");

        var error = Assert.Throws<ArgumentException>(() => new AppRule(
            "game",
            root,
            [root.ToUpperInvariant()],
            AppRuleCategory.Game));

        Assert.Equal("helperExecutablePaths", error.ParamName);
    }

    [Fact]
    public void ConfiguredConstructor_RejectsHelpersDuplicatedAfterCanonicalization()
    {
        string directory = Path.Combine(Path.GetTempPath(), "NightGate");
        string root = Path.Combine(directory, "game.exe");
        string helper = Path.Combine(directory, "bin", "..", "helper.exe");

        var error = Assert.Throws<ArgumentException>(() => new AppRule(
            "game",
            root,
            [helper, Path.Combine(directory, "HELPER.EXE")],
            AppRuleCategory.Game));

        Assert.Equal("helperExecutablePaths", error.ParamName);
    }

    [Fact]
    public void ConfiguredConstructor_RejectsInvalidHelperPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "NightGate", "game.exe");

        var error = Assert.Throws<ArgumentException>(() => new AppRule(
            "game",
            root,
            ["helper.exe"],
            AppRuleCategory.Game));

        Assert.Equal("helperExecutablePaths", error.ParamName);
    }

    [Fact]
    public void ConfiguredConstructor_RejectsUndefinedCategory()
    {
        string root = Path.Combine(Path.GetTempPath(), "NightGate", "game.exe");

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new AppRule(
            "game",
            root,
            [],
            (AppRuleCategory)999));

        Assert.Equal("category", error.ParamName);
    }

    [Theory]
    [InlineData(AppRuleCategory.Game)]
    [InlineData(AppRuleCategory.Voice)]
    public void ConfiguredConstructor_AcceptsExactlySupportedCategories(AppRuleCategory category)
    {
        string root = Path.Combine(Path.GetTempPath(), "NightGate", $"{category}.exe");

        var rule = new AppRule("rule", root, [], category);

        Assert.Equal(category, rule.Category);
    }

    [Fact]
    public void Constructor_UsesDefaultSessionDuration()
    {
        var rule = new AppRule("game");

        Assert.Equal(35, rule.SessionMinutes);
    }

    [Fact]
    public void Constructor_RejectsDurationBelowMinimum()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new AppRule("game", 14));

        Assert.Equal("sessionMinutes", error.ParamName);
    }

    [Fact]
    public void Constructor_RejectsDurationAboveMaximum()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new AppRule("game", 91));

        Assert.Equal("sessionMinutes", error.ParamName);
    }

    [Theory]
    [InlineData(15, "2026-07-11T00:25:00+08:00")]
    [InlineData(35, "2026-07-11T00:05:00+08:00")]
    [InlineData(90, "2026-07-10T23:10:00+08:00")]
    public void CalculateLastStart_SubtractsAcceptedDurationFromLock(
        int sessionMinutes,
        string expectedText)
    {
        var lockTime = DateTimeOffset.Parse("2026-07-11T00:40:00+08:00");
        var rule = new AppRule("game", sessionMinutes);

        DateTimeOffset actual = ScheduleEvaluator.CalculateLastStart(lockTime, rule);

        Assert.Equal(DateTimeOffset.Parse(expectedText), actual);
    }
}
