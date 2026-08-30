using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;

namespace NightGate.Desktop;

public sealed record DashboardPresentation(
    string PhaseText,
    string CountdownText,
    string ProtectionText,
    string TonightRuleText,
    string ConfigurationText,
    string SleepTimeoutText,
    string StatusText,
    bool ShouldShowAttention,
    bool CanRequestTeamRescue,
    bool CanRequestEmergency,
    bool CanRequestEntertainment);

public static class DashboardPresentationFactory
{
    public static DashboardPresentation Create(
        DesktopPolicyResult policy,
        DateTimeOffset now,
        SleepTimeoutSnapshot? sleepTimeout,
        DesktopRuleSettingsStateDto? configuredRules = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.ExecutablePolicy is null)
        {
            return new(
                "保护已暂停",
                "等待服务恢复",
                "网页或电脑保护暂时降级",
                "今晚规则暂不可用",
                "应用和网站规则尚未读取",
                "睡眠超时尚未读取",
                "保护暂时降级，电脑保持可用。",
                false,
                false,
                false,
                false);
        }

        DesktopPolicySnapshotDto snapshot = policy.ExecutablePolicy;
        // The service evaluation time is rollback-hardened. The desktop wall clock is
        // only a transport/display fallback for degraded policy and must not make an
        // already enforced cutoff appear open again.
        now = snapshot.EvaluatedAt;
        GameCutoffSummary? gameCutoffs = GetGameCutoffs(snapshot);
        if (snapshot.Phase == DesktopNightPhase.Free)
        {
            bool someGamesWindingDown = gameCutoffs is { } cutoffs
                && cutoffs.Earliest < snapshot.Window.LastStart
                && now >= cutoffs.Earliest;
            return new(
                someGamesWindingDown ? "部分游戏已进入收尾" : "自由时间",
                someGamesWindingDown
                    ? $"距离默认最后开局 {FormatRemaining(snapshot.Window.LastStart - now)}"
                    : $"距离最后开局 {FormatRemaining(snapshot.Window.LastStart - now)}",
                "保护正常",
                FormatTonightRule(snapshot.Window, gameCutoffs),
                FormatConfiguration(snapshot, configuredRules, now),
                FormatSleepTimeout(sleepTimeout),
                someGamesWindingDown
                    ? "已到截止时间的游戏不再开新局；其余游戏按设置页各自行内时间执行。"
                    : "今晚按自己的节奏玩，收尾前不会打扰你。",
                someGamesWindingDown,
                someGamesWindingDown,
                someGamesWindingDown,
                someGamesWindingDown);
        }

        (string phaseText,
            string countdownText,
            string statusText,
            bool shouldShowAttention,
            bool exceptionsAvailable) =
            snapshot.Phase switch
            {
                DesktopNightPhase.LastStart when gameCutoffs is
                    { UsesCustomCutoffs: true } => (
                    FormatCustomCutoffPhase(gameCutoffs, now),
                    $"距离锁屏 {FormatRemaining(snapshot.Window.Lock - now)}",
                    FormatCustomCutoffStatus(gameCutoffs, now),
                    true,
                    true),
                DesktopNightPhase.LastStart => (
                    "最后开局已结束",
                    $"距离锁屏 {FormatRemaining(snapshot.Window.Lock - now)}",
                    "可以继续当前一局，但不要再开始新的娱乐。",
                    true,
                    true),
                DesktopNightPhase.Grace when gameCutoffs is
                    { UsesCustomCutoffs: true } => (
                    FormatCustomCutoffPhase(gameCutoffs, now),
                    $"距离锁屏 {FormatRemaining(snapshot.Window.Lock - now)}",
                    FormatCustomCutoffStatus(gameCutoffs, now),
                    true,
                    true),
                DesktopNightPhase.Grace => (
                    "善后时间",
                    $"距离锁屏 {FormatRemaining(snapshot.Window.Lock - now)}",
                    "保存进度，慢慢收尾。",
                    true,
                    true),
                DesktopNightPhase.LandingLocked => (
                    "已到收尾时间",
                    "电脑正在进入锁屏保护",
                    "如有需要，可以使用下面的例外入口。",
                    true,
                    true),
                DesktopNightPhase.CoolingOff when snapshot.ActiveOverride is
                    { Kind: DesktopOverrideKind.Entertainment } cooling => (
                        "娱乐再用冷静期",
                        $"冷静期剩余 {FormatRemaining(cooling.StartsAtUtc - now)}",
                        "娱乐窗口会由服务在冷静期结束后开启。",
                        true,
                        false),
                DesktopNightPhase.OverrideActive when snapshot.ActiveOverride is { } active => (
                    OverridePhaseText(active.Kind),
                    $"剩余 {FormatRemaining(active.EndsAtUtc - now)}",
                    "窗口到期后，收尾保护会自动恢复。",
                    false,
                    false),
                DesktopNightPhase.Morning => (
                    "早上好",
                    "今晚保护已结束",
                    "新的一天开始了，不需要补偿昨晚。",
                    false,
                    false),
                _ => (
                    "保护已暂停",
                    "等待服务恢复",
                    "暂时无法识别当前阶段，电脑保持可用。",
                    false,
                    false),
            };
        return new(
            phaseText,
            countdownText,
            "保护正常",
            FormatTonightRule(snapshot.Window, gameCutoffs),
            FormatConfiguration(snapshot, configuredRules, now),
            FormatSleepTimeout(sleepTimeout),
            statusText,
            shouldShowAttention,
            exceptionsAvailable,
            exceptionsAvailable,
            exceptionsAvailable);
    }

    private static string FormatTonightRule(
        DesktopNightWindowDto window,
        GameCutoffSummary? gameCutoffs)
    {
        string defaultRule =
            $"{window.LastStart.ToString("HH:mm", CultureInfo.InvariantCulture)} 默认最晚开新一局";
        string gameRule = gameCutoffs switch
        {
            { UsesCustomCutoffs: true } cutoffs when cutoffs.Earliest == cutoffs.Latest =>
                $" · 游戏最晚开新一局 {FormatClock(cutoffs.Earliest)}",
            { UsesCustomCutoffs: true } cutoffs =>
                $" · 游戏各自 {FormatClock(cutoffs.Earliest)}–{FormatClock(cutoffs.Latest)}",
            _ => string.Empty,
        };
        return defaultRule
            + gameRule
            + $" · {window.Lock.ToString("HH:mm", CultureInfo.InvariantCulture)} 锁屏"
            + $" · {window.LightsOut.ToString("HH:mm", CultureInfo.InvariantCulture)} 关灯"
            + $" · {window.Wake.ToString("HH:mm", CultureInfo.InvariantCulture)} 起床";
    }

    private static GameCutoffSummary? GetGameCutoffs(DesktopPolicySnapshotDto snapshot)
    {
        DateTimeOffset[] cutoffs = snapshot.AppRules
            .Where(rule => rule.IsConfigured && rule.Category == DesktopAppRuleCategory.Game)
            .Select(rule => snapshot.Window.Lock.AddMinutes(-rule.SessionMinutes))
            .Order()
            .ToArray();
        return cutoffs.Length == 0
            ? null
            : new(
                cutoffs[0],
                cutoffs[^1],
                cutoffs[0] != snapshot.Window.LastStart
                    || cutoffs[^1] != snapshot.Window.LastStart);
    }

    private static string FormatClock(DateTimeOffset value) =>
        value.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string FormatCustomCutoffStatus(
        GameCutoffSummary cutoffs,
        DateTimeOffset now) => now switch
        {
            _ when now < cutoffs.Earliest =>
                "这些游戏尚未到各自行内截止；请按设置页时间安排最后一局。",
            _ when now < cutoffs.Latest =>
                "已到截止时间的游戏不再开新局；其余游戏按设置页各自行内时间执行。",
            _ => "每个游戏都已按设置页中的各自行内时间进入收尾；当前一局仍可安全结束。",
        };

    private static string FormatCustomCutoffPhase(
        GameCutoffSummary cutoffs,
        DateTimeOffset now) => now switch
        {
            _ when now < cutoffs.Earliest => "游戏仍按各自时间",
            _ when now < cutoffs.Latest => "游戏分批进入收尾",
            _ => "游戏已按各自时间收尾",
        };

    private sealed record GameCutoffSummary(
        DateTimeOffset Earliest,
        DateTimeOffset Latest,
        bool UsesCustomCutoffs);

    private static string FormatConfiguration(
        DesktopPolicySnapshotDto policy,
        DesktopRuleSettingsStateDto? configuredRules,
        DateTimeOffset now)
    {
        string active = $"{policy.AppRules.Count} 个应用 · {policy.SiteRules.Count} 个网站";
        if (configuredRules?.PendingAppRules is not { } pendingApps
            || configuredRules.PendingSiteRules is not { } pendingSites
            || configuredRules.PendingEffectiveNightDate is not { } effectiveNight)
        {
            return active;
        }

        string pending = $"{pendingApps.Count} 个应用 · {pendingSites.Count} 个网站";
        DateOnly localDate = DateOnly.FromDateTime(now.DateTime);
        string timing = effectiveNight == localDate
            ? "今晚生效"
            : $"{effectiveNight:MM-dd} 晚生效";
        return $"{timing}：{pending}（当前生效：{active}）";
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        TimeSpan safe = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        return $"{(int)safe.TotalHours:00}:{safe.Minutes:00}:{safe.Seconds:00}";
    }

    private static string FormatSleepTimeout(SleepTimeoutSnapshot? value)
    {
        if (value is null)
        {
            return "睡眠超时尚未读取";
        }

        string active = value.ActiveSource switch
        {
            DesktopPowerSource.Ac => "接通电源",
            DesktopPowerSource.Battery => "电池",
            DesktopPowerSource.Unknown => "未知电源",
            _ => "未知电源",
        };
        return $"当前：{active} · 接通电源 {FormatTimeout(value.AcTimeout)} · "
            + $"电池 {FormatTimeout(value.BatteryTimeout)}";
    }

    private static string FormatTimeout(TimeSpan timeout)
    {
        if (timeout == TimeSpan.Zero)
        {
            return "从不";
        }

        if (timeout.TotalMinutes < 60)
        {
            return $"{(int)timeout.TotalMinutes} 分钟";
        }

        int totalMinutes = (int)timeout.TotalMinutes;
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return minutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {minutes} 分钟";
    }

    private static string OverridePhaseText(DesktopOverrideKind kind) => kind switch
    {
        DesktopOverrideKind.TeamRescue => "团队救场中",
        DesktopOverrideKind.Emergency => "紧急解锁中",
        DesktopOverrideKind.Entertainment => "娱乐窗口中",
        _ => "例外窗口中",
    };
}

public interface IDesktopOverrideGateway
{
    ValueTask<DesktopOverrideResult> RequestAsync(
        DesktopOverrideRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class DesktopClientOverrideGateway : IDesktopOverrideGateway
{
    private readonly NightGateDesktopClient _client;
    private readonly ICutoffPipelineBarrier _cutoffBarrier;

    public DesktopClientOverrideGateway(
        NightGateDesktopClient client,
        ICutoffPipelineBarrier? cutoffBarrier = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _cutoffBarrier = cutoffBarrier ?? new CutoffPipelineBarrier();
    }

    public async ValueTask<DesktopOverrideResult> RequestAsync(
        DesktopOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        using IDisposable lease = await _cutoffBarrier
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        return await _client.RequestOverrideAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }
}

internal enum DesktopOverrideRequestLifecycleStage
{
    Started,
    Completed,
}

internal sealed record DesktopOverrideRequestLifecycle(
    DesktopOverrideRequestLifecycleStage Stage,
    DesktopOverrideRequest Request,
    DesktopOverrideResult? Result = null,
    bool WasCanceled = false,
    Exception? Failure = null);

internal delegate ValueTask DesktopOverrideRequestLifecycleHandler(
    DesktopOverrideRequestLifecycle lifecycle);

public sealed class DashboardViewModel : INotifyPropertyChanged
{
    private readonly IDesktopOverrideGateway _overrideGateway;
    private readonly AsyncRelayCommand _teamRescueCommand;
    private readonly AsyncRelayCommand _emergencyHealthCommand;
    private readonly AsyncRelayCommand _emergencySafetyCommand;
    private readonly AsyncRelayCommand _emergencyUrgentWorkCommand;
    private readonly AsyncRelayCommand _entertainmentCommand;
    private DashboardPresentation _presentation;
    private DesktopPolicyResult _latestPolicy;
    private DateTimeOffset _latestNow;
    private SleepTimeoutSnapshot? _latestSleepTimeout;
    private int _requestInFlight;

    public DashboardViewModel(IDesktopOverrideGateway overrideGateway)
        : this(
            overrideGateway,
            new UserExperienceViewModel(new UnavailableUserExperienceGateway()))
    {
    }

    internal DashboardViewModel(
        IDesktopOverrideGateway overrideGateway,
        UserExperienceViewModel experience)
    {
        ArgumentNullException.ThrowIfNull(overrideGateway);
        ArgumentNullException.ThrowIfNull(experience);
        _overrideGateway = overrideGateway;
        Experience = experience;
        Experience.PropertyChanged += ExperiencePropertyChanged;
        _latestPolicy = DesktopPolicyResult.FailOpen("service-not-contacted");
        _latestNow = DateTimeOffset.Now;
        _presentation = DashboardPresentationFactory.Create(
            _latestPolicy,
            _latestNow,
            null,
            Experience.RuleSettings);
        _teamRescueCommand = new(
            () => RequestTeamRescueAsync().AsTask(),
            () => !IsRequestInFlight && _presentation.CanRequestTeamRescue);
        _emergencyHealthCommand = EmergencyCommandFor(DesktopEmergencyReason.Health);
        _emergencySafetyCommand = EmergencyCommandFor(DesktopEmergencyReason.Safety);
        _emergencyUrgentWorkCommand = EmergencyCommandFor(DesktopEmergencyReason.UrgentWork);
        _entertainmentCommand = new(
            () => RequestEntertainmentAsync().AsTask(),
            () => !IsRequestInFlight && _presentation.CanRequestEntertainment);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal event DesktopOverrideRequestLifecycleHandler? OverrideRequestLifecycle;

    public UserExperienceViewModel Experience { get; }

    public DashboardPresentation Presentation => _presentation;

    public ICommand TeamRescueCommand => _teamRescueCommand;

    public ICommand EmergencyHealthCommand => _emergencyHealthCommand;

    public ICommand EmergencySafetyCommand => _emergencySafetyCommand;

    public ICommand EmergencyUrgentWorkCommand => _emergencyUrgentWorkCommand;

    public ICommand EntertainmentCommand => _entertainmentCommand;

    public void ApplyPolicy(
        DesktopPolicyResult policy,
        DateTimeOffset now,
        SleepTimeoutSnapshot? sleepTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _latestPolicy = policy;
        _latestNow = now;
        _latestSleepTimeout = sleepTimeout;
        SetPresentation(DashboardPresentationFactory.Create(
            policy,
            now,
            sleepTimeout,
            Experience.RuleSettings));
    }

    private void ExperiencePropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(UserExperienceViewModel.RuleSettings))
        {
            return;
        }

        SetPresentation(DashboardPresentationFactory.Create(
            _latestPolicy,
            _latestNow,
            _latestSleepTimeout,
            Experience.RuleSettings));
    }

    public ValueTask<DesktopOverrideResult> RequestTeamRescueAsync(
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            new DesktopOverrideRequest(DesktopOverrideKind.TeamRescue),
            cancellationToken);

    public ValueTask<DesktopOverrideResult> RequestEmergencyAsync(
        DesktopEmergencyReason reason,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        return RequestAsync(
            new DesktopOverrideRequest(DesktopOverrideKind.Emergency, reason),
            cancellationToken);
    }

    public ValueTask<DesktopOverrideResult> RequestEntertainmentAsync(
        CancellationToken cancellationToken = default) =>
        RequestAsync(
            new DesktopOverrideRequest(DesktopOverrideKind.Entertainment),
            cancellationToken);

    private bool IsRequestInFlight => Volatile.Read(ref _requestInFlight) != 0;

    private AsyncRelayCommand EmergencyCommandFor(DesktopEmergencyReason reason) => new(
        () => RequestEmergencyAsync(reason).AsTask(),
        () => !IsRequestInFlight && _presentation.CanRequestEmergency);

    private async ValueTask<DesktopOverrideResult> RequestAsync(
        DesktopOverrideRequest request,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _requestInFlight, 1, 0) != 0)
        {
            DesktopOverrideResult inProgress = RejectedLocally("request-in-progress");
            SetStatus(FriendlyRejection(inProgress.Error));
            return inProgress;
        }

        RaiseCommandStateChanged();
        SetStatus("正在向本机服务确认，请稍候……");
        DesktopOverrideResult? completedResult = null;
        bool wasCanceled = false;
        Exception? lifecycleFailure = null;
        try
        {
            await PublishOverrideLifecycleAsync(new(
                DesktopOverrideRequestLifecycleStage.Started,
                request));
            DesktopOverrideResult result = await _overrideGateway
                .RequestAsync(request, cancellationToken);
            completedResult = result;
            DesktopPolicyResult refreshedPolicy = result.PolicyAfterRequest;
            DateTimeOffset refreshedAt = refreshedPolicy.ExecutablePolicy?.EvaluatedAt
                ?? _latestNow;
            ApplyPolicy(refreshedPolicy, refreshedAt, _latestSleepTimeout);
            if (!result.Accepted)
            {
                SetStatus(FriendlyRejection(result.Error));
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            wasCanceled = true;
            SetStatus("请求已取消，今晚的保护规则没有改变。");
            throw;
        }
        catch (Exception exception)
        {
            lifecycleFailure = exception;
            completedResult = RejectedLocally("request-unavailable");
            SetStatus("暂时无法联系本机服务，今晚的保护规则没有改变。");
            return completedResult;
        }
        finally
        {
            await PublishOverrideLifecycleAsync(new(
                DesktopOverrideRequestLifecycleStage.Completed,
                request,
                completedResult,
                wasCanceled,
                lifecycleFailure));
            Volatile.Write(ref _requestInFlight, 0);
            RaiseCommandStateChanged();
        }
    }

    private async ValueTask PublishOverrideLifecycleAsync(
        DesktopOverrideRequestLifecycle lifecycle)
    {
        DesktopOverrideRequestLifecycleHandler? handlers = OverrideRequestLifecycle;
        if (handlers is null)
        {
            return;
        }

        foreach (DesktopOverrideRequestLifecycleHandler handler in
                 handlers.GetInvocationList())
        {
            try
            {
                await handler(lifecycle);
            }
            catch (Exception)
            {
                // Observers cannot alter the request result or suppress cleanup.
            }
        }
    }

    private DesktopOverrideResult RejectedLocally(string error) =>
        new(false, error, null, _latestPolicy);

    private static string FriendlyRejection(string? error) => error switch
    {
        "teamRescueCooldownActive" =>
            "团队救场仍在 168 小时冷却期，本次未启用。",
        "teamRescueUnavailable" =>
            "当前游戏或语音程序快照尚未准备好或已经变化，本次未启用，也没有消耗救场机会。",
        "overrideAlreadyActive" =>
            "已有例外窗口正在生效，不能叠加新的例外。",
        "alreadyUsedTonight" =>
            "今晚的娱乐再用已经使用，不能续期。",
        "emergencyReasonRequired" =>
            "请选择健康、安全或紧急工作原因。",
        "noActiveNight" =>
            "当前没有可使用例外的夜间状态。",
        "service-unavailable" or "service-degraded" or "request-unavailable" =>
            "本机服务暂时不可用，今晚的保护规则没有改变。",
        "request-in-progress" =>
            "已有一个例外请求正在确认，请稍候。",
        "cooldown" =>
            "娱乐再用仍在冷静期，今晚的保护规则没有改变。",
        "unavailable" =>
            "这个入口目前不可用，今晚的保护规则没有改变。",
        _ => "服务没有接受这次请求，今晚的保护规则没有改变。",
    };

    private void SetStatus(string status) =>
        SetPresentation(_presentation with { StatusText = status });

    private void SetPresentation(DashboardPresentation presentation)
    {
        _presentation = presentation;
        OnPropertyChanged(nameof(Presentation));
        RaiseCommandStateChanged();
    }

    private void RaiseCommandStateChanged()
    {
        _teamRescueCommand.RaiseCanExecuteChanged();
        _emergencyHealthCommand.RaiseCanExecuteChanged();
        _emergencySafetyCommand.RaiseCanExecuteChanged();
        _emergencyUrgentWorkCommand.RaiseCanExecuteChanged();
        _entertainmentCommand.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool> canExecute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(canExecute);
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && _canExecute();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (OperationCanceledException)
        {
            // A canceled UI request has already restored a calm status message.
        }
        catch (Exception)
        {
            // Command entry points must never tear down the tray application.
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
