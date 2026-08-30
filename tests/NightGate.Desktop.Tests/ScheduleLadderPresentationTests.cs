using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class ScheduleLadderPresentationTests
{
    [Fact]
    public void WorkNight_ExplainsAutomaticProgressionAndHighlightsCurrentStep()
    {
        ScheduleLadderPresentation presentation = ScheduleLadderPresentationFactory.Create(
            currentStep: 2,
            nightDate: new DateOnly(2026, 7, 14),
            pendingStep: null,
            pendingStepConfirmedAtUtc: null,
            pendingStepEffectiveNightDate: null,
            timeZone: TimeZoneInfo.Utc);

        Assert.Equal("你现在在第 2 阶 · 提前 15 分钟", presentation.CurrentStepHeading);
        Assert.Contains("不是四选一", presentation.AutomaticProgressionText, StringComparison.Ordinal);
        Assert.Equal("今晚按工作日时间执行。", presentation.TonightContextText);
        Assert.Collection(
            presentation.TonightMilestones,
            item =>
            {
                Assert.Equal("默认最晚开新一局", item.Label);
                Assert.Equal("23:50", item.TimeText);
            },
            item =>
            {
                Assert.Equal("Windows 锁屏", item.Label);
                Assert.Equal("00:25", item.TimeText);
            },
            item =>
            {
                Assert.Equal("建议关灯", item.Label);
                Assert.Equal("00:45", item.TimeText);
            },
            item =>
            {
                Assert.Equal("起床", item.Label);
                Assert.Equal("08:45", item.TimeText);
            });
        Assert.Collection(
            presentation.Steps,
            item => Assert.Equal(DesktopScheduleLadderStepState.Completed, item.State),
            item => Assert.Equal(DesktopScheduleLadderStepState.Current, item.State),
            item => Assert.Equal(DesktopScheduleLadderStepState.Next, item.State),
            item => Assert.Equal(DesktopScheduleLadderStepState.Future, item.State));
        Assert.Contains("最近 4 个计入晋级的工作夜", presentation.ProgressionRuleText, StringComparison.Ordinal);
        Assert.Contains("至少 3 晚达标", presentation.ProgressionRuleText, StringComparison.Ordinal);
        Assert.Contains("不倒退、不惩罚", presentation.ProgressionSafetyText, StringComparison.Ordinal);
    }

    [Fact]
    public void FridayNight_ShowsActualWeekendShiftedTimes()
    {
        ScheduleLadderPresentation presentation = ScheduleLadderPresentationFactory.Create(
            currentStep: 4,
            nightDate: new DateOnly(2026, 7, 17),
            pendingStep: null,
            pendingStepConfirmedAtUtc: null,
            pendingStepEffectiveNightDate: null,
            timeZone: TimeZoneInfo.Utc);

        Assert.True(presentation.IsWeekendShifted);
        Assert.Equal("今晚是周五，已按周末规则整体顺延 1 小时。", presentation.TonightContextText);
        Assert.Equal(
            ["00:20", "00:55", "01:15", "09:15"],
            presentation.TonightMilestones.Select(item => item.TimeText));
        Assert.Equal("当前 · 目标作息", presentation.Steps[^1].StatusText);
        Assert.Contains("已经到达目标台阶", presentation.ProgressionRuleText, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingStep_ExplainsPhoneConfirmationInsteadOfPretendingToKnowRollingScore()
    {
        ScheduleLadderPresentation presentation = ScheduleLadderPresentationFactory.Create(
            currentStep: 1,
            nightDate: new DateOnly(2026, 7, 14),
            pendingStep: 2,
            pendingStepConfirmedAtUtc: null,
            pendingStepEffectiveNightDate: null,
            timeZone: TimeZoneInfo.Utc);

        Assert.Equal(DesktopScheduleLadderStepState.Next, presentation.Steps[1].State);
        Assert.Equal("已解锁", presentation.Steps[1].StatusText);
        Assert.Contains("完成 iPhone 同步清单", presentation.ProgressionRuleText, StringComparison.Ordinal);
    }
}
