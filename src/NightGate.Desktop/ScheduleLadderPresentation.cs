using System.Globalization;
using NightGate.Core;

namespace NightGate.Desktop;

public enum DesktopScheduleLadderStepState
{
    Completed,
    Current,
    Next,
    Future,
}

public sealed record ScheduleMilestonePresentation(
    string Label,
    string TimeText,
    string HelpText);

public sealed record ScheduleLadderStepPresentation(
    int Number,
    string Name,
    string Description,
    string StatusText,
    DesktopScheduleLadderStepState State,
    string LastStartText,
    string LockText,
    string LightsOutText,
    string WakeText);

public sealed record ScheduleLadderPresentation(
    int CurrentStep,
    string CurrentStepHeading,
    string AutomaticProgressionText,
    string TonightContextText,
    bool IsWeekendShifted,
    IReadOnlyList<ScheduleMilestonePresentation> TonightMilestones,
    IReadOnlyList<ScheduleLadderStepPresentation> Steps,
    string ProgressionRuleText,
    string ProgressionDefinitionText,
    string ProgressionSafetyText,
    string DefaultLastStartHelpText);

public static class ScheduleLadderPresentationFactory
{
    private static readonly string[] StepNames =
    [
        "先稳住",
        "提前 15 分钟",
        "再提前 15 分钟",
        "目标作息",
    ];

    private static readonly string[] StepDescriptions =
    [
        "从这里开始，不要求一次到位",
        "整套作息比第 1 阶提前 15 分钟",
        "整套作息再提前 15 分钟",
        "00:15 左右关灯，08:15 起床",
    ];

    public static ScheduleLadderPresentation Create(
        int currentStep,
        DateOnly nightDate,
        int? pendingStep,
        DateTimeOffset? pendingStepConfirmedAtUtc,
        DateOnly? pendingStepEffectiveNightDate,
        TimeZoneInfo? timeZone = null)
    {
        ScheduleStep current = ScheduleProfile.Default.Steps
            .SingleOrDefault(step => step.Number == currentStep)
            ?? throw new ArgumentOutOfRangeException(nameof(currentStep));
        if (nightDate == default)
        {
            throw new ArgumentOutOfRangeException(nameof(nightDate));
        }

        timeZone ??= TimeZoneInfo.Local;
        NightWindow tonight = ScheduleEvaluator.CreateWindow(
            nightDate,
            current,
            timeZone);
        bool weekendShifted = nightDate.DayOfWeek is
            DayOfWeek.Friday or DayOfWeek.Saturday;
        ScheduleLadderStepPresentation[] steps = ScheduleProfile.Default.Steps
            .Select(step => PresentStep(
                step,
                currentStep,
                pendingStep,
                pendingStepConfirmedAtUtc))
            .ToArray();

        return new(
            currentStep,
            $"你现在在第 {currentStep} 阶 · {StepNames[currentStep - 1]}",
            "这不是四选一：收尾会先执行当前台阶；达标后才会邀请你进入下一阶，不需要现在选择。",
            weekendShifted
                ? $"今晚是{ChineseDayOfWeek(nightDate.DayOfWeek)}，已按周末规则整体顺延 1 小时。"
                : "今晚按工作日时间执行。",
            weekendShifted,
            PresentMilestones(tonight),
            steps,
            ProgressionRule(
                currentStep,
                pendingStep,
                pendingStepConfirmedAtUtc,
                pendingStepEffectiveNightDate),
            "计入晋级：周日至周四的夜晚，真正紧急情况不计入分母。达标：门禁后未启动新娱乐、按时锁屏，且未使用团队救场、娱乐再用或管理员绕过。",
            "没达成就留在当前阶，不倒退、不惩罚。",
            "“默认最晚开新一局”按 35 分钟估算；每个游戏会根据你设置的典型一局时长（15–90 分钟）自动前移。");
    }

    private static IReadOnlyList<ScheduleMilestonePresentation> PresentMilestones(
        NightWindow window) =>
    [
        new(
            "默认最晚开新一局",
            Format(window.LastStart),
            "到点后不再开始新的游戏或视频"),
        new(
            "Windows 锁屏",
            Format(window.Lock),
            "当前内容可以先安全收尾"),
        new(
            "建议关灯",
            Format(window.LightsOut),
            "离开屏幕，开始准备睡觉"),
        new(
            "起床",
            Format(window.Wake),
            "睡眠计划的目标起床时间"),
    ];

    private static ScheduleLadderStepPresentation PresentStep(
        ScheduleStep step,
        int currentStep,
        int? pendingStep,
        DateTimeOffset? pendingStepConfirmedAtUtc)
    {
        DesktopScheduleLadderStepState state = step.Number switch
        {
            _ when step.Number < currentStep => DesktopScheduleLadderStepState.Completed,
            _ when step.Number == currentStep => DesktopScheduleLadderStepState.Current,
            _ when step.Number == currentStep + 1 => DesktopScheduleLadderStepState.Next,
            _ => DesktopScheduleLadderStepState.Future,
        };
        string status = step.Number switch
        {
            _ when step.Number == currentStep && step.Number == 4 => "当前 · 目标作息",
            _ when step.Number == currentStep => "当前",
            _ when step.Number < currentStep => "已完成",
            _ when step.Number == pendingStep && pendingStepConfirmedAtUtc is null => "已解锁",
            _ when step.Number == pendingStep => "即将启用",
            _ when step.Number == currentStep + 1 => "下一步",
            4 => "最终目标",
            _ => "之后",
        };

        return new(
            step.Number,
            StepNames[step.Number - 1],
            StepDescriptions[step.Number - 1],
            status,
            state,
            Format(step.LastStart),
            Format(step.Lock),
            Format(step.LightsOut),
            Format(step.Wake));
    }

    private static string ProgressionRule(
        int currentStep,
        int? pendingStep,
        DateTimeOffset? pendingStepConfirmedAtUtc,
        DateOnly? pendingStepEffectiveNightDate)
    {
        if (pendingStep is { } pending)
        {
            if (pendingStepConfirmedAtUtc is null)
            {
                return $"第 {pending} 阶已解锁 → 完成 iPhone 同步清单 → 确认后从下一晚启用";
            }

            string effective = pendingStepEffectiveNightDate is { } date
                ? date.ToString("MM-dd", CultureInfo.InvariantCulture)
                : "下一晚";
            return $"第 {pending} 阶已确认 → 将从 {effective} 对应的晚间启用";
        }

        return currentStep == 4
            ? "已经到达目标台阶；继续按当前节奏生活即可。"
            : $"最近 4 个计入晋级的工作夜 → 至少 3 晚达标 → 邀请进入第 {currentStep + 1} 阶";
    }

    private static string ChineseDayOfWeek(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => throw new ArgumentOutOfRangeException(nameof(dayOfWeek)),
    };

    private static string Format(DateTimeOffset value) =>
        value.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Format(TimeOnly value) =>
        value.ToString("HH:mm", CultureInfo.InvariantCulture);
}
