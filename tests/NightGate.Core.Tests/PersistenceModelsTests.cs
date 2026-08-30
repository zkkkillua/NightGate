using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightGate.Core;

namespace NightGate.Core.Tests;

public sealed class PersistenceModelsTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 8, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnboardingState_DefaultsToVersionOneAndNoCompletedSteps()
    {
        OnboardingState state = OnboardingState.Initial;

        Assert.Equal(1, state.WizardVersion);
        Assert.Equal(0, state.CompletedStep);
        Assert.False(state.ChromeVerified);
        Assert.False(state.IncognitoProtected);
        Assert.False(state.IncognitoWarningAcknowledged);
        Assert.False(state.ChromeDegradedAcknowledged);
        Assert.Equal(0, state.IPhoneConfirmedThroughStep);
        Assert.Null(state.CompletedAtUtc);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 4)]
    public void OnboardingState_AcceptsSupportedSequentialBounds(
        int completedStep,
        int phoneStep)
    {
        OnboardingState state = new(
            completedStep,
            ChromeVerified: true,
            IncognitoProtected: false,
            IncognitoWarningAcknowledged: true,
            IPhoneConfirmedThroughStep: phoneStep,
            CompletedAtUtc: completedStep == 5 ? Now : null);

        Assert.Equal(completedStep, state.CompletedStep);
        Assert.Equal(phoneStep, state.IPhoneConfirmedThroughStep);
    }

    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(6, 0, 1)]
    [InlineData(0, -1, 1)]
    [InlineData(0, 5, 1)]
    [InlineData(0, 0, 2)]
    public void OnboardingState_RejectsUnsupportedBounds(
        int completedStep,
        int phoneStep,
        int wizardVersion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnboardingState(
            completedStep,
            IPhoneConfirmedThroughStep: phoneStep,
            WizardVersion: wizardVersion));
    }

    [Fact]
    public void OnboardingState_RejectsNonUtcOrPrematureCompletionTime()
    {
        Assert.Throws<ArgumentException>(() => new OnboardingState(
            5,
            CompletedAtUtc: Now.ToOffset(TimeSpan.FromHours(8))));
        Assert.Throws<ArgumentException>(() => new OnboardingState(
            4,
            CompletedAtUtc: Now));
    }

    [Fact]
    public void OnboardingState_ChromeStepRequiresVerificationAndIncognitoDecision()
    {
        Assert.Throws<ArgumentException>(() => new OnboardingState(
            3,
            IncognitoWarningAcknowledged: true));
        Assert.Throws<ArgumentException>(() => new OnboardingState(
            3,
            ChromeVerified: true));

        _ = new OnboardingState(
            3,
            ChromeVerified: true,
            IncognitoProtected: true);
        _ = new OnboardingState(
            3,
            ChromeVerified: true,
            IncognitoWarningAcknowledged: true);
    }

    [Fact]
    public void OnboardingState_CompletedChromeStepAllowsExplicitDegradedAcknowledgement()
    {
        OnboardingState state = new(
            CompletedStep: 3,
            ChromeVerified: false,
            IncognitoProtected: false,
            IncognitoWarningAcknowledged: false,
            IPhoneConfirmedThroughStep: 0,
            ChromeDegradedAcknowledged: true);

        Assert.True(state.ChromeDegradedAcknowledged);
        Assert.False(state.ChromeVerified);
    }

    [Fact]
    public void OnboardingState_OldJsonDefaultsChromeDegradedAcknowledgementToFalse()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        const string legacyJson =
            "{\"wizardVersion\":1,\"completedStep\":3,\"chromeVerified\":true,\"incognitoProtected\":false,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0,\"completedAtUtc\":null}";

        OnboardingState state = JsonSerializer.Deserialize<OnboardingState>(
            legacyJson,
            options)!;

        Assert.False(state.ChromeDegradedAcknowledged);
        Assert.Equal(1, state.WizardVersion);
    }

    [Fact]
    public void RuleSettingsState_DefaultsToEmptyActiveAndNoPendingRules()
    {
        RuleSettingsState state = RuleSettingsState.Initial;

        Assert.True(state.ActiveAppRules.IsEmpty);
        Assert.True(state.ActiveSiteRules.IsEmpty);
        Assert.Null(state.PendingAppRules);
        Assert.Null(state.PendingSiteRules);
        Assert.Null(state.PendingEffectiveNightDate);
        Assert.Null(state.PendingSavedAtUtc);
    }

    [Fact]
    public void RuleSettingsState_AcceptsCompleteCanonicalActiveAndPendingSets()
    {
        AppRule activeApp = ConfiguredApp("game", @"C:\Games\game.exe");
        AppRule pendingApp = ConfiguredApp("next", @"C:\Games\next.exe");
        RuleSettingsState state = new(
            ActiveAppRules: [activeApp],
            ActiveSiteRules: [new("video.example.com")],
            PendingAppRules: [pendingApp],
            PendingSiteRules: [new("social.example.com")],
            PendingEffectiveNightDate: new(2026, 7, 9),
            PendingSavedAtUtc: Now);

        Assert.Equal(activeApp, Assert.Single(state.ActiveAppRules));
        Assert.Equal(new SiteRule("video.example.com"), Assert.Single(state.ActiveSiteRules));
        Assert.Equal(pendingApp, Assert.Single(state.PendingAppRules!.Value));
        Assert.Equal(new DateOnly(2026, 7, 9), state.PendingEffectiveNightDate);
    }

    [Fact]
    public void RuleSettingsState_RejectsEveryPartialPendingShape()
    {
        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            PendingAppRules: [],
            PendingSiteRules: [],
            PendingEffectiveNightDate: new(2026, 7, 9)));
        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            PendingAppRules: [],
            PendingEffectiveNightDate: new(2026, 7, 9),
            PendingSavedAtUtc: Now));
        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            PendingSiteRules: [],
            PendingEffectiveNightDate: new(2026, 7, 9),
            PendingSavedAtUtc: Now));
    }

    [Fact]
    public void RuleSettingsState_RejectsNoncanonicalOrDuplicateRulesAndOverOneHundred()
    {
        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            ActiveAppRules: [new AppRule("unconfigured") ]));
        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            ActiveAppRules:
            [
                ConfiguredApp("same", @"C:\Games\one.exe"),
                ConfiguredApp("SAME", @"C:\Games\two.exe"),
            ]));
        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            ActiveSiteRules: [new("Example.COM") ]));
        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            ActiveSiteRules: [new("example.com"), new("example.com") ]));
        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            ActiveSiteRules: Enumerable.Range(0, 101)
                .Select(index => new SiteRule($"site-{index:D3}.example.com"))
                .ToImmutableArray()));
    }

    [Fact]
    public void RuleSettingsState_RejectsEveryCrossRuleExecutablePathAmbiguity()
    {
        AppRule first = new(
            "first",
            @"C:\Games\first.exe",
            [@"C:\Games\shared-helper.exe"],
            AppRuleCategory.Game,
            35);
        AppRule sharedHelper = new(
            "second",
            @"C:\Games\second.exe",
            [@"c:\games\SHARED-HELPER.exe"],
            AppRuleCategory.Game,
            35);
        AppRule helperMatchesOtherRoot = new(
            "third",
            @"C:\Games\third.exe",
            [@"c:\games\FIRST.exe"],
            AppRuleCategory.Game,
            35);

        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            ActiveAppRules: [first, sharedHelper]));
        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            ActiveAppRules: [first, helperMatchesOtherRoot]));
        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            PendingAppRules: [first, sharedHelper],
            PendingSiteRules: [],
            PendingEffectiveNightDate: new(2026, 7, 15),
            PendingSavedAtUtc: Now));
    }

    [Fact]
    public void RuleSettingsState_RejectsInvalidPendingDatesAndTimes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RuleSettingsState(
            PendingAppRules: [],
            PendingSiteRules: [],
            PendingEffectiveNightDate: default(DateOnly),
            PendingSavedAtUtc: Now));
        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            PendingAppRules: [],
            PendingSiteRules: [],
            PendingEffectiveNightDate: new(2026, 7, 9),
            PendingSavedAtUtc: Now.ToOffset(TimeSpan.FromHours(8))));
    }

    [Fact]
    public void AppRule_RejectsUnboundedIdentifiersHelpersAndExecutablePaths()
    {
        const int maximumIdLength = 128;
        const int maximumHelperExecutablePaths = 32;
        const int maximumExecutablePathLength = 1024;
        Assert.Throws<ArgumentException>(() => ConfiguredApp(
            new string('i', maximumIdLength + 1),
            @"C:\Games\game.exe"));
        Assert.Throws<ArgumentException>(() => new AppRule(
            "game",
            @"C:\Games\game.exe",
            Enumerable.Range(0, maximumHelperExecutablePaths + 1)
                .Select(index => $@"C:\Games\helper-{index:D2}.exe"),
            AppRuleCategory.Game));
        Assert.Throws<ArgumentException>(() => ConfiguredApp(
            "game",
            @"C:\Games\" + new string('p', maximumExecutablePathLength) + ".exe"));
    }

    [Fact]
    public void RuleSettingsState_RejectsOversizedUtf8PersistencePayload()
    {
        ImmutableArray<AppRule> rules = Enumerable
            .Range(0, RuleSettingsState.MaximumRulesPerSet)
            .Select(index => new AppRule(
                $"game-{index:D3}",
                $@"C:\Games\root-{index:D3}.exe",
                Enumerable.Range(0, 32)
                    .Select(helper =>
                        $@"C:\Games\{index:D3}\{helper:D2}\{new string('游', 220)}.exe"),
                AppRuleCategory.Game))
            .ToImmutableArray();

        Assert.Throws<ArgumentException>(() => new RuleSettingsState(
            ActiveAppRules: rules));
    }

    [Fact]
    public void NightSelfReport_ValidatesNightDateAndUtcUpdateTime()
    {
        NightSelfReport report = new(new(2026, 7, 7), true, null, Now);

        Assert.True(report.PhoneOutOfReach);
        Assert.Null(report.WakeWithinWindow);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NightSelfReport(
            default,
            null,
            null,
            Now));
        Assert.Throws<ArgumentException>(() => new NightSelfReport(
            new(2026, 7, 7),
            null,
            null,
            Now.ToOffset(TimeSpan.FromHours(8))));
    }

    [Fact]
    public void NoticeClaim_ValidatesDateKindAndUtcClaimTime()
    {
        NoticeClaim claim = new(new(2026, 7, 7), NightNoticeKind.Grace10, Now);

        Assert.Equal(NightNoticeKind.Grace10, claim.Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() => new NoticeClaim(
            default,
            NightNoticeKind.IfThenPlan,
            Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NoticeClaim(
            new(2026, 7, 7),
            (NightNoticeKind)999,
            Now));
        Assert.Throws<ArgumentException>(() => new NoticeClaim(
            new(2026, 7, 7),
            NightNoticeKind.LastStart,
            Now.ToOffset(TimeSpan.FromHours(8))));
    }

    [Fact]
    public void LegacyTaskMigrationRecord_ValidatesBoundedIdentityStatusAndUtcTimes()
    {
        LegacyTaskMigrationRecord record = new(
            "legacy-shutdown",
            @"\NightGate\Old shutdown",
            new string('a', 64),
            true,
            LegacyTaskMigrationStatus.Prepared,
            Now);

        Assert.Equal(LegacyTaskMigrationStatus.Prepared, record.Status);
        Assert.Equal(0, (int)LegacyTaskMigrationStatus.Prepared);
        Assert.Equal(1, (int)LegacyTaskMigrationStatus.Disabled);
        Assert.Equal(2, (int)LegacyTaskMigrationStatus.Restored);
        Assert.Equal(3, (int)LegacyTaskMigrationStatus.Failed);
        Assert.Equal(4, (int)LegacyTaskMigrationStatus.RestorePrepared);
        LegacyTaskMigrationRecord restorePrepared = new(
            "restore-prepared",
            @"\Task",
            new string('a', 64),
            true,
            LegacyTaskMigrationStatus.RestorePrepared,
            Now);
        Assert.Null(restorePrepared.CompletedAtUtc);
        Assert.Throws<ArgumentException>(() => new LegacyTaskMigrationRecord(
            " ", @"\Task", new string('a', 64), true,
            LegacyTaskMigrationStatus.Prepared, Now));
        Assert.Throws<ArgumentException>(() => new LegacyTaskMigrationRecord(
            "id", new string('x', 1025), new string('a', 64), true,
            LegacyTaskMigrationStatus.Prepared, Now));
        Assert.Throws<ArgumentException>(() => new LegacyTaskMigrationRecord(
            "id", @"\Task", new string('a', 63),
            true, LegacyTaskMigrationStatus.Prepared, Now));
        Assert.Throws<ArgumentException>(() => new LegacyTaskMigrationRecord(
            "id", @"\Task", new string('A', 64),
            true, LegacyTaskMigrationStatus.Prepared, Now));
        Assert.Throws<ArgumentException>(() => new LegacyTaskMigrationRecord(
            "id", @"\Task", new string('g', 64),
            true, LegacyTaskMigrationStatus.Prepared, Now));
        Assert.Throws<ArgumentException>(() => new LegacyTaskMigrationRecord(
            "id", @"\Task", "shutdown.exe /s /t 0", true,
            LegacyTaskMigrationStatus.Prepared, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LegacyTaskMigrationRecord(
            "id", @"\Task", new string('a', 64), true,
            (LegacyTaskMigrationStatus)999, Now));
        Assert.Throws<ArgumentException>(() => new LegacyTaskMigrationRecord(
            "id", @"\Task", new string('a', 64), true,
            LegacyTaskMigrationStatus.Disabled,
            Now.ToOffset(TimeSpan.FromHours(8))));
        Assert.Throws<ArgumentException>(() => new LegacyTaskMigrationRecord(
            "id", @"\Task", new string('a', 64), true,
            LegacyTaskMigrationStatus.Prepared,
            Now,
            DisabledStateVerified: true));
        Assert.Throws<ArgumentException>(() => new LegacyTaskMigrationRecord(
            "id", @"\Task", new string('a', 64), true,
            LegacyTaskMigrationStatus.RestorePrepared,
            Now,
            Now.AddSeconds(1)));
    }

    [Fact]
    public void NewPersistenceModelsExposeNoCredentialOrBrowsingHistoryFields()
    {
        Type[] types =
        [
            typeof(OnboardingState),
            typeof(RuleSettingsState),
            typeof(NightSelfReport),
            typeof(NoticeClaim),
            typeof(LegacyTaskMigrationRecord),
        ];
        string[] forbidden = ["password", "credential", "apple", "url", "title", "history"];

        string[] names = types
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(names, name => forbidden.Any(token =>
            name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void SettingsModels_RoundTripWithStrictPersistenceJson()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        OnboardingState onboarding = new(
            5,
            ChromeVerified: true,
            IncognitoWarningAcknowledged: true,
            IPhoneConfirmedThroughStep: 4,
            CompletedAtUtc: Now);
        RuleSettingsState rules = new(
            ActiveAppRules: [ConfiguredApp("game", @"C:\Games\game.exe")],
            ActiveSiteRules: [new("video.example.com")]);

        OnboardingState onboardingCopy = JsonSerializer.Deserialize<OnboardingState>(
            JsonSerializer.Serialize(onboarding, options),
            options)!;
        RuleSettingsState rulesCopy = JsonSerializer.Deserialize<RuleSettingsState>(
            JsonSerializer.Serialize(rules, options),
            options)!;

        Assert.Equal(onboarding, onboardingCopy);
        Assert.Equal(Assert.Single(rules.ActiveAppRules).Id, Assert.Single(rulesCopy.ActiveAppRules).Id);
        Assert.Equal(Assert.Single(rules.ActiveSiteRules), Assert.Single(rulesCopy.ActiveSiteRules));
    }

    private static AppRule ConfiguredApp(string id, string root) => new(
        id,
        root,
        [],
        AppRuleCategory.Game);
}
