using System.ComponentModel;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class UserExperienceViewModelTests
{
    [Fact]
    public void ChromeSitePermissionSetup_ReportsOpenedAndManualFallbackStates()
    {
        ChromeOptionsLauncher launcher = new();
        UserExperienceViewModel viewModel = new(
            new ExperienceGateway(),
            legacyMigrationCoordinator: null,
            gameDiscovery: null,
            launcher);

        launcher.Opens = true;
        viewModel.OpenChromeExtensionOptions();

        Assert.Contains("尝试打开", viewModel.ChromeExtensionOptionsStatusText, StringComparison.Ordinal);
        Assert.Contains("页面没有出现", viewModel.ChromeExtensionOptionsStatusText, StringComparison.Ordinal);
        Assert.Contains("允许", viewModel.ChromeExtensionOptionsStatusText, StringComparison.Ordinal);

        launcher.Opens = false;
        viewModel.OpenChromeExtensionOptions();

        Assert.Contains("chrome://extensions", viewModel.ChromeExtensionOptionsStatusText, StringComparison.Ordinal);
        Assert.Contains("扩展程序选项", viewModel.ChromeExtensionOptionsStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_TransitionsFromLoadingThroughUnavailableAndRecovers()
    {
        ExperienceGateway gateway = new()
        {
            StateResult = DesktopUserStateResult.Unavailable("fail-open"),
        };
        UserExperienceViewModel viewModel = new(gateway);

        Assert.True(viewModel.IsLoading);
        Assert.False(viewModel.IsAvailable);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsUnavailable);
        Assert.Equal("保护服务暂不可用", viewModel.Connection.Title);
        Assert.Equal(
            "电脑保持可用。你可以立即重试，收尾也会在后台自动重新连接。",
            viewModel.Connection.Body);

        gateway.StateResult = new(true, null, State(completedStep: 2));
        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsAvailable);
        Assert.False(viewModel.IsUnavailable);
        Assert.Equal(3, viewModel.SelectedOnboardingStep);
    }

    [Fact]
    public async Task FailedRefresh_DisablesMutationOfRetainedAuthoritativeState()
    {
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State(completedStep: 1)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();
        viewModel.AddAppRule(
            @"C:\Games\Example\game.exe",
            DesktopAppRuleCategory.Game,
            35);

        gateway.StateResult = DesktopUserStateResult.Unavailable("restart");
        await viewModel.RefreshAsync();
        DesktopRuleSettingsMutationResult result = await viewModel.SaveRulesAsync();

        Assert.True(viewModel.IsUnavailable);
        Assert.False(result.Saved);
        Assert.Equal(0, gateway.RuleCalls);
        Assert.False(viewModel.SaveRulesCommand.CanExecute(null));
        Assert.False(viewModel.NextOnboardingCommand.CanExecute(null));
    }

    [Fact]
    public async Task AutomaticRefresh_PreservesDirtyLocalDraftsWhileUpdatingAuthoritativeState()
    {
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State(completedStep: 2)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();
        viewModel.AddAppRule(
            @"C:\Games\LocalDraft\draft.exe",
            DesktopAppRuleCategory.Game,
            35);
        viewModel.SiteSelections.Single(site => site.Domain == "youtube.com").IsSelected = true;
        viewModel.AcknowledgedIncognitoWarning = true;
        viewModel.AcknowledgedChromeDegraded = true;
        viewModel.IPhone.HealthSleepScheduleConfigured = true;
        viewModel.PhoneOutOfReach = true;
        viewModel.WakeWithinWindow = false;

        DesktopUserStateDto authoritative = State(
            qualifying: 3,
            eligible: 4,
            completedStep: 2) with
        {
            Progress = State().Progress with { CurrentStep = 2 },
            Rules = DelayedRules(),
            SelfReport = new(
                new DateOnly(2026, 7, 14),
                false,
                true,
                new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero)),
        };
        gateway.StateResult = new(true, null, authoritative);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsAvailable);
        Assert.Equal("第 2 台阶", viewModel.ProgressStepText);
        Assert.Contains("3/4", viewModel.WeeklySummaryText, StringComparison.Ordinal);
        Assert.Equal(
            @"C:\Games\LocalDraft\draft.exe",
            Assert.Single(viewModel.ConfiguredApps).RootExecutablePath);
        Assert.True(viewModel.SiteSelections.Single(
            site => site.Domain == "youtube.com").IsSelected);
        Assert.True(viewModel.AcknowledgedIncognitoWarning);
        Assert.True(viewModel.AcknowledgedChromeDegraded);
        Assert.True(viewModel.IPhone.HealthSleepScheduleConfigured);
        Assert.True(viewModel.PhoneOutOfReach);
        Assert.False(viewModel.WakeWithinWindow);
    }

    [Fact]
    public async Task RuleEditor_IsDisabledForTheWholeSaveOperation()
    {
        TaskCompletionSource<DesktopRuleSettingsMutationResult> completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State(completedStep: 1)),
            RuleCompletion = completion,
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();
        viewModel.AddAppRule(
            @"C:\Games\First\first.exe",
            DesktopAppRuleCategory.Game,
            35);

        Task<DesktopRuleSettingsMutationResult> save = viewModel.SaveRulesAsync().AsTask();

        Assert.False(viewModel.CanEditRules);
        viewModel.AddAppRule(
            @"C:\Games\Late\late.exe",
            DesktopAppRuleCategory.Game,
            35);
        Assert.Single(viewModel.ConfiguredApps);

        completion.SetResult(new(
            true,
            null,
            DelayedRules(),
            true,
            true,
            new DateOnly(2026, 7, 14)));
        DesktopRuleSettingsMutationResult result = await save;

        Assert.True(result.Saved);
        Assert.True(viewModel.CanEditRules);
        Assert.True(viewModel.CanCompleteOnboardingStep);
        Assert.Equal(1, gateway.RuleCalls);
    }

    [Fact]
    public async Task AcceptedProgression_ImmediatelyDisablesRepeatConfirmation()
    {
        DesktopUserStateDto state = State(completedStep: 5) with
        {
            Progress = new(
                1,
                null,
                null,
                2,
                new DateOnly(2026, 7, 14),
                null,
                null),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, state),
            ProgressionResult = new(
                true,
                null,
                2,
                new DateOnly(2026, 7, 16)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();
        CompleteIPhoneChecklist(viewModel.IPhone);
        Assert.True(viewModel.CanConfirmProgression);

        DesktopIPhoneProgressionResult accepted = await viewModel
            .ConfirmPendingProgressionAsync();
        DesktopIPhoneProgressionResult repeated = await viewModel
            .ConfirmPendingProgressionAsync();

        Assert.True(accepted.Accepted);
        Assert.False(repeated.Accepted);
        Assert.Equal(1, gateway.ProgressionCalls);
        Assert.False(viewModel.CanConfirmProgression);
        Assert.False(viewModel.HasPendingProgression);
        Assert.Equal(0, viewModel.IPhone.ConfirmedCount);
        Assert.Contains("已确认", viewModel.ProgressionInvitationText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedStepNavigation_IsLocalAndFutureStepsCannotBeSelected()
    {
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State(completedStep: 3)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();

        Assert.Equal(4, viewModel.SelectedOnboardingStep);
        Assert.Equal(5, viewModel.OnboardingSteps.Count);
        Assert.False(viewModel.OnboardingSteps.Single(step => step.Number == 5).CanNavigate);

        viewModel.SelectOnboardingStep(2);
        await viewModel.AdvanceOnboardingAsync();

        Assert.Equal(3, viewModel.SelectedOnboardingStep);
        Assert.Equal(0, gateway.OnboardingCalls);

        viewModel.SelectOnboardingStep(5);
        Assert.Equal(3, viewModel.SelectedOnboardingStep);
        Assert.Equal(0, gateway.OnboardingCalls);
    }

    [Fact]
    public async Task RejectedCompletion_StaysOnCurrentStep()
    {
        DesktopUserStateDto state = State(completedStep: 2) with
        {
            ChromeProtection = new("healthy", true, true, DateTimeOffset.UtcNow, "1.0.0"),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, state),
            OnboardingResult = new(false, "chromeSetupIncomplete", null),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();

        await viewModel.AdvanceOnboardingAsync();

        Assert.Equal(3, viewModel.SelectedOnboardingStep);
        Assert.Equal(1, gateway.OnboardingCalls);
        Assert.Contains("还没有完成", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRequirements_AreConcreteForEveryWizardStep()
    {
        UserExperienceViewModel first = await ViewModelAtAsync(0);
        Assert.Null(first.OnboardingMissingRequirement);

        UserExperienceViewModel rules = await ViewModelAtAsync(1);
        Assert.Equal(
            "请先添加至少一个程序或网站并保存规则。",
            rules.OnboardingMissingRequirement);

        UserExperienceViewModel healthyChrome = await ViewModelAtAsync(
            2,
            new("healthy", true, false, DateTimeOffset.UtcNow, "1.0.0"));
        Assert.Equal("请确认隐身模式保护状态。", healthyChrome.OnboardingMissingRequirement);

        UserExperienceViewModel degradedChrome = await ViewModelAtAsync(2);
        Assert.Equal(
            "Chrome 扩展未连接；确认网页保护降级后才能继续。",
            degradedChrome.OnboardingMissingRequirement);
        degradedChrome.AcknowledgedChromeDegraded = true;
        Assert.Null(degradedChrome.OnboardingMissingRequirement);

        UserExperienceViewModel phone = await ViewModelAtAsync(3);
        Assert.Equal(10, phone.IPhone.RequiredCount);
        Assert.Equal(0, phone.IPhone.ConfirmedCount);
        Assert.Equal(10, phone.IPhone.RemainingCount);
        Assert.Equal("iPhone 清单还有 10 项未确认。", phone.OnboardingMissingRequirement);
        phone.IPhone.HealthSleepScheduleConfigured = true;
        Assert.Equal(1, phone.IPhone.ConfirmedCount);
        Assert.Equal(9, phone.IPhone.RemainingCount);
        Assert.Equal("iPhone 清单还有 9 项未确认。", phone.OnboardingMissingRequirement);

        UserExperienceViewModel boundary = await ViewModelAtAsync(4);
        Assert.Equal("请先确认收尾的保护边界。", boundary.OnboardingMissingRequirement);
    }

    [Fact]
    public async Task ChromeDegradedAcknowledgement_IsRestoredAndProjectedToRequest()
    {
        DesktopUserStateDto state = State(completedStep: 2) with
        {
            Onboarding = new(1, 2, false, false, false, 0, null, true),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, state),
            OnboardingResult = new(
                true,
                null,
                new(1, 3, false, false, false, 0, null, true)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();

        Assert.True(viewModel.AcknowledgedChromeDegraded);
        Assert.True(viewModel.CanCompleteOnboardingStep);
        await viewModel.AdvanceOnboardingAsync();

        Assert.True(gateway.OnboardingRequest!.ChromeDegradedAcknowledged);
        Assert.False(gateway.OnboardingRequest.ChromeVerified);
        Assert.Equal(4, viewModel.SelectedOnboardingStep);
    }

    [Fact]
    public async Task CompletedOnboarding_ExposesFiveOrderedLocalSettingsCategories()
    {
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State(completedStep: 5)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();

        Assert.Equal("设置", viewModel.SettingsTabTitle);
        Assert.Collection(
            viewModel.SettingsCategories,
            item => Assert.Equal(DesktopSettingsCategory.Schedule, item.Id),
            item => Assert.Equal(DesktopSettingsCategory.Rules, item.Id),
            item => Assert.Equal(DesktopSettingsCategory.Chrome, item.Id),
            item => Assert.Equal(DesktopSettingsCategory.IPhone, item.Id),
            item => Assert.Equal(DesktopSettingsCategory.Privacy, item.Id));
        Assert.Equal(DesktopSettingsCategory.Schedule, viewModel.SelectedSettingsCategory.Id);

        DesktopSettingsCategoryPresentation privacy = viewModel.SettingsCategories[^1];
        viewModel.SelectedSettingsCategory = privacy;

        Assert.Same(privacy, viewModel.SelectedSettingsCategory);
        Assert.Equal(0, gateway.SettingsMutationCalls);
    }

    [Fact]
    public async Task Refresh_ProjectsPersistedStateIntoCalmChineseSummary()
    {
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State(
                qualifying: 3,
                eligible: 4,
                medianLock: new TimeOnly(0, 27))),
        };
        UserExperienceViewModel viewModel = new(gateway);

        await viewModel.RefreshAsync();

        Assert.True(viewModel.IsAvailable);
        Assert.Equal("第 1 台阶", viewModel.ProgressStepText);
        Assert.Contains("00:40 锁屏", viewModel.CurrentScheduleText, StringComparison.Ordinal);
        Assert.Contains("不倒退", viewModel.ProgressionInvitationText, StringComparison.Ordinal);
        Assert.Equal(1, viewModel.ScheduleLadder.CurrentStep);
        Assert.Equal("00:40", viewModel.ScheduleLadder.TonightMilestones[1].TimeText);
        Assert.Contains(
            "不是四选一",
            viewModel.ScheduleLadder.AutomaticProgressionText,
            StringComparison.Ordinal);
        Assert.Contains("3/4", viewModel.WeeklySummaryText, StringComparison.Ordinal);
        Assert.Contains("00:27", viewModel.WeeklyLockText, StringComparison.Ordinal);
        Assert.DoesNotContain("失败", viewModel.WeeklySummaryText, StringComparison.Ordinal);
        Assert.Equal(5, viewModel.SiteSelections.Count);
    }

    [Fact]
    public async Task ProgressionChecklist_IsBoundToPendingStepAndUnlockNight()
    {
        DateOnly firstUnlock = new(2026, 7, 14);
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, PendingProgressionState(2, firstUnlock)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();

        Assert.True(viewModel.CanEditIPhoneChecklist);
        Assert.Contains("第 2 台阶", viewModel.IPhoneChecklistTargetText, StringComparison.Ordinal);
        Assert.Contains("07-14", viewModel.IPhoneChecklistTargetText, StringComparison.Ordinal);
        Assert.Contains("23:50", viewModel.IPhoneChecklistTargetText, StringComparison.Ordinal);
        Assert.Contains("00:25", viewModel.IPhoneChecklistTargetText, StringComparison.Ordinal);
        Assert.Contains("00:45", viewModel.IPhoneChecklistTargetText, StringComparison.Ordinal);
        Assert.Contains("08:45", viewModel.IPhoneChecklistTargetText, StringComparison.Ordinal);

        CompleteIPhoneChecklist(viewModel.IPhone);
        await viewModel.RefreshAsync();
        Assert.Equal(10, viewModel.IPhone.ConfirmedCount);

        gateway.StateResult = new(
            true,
            null,
            PendingProgressionState(2, firstUnlock.AddDays(1)));
        await viewModel.RefreshAsync();

        Assert.Equal(0, viewModel.IPhone.ConfirmedCount);
        Assert.Contains("07-15", viewModel.IPhoneChecklistTargetText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedOnboarding_WithoutPendingStepPreventsChecklistPreselection()
    {
        UserExperienceViewModel viewModel = new(new ExperienceGateway
        {
            StateResult = new(true, null, State(completedStep: 5)),
        });
        await viewModel.RefreshAsync();

        Assert.False(viewModel.CanEditIPhoneChecklist);
        Assert.Contains("没有待确认", viewModel.IPhoneChecklistTargetText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletingOnboardingClearsInitialChecklistBeforeWaitingForProgression()
    {
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State(completedStep: 4)),
            OnboardingResult = new(
                true,
                null,
                new(1, 5, false, false, false, 1, DateTimeOffset.UtcNow)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();
        CompleteIPhoneChecklist(viewModel.IPhone);
        viewModel.AcknowledgedProtectionLimit = true;

        DesktopOnboardingMutationResult result = await viewModel
            .CompleteCurrentOnboardingStepAsync();

        Assert.True(result.Accepted);
        Assert.Equal(0, viewModel.IPhone.ConfirmedCount);
        Assert.False(viewModel.CanEditIPhoneChecklist);
    }

    [Fact]
    public async Task WeeklyReport_SeparatesEmergencyReasons()
    {
        DesktopUserStateDto state = State(completedStep: 5) with
        {
            WeeklyReport = State().WeeklyReport with
            {
                OverrideReasons = new(4, 5, 1, 2, 3, 6),
            },
        };
        UserExperienceViewModel viewModel = new(new ExperienceGateway
        {
            StateResult = new(true, null, state),
        });
        await viewModel.RefreshAsync();

        Assert.Contains("健康 1 次", viewModel.WeeklyOverrideText, StringComparison.Ordinal);
        Assert.Contains("安全 2 次", viewModel.WeeklyOverrideText, StringComparison.Ordinal);
        Assert.Contains("紧急工作 3 次", viewModel.WeeklyOverrideText, StringComparison.Ordinal);
        Assert.Contains("其他 6 次", viewModel.WeeklyOverrideText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearHistory_ExplainsThatAllHistoryIsClearedButTonightIsRetained()
    {
        UserExperienceViewModel viewModel = new(new ExperienceGateway
        {
            StateResult = new(true, null, State(completedStep: 5)),
        });
        await viewModel.RefreshAsync();

        DesktopClearHistoryResult result = await viewModel.ClearHistoryAsync();

        Assert.True(result.Cleared);
        Assert.Contains("全部本机历史", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("今晚状态", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("90 天内", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearHistoryConfirmation_CancelSkipsAndConfirmRunsMutation()
    {
        foreach (bool decision in new[] { false, true })
        {
            ExperienceGateway gateway = new()
            {
                StateResult = new(true, null, State(completedStep: 5)),
            };
            UserExperienceViewModel viewModel = new(gateway);
            await viewModel.RefreshAsync();
            ConfirmationDialogs dialogs = new(decision);

            bool completed = await NightGate.Desktop.Views.SettingsView
                .ConfirmAndClearHistoryAsync(viewModel, dialogs, owner: null);

            Assert.Equal(decision, completed);
            Assert.Equal(decision ? 1 : 0, gateway.ClearHistoryCalls);
            Assert.Equal("清除全部本机历史", dialogs.LastRequest?.Title);
        }
    }

    [Fact]
    public void TrayExitConfirmation_OnlyExplicitConfirmationContinues()
    {
        ConfirmationDialogs cancelled = new(false);
        ConfirmationDialogs confirmed = new(true);

        Assert.False(TrayExitPrompt.Confirm(cancelled, owner: null));
        Assert.True(TrayExitPrompt.Confirm(confirmed, owner: null));
        Assert.Equal("退出收尾", cancelled.LastRequest?.Title);
        Assert.Equal("退出并停止保护", confirmed.LastRequest?.ConfirmText);
    }

    [Fact]
    public async Task AddAndSaveRules_SendsCurrentDraftAndExplainsDelayedActivation()
    {
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();
        viewModel.AddAppRule(
            @"C:\Games\Example\game.exe",
            DesktopAppRuleCategory.Game,
            45);
        viewModel.SiteSelections.Single(site => site.Domain == "youtube.com").IsSelected = true;
        gateway.RuleResult = new(
            true,
            null,
            DelayedRules(),
            false,
            false,
            new DateOnly(2026, 7, 15));

        DesktopRuleSettingsMutationResult result = await viewModel.SaveRulesAsync();

        Assert.True(result.Saved);
        DesktopAppRuleDraft sentApp = Assert.Single(gateway.SavedApps!);
        Assert.Equal("game", sentApp.Id);
        Assert.Equal(45, sentApp.SessionMinutes);
        Assert.Equal(["youtube.com"], gateway.SavedSites);
        Assert.Contains("次日晚间", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedOnboarding_CanSaveAnEmptyRuleSetAfterRemovingTheLastRules()
    {
        DesktopUserStateDto state = State(completedStep: 5) with
        {
            Rules = new(
                [new(
                    "game",
                    @"C:\Games\Example\game.exe",
                    [],
                    DesktopAppRuleCategory.Game,
                    35,
                    true)],
                [new("youtube.com")],
                null,
                null,
                null,
                null),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, state),
            RuleResult = new(
                true,
                null,
                EmptyRules(),
                true,
                true,
                new DateOnly(2026, 7, 14)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();

        viewModel.SelectedAppRule = Assert.Single(viewModel.ConfiguredApps);
        viewModel.RemoveSelectedAppRule();
        viewModel.SiteSelections.Single(
            site => site.Domain == "youtube.com").IsSelected = false;

        Assert.True(viewModel.CanSaveRules);
        DesktopRuleSettingsMutationResult result = await viewModel.SaveRulesAsync();

        Assert.True(result.Saved);
        Assert.Empty(gateway.SavedApps!);
        Assert.Empty(gateway.SavedSites!);
        Assert.Contains("今晚生效", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredRule_CanRemoveOneHelperWithoutRebuildingTheRule()
    {
        const string root = @"C:\Games\Example\game.exe";
        const string firstHelper = @"C:\Games\Example\launcher.exe";
        const string secondHelper = @"C:\Games\Example\anti-cheat.exe";
        DesktopRuleSettingsStateDto initialRules = new(
            [new(
                "game",
                root,
                [firstHelper, secondHelper],
                DesktopAppRuleCategory.Game,
                35,
                true)],
            [],
            null,
            null,
            null,
            null);
        DesktopRuleSettingsStateDto savedRules = new(
            [new(
                "game",
                root,
                [secondHelper],
                DesktopAppRuleCategory.Game,
                35,
                true)],
            [],
            null,
            null,
            null,
            null);
        ExperienceGateway gateway = new()
        {
            StateResult = new(
                true,
                null,
                State(completedStep: 5) with { Rules = initialRules }),
            RuleResult = new(
                true,
                null,
                savedRules,
                true,
                true,
                new DateOnly(2026, 7, 14)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();

        DesktopAppRuleItemViewModel rule = Assert.Single(viewModel.ConfiguredApps);
        viewModel.RemoveHelperFromAppRule(rule, firstHelper);

        DesktopAppRuleItemViewModel edited = Assert.Single(viewModel.ConfiguredApps);
        Assert.Equal(root, edited.RootExecutablePath);
        Assert.Equal([secondHelper], edited.HelperExecutablePaths);
        Assert.True(viewModel.CanSaveRules);

        DesktopRuleSettingsMutationResult result = await viewModel.SaveRulesAsync();

        Assert.True(result.Saved);
        Assert.Equal(
            [secondHelper],
            Assert.Single(gateway.SavedApps!).HelperExecutablePaths);
    }

    [Fact]
    public async Task FirstOnboardingStep_AdvancesOnlyThroughServiceConfirmation()
    {
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
            OnboardingResult = new(
                true,
                null,
                new(1, 1, false, false, false, 0, null)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();

        DesktopOnboardingMutationResult result = await viewModel
            .CompleteCurrentOnboardingStepAsync();

        Assert.True(result.Accepted);
        Assert.Equal(1, gateway.OnboardingRequest!.Step);
        Assert.Equal(1, viewModel.CompletedOnboardingStep);
        Assert.Contains("选择要保护", viewModel.OnboardingTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChromeOnboarding_UsesLiveHealthAndRequiresExplicitIncognitoWarning()
    {
        DesktopUserStateDto state = State() with
        {
            Onboarding = new(1, 2, false, false, false, 0, null),
            ChromeProtection = new(
                "healthy",
                true,
                false,
                new DateTimeOffset(2026, 7, 14, 16, 0, 0, TimeSpan.Zero),
                "1.0.0"),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, state),
            OnboardingResult = new(
                true,
                null,
                new(1, 3, true, false, true, 0, null)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();

        Assert.False(viewModel.CanCompleteOnboardingStep);
        viewModel.AcknowledgedIncognitoWarning = true;
        Assert.True(viewModel.CanCompleteOnboardingStep);

        DesktopOnboardingMutationResult result = await viewModel
            .CompleteCurrentOnboardingStepAsync();

        Assert.True(result.Accepted);
        Assert.True(gateway.OnboardingRequest!.ChromeVerified);
        Assert.False(gateway.OnboardingRequest.IncognitoProtected);
        Assert.True(gateway.OnboardingRequest.IncognitoWarningAcknowledged);
    }

    [Fact]
    public async Task HealthyChromeWithoutIncognito_ExplicitlyLabelsDegradedProtectionAndLockFallback()
    {
        UserExperienceViewModel viewModel = await ViewModelAtAsync(
            completedStep: 2,
            chrome: new(
                "healthy",
                true,
                false,
                new DateTimeOffset(2026, 7, 14, 16, 0, 0, TimeSpan.Zero),
                "1.0.0"));

        Assert.Contains("网页保护降级", viewModel.ChromeProtectionText);
        Assert.Contains("普通窗口仍受保护", viewModel.ChromeProtectionText);
        Assert.Contains("Windows 锁屏仍会执行", viewModel.ChromeProtectionText);
    }

    [Fact]
    public async Task ChromeProtectionNotReady_ShowsClearDegradedMessageAndRequiresAcknowledgement()
    {
        UserExperienceViewModel viewModel = await ViewModelAtAsync(
            completedStep: 2,
            chrome: new(
                "protectionDegraded",
                false,
                true,
                new DateTimeOffset(2026, 7, 14, 16, 0, 0, TimeSpan.Zero),
                "1.0.0"));

        Assert.Contains("尚未就绪", viewModel.ChromeProtectionText);
        Assert.Contains("网页保护降级", viewModel.OnboardingMissingRequirement);
    }

    [Fact]
    public async Task ClaimedLastStartNotice_DoesNotClaimAllGamesCloseTogether()
    {
        ExperienceGateway gateway = new()
        {
            NoticeResult = new(
                true,
                null,
                DesktopNightNoticeKind.LastStart,
                new DateOnly(2026, 7, 14)),
        };
        UserExperienceViewModel viewModel = new(gateway);
        DesktopNoticePresentation? notice = null;
        viewModel.NoticeRaised += (_, value) => notice = value;

        await viewModel.PollNoticeAsync();

        Assert.NotNull(notice);
        Assert.Equal("游戏截止时间提醒", notice!.Title);
        Assert.Equal(
            "正在进行的一局可以安全收尾；是否还能开新局，请看设置页中每个游戏自己的截止时间。",
            notice.Message);
    }

    [Fact]
    public async Task LegacyShutdownTasks_RequireExplicitSelectionBeforeDisable()
    {
        LegacyShutdownTaskCandidate first = new(
            @"\Old shutdown\first",
            new string('a', 64),
            true);
        LegacyShutdownTaskCandidate second = new(
            @"\Old shutdown\second",
            new string('b', 64),
            true);
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = Snapshot([first, second]),
            DisableResult = Snapshot([first]),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
        };
        UserExperienceViewModel viewModel = new(gateway, legacy);

        await viewModel.RefreshAsync();

        Assert.Equal(2, viewModel.LegacyShutdownTasks.Count);
        Assert.False(viewModel.CanDisableSelectedLegacyTasks);
        viewModel.LegacyShutdownTasks[1].IsSelected = true;
        Assert.True(viewModel.CanDisableSelectedLegacyTasks);

        await viewModel.DisableSelectedLegacyTasksAsync();

        Assert.Equal([second], legacy.DisabledSelection);
        Assert.Equal(first.TaskPath, Assert.Single(viewModel.LegacyShutdownTasks).TaskPath);
        Assert.Contains("不会删除", viewModel.LegacyMigrationStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyShutdownTasks_CanRestorePersistedDisabledTasks()
    {
        LegacyShutdownTaskCandidate candidate = new(
            @"\Old shutdown\first",
            new string('a', 64),
            true);
        DesktopLegacyTaskMigration disabled = new(
            "migration-1",
            candidate.TaskPath,
            candidate.ActionFingerprint,
            true,
            DesktopLegacyTaskMigrationStatus.Disabled,
            new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 1, 0, 1, TimeSpan.Zero));
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = Snapshot([], [disabled]),
            RestoreResult = Snapshot([]),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
        };
        UserExperienceViewModel viewModel = new(gateway, legacy);
        await viewModel.RefreshAsync();

        Assert.True(viewModel.CanRestoreLegacyTasks);
        await viewModel.RestoreLegacyTasksAsync();

        Assert.Equal(1, legacy.RestoreCalls);
        Assert.False(viewModel.CanRestoreLegacyTasks);
        Assert.Contains("没有发现", viewModel.LegacyMigrationStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyShutdownTasks_PendingRecoveryCanBeReconciledAndRestoredInOneClick()
    {
        LegacyShutdownTaskCandidate candidate = new(
            @"\Old shutdown\first",
            new string('a', 64),
            true);
        DesktopLegacyTaskMigration disabled = new(
            "migration-1",
            candidate.TaskPath,
            candidate.ActionFingerprint,
            true,
            DesktopLegacyTaskMigrationStatus.Disabled,
            new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 1, 0, 1, TimeSpan.Zero));
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = new(true, null, [], [], 1, 0),
            RestoreResult = Snapshot([]),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
        };
        UserExperienceViewModel viewModel = new(gateway, legacy);
        await viewModel.RefreshAsync();
        legacy.RefreshResult = Snapshot([], [disabled]);

        Assert.True(viewModel.CanRestoreLegacyTasks);
        await viewModel.RestoreLegacyTasksAsync();

        Assert.Equal(1, legacy.RefreshCalls);
        Assert.Equal(1, legacy.RestoreCalls);
        Assert.False(viewModel.CanRestoreLegacyTasks);
    }

    [Fact]
    public async Task LegacyShutdownTasks_PersistentPendingRecoveryUsesRestorePathInsteadOfRefreshLoop()
    {
        DesktopLegacyMigrationSnapshot pending = new(
            true,
            null,
            [],
            [],
            1,
            0);
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = pending,
            RestoreResult = Snapshot([]),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
        };
        UserExperienceViewModel viewModel = new(gateway, legacy);
        await viewModel.RefreshAsync();

        await viewModel.RestoreLegacyTasksAsync();

        Assert.Equal(1, legacy.RestoreCalls);
        Assert.False(viewModel.CanRestoreLegacyTasks);
    }

    [Fact]
    public async Task LegacyShutdownTasks_PendingDisableAndPendingRestoreHaveDistinctInstructions()
    {
        FakeLegacyCoordinator pendingDisable = new()
        {
            RefreshResult = new(
                true,
                null,
                [],
                [],
                PendingRecoveryCount: 2,
                FailedCount: 0),
        };
        UserExperienceViewModel disableViewModel = new(new ExperienceGateway
        {
            StateResult = new(true, null, State()),
        }, pendingDisable);

        await disableViewModel.RefreshAsync();

        Assert.Contains("2 项停用尚未完成", disableViewModel.LegacyMigrationStatusText,
            StringComparison.Ordinal);
        Assert.Contains("重新勾选并再次停用", disableViewModel.LegacyMigrationStatusText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("恢复尚未完成", disableViewModel.LegacyMigrationStatusText,
            StringComparison.Ordinal);

        FakeLegacyCoordinator pendingRestore = new()
        {
            RefreshResult = new(
                true,
                null,
                [],
                [],
                PendingRecoveryCount: 0,
                FailedCount: 0,
                PendingRestoreCount: 1),
        };
        UserExperienceViewModel restoreViewModel = new(new ExperienceGateway
        {
            StateResult = new(true, null, State()),
        }, pendingRestore);

        await restoreViewModel.RefreshAsync();

        Assert.True(restoreViewModel.CanRestoreLegacyTasks);
        Assert.Contains("1 项恢复尚未完成", restoreViewModel.LegacyMigrationStatusText,
            StringComparison.Ordinal);
        Assert.Contains("恢复此前停用的旧任务", restoreViewModel.LegacyMigrationStatusText,
            StringComparison.Ordinal);
        Assert.Contains("管理员确认", restoreViewModel.LegacyMigrationStatusText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("重新勾选并再次停用", restoreViewModel.LegacyMigrationStatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyShutdownTasks_ChangedRestoreExplainsManualRecoveryInsteadOfRetryingForever()
    {
        LegacyShutdownTaskCandidate candidate = new(
            @"\Old shutdown\first",
            new string('a', 64),
            true);
        DesktopLegacyTaskMigration disabled = new(
            "migration-1",
            candidate.TaskPath,
            candidate.ActionFingerprint,
            true,
            DesktopLegacyTaskMigrationStatus.Disabled,
            new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 1, 0, 1, TimeSpan.Zero));
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = Snapshot([], [disabled]),
            RestoreResult = new(
                true,
                null,
                [],
                [],
                PendingRecoveryCount: 0,
                FailedCount: 1),
        };
        UserExperienceViewModel viewModel = new(new ExperienceGateway
        {
            StateResult = new(true, null, State()),
        }, legacy);
        await viewModel.RefreshAsync();

        await viewModel.RestoreLegacyTasksAsync();

        Assert.False(viewModel.CanRestoreLegacyTasks);
        Assert.Contains("任务计划程序", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("手动", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("再次点击", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("已经恢复", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("手动核对", viewModel.LegacyMigrationStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyShutdownTasks_MixedPendingAndFailedRestoreExplainsBothActions()
    {
        LegacyShutdownTaskCandidate candidate = new(
            @"\Old shutdown\first",
            new string('a', 64),
            true);
        DesktopLegacyTaskMigration disabled = new(
            "migration-1",
            candidate.TaskPath,
            candidate.ActionFingerprint,
            true,
            DesktopLegacyTaskMigrationStatus.Disabled,
            new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 1, 0, 1, TimeSpan.Zero));
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = Snapshot([], [disabled]),
            RestoreResult = new(
                true,
                null,
                [],
                [],
                PendingRecoveryCount: 0,
                FailedCount: 1,
                PendingRestoreCount: 1),
        };
        UserExperienceViewModel viewModel = new(new ExperienceGateway
        {
            StateResult = new(true, null, State()),
        }, legacy);
        await viewModel.RefreshAsync();

        await viewModel.RestoreLegacyTasksAsync();

        Assert.Contains("仍有 1 项恢复尚未完成", viewModel.StatusMessage,
            StringComparison.Ordinal);
        Assert.Contains("另有 1 项", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("手动核对", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("1 项恢复尚未完成", viewModel.LegacyMigrationStatusText,
            StringComparison.Ordinal);
        Assert.Contains("另有 1 项", viewModel.LegacyMigrationStatusText,
            StringComparison.Ordinal);
        Assert.Contains("手动核对", viewModel.LegacyMigrationStatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyShutdownTasks_FingerprintChangeNeverInheritsPriorConsent()
    {
        LegacyShutdownTaskCandidate original = new(
            @"\Old shutdown\first",
            new string('a', 64),
            true);
        LegacyShutdownTaskCandidate changed = original with
        {
            ActionFingerprint = new string('b', 64),
        };
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = Snapshot([original]),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
        };
        UserExperienceViewModel viewModel = new(gateway, legacy);
        await viewModel.RefreshAsync();
        viewModel.LegacyShutdownTasks.Single().IsSelected = true;

        legacy.RefreshResult = Snapshot([changed]);
        await viewModel.RefreshAsync();

        LegacyShutdownTaskChoiceViewModel replacement =
            Assert.Single(viewModel.LegacyShutdownTasks);
        Assert.Equal(changed.ActionFingerprint, replacement.Candidate.ActionFingerprint);
        Assert.False(replacement.IsSelected);
        Assert.False(viewModel.CanDisableSelectedLegacyTasks);
    }

    [Fact]
    public async Task LegacyShutdownTasks_ScanFailureIsNeverPresentedAsNoConflicts()
    {
        DesktopLegacyMigrationSnapshot unavailableScan = new(
            true,
            null,
            [],
            [],
            0,
            0,
            false);
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = unavailableScan,
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
        };
        UserExperienceViewModel viewModel = new(gateway, legacy);

        await viewModel.RefreshAsync();

        Assert.Contains("无法扫描", viewModel.LegacyMigrationStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("没有发现", viewModel.LegacyMigrationStatusText, StringComparison.Ordinal);
        Assert.False(viewModel.CanDisableSelectedLegacyTasks);
    }

    [Fact]
    public async Task LegacyShutdownTasks_UnverifiedDisabledRecordNeverClaimsWindowsConfirmedIt()
    {
        DesktopLegacyTaskMigration disabled = new(
            "migration-1",
            @"\Old shutdown\first",
            new string('a', 64),
            true,
            DesktopLegacyTaskMigrationStatus.Disabled,
            new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 1, 0, 1, TimeSpan.Zero));
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = new(
                true,
                null,
                [],
                [disabled],
                0,
                0,
                true,
                1),
        };
        UserExperienceViewModel viewModel = new(new ExperienceGateway
        {
            StateResult = new(true, null, State()),
        }, legacy);

        await viewModel.RefreshAsync();

        Assert.Contains("不能确认", viewModel.LegacyMigrationStatusText, StringComparison.Ordinal);
        Assert.Contains("重新勾选", viewModel.LegacyMigrationStatusText, StringComparison.Ordinal);
        Assert.Contains("管理员确认", viewModel.LegacyMigrationStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("已从 Windows 重新核对", viewModel.LegacyMigrationStatusText, StringComparison.Ordinal);
        Assert.True(viewModel.CanRestoreLegacyTasks);
    }

    [Fact]
    public async Task LegacyShutdownTasks_VerifiedRecordClaimsOnlyHistoricalWindowsConfirmation()
    {
        DesktopLegacyTaskMigration disabled = new(
            "migration-1",
            @"\Old shutdown\first",
            new string('a', 64),
            true,
            DesktopLegacyTaskMigrationStatus.Disabled,
            new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 1, 0, 1, TimeSpan.Zero),
            DisabledStateVerified: true);
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = Snapshot([], [disabled]),
        };
        UserExperienceViewModel viewModel = new(new ExperienceGateway
        {
            StateResult = new(true, null, State()),
        }, legacy);

        await viewModel.RefreshAsync();

        Assert.Contains("此前已由 Windows 确认停用", viewModel.LegacyMigrationStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("重新核对", viewModel.LegacyMigrationStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyShutdownTasks_PendingDisableAndFailedRestoreUseAccurateWording()
    {
        LegacyShutdownTaskCandidate candidate = new(
            @"\Old shutdown\first",
            new string('a', 64),
            true);
        DesktopLegacyTaskMigration disabled = new(
            "migration-1",
            candidate.TaskPath,
            candidate.ActionFingerprint,
            true,
            DesktopLegacyTaskMigrationStatus.Disabled,
            new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 15, 1, 0, 1, TimeSpan.Zero));
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = Snapshot([candidate]),
            DisableResult = new(true, null, [], [], 1, 0),
            RestoreResult = Snapshot([], [disabled]),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
        };
        UserExperienceViewModel viewModel = new(gateway, legacy);
        await viewModel.RefreshAsync();
        viewModel.LegacyShutdownTasks.Single().IsSelected = true;

        await viewModel.DisableSelectedLegacyTasksAsync();

        Assert.Contains("再次停用", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("管理员确认", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("处理完成", viewModel.StatusMessage, StringComparison.Ordinal);

        legacy.RefreshResult = Snapshot([], [disabled]);
        await viewModel.RefreshAsync();
        await viewModel.RestoreLegacyTasksAsync();

        Assert.Contains("仍有", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("已经恢复", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyShutdownTasks_RejectedPrepareNeverClaimsTaskWasProcessed()
    {
        LegacyShutdownTaskCandidate candidate = new(
            @"\Old shutdown\first",
            new string('a', 64),
            true);
        FakeLegacyCoordinator legacy = new()
        {
            RefreshResult = Snapshot([candidate]),
            DisableResult = Snapshot([candidate]),
        };
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
        };
        UserExperienceViewModel viewModel = new(gateway, legacy);
        await viewModel.RefreshAsync();
        viewModel.LegacyShutdownTasks.Single().IsSelected = true;

        await viewModel.DisableSelectedLegacyTasksAsync();

        Assert.Contains("未处理", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("保持原样", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("处理结果已保存", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_AutomaticallyDiscoversGamesWithoutChangingRules()
    {
        FakeGameDiscovery discovery = new(DiscoverySnapshot(
            new DiscoveredGame(
                "Example Adventure",
                @"C:\Games\Example\Example.exe",
                GameDiscoverySource.Steam,
                GameDiscoveryConfidence.High)));
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
        };
        UserExperienceViewModel viewModel = new(gateway, null, discovery);

        await viewModel.RefreshAsync();

        Assert.Equal(1, discovery.Calls);
        DiscoveredGameChoiceViewModel choice = Assert.Single(viewModel.DiscoveredGames);
        Assert.Equal("Example Adventure", choice.DisplayName);
        Assert.Equal("Steam", choice.SourceText);
        Assert.Empty(viewModel.ConfiguredApps);
        Assert.Contains("找到 1 个游戏", viewModel.GameDiscoveryStatusText, StringComparison.Ordinal);
        Assert.Equal(0, gateway.RuleCalls);

        await viewModel.RefreshAsync();

        Assert.Equal(1, discovery.Calls);
    }

    [Fact]
    public async Task SlowAutomaticGameDiscovery_KeepsRuleEditingAvailable()
    {
        BlockingGameDiscovery discovery = new();
        UserExperienceViewModel viewModel = new(
            new ExperienceGateway
            {
                StateResult = new(true, null, State()),
            },
            null,
            discovery);

        Task refresh = viewModel.RefreshAsync().AsTask();
        await discovery.Started.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.IsAvailable);
        Assert.True(viewModel.IsGameDiscoveryInFlight);
        Assert.True(viewModel.CanEditRules);
        Assert.False(viewModel.CanScanInstalledGames);

        viewModel.AddAppRule(
            @"C:\Games\Editable\Editable.exe",
            DesktopAppRuleCategory.Game,
            35);
        DesktopAppRuleItemViewModel rule = Assert.Single(viewModel.ConfiguredApps);
        viewModel.SelectedAppRule = rule;
        viewModel.SelectedAppRuleSessionMinutes = 60;

        Assert.Equal(60, rule.SessionMinutes);

        discovery.Complete(DiscoverySnapshot());
        await refresh;
    }

    [Fact]
    public async Task SelectedDiscoveredGames_AreAddedAndSavedWithFriendlyName()
    {
        const string path = @"C:\Games\Example\Example.exe";
        FakeGameDiscovery discovery = new(DiscoverySnapshot(
            new DiscoveredGame(
                "Example Adventure",
                path,
                GameDiscoverySource.Epic,
                GameDiscoveryConfidence.High)));
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
            RuleResult = new(
                true,
                null,
                new(
                    [],
                    [],
                    [new(
                        "Example Adventure",
                        path,
                        [],
                        DesktopAppRuleCategory.Game,
                        60,
                        true)],
                    [],
                    null,
                    null),
                true,
                false,
                null),
        };
        UserExperienceViewModel viewModel = new(gateway, null, discovery);
        await viewModel.RefreshAsync();
        DiscoveredGameChoiceViewModel choice = Assert.Single(viewModel.DiscoveredGames);
        choice.IsSelected = true;
        choice.SessionMinutes = 60;

        viewModel.AddSelectedDiscoveredGames();

        DesktopAppRuleItemViewModel configured = Assert.Single(viewModel.ConfiguredApps);
        Assert.Equal("Example Adventure", configured.Name);
        Assert.Equal(path, configured.RootExecutablePath);
        Assert.Equal(60, configured.SessionMinutes);
        Assert.True(choice.IsAlreadyConfigured);
        Assert.False(choice.IsSelected);

        DesktopRuleSettingsMutationResult result = await viewModel.SaveRulesAsync();

        Assert.True(result.Saved);
        DesktopAppRuleDraft saved = Assert.Single(gateway.SavedApps!);
        Assert.Equal("Example Adventure", saved.Id);
        Assert.Equal(path, saved.RootExecutablePath);
        Assert.Equal(60, saved.SessionMinutes);
    }

    [Fact]
    public async Task DiscoveredAndConfiguredGames_KeepIndependentSessionDurations()
    {
        const string firstPath = @"C:\Games\First\First.exe";
        const string secondPath = @"C:\Games\Second\Second.exe";
        FakeGameDiscovery discovery = new(DiscoverySnapshot(
            new DiscoveredGame(
                "First Game",
                firstPath,
                GameDiscoverySource.Steam,
                GameDiscoveryConfidence.High),
            new DiscoveredGame(
                "Second Game",
                secondPath,
                GameDiscoverySource.Epic,
                GameDiscoveryConfidence.High)));
        UserExperienceViewModel viewModel = new(
            new ExperienceGateway
            {
                StateResult = new(true, null, State()),
            },
            null,
            discovery);
        await viewModel.RefreshAsync();
        DiscoveredGameChoiceViewModel first = viewModel.DiscoveredGames.Single(
            game => game.ExecutablePath == firstPath);
        DiscoveredGameChoiceViewModel second = viewModel.DiscoveredGames.Single(
            game => game.ExecutablePath == secondPath);
        first.SessionMinutes = 15;
        second.SessionMinutes = 90;
        first.IsSelected = true;
        second.IsSelected = true;

        viewModel.AddSelectedDiscoveredGames();

        Assert.Equal(
            15,
            viewModel.ConfiguredApps.Single(app => app.RootExecutablePath == firstPath)
                .SessionMinutes);
        DesktopAppRuleItemViewModel secondRule = viewModel.ConfiguredApps.Single(
            app => app.RootExecutablePath == secondPath);
        Assert.Equal(90, secondRule.SessionMinutes);

        viewModel.SelectedAppRule = secondRule;
        viewModel.SelectedAppRuleSessionMinutes = 45;

        Assert.Equal(
            45,
            viewModel.ConfiguredApps.Single(app => app.RootExecutablePath == secondPath)
                .SessionMinutes);
        Assert.Equal(
            15,
            viewModel.ConfiguredApps.Single(app => app.RootExecutablePath == firstPath)
                .SessionMinutes);
    }

    [Fact]
    public async Task Rescan_PreservesPendingDiscoveredGameSelectionAndDuration()
    {
        const string path = @"C:\Games\Pending\Pending.exe";
        FakeGameDiscovery discovery = new(DiscoverySnapshot(
            new DiscoveredGame(
                "Pending Game",
                path,
                GameDiscoverySource.Steam,
                GameDiscoveryConfidence.High)));
        UserExperienceViewModel viewModel = new(
            new ExperienceGateway
            {
                StateResult = new(true, null, State()),
            },
            null,
            discovery);
        await viewModel.RefreshAsync();
        DiscoveredGameChoiceViewModel before = Assert.Single(viewModel.DiscoveredGames);
        before.SessionMinutes = 90;
        before.IsSelected = true;

        await viewModel.ScanInstalledGamesAsync();

        DiscoveredGameChoiceViewModel after = Assert.Single(viewModel.DiscoveredGames);
        Assert.NotSame(before, after);
        Assert.True(after.IsSelected);
        Assert.Equal(90, after.SessionMinutes);
    }

    [Fact]
    public async Task ConfiguredGame_RowDurationEditMarksRulesDirtyAndSavesTheNewValue()
    {
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State() with { Rules = DelayedRules() }),
            RuleResult = new(
                true,
                null,
                DelayedRules(),
                true,
                false,
                null),
        };
        UserExperienceViewModel viewModel = new(gateway);
        await viewModel.RefreshAsync();
        DesktopAppRuleItemViewModel configured = Assert.Single(viewModel.ConfiguredApps);

        _ = Assert.IsAssignableFrom<INotifyPropertyChanged>(configured);
        typeof(DesktopAppRuleItemViewModel)
            .GetProperty(nameof(DesktopAppRuleItemViewModel.SessionMinutes))!
            .SetValue(configured, 60);

        Assert.True(viewModel.CanSaveRules);
        DesktopRuleSettingsMutationResult result = await viewModel.SaveRulesAsync();

        Assert.True(result.Saved);
        Assert.Equal(60, Assert.Single(gateway.SavedApps!).SessionMinutes);
    }

    [Fact]
    public async Task SelectedRuleActionsTrackWhetherAConfiguredRowIsSelected()
    {
        UserExperienceViewModel viewModel = new(new ExperienceGateway
        {
            StateResult = new(true, null, State() with { Rules = DelayedRules() }),
        });
        await viewModel.RefreshAsync();
        DesktopAppRuleItemViewModel configured = Assert.Single(viewModel.ConfiguredApps);

        Assert.False(viewModel.CanEditSelectedAppRule);

        viewModel.SelectedAppRule = configured;

        Assert.True(viewModel.CanEditSelectedAppRule);

        viewModel.RemoveSelectedAppRule();

        Assert.False(viewModel.CanEditSelectedAppRule);
    }

    [Fact]
    public async Task RemovingConfiguredDiscoveredGame_MakesItSelectableAgain()
    {
        const string path = @"C:\Games\Example\Example.exe";
        FakeGameDiscovery discovery = new(DiscoverySnapshot(
            new DiscoveredGame(
                "Example Adventure",
                path,
                GameDiscoverySource.XboxGamingServices,
                GameDiscoveryConfidence.High)));
        ExperienceGateway gateway = new()
        {
            StateResult = new(true, null, State()),
        };
        UserExperienceViewModel viewModel = new(gateway, null, discovery);
        await viewModel.RefreshAsync();
        DiscoveredGameChoiceViewModel choice = Assert.Single(viewModel.DiscoveredGames);
        choice.IsSelected = true;
        viewModel.AddSelectedDiscoveredGames();

        viewModel.RemoveSelectedAppRule();

        Assert.Empty(viewModel.ConfiguredApps);
        Assert.False(choice.IsAlreadyConfigured);
        Assert.True(choice.CanSelect);
        choice.IsSelected = true;
        Assert.True(viewModel.CanAddSelectedDiscoveredGames);
    }

    private static GameDiscoverySnapshot DiscoverySnapshot(params DiscoveredGame[] games) => new(
        [.. games],
        [new(
            games.Length == 0 ? GameDiscoverySource.Steam : games[0].Source,
            GameDiscoverySourceState.Succeeded,
            games.Length)]);

    private static DesktopLegacyMigrationSnapshot Snapshot(
        IReadOnlyList<LegacyShutdownTaskCandidate> candidates,
        IReadOnlyList<DesktopLegacyTaskMigration>? disabled = null) => new(
        true,
        null,
        candidates,
        disabled ?? [],
        0,
        0);

    private static void CompleteIPhoneChecklist(IPhoneChecklistViewModel checklist)
    {
        checklist.HealthSleepScheduleConfigured = true;
        checklist.SleepFocusConfigured = true;
        checklist.DowntimeConfigured = true;
        checklist.BlockAtDowntimeEnabled = true;
        checklist.EntertainmentCategoriesRestricted = true;
        checklist.RequiredAppsAllowed = true;
        checklist.SafariNotAllowlisted = true;
        checklist.DistinctRecoverableScreenTimePasscodeAcknowledged = true;
        checklist.OldAlarmsChecked = true;
        checklist.PhonePlacementPlanned = true;
    }

    private static async Task<UserExperienceViewModel> ViewModelAtAsync(
        int completedStep,
        DesktopChromeProtectionStatusDto? chrome = null)
    {
        DesktopUserStateDto state = State(completedStep: completedStep) with
        {
            ChromeProtection = chrome ?? new("missing", false, false, null, null),
        };
        UserExperienceViewModel viewModel = new(new ExperienceGateway
        {
            StateResult = new(true, null, state),
        });
        await viewModel.RefreshAsync();
        return viewModel;
    }

    private static DesktopUserStateDto State(
        int qualifying = 0,
        int eligible = 0,
        TimeOnly? medianLock = null,
        int completedStep = 0) => new(
            new(1, null, null, null, null, null, null),
            new(1, completedStep, false, false, false, 0, null),
            EmptyRules(),
            new(
                new DateOnly(2026, 7, 9),
                new DateOnly(2026, 7, 15),
                eligible,
                eligible,
                qualifying,
                medianLock is null ? 0 : eligible,
                medianLock,
                null,
                new(0, 0, 0, 0, 0, 0)),
            new DateOnly(2026, 7, 14),
            null,
            new("missing", false, false, null, null));

    private static DesktopUserStateDto PendingProgressionState(int pendingStep, DateOnly unlocked) =>
        State(completedStep: 5) with
        {
            Progress = new(
                pendingStep - 1,
                null,
                unlocked,
                pendingStep,
                unlocked,
                null,
                null),
        };

    private static DesktopRuleSettingsStateDto EmptyRules() => new(
        [],
        [],
        null,
        null,
        null,
        null);

    private static DesktopRuleSettingsStateDto DelayedRules() => new(
        [],
        [],
        [new(
            "game",
            @"C:\Games\Example\game.exe",
            [],
            DesktopAppRuleCategory.Game,
            45,
            true)],
        [new("youtube.com")],
        new DateOnly(2026, 7, 15),
        new DateTimeOffset(2026, 7, 14, 15, 0, 0, TimeSpan.Zero));

    private sealed class ExperienceGateway : IUserExperienceGateway
    {
        public DesktopUserStateResult StateResult { get; set; } =
            DesktopUserStateResult.Unavailable("not-set");

        public DesktopOnboardingMutationResult OnboardingResult { get; set; } =
            new(false, "not-set", null);

        public DesktopRuleSettingsMutationResult RuleResult { get; set; } =
            new(false, "not-set", null, false, false, null);

        public TaskCompletionSource<DesktopRuleSettingsMutationResult>? RuleCompletion
        {
            get;
            init;
        }

        public DesktopIPhoneProgressionResult ProgressionResult { get; set; } =
            new(false, "not-set", null, null);

        public DesktopNoticeClaimResult NoticeResult { get; set; } =
            new(false, null, null, null);

        public DesktopOnboardingStepRequest? OnboardingRequest { get; private set; }

        public int OnboardingCalls { get; private set; }

        public int RuleCalls { get; private set; }

        public int ProgressionCalls { get; private set; }

        public int ClearHistoryCalls { get; private set; }

        public int SettingsMutationCalls => OnboardingCalls + RuleCalls;

        public IReadOnlyList<DesktopAppRuleDraft>? SavedApps { get; private set; }

        public IReadOnlyList<string>? SavedSites { get; private set; }

        public ValueTask<DesktopUserStateResult> GetUserStateAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(StateResult);

        public ValueTask<DesktopOnboardingMutationResult> CompleteOnboardingStepAsync(
            DesktopOnboardingStepRequest request,
            CancellationToken cancellationToken = default)
        {
            OnboardingCalls++;
            OnboardingRequest = request;
            return ValueTask.FromResult(OnboardingResult);
        }

        public ValueTask<DesktopRuleSettingsMutationResult> SaveRuleSettingsAsync(
            IReadOnlyList<DesktopAppRuleDraft> appRules,
            IReadOnlyList<string> siteDomains,
            CancellationToken cancellationToken = default)
        {
            RuleCalls++;
            SavedApps = appRules;
            SavedSites = siteDomains;
            return RuleCompletion is null
                ? ValueTask.FromResult(RuleResult)
                : new ValueTask<DesktopRuleSettingsMutationResult>(RuleCompletion.Task);
        }

        public ValueTask<DesktopSelfReportMutationResult> SaveNightSelfReportAsync(
            DateOnly nightDate,
            bool? phoneOutOfReach,
            bool? wakeWithinWindow,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                new DesktopSelfReportMutationResult(false, "not-set", null));

        public ValueTask<DesktopNoticeClaimResult> ClaimDueNoticeAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(NoticeResult);

        public ValueTask<DesktopIPhoneProgressionResult> ConfirmIPhoneProgressionAsync(
            int step,
            DesktopIPhoneChecklist checklist,
            CancellationToken cancellationToken = default)
        {
            ProgressionCalls++;
            return ValueTask.FromResult(ProgressionResult);
        }

        public ValueTask<DesktopClearHistoryResult> ClearHistoryAsync(
            CancellationToken cancellationToken = default)
        {
            ClearHistoryCalls++;
            return ValueTask.FromResult(new DesktopClearHistoryResult(true, null));
        }
    }

    private sealed class ConfirmationDialogs(bool result) : IConfirmationDialogService
    {
        public ConfirmationDialogRequest? LastRequest { get; private set; }

        public bool Confirm(System.Windows.Window? owner, ConfirmationDialogRequest request)
        {
            LastRequest = request;
            return result;
        }
    }

    private sealed class FakeGameDiscovery(GameDiscoverySnapshot snapshot) : IGameDiscovery
    {
        public int Calls { get; private set; }

        public ValueTask<GameDiscoverySnapshot> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class BlockingGameDiscovery : IGameDiscovery
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<GameDiscoverySnapshot> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public ValueTask<GameDiscoverySnapshot> DiscoverAsync(
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            return new(_completion.Task.WaitAsync(cancellationToken));
        }

        public void Complete(GameDiscoverySnapshot snapshot) =>
            _completion.TrySetResult(snapshot);
    }

    private sealed class FakeLegacyCoordinator : ILegacyTaskMigrationCoordinator
    {
        public DesktopLegacyMigrationSnapshot RefreshResult { get; set; } = Snapshot([]);

        public DesktopLegacyMigrationSnapshot DisableResult { get; init; } = Snapshot([]);

        public DesktopLegacyMigrationSnapshot RestoreResult { get; init; } = Snapshot([]);

        public IReadOnlyList<LegacyShutdownTaskCandidate>? DisabledSelection { get; private set; }

        public int RestoreCalls { get; private set; }

        public int RefreshCalls { get; private set; }

        public ValueTask<DesktopLegacyMigrationSnapshot> RefreshAsync(
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return ValueTask.FromResult(RefreshResult);
        }

        public ValueTask<DesktopLegacyMigrationSnapshot> DisableSelectedAsync(
            IReadOnlyList<LegacyShutdownTaskCandidate> selected,
            CancellationToken cancellationToken = default)
        {
            DisabledSelection = selected;
            return ValueTask.FromResult(DisableResult);
        }

        public ValueTask<DesktopLegacyMigrationSnapshot> RestoreDisabledAsync(
            CancellationToken cancellationToken = default)
        {
            RestoreCalls++;
            return ValueTask.FromResult(RestoreResult);
        }
    }

    private sealed class ChromeOptionsLauncher : IChromeExtensionOptionsLauncher
    {
        public bool Opens { get; set; }

        public bool TryOpen() => Opens;
    }
}
