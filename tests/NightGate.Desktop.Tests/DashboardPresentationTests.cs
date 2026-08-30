using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DashboardPresentationTests
{
    [Fact]
    public async Task MorningDashboard_ShowsRulesStagedForTonightInsteadOfZeroActiveRules()
    {
        DateOnly tonight = new(2026, 7, 18);
        DesktopRuleSettingsStateDto stagedRules = new(
            [],
            [],
            Enumerable.Range(1, 11)
                .Select(index => new DesktopAppRuleDto(
                    $"game-{index}",
                    $@"C:\Games\Game{index}\game.exe",
                    [],
                    DesktopAppRuleCategory.Game,
                    35,
                    true))
                .ToArray(),
            [],
            tonight,
            new DateTimeOffset(2026, 7, 18, 8, 0, 0, TimeSpan.Zero));
        UserExperienceViewModel experience = new(
            new StagedRulesExperienceGateway(UserState(tonight, stagedRules)));
        DashboardViewModel dashboard = new(new RejectingOverrideGateway(), experience);
        DateTimeOffset now = new(2026, 7, 18, 16, 0, 0, TimeSpan.FromHours(8));

        dashboard.ApplyPolicy(Policy(DesktopNightPhase.Morning, now, appCount: 0, siteCount: 0), now);
        Assert.Equal("0 个应用 · 0 个网站", dashboard.Presentation.ConfigurationText);

        await experience.RefreshAsync();

        Assert.Equal(
            "今晚生效：11 个应用 · 0 个网站（当前生效：0 个应用 · 0 个网站）",
            dashboard.Presentation.ConfigurationText);
    }

    [Fact]
    public void DegradedPolicy_ProducesCalmFailOpenPresentation()
    {
        DashboardPresentation presentation = DashboardPresentationFactory.Create(
            DesktopPolicyResult.FailOpen("service-unavailable"),
            new DateTimeOffset(2026, 7, 7, 0, 30, 0, TimeSpan.Zero),
            null);

        Assert.Equal("保护已暂停", presentation.PhaseText);
        Assert.Equal("等待服务恢复", presentation.CountdownText);
        Assert.Equal("保护暂时降级，电脑保持可用。", presentation.StatusText);
        Assert.Equal("睡眠超时尚未读取", presentation.SleepTimeoutText);
        Assert.False(presentation.ShouldShowAttention);
        Assert.False(presentation.CanRequestTeamRescue);
        Assert.False(presentation.CanRequestEmergency);
        Assert.False(presentation.CanRequestEntertainment);
    }

    [Fact]
    public void Free_IsSilentAndShowsTonightRulesAndReadOnlySleepTimeout()
    {
        DateTimeOffset now = new(2026, 7, 6, 22, 0, 0, TimeSpan.Zero);
        DashboardPresentation presentation = DashboardPresentationFactory.Create(
            Policy(DesktopNightPhase.Free, now),
            now,
            new SleepTimeoutSnapshot(
                DesktopPowerSource.Ac,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(15)));

        Assert.Equal("自由时间", presentation.PhaseText);
        Assert.Equal("距离最后开局 02:05:00", presentation.CountdownText);
        Assert.Equal("保护正常", presentation.ProtectionText);
        Assert.Equal(
            "00:05 默认最晚开新一局 · 00:40 锁屏 · 01:00 关灯 · 09:00 起床",
            presentation.TonightRuleText);
        Assert.Equal("2 个应用 · 3 个网站", presentation.ConfigurationText);
        Assert.Equal(
            "当前：接通电源 · 接通电源 30 分钟 · 电池 15 分钟",
            presentation.SleepTimeoutText);
        Assert.False(presentation.ShouldShowAttention);
        Assert.False(presentation.CanRequestTeamRescue);
        Assert.False(presentation.CanRequestEmergency);
        Assert.False(presentation.CanRequestEntertainment);
    }

    [Fact]
    public void Free_AfterEarliestConfiguredGameCutoff_ShowsPartialWindDownAndRange()
    {
        DateTimeOffset now = new(2026, 7, 6, 23, 15, 0, TimeSpan.Zero);
        DesktopAppRuleDto[] rules =
        [
            Game("short", 15),
            Game("default", 35),
            Game("long", 90),
            new(
                "voice",
                @"C:\Apps\Voice\voice.exe",
                [],
                DesktopAppRuleCategory.Voice,
                90,
                true),
        ];

        DashboardPresentation presentation = DashboardPresentationFactory.Create(
            Policy(DesktopNightPhase.Free, now, appRules: rules),
            now,
            null);

        Assert.Equal("部分游戏已进入收尾", presentation.PhaseText);
        Assert.Equal("距离默认最后开局 00:50:00", presentation.CountdownText);
        Assert.Equal(
            "已到截止时间的游戏不再开新局；其余游戏按设置页各自行内时间执行。",
            presentation.StatusText);
        Assert.Equal(
            "00:05 默认最晚开新一局 · 游戏各自 23:10–00:25 · "
            + "00:40 锁屏 · 01:00 关灯 · 09:00 起床",
            presentation.TonightRuleText);
        Assert.True(presentation.ShouldShowAttention);
        Assert.True(presentation.CanRequestTeamRescue);
        Assert.True(presentation.CanRequestEmergency);
        Assert.True(presentation.CanRequestEntertainment);
    }

    [Fact]
    public void RolledBackDesktopClock_DoesNotReopenAnAuthoritativelyClosedGameCutoff()
    {
        DateTimeOffset evaluatedAt = new(2026, 7, 7, 0, 10, 0, TimeSpan.Zero);
        DateTimeOffset rolledBackDesktopClock = new(
            2026,
            7,
            6,
            23,
            0,
            0,
            TimeSpan.Zero);

        DashboardPresentation presentation = DashboardPresentationFactory.Create(
            Policy(
                DesktopNightPhase.LastStart,
                evaluatedAt,
                appRules: [Game("long", 90), Game("short", 15)]),
            rolledBackDesktopClock,
            null);

        Assert.Equal("游戏分批进入收尾", presentation.PhaseText);
        Assert.Equal("距离锁屏 00:30:00", presentation.CountdownText);
        Assert.Equal(
            "已到截止时间的游戏不再开新局；其余游戏按设置页各自行内时间执行。",
            presentation.StatusText);
    }

    [Fact]
    public void LastStart_WithMixedGameDurations_DoesNotClaimEveryGameClosedTogether()
    {
        DateTimeOffset now = new(2026, 7, 7, 0, 10, 0, TimeSpan.Zero);
        DesktopAppRuleDto[] rules =
        [
            Game("short", 15),
            Game("default", 35),
            Game("long", 90),
        ];

        DashboardPresentation presentation = DashboardPresentationFactory.Create(
            Policy(DesktopNightPhase.LastStart, now, appRules: rules),
            now,
            null);

        Assert.Equal("游戏分批进入收尾", presentation.PhaseText);
        Assert.Equal("距离锁屏 00:30:00", presentation.CountdownText);
        Assert.Equal(
            "已到截止时间的游戏不再开新局；其余游戏按设置页各自行内时间执行。",
            presentation.StatusText);
        Assert.True(presentation.ShouldShowAttention);
    }

    [Fact]
    public void Free_WithoutConfiguredGames_KeepsTheSimpleDefaultPresentation()
    {
        DateTimeOffset now = new(2026, 7, 6, 23, 15, 0, TimeSpan.Zero);
        DesktopAppRuleDto[] nonGames =
        [
            new(
                "voice",
                @"C:\Apps\Voice\voice.exe",
                [],
                DesktopAppRuleCategory.Voice,
                90,
                true),
            new(
                "unfinished-game",
                null,
                [],
                DesktopAppRuleCategory.Game,
                90,
                false),
        ];

        DashboardPresentation presentation = DashboardPresentationFactory.Create(
            Policy(DesktopNightPhase.Free, now, appRules: nonGames),
            now,
            null);

        Assert.Equal("自由时间", presentation.PhaseText);
        Assert.Equal("距离最后开局 00:50:00", presentation.CountdownText);
        Assert.Equal(
            "00:05 默认最晚开新一局 · 00:40 锁屏 · 01:00 关灯 · 09:00 起床",
            presentation.TonightRuleText);
        Assert.Equal("今晚按自己的节奏玩，收尾前不会打扰你。", presentation.StatusText);
    }

    [Fact]
    public void LastStart_BeforeEveryShortGameCutoff_DoesNotSayAnyGameIsClosed()
    {
        DateTimeOffset now = new(2026, 7, 7, 0, 10, 0, TimeSpan.Zero);

        DashboardPresentation presentation = DashboardPresentationFactory.Create(
            Policy(
                DesktopNightPhase.LastStart,
                now,
                appRules: [Game("short-one", 15), Game("short-two", 15)]),
            now,
            null);

        Assert.Equal("游戏仍按各自时间", presentation.PhaseText);
        Assert.Equal(
            "这些游戏尚未到各自行内截止；请按设置页时间安排最后一局。",
            presentation.StatusText);
        Assert.Equal(
            "00:05 默认最晚开新一局 · 游戏最晚开新一局 00:25 · "
            + "00:40 锁屏 · 01:00 关灯 · 09:00 起床",
            presentation.TonightRuleText);
    }

    [Theory]
    [InlineData(DesktopNightPhase.LastStart, "最后开局已结束", "距离锁屏 00:30:00", true)]
    [InlineData(DesktopNightPhase.Grace, "善后时间", "距离锁屏 00:10:00", true)]
    [InlineData(DesktopNightPhase.LandingLocked, "已到收尾时间", "电脑正在进入锁屏保护", true)]
    [InlineData(DesktopNightPhase.Morning, "早上好", "今晚保护已结束", false)]
    public void ScheduledPhases_UseCalmChineseAndOnlyRestrictedPhasesOfferExceptions(
        DesktopNightPhase phase,
        string expectedPhase,
        string expectedCountdown,
        bool exceptionsAvailable)
    {
        DateTimeOffset now = phase switch
        {
            DesktopNightPhase.LastStart => new(2026, 7, 7, 0, 10, 0, TimeSpan.Zero),
            DesktopNightPhase.Grace => new(2026, 7, 7, 0, 30, 0, TimeSpan.Zero),
            DesktopNightPhase.LandingLocked => new(2026, 7, 7, 0, 40, 0, TimeSpan.Zero),
            DesktopNightPhase.Morning => new(2026, 7, 7, 9, 0, 0, TimeSpan.Zero),
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };

        DashboardPresentation presentation = DashboardPresentationFactory.Create(
            Policy(phase, now),
            now,
            null);

        Assert.Equal(expectedPhase, presentation.PhaseText);
        Assert.Equal(expectedCountdown, presentation.CountdownText);
        Assert.Equal(exceptionsAvailable, presentation.ShouldShowAttention);
        Assert.Equal(exceptionsAvailable, presentation.CanRequestTeamRescue);
        Assert.Equal(exceptionsAvailable, presentation.CanRequestEmergency);
        Assert.Equal(exceptionsAvailable, presentation.CanRequestEntertainment);
    }

    [Fact]
    public void EntertainmentCoolingOff_ShowsOnlyTheServiceAuthoritativeCountdown()
    {
        DateTimeOffset requested = new(2026, 7, 7, 0, 40, 0, TimeSpan.Zero);
        DesktopActiveOverrideDto activeOverride = new(
            DesktopOverrideKind.Entertainment,
            requested,
            requested.AddMinutes(10),
            requested.AddMinutes(30),
            []);
        DateTimeOffset now = requested.AddMinutes(1);

        DashboardPresentation presentation = DashboardPresentationFactory.Create(
            Policy(DesktopNightPhase.CoolingOff, now, activeOverride),
            now,
            null);

        Assert.Equal("娱乐再用冷静期", presentation.PhaseText);
        Assert.Equal("冷静期剩余 00:09:00", presentation.CountdownText);
        Assert.True(presentation.ShouldShowAttention);
        Assert.False(presentation.CanRequestTeamRescue);
        Assert.False(presentation.CanRequestEmergency);
        Assert.False(presentation.CanRequestEntertainment);
    }

    [Theory]
    [InlineData(DesktopOverrideKind.TeamRescue, "团队救场中")]
    [InlineData(DesktopOverrideKind.Emergency, "紧急解锁中")]
    [InlineData(DesktopOverrideKind.Entertainment, "娱乐窗口中")]
    public void ActiveOverride_UsesItsAuthoritativeEndAndOffersNoNestedOverride(
        DesktopOverrideKind kind,
        string expectedPhase)
    {
        DateTimeOffset starts = new(2026, 7, 7, 0, 40, 0, TimeSpan.Zero);
        DesktopActiveOverrideDto activeOverride = new(
            kind,
            starts,
            starts,
            starts.AddMinutes(20),
            kind == DesktopOverrideKind.TeamRescue ? ["game"] : []);
        DateTimeOffset now = starts.AddMinutes(1);

        DashboardPresentation presentation = DashboardPresentationFactory.Create(
            Policy(DesktopNightPhase.OverrideActive, now, activeOverride),
            now,
            null);

        Assert.Equal(expectedPhase, presentation.PhaseText);
        Assert.Equal("剩余 00:19:00", presentation.CountdownText);
        Assert.False(presentation.ShouldShowAttention);
        Assert.False(presentation.CanRequestTeamRescue);
        Assert.False(presentation.CanRequestEmergency);
        Assert.False(presentation.CanRequestEntertainment);
    }

    [Theory]
    [InlineData(
        "teamRescueCooldownActive",
        "团队救场仍在 168 小时冷却期，本次未启用。")]
    [InlineData(
        "teamRescueUnavailable",
        "当前游戏或语音程序快照尚未准备好或已经变化，本次未启用，也没有消耗救场机会。")]
    [InlineData(
        "overrideAlreadyActive",
        "已有例外窗口正在生效，不能叠加新的例外。")]
    [InlineData(
        "alreadyUsedTonight",
        "今晚的娱乐再用已经使用，不能续期。")]
    [InlineData(
        "emergencyReasonRequired",
        "请选择健康、安全或紧急工作原因。")]
    [InlineData(
        "noActiveNight",
        "当前没有可使用例外的夜间状态。")]
    [InlineData(
        "service-unavailable",
        "本机服务暂时不可用，今晚的保护规则没有改变。")]
    [InlineData(
        "service-degraded",
        "本机服务暂时不可用，今晚的保护规则没有改变。")]
    [InlineData(
        "request-unavailable",
        "本机服务暂时不可用，今晚的保护规则没有改变。")]
    [InlineData(
        "request-in-progress",
        "已有一个例外请求正在确认，请稍候。")]
    public async Task OverrideRejection_MapsProtocolCodeToVisibleFriendlyStatus(
        string error,
        string expectedStatus)
    {
        DateTimeOffset now = new(2026, 7, 7, 0, 40, 0, TimeSpan.Zero);
        DesktopPolicyResult restricted = Policy(
            DesktopNightPhase.LandingLocked,
            now);
        DashboardViewModel dashboard = new(new FixedOverrideGateway(new(
            false,
            error,
            null,
            restricted)));
        dashboard.ApplyPolicy(restricted, now, null);

        DesktopOverrideResult result = await dashboard.RequestTeamRescueAsync();

        Assert.False(result.Accepted);
        Assert.Equal(expectedStatus, dashboard.Presentation.StatusText);
    }

    private static DesktopPolicyResult Policy(
        DesktopNightPhase phase,
        DateTimeOffset evaluatedAt,
        DesktopActiveOverrideDto? activeOverride = null,
        int appCount = 2,
        int siteCount = 3,
        IReadOnlyList<DesktopAppRuleDto>? appRules = null)
    {
        DateOnly date = new(2026, 7, 6);
        DateTimeOffset protectedStart = new(2026, 7, 6, 21, 0, 0, TimeSpan.Zero);
        DateTimeOffset lastStart = new(2026, 7, 7, 0, 5, 0, TimeSpan.Zero);
        DesktopNightWindowDto window = new(
            date,
            protectedStart,
            lastStart,
            lastStart.AddMinutes(35),
            lastStart.AddMinutes(55),
            lastStart.AddHours(8).AddMinutes(55));
        DesktopPolicySnapshotDto policy = new(
            evaluatedAt,
            phase,
            window,
            appRules ?? Enumerable.Range(1, appCount)
                .Select(index => Game($"app-{index}", 35))
                .ToArray(),
            Enumerable.Range(1, siteCount)
                .Select(index => new DesktopSiteRuleDto($"site-{index}.example"))
                .ToArray(),
            true,
            false,
            activeOverride);
        DesktopServiceRuntimeStatusDto status = new(true, false, null, policy);
        return new(true, false, null, status);
    }

    private static DesktopAppRuleDto Game(string id, int sessionMinutes) => new(
        id,
        $@"C:\Apps\{id}\game.exe",
        [],
        DesktopAppRuleCategory.Game,
        sessionMinutes,
        true);

    private static DesktopUserStateDto UserState(
        DateOnly currentNightDate,
        DesktopRuleSettingsStateDto rules) => new(
        new(1, null, null, null, null, null, null),
        new(1, 5, true, true, true, 1, DateTimeOffset.UtcNow, true),
        rules,
        new(
            currentNightDate.AddDays(-6),
            currentNightDate,
            0,
            0,
            0,
            0,
            null,
            null,
            new(0, 0, 0, 0, 0, 0)),
        currentNightDate,
        null,
        new("healthy", true, true, DateTimeOffset.UtcNow, "1.0.0"));

    private sealed class StagedRulesExperienceGateway(DesktopUserStateDto state) :
        IUserExperienceGateway
    {
        public ValueTask<DesktopUserStateResult> GetUserStateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesktopUserStateResult(true, null, state));

        public ValueTask<DesktopOnboardingMutationResult> CompleteOnboardingStepAsync(
            DesktopOnboardingStepRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesktopOnboardingMutationResult(false, "not-used", null));

        public ValueTask<DesktopRuleSettingsMutationResult> SaveRuleSettingsAsync(
            IReadOnlyList<DesktopAppRuleDraft> appRules,
            IReadOnlyList<string> siteDomains,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesktopRuleSettingsMutationResult(
                false,
                "not-used",
                null,
                false,
                false,
                null));

        public ValueTask<DesktopSelfReportMutationResult> SaveNightSelfReportAsync(
            DateOnly nightDate,
            bool? phoneOutOfReach,
            bool? wakeWithinWindow,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesktopSelfReportMutationResult(false, "not-used", null));

        public ValueTask<DesktopNoticeClaimResult> ClaimDueNoticeAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesktopNoticeClaimResult(false, "not-used", null, null));

        public ValueTask<DesktopIPhoneProgressionResult> ConfirmIPhoneProgressionAsync(
            int step,
            DesktopIPhoneChecklist checklist,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesktopIPhoneProgressionResult(
                false,
                "not-used",
                null,
                null));

        public ValueTask<DesktopClearHistoryResult> ClearHistoryAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesktopClearHistoryResult(false, "not-used"));
    }

    private sealed class RejectingOverrideGateway : IDesktopOverrideGateway
    {
        public ValueTask<DesktopOverrideResult> RequestAsync(
            DesktopOverrideRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedOverrideGateway(DesktopOverrideResult result) :
        IDesktopOverrideGateway
    {
        public ValueTask<DesktopOverrideResult> RequestAsync(
            DesktopOverrideRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }
}
