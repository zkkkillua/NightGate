using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;

namespace NightGate.Desktop;

public interface IUserExperienceGateway
{
    ValueTask<DesktopUserStateResult> GetUserStateAsync(
        CancellationToken cancellationToken = default);

    ValueTask<DesktopOnboardingMutationResult> CompleteOnboardingStepAsync(
        DesktopOnboardingStepRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<DesktopRuleSettingsMutationResult> SaveRuleSettingsAsync(
        IReadOnlyList<DesktopAppRuleDraft> appRules,
        IReadOnlyList<string> siteDomains,
        CancellationToken cancellationToken = default);

    ValueTask<DesktopSelfReportMutationResult> SaveNightSelfReportAsync(
        DateOnly nightDate,
        bool? phoneOutOfReach,
        bool? wakeWithinWindow,
        CancellationToken cancellationToken = default);

    ValueTask<DesktopNoticeClaimResult> ClaimDueNoticeAsync(
        CancellationToken cancellationToken = default);

    ValueTask<DesktopIPhoneProgressionResult> ConfirmIPhoneProgressionAsync(
        int step,
        DesktopIPhoneChecklist checklist,
        CancellationToken cancellationToken = default);

    ValueTask<DesktopClearHistoryResult> ClearHistoryAsync(
        CancellationToken cancellationToken = default);
}

public sealed class DesktopClientUserExperienceGateway(NightGateDesktopClient client) :
    IUserExperienceGateway
{
    private readonly NightGateDesktopClient _client = client
        ?? throw new ArgumentNullException(nameof(client));

    public ValueTask<DesktopUserStateResult> GetUserStateAsync(
        CancellationToken cancellationToken = default) =>
        _client.GetUserStateAsync(cancellationToken);

    public ValueTask<DesktopOnboardingMutationResult> CompleteOnboardingStepAsync(
        DesktopOnboardingStepRequest request,
        CancellationToken cancellationToken = default) =>
        _client.CompleteOnboardingStepAsync(request, cancellationToken);

    public ValueTask<DesktopRuleSettingsMutationResult> SaveRuleSettingsAsync(
        IReadOnlyList<DesktopAppRuleDraft> appRules,
        IReadOnlyList<string> siteDomains,
        CancellationToken cancellationToken = default) =>
        _client.SaveRuleSettingsAsync(appRules, siteDomains, cancellationToken);

    public ValueTask<DesktopSelfReportMutationResult> SaveNightSelfReportAsync(
        DateOnly nightDate,
        bool? phoneOutOfReach,
        bool? wakeWithinWindow,
        CancellationToken cancellationToken = default) =>
        _client.SaveNightSelfReportAsync(
            nightDate,
            phoneOutOfReach,
            wakeWithinWindow,
            cancellationToken);

    public ValueTask<DesktopNoticeClaimResult> ClaimDueNoticeAsync(
        CancellationToken cancellationToken = default) =>
        _client.ClaimDueNoticeAsync(cancellationToken);

    public ValueTask<DesktopIPhoneProgressionResult> ConfirmIPhoneProgressionAsync(
        int step,
        DesktopIPhoneChecklist checklist,
        CancellationToken cancellationToken = default) =>
        _client.ConfirmIPhoneProgressionAsync(step, checklist, cancellationToken);

    public ValueTask<DesktopClearHistoryResult> ClearHistoryAsync(
        CancellationToken cancellationToken = default) =>
        _client.ClearHistoryAsync(cancellationToken);
}

internal sealed class UnavailableUserExperienceGateway : IUserExperienceGateway
{
    private const string Error = "service-not-configured";

    public ValueTask<DesktopUserStateResult> GetUserStateAsync(
        CancellationToken cancellationToken = default) => ValueTask.FromResult(
            DesktopUserStateResult.Unavailable(Error));

    public ValueTask<DesktopOnboardingMutationResult> CompleteOnboardingStepAsync(
        DesktopOnboardingStepRequest request,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new DesktopOnboardingMutationResult(false, Error, null));

    public ValueTask<DesktopRuleSettingsMutationResult> SaveRuleSettingsAsync(
        IReadOnlyList<DesktopAppRuleDraft> appRules,
        IReadOnlyList<string> siteDomains,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new DesktopRuleSettingsMutationResult(false, Error, null, false, false, null));

    public ValueTask<DesktopSelfReportMutationResult> SaveNightSelfReportAsync(
        DateOnly nightDate,
        bool? phoneOutOfReach,
        bool? wakeWithinWindow,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new DesktopSelfReportMutationResult(false, Error, null));

    public ValueTask<DesktopNoticeClaimResult> ClaimDueNoticeAsync(
        CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new DesktopNoticeClaimResult(false, Error, null, null));

    public ValueTask<DesktopIPhoneProgressionResult> ConfirmIPhoneProgressionAsync(
        int step,
        DesktopIPhoneChecklist checklist,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new DesktopIPhoneProgressionResult(false, Error, null, null));

    public ValueTask<DesktopClearHistoryResult> ClearHistoryAsync(
        CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new DesktopClearHistoryResult(false, Error));
}

public sealed record DesktopNoticePresentation(string Title, string Message);

public sealed class DesktopSiteSelectionViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public DesktopSiteSelectionViewModel(string domain, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Domain = domain;
        DisplayName = displayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Domain { get; }

    public string DisplayName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }
}

public sealed class DesktopAppRuleItemViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<int> SessionMinuteOptionsValue =
        [15, 25, 35, 45, 60, 90];
    private readonly Action _changed;
    private int _sessionMinutes;

    public DesktopAppRuleItemViewModel(
        string id,
        string rootExecutablePath,
        IReadOnlyList<string> helperExecutablePaths,
        DesktopAppRuleCategory category,
        int sessionMinutes,
        Action? changed = null)
    {
        Id = id;
        RootExecutablePath = rootExecutablePath;
        HelperExecutablePaths = helperExecutablePaths;
        Category = category;
        _sessionMinutes = sessionMinutes;
        _changed = changed ?? (() => { });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string RootExecutablePath { get; }

    public IReadOnlyList<string> HelperExecutablePaths { get; }

    public DesktopAppRuleCategory Category { get; }

    public int SessionMinutes
    {
        get => _sessionMinutes;
        set
        {
            if (!IsGame || value is < 15 or > 90 || _sessionMinutes == value)
            {
                return;
            }

            _sessionMinutes = value;
            PropertyChanged?.Invoke(this, new(nameof(SessionMinutes)));
            PropertyChanged?.Invoke(this, new(nameof(SessionText)));
            _changed();
        }
    }

    public IReadOnlyList<int> SessionMinuteOptions => SessionMinuteOptionsValue;

    public bool IsGame => Category == DesktopAppRuleCategory.Game;

    public string Name => Id;

    public string CategoryText => Category == DesktopAppRuleCategory.Game
        ? "游戏"
        : "语音通信";

    public string SessionText => Category == DesktopAppRuleCategory.Game
        ? $"典型一局 {SessionMinutes} 分钟"
        : "团队救场时允许继续通信";

    public DesktopAppRuleDraft ToDraft() => new(
        Id,
        RootExecutablePath,
        HelperExecutablePaths,
        Category,
        SessionMinutes);

    internal DesktopAppRuleItemViewModel WithHelpers(
        IReadOnlyList<string> helperExecutablePaths) => new(
            Id,
            RootExecutablePath,
            helperExecutablePaths,
            Category,
            SessionMinutes,
            _changed);
}

public sealed class LegacyShutdownTaskChoiceViewModel : INotifyPropertyChanged
{
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public LegacyShutdownTaskChoiceViewModel(
        LegacyShutdownTaskCandidate candidate,
        bool isSelected = false,
        Action? selectionChanged = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Candidate = candidate;
        _isSelected = isSelected;
        _selectionChanged = selectionChanged ?? (() => { });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LegacyShutdownTaskCandidate Candidate { get; }

    public string TaskPath => Candidate.TaskPath;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
            _selectionChanged();
        }
    }
}

public sealed class IPhoneChecklistViewModel : INotifyPropertyChanged
{
    private readonly Action _changed;
    private bool _healthSleepScheduleConfigured;
    private bool _sleepFocusConfigured;
    private bool _downtimeConfigured;
    private bool _blockAtDowntimeEnabled;
    private bool _entertainmentCategoriesRestricted;
    private bool _requiredAppsAllowed;
    private bool _safariNotAllowlisted;
    private bool _distinctRecoverableScreenTimePasscodeAcknowledged;
    private bool _oldAlarmsChecked;
    private bool _phonePlacementPlanned;

    public IPhoneChecklistViewModel(Action? changed = null) =>
        _changed = changed ?? (() => { });

    public event PropertyChangedEventHandler? PropertyChanged;

    public int RequiredCount => 10;

    public int ConfirmedCount =>
        (HealthSleepScheduleConfigured ? 1 : 0)
        + (SleepFocusConfigured ? 1 : 0)
        + (DowntimeConfigured ? 1 : 0)
        + (BlockAtDowntimeEnabled ? 1 : 0)
        + (EntertainmentCategoriesRestricted ? 1 : 0)
        + (RequiredAppsAllowed ? 1 : 0)
        + (SafariNotAllowlisted ? 1 : 0)
        + (DistinctRecoverableScreenTimePasscodeAcknowledged ? 1 : 0)
        + (OldAlarmsChecked ? 1 : 0)
        + (PhonePlacementPlanned ? 1 : 0);

    public int RemainingCount => RequiredCount - ConfirmedCount;

    public bool HealthSleepScheduleConfigured
    {
        get => _healthSleepScheduleConfigured;
        set => Set(ref _healthSleepScheduleConfigured, value, nameof(HealthSleepScheduleConfigured));
    }

    public bool SleepFocusConfigured
    {
        get => _sleepFocusConfigured;
        set => Set(ref _sleepFocusConfigured, value, nameof(SleepFocusConfigured));
    }

    public bool DowntimeConfigured
    {
        get => _downtimeConfigured;
        set => Set(ref _downtimeConfigured, value, nameof(DowntimeConfigured));
    }

    public bool BlockAtDowntimeEnabled
    {
        get => _blockAtDowntimeEnabled;
        set => Set(ref _blockAtDowntimeEnabled, value, nameof(BlockAtDowntimeEnabled));
    }

    public bool EntertainmentCategoriesRestricted
    {
        get => _entertainmentCategoriesRestricted;
        set => Set(
            ref _entertainmentCategoriesRestricted,
            value,
            nameof(EntertainmentCategoriesRestricted));
    }

    public bool RequiredAppsAllowed
    {
        get => _requiredAppsAllowed;
        set => Set(ref _requiredAppsAllowed, value, nameof(RequiredAppsAllowed));
    }

    public bool SafariNotAllowlisted
    {
        get => _safariNotAllowlisted;
        set => Set(ref _safariNotAllowlisted, value, nameof(SafariNotAllowlisted));
    }

    public bool DistinctRecoverableScreenTimePasscodeAcknowledged
    {
        get => _distinctRecoverableScreenTimePasscodeAcknowledged;
        set => Set(
            ref _distinctRecoverableScreenTimePasscodeAcknowledged,
            value,
            nameof(DistinctRecoverableScreenTimePasscodeAcknowledged));
    }

    public bool OldAlarmsChecked
    {
        get => _oldAlarmsChecked;
        set => Set(ref _oldAlarmsChecked, value, nameof(OldAlarmsChecked));
    }

    public bool PhonePlacementPlanned
    {
        get => _phonePlacementPlanned;
        set => Set(ref _phonePlacementPlanned, value, nameof(PhonePlacementPlanned));
    }

    public bool IsComplete => ToDto().IsComplete;

    public DesktopIPhoneChecklist ToDto() => new(
        HealthSleepScheduleConfigured,
        SleepFocusConfigured,
        DowntimeConfigured,
        BlockAtDowntimeEnabled,
        RequiredAppsAllowed,
        SafariNotAllowlisted,
        DistinctRecoverableScreenTimePasscodeAcknowledged,
        OldAlarmsChecked,
        PhonePlacementPlanned,
        EntertainmentCategoriesRestricted);

    public void Reset()
    {
        if (ConfirmedCount == 0)
        {
            return;
        }

        _healthSleepScheduleConfigured = false;
        _sleepFocusConfigured = false;
        _downtimeConfigured = false;
        _blockAtDowntimeEnabled = false;
        _entertainmentCategoriesRestricted = false;
        _requiredAppsAllowed = false;
        _safariNotAllowlisted = false;
        _distinctRecoverableScreenTimePasscodeAcknowledged = false;
        _oldAlarmsChecked = false;
        _phonePlacementPlanned = false;
        PropertyChanged?.Invoke(this, new(string.Empty));
        _changed();
    }

    private void Set(ref bool field, bool value, string propertyName)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new(propertyName));
        PropertyChanged?.Invoke(this, new(nameof(IsComplete)));
        PropertyChanged?.Invoke(this, new(nameof(ConfirmedCount)));
        PropertyChanged?.Invoke(this, new(nameof(RemainingCount)));
        _changed();
    }
}

public sealed class UserExperienceViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<int> GameSessionMinuteOptionsValue =
        [15, 25, 35, 45, 60, 90];
    private readonly IUserExperienceGateway _gateway;
    private readonly ILegacyTaskMigrationCoordinator? _legacyMigrationCoordinator;
    private readonly IGameDiscovery? _gameDiscovery;
    private readonly IChromeExtensionOptionsLauncher? _chromeExtensionOptionsLauncher;
    private readonly AsyncRelayCommand _refreshCommand;
    private readonly AsyncRelayCommand _saveRulesCommand;
    private readonly AsyncRelayCommand _scanInstalledGamesCommand;
    private readonly AsyncRelayCommand _addSelectedDiscoveredGamesCommand;
    private readonly AsyncRelayCommand _previousOnboardingCommand;
    private readonly AsyncRelayCommand _nextOnboardingCommand;
    private readonly AsyncRelayCommand _saveSelfReportCommand;
    private readonly AsyncRelayCommand _confirmProgressionCommand;
    private readonly AsyncRelayCommand _disableSelectedLegacyTasksCommand;
    private readonly AsyncRelayCommand _restoreLegacyTasksCommand;
    private readonly AsyncRelayCommand _openChromeExtensionOptionsCommand;
    private DesktopUserStateDto? _state;
    private DesktopLegacyMigrationSnapshot? _legacyMigrationSnapshot;
    private DesktopAppRuleItemViewModel? _selectedAppRule;
    private DesktopConnectionPresentation _connection;
    private IReadOnlyList<DesktopOnboardingStepPresentation> _onboardingSteps = [];
    private DesktopSettingsCategoryPresentation _selectedSettingsCategory;
    private string _statusMessage = "正在读取本机设置……";
    private string _gameDiscoveryStatusText = "正在准备扫描已安装的游戏……";
    private string _chromeExtensionOptionsStatusText =
        "先在“程序与网站”中保存娱乐网站，再到扩展选项选择相同的网站，并在 Chrome 提示中允许访问。";
    private bool _operationInFlight;
    private bool _gameDiscoveryStarted;
    private bool _gameDiscoveryInFlight;
    private bool _rulesSaved;
    private bool _rulesDirty;
    private bool _onboardingAcknowledgementsDirty;
    private bool _selfReportDirty;
    private bool _acknowledgedProtectionLimit;
    private bool _acknowledgedIncognitoWarning;
    private bool _acknowledgedChromeDegraded;
    private int _selectedOnboardingStep = 1;
    private int _selectedGameSessionMinutes = 35;
    private bool? _phoneOutOfReach;
    private bool? _wakeWithinWindow;
    private (int Step, DateOnly UnlockedByNightDate)? _iPhoneChecklistTarget;

    public UserExperienceViewModel(
        IUserExperienceGateway gateway,
        ILegacyTaskMigrationCoordinator? legacyMigrationCoordinator = null)
        : this(
            gateway,
            legacyMigrationCoordinator,
            gameDiscovery: null,
            chromeExtensionOptionsLauncher: null)
    {
    }

    internal UserExperienceViewModel(
        IUserExperienceGateway gateway,
        ILegacyTaskMigrationCoordinator? legacyMigrationCoordinator,
        IGameDiscovery? gameDiscovery,
        IChromeExtensionOptionsLauncher? chromeExtensionOptionsLauncher = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
        _legacyMigrationCoordinator = legacyMigrationCoordinator;
        _gameDiscovery = gameDiscovery;
        _chromeExtensionOptionsLauncher = chromeExtensionOptionsLauncher;
        _connection = new(
            DesktopConnectionState.Loading,
            "正在连接保护服务",
            "正在读取本机设置……");
        ConfiguredApps = [];
        DiscoveredGames = [];
        LegacyShutdownTasks = [];
        SettingsCategories =
        [
            new(DesktopSettingsCategory.Schedule, "作息与晋级", "当前台阶与下一台阶"),
            new(DesktopSettingsCategory.Rules, "程序与网站", "游戏、语音和娱乐网站"),
            new(DesktopSettingsCategory.Chrome, "Chrome 网页保护", "扩展与降级状态"),
            new(DesktopSettingsCategory.IPhone, "iPhone 清单", "Apple 设置与晋级确认"),
            new(DesktopSettingsCategory.Privacy, "旧任务、历史与隐私", "迁移、清除与保护边界"),
        ];
        _selectedSettingsCategory = SettingsCategories[0];
        SiteSelections =
        [
            new("bilibili.com", "哔哩哔哩"),
            new("iqiyi.com", "爱奇艺"),
            new("netflix.com", "Netflix"),
            new("v.qq.com", "腾讯视频"),
            new("youtube.com", "YouTube"),
        ];
        foreach (DesktopSiteSelectionViewModel site in SiteSelections)
        {
            site.PropertyChanged += (_, _) =>
            {
                _rulesSaved = false;
                _rulesDirty = true;
                RaiseCommandStateChanged();
            };
        }

        IPhone = new(RaiseCommandStateChanged);
        _refreshCommand = new(() => RefreshAsync().AsTask(), () => !OperationInFlight);
        _saveRulesCommand = new(() => SaveRulesAsync().AsTask(), () => CanSaveRules);
        _scanInstalledGamesCommand = new(
            () => ScanInstalledGamesAsync().AsTask(),
            () => CanScanInstalledGames);
        _addSelectedDiscoveredGamesCommand = new(
            () =>
            {
                AddSelectedDiscoveredGames();
                return Task.CompletedTask;
            },
            () => CanAddSelectedDiscoveredGames);
        _previousOnboardingCommand = new(
            () =>
            {
                SelectOnboardingStep(SelectedOnboardingStep - 1);
                return Task.CompletedTask;
            },
            () => IsAvailable && !OperationInFlight && SelectedOnboardingStep > 1);
        _nextOnboardingCommand = new(
            AdvanceOnboardingAsync,
            CanAdvanceOnboarding);
        _saveSelfReportCommand = new(
            () => SaveSelfReportAsync().AsTask(),
            () => IsAvailable && !OperationInFlight);
        _confirmProgressionCommand = new(
            () => ConfirmPendingProgressionAsync().AsTask(),
            () => CanConfirmProgression);
        _disableSelectedLegacyTasksCommand = new(
            () => DisableSelectedLegacyTasksAsync().AsTask(),
            () => CanDisableSelectedLegacyTasks);
        _restoreLegacyTasksCommand = new(
            () => RestoreLegacyTasksAsync().AsTask(),
            () => CanRestoreLegacyTasks);
        _openChromeExtensionOptionsCommand = new(
            () =>
            {
                OpenChromeExtensionOptions();
                return Task.CompletedTask;
            },
            () => true);
        RebuildOnboardingSteps();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<DesktopNoticePresentation>? NoticeRaised;

    public ObservableCollection<DesktopAppRuleItemViewModel> ConfiguredApps { get; }

    public ObservableCollection<DiscoveredGameChoiceViewModel> DiscoveredGames { get; }

    public ObservableCollection<DesktopSiteSelectionViewModel> SiteSelections { get; }

    public ObservableCollection<LegacyShutdownTaskChoiceViewModel> LegacyShutdownTasks
    {
        get;
    }

    public IPhoneChecklistViewModel IPhone { get; }

    public ICommand RefreshCommand => _refreshCommand;

    public ICommand SaveRulesCommand => _saveRulesCommand;

    public ICommand ScanInstalledGamesCommand => _scanInstalledGamesCommand;

    public ICommand AddSelectedDiscoveredGamesCommand =>
        _addSelectedDiscoveredGamesCommand;

    public ICommand PreviousOnboardingCommand => _previousOnboardingCommand;

    public ICommand NextOnboardingCommand => _nextOnboardingCommand;

    public ICommand SaveSelfReportCommand => _saveSelfReportCommand;

    public ICommand ConfirmProgressionCommand => _confirmProgressionCommand;

    public ICommand DisableSelectedLegacyTasksCommand =>
        _disableSelectedLegacyTasksCommand;

    public ICommand RestoreLegacyTasksCommand => _restoreLegacyTasksCommand;

    public ICommand OpenChromeExtensionOptionsCommand =>
        _openChromeExtensionOptionsCommand;

    public DesktopConnectionPresentation Connection => _connection;

    public DesktopRuleSettingsStateDto? RuleSettings => _state?.Rules;

    public bool IsLoading => Connection.State == DesktopConnectionState.Loading;

    public bool IsAvailable =>
        Connection.State == DesktopConnectionState.Available && _state is not null;

    public bool IsUnavailable => Connection.State == DesktopConnectionState.Unavailable;

    public bool IsOnboardingComplete => CompletedOnboardingStep == 5;

    public bool HasPendingProgression => _state?.Progress.PendingStep is not null
        && _state.Progress.PendingStepConfirmedAtUtc is null;

    public bool CanEditIPhoneChecklist => IsAvailable
        && !OperationInFlight
        && (!IsOnboardingComplete || HasPendingProgression);

    public string IPhoneChecklistTargetText
    {
        get
        {
            if (_state is null)
            {
                return "手机设置目标尚未读取。";
            }

            DesktopProgressStateDto progress = _state.Progress;
            if (!IsOnboardingComplete)
            {
                return $"首次设置目标：第 {progress.CurrentStep} 台阶 · {StepTimes(progress.CurrentStep)}";
            }

            if (progress.PendingStep is { } pending
                && progress.PendingStepConfirmedAtUtc is null
                && progress.PendingStepUnlockedByNightDate is { } unlockedBy)
            {
                return $"待确认目标：第 {pending} 台阶（{unlockedBy:MM-dd} 晚解锁） · {StepTimes(pending)}";
            }

            if (progress.PendingStep is { } confirmed
                && progress.PendingStepConfirmedAtUtc is not null)
            {
                return $"第 {confirmed} 台阶已确认，将从 {progress.PendingStepEffectiveNightDate:MM-dd} 对应的晚间开始；清单已清空。";
            }

            return "当前没有待确认的晋级。达到最近 4 个合格工作夜中的 3 晚后，这里会显示下一台阶的准确时间。";
        }
    }

    public int CompletedOnboardingStep => _state?.Onboarding.CompletedStep ?? 0;

    public int SelectedOnboardingStep => _selectedOnboardingStep;

    public IReadOnlyList<DesktopOnboardingStepPresentation> OnboardingSteps =>
        _onboardingSteps;

    public IReadOnlyList<DesktopSettingsCategoryPresentation> SettingsCategories { get; }

    public DesktopSettingsCategoryPresentation SelectedSettingsCategory
    {
        get => _selectedSettingsCategory;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            DesktopSettingsCategoryPresentation? known = SettingsCategories
                .FirstOrDefault(category => category.Id == value.Id);
            if (known is null || ReferenceEquals(_selectedSettingsCategory, known))
            {
                return;
            }

            _selectedSettingsCategory = known;
            OnPropertyChanged(nameof(SelectedSettingsCategory));
        }
    }

    public string SettingsTabTitle => IsOnboardingComplete ? "设置" : "设置与向导";

    public string ProgressStepText => _state is null
        ? "台阶尚未读取"
        : $"第 {_state.Progress.CurrentStep} 台阶";

    public string CurrentScheduleText => _state?.Progress.CurrentStep switch
    {
        1 => "工作日：00:05 最后开局 · 00:40 锁屏 · 01:00 关灯 · 09:00 起床",
        2 => "工作日：23:50 最后开局 · 00:25 锁屏 · 00:45 关灯 · 08:45 起床",
        3 => "工作日：23:35 最后开局 · 00:10 锁屏 · 00:30 关灯 · 08:30 起床",
        4 => "工作日：23:20 最后开局 · 23:55 锁屏 · 00:15 关灯 · 08:15 起床",
        _ => "作息时间尚未读取",
    };

    public ScheduleLadderPresentation ScheduleLadder
    {
        get
        {
            DesktopProgressStateDto? progress = _state?.Progress;
            return ScheduleLadderPresentationFactory.Create(
                progress?.CurrentStep ?? 1,
                _state?.CurrentNightDate ?? new DateOnly(2000, 1, 3),
                progress?.PendingStep,
                progress?.PendingStepConfirmedAtUtc,
                progress?.PendingStepEffectiveNightDate);
        }
    }

    public string ProgressionInvitationText
    {
        get
        {
            DesktopProgressStateDto? progress = _state?.Progress;
            if (progress?.PendingStep is not { } pending)
            {
                return "最近 4 个计入晋级的工作夜中至少 3 晚达标，才会邀请你进入下一台阶；未达成只保持，不倒退。";
            }

            return progress.PendingStepConfirmedAtUtc is null
                ? $"第 {pending} 台阶已经解锁。完成下方 iPhone 清单后再确认启用。"
                : $"第 {pending} 台阶已确认，将从 {progress.PendingStepEffectiveNightDate:MM-dd} 对应的晚间开始。";
        }
    }

    public string OnboardingTitle => SelectedOnboardingStep switch
    {
        1 => "先看看四级作息台阶",
        2 => "选择要保护的游戏和网站",
        3 => "确认 Chrome 网页保护",
        4 => "同步设置 iPhone",
        5 => "最后确认保护边界",
        _ => "首次设置",
    };

    public string OnboardingBody => SelectedOnboardingStep switch
    {
        1 => "这不是四选一：先从第 1 阶开始。下面会展示今晚实际时间、四级路线和自动晋级规则。",
        2 => "选择游戏、语音工具和视频网站。22:30 后保存的修改从次日晚间生效。",
        3 => "扩展必须与本机服务保持心跳；隐身模式未保护时会明确显示网页保护降级。",
        4 => "Windows 不会替你修改 Apple 设置。请逐项在手机上完成并确认。",
        5 => "收尾防临时冲动，不防拥有管理员权限的你主动拆除。紧急入口始终保留。",
        _ => string.Empty,
    };

    public string ChromeProtectionText => _state?.ChromeProtection switch
    {
        { Status: "healthy", IncognitoProtected: true } health =>
            $"Chrome 网页保护已连接（扩展 {health.ExtensionVersion}），隐身模式也受保护。",
        { Status: "healthy" } health =>
            $"网页保护降级：Chrome 扩展 {health.ExtensionVersion} 已连接，普通窗口仍受保护，但隐身模式未保护；最终 Windows 锁屏仍会执行。",
        { Status: "stale" } =>
            "Chrome 心跳已超过 90 秒，网页保护暂时降级；Windows 锁屏仍会执行。",
        { Status: "extensionMismatch" } =>
            "检测到的 Chrome 扩展与本程序不匹配，网页保护暂时降级。",
        { Status: "protectionDegraded" } =>
            "Chrome 扩展已连接，但网页保护尚未就绪；请检查扩展状态，网页保护暂时降级。",
        { Status: "degraded" } =>
            "暂时无法读取 Chrome 保护状态；Windows 锁屏仍会执行。",
        _ => "等待 Chrome 扩展与本机服务建立受验证心跳。",
    };

    public string ChromeExtensionOptionsStatusText
    {
        get => _chromeExtensionOptionsStatusText;
        private set
        {
            if (_chromeExtensionOptionsStatusText == value)
            {
                return;
            }

            _chromeExtensionOptionsStatusText = value;
            OnPropertyChanged(nameof(ChromeExtensionOptionsStatusText));
        }
    }

    public void OpenChromeExtensionOptions()
    {
        bool opened = _chromeExtensionOptionsLauncher?.TryOpen() == true;
        ChromeExtensionOptionsStatusText = opened
            ? "已让 Chrome 尝试打开扩展选项。页面出现后，请选择与“程序与网站”相同的网站，点击“保存并授权”，并在 Chrome 提示中允许访问；若页面没有出现，请使用下方手动路径。"
            : "没有自动打开。请在 Chrome 地址栏输入 chrome://extensions，找到“收尾”，依次打开“详细信息”→“扩展程序选项”。";
    }

    public string LegacyMigrationStatusText
    {
        get
        {
            DesktopLegacyMigrationSnapshot? snapshot = _legacyMigrationSnapshot;
            if (_legacyMigrationCoordinator is null)
            {
                return "旧自动关机任务会在 Windows 安装版首次运行时检查。";
            }

            if (snapshot is null)
            {
                return "正在检查旧的 Windows 自动关机计划任务……";
            }

            if (!snapshot.Available)
            {
                return "暂时无法核对旧计划任务；本次没有改动任何任务，稍后可以重试。";
            }

            if (!snapshot.ScanAvailable)
            {
                List<string> details = [];
                if (snapshot.UnverifiedDisabledCount > 0)
                {
                    details.Add($"另有 {snapshot.UnverifiedDisabledCount} 项停用记录当前无法从 Windows 核对，不能确认仍处于停用状态");
                }
                else if (snapshot.DisabledMigrations.Count > 0)
                {
                    details.Add($"此前已由 Windows 确认停用的 {snapshot.DisabledMigrations.Count} 项仍可恢复");
                }

                if (snapshot.PendingRecoveryCount > 0)
                {
                    details.Add($"有 {snapshot.PendingRecoveryCount} 项停用尚未完成，请重新勾选并再次停用");
                }

                if (snapshot.PendingRestoreCount > 0)
                {
                    details.Add($"有 {snapshot.PendingRestoreCount} 项恢复需要再次点击“恢复此前停用的旧任务”，Windows 可能要求一次管理员确认");
                }

                if (snapshot.FailedCount > 0)
                {
                    details.Add($"另有 {snapshot.FailedCount} 项内容已变化或缺少可信记录；如需重新启用，请在 Windows 任务计划程序中手动核对");
                }

                string suffix = details.Count == 0
                    ? string.Empty
                    : "；" + string.Join("；", details);
                return $"暂时无法扫描旧的 Windows 自动关机计划任务；本次没有改动任何新任务{suffix}。";
            }

            List<string> pending = [];
            if (snapshot.UnverifiedDisabledCount > 0)
            {
                pending.Add($"有 {snapshot.UnverifiedDisabledCount} 项记录当前不能确认仍处于停用状态；请在下方重新勾选停用，Windows 可能要求一次管理员确认");
            }

            if (snapshot.PendingRecoveryCount > 0)
            {
                pending.Add($"有 {snapshot.PendingRecoveryCount} 项停用尚未完成；请在下方重新勾选并再次停用，Windows 可能要求一次管理员确认");
            }

            if (snapshot.PendingRestoreCount > 0)
            {
                pending.Add($"有 {snapshot.PendingRestoreCount} 项恢复尚未完成；请再次点击“恢复此前停用的旧任务”，Windows 可能要求一次管理员确认");
            }

            if (pending.Count > 0 && snapshot.FailedCount > 0)
            {
                pending.Add($"另有 {snapshot.FailedCount} 项内容已变化或缺少可信记录；如需重新启用，请在 Windows 任务计划程序中手动核对");
            }

            if (pending.Count > 0)
            {
                return string.Join("；", pending) + "。";
            }

            List<string> parts = [];
            if (snapshot.Candidates.Count > 0)
            {
                parts.Add($"发现 {snapshot.Candidates.Count} 项旧自动关机任务；只有勾选并确认的任务会被停用，不会删除");
            }

            if (snapshot.DisabledMigrations.Count > 0)
            {
                parts.Add($"此前已由 Windows 确认停用 {snapshot.DisabledMigrations.Count} 项（未删除），可随时恢复");
            }

            if (snapshot.FailedCount > 0)
            {
                parts.Add($"另有 {snapshot.FailedCount} 项的内容已变化或缺少足够的核对信息，收尾没有继续改动；如需重新启用，请在 Windows 任务计划程序中手动核对");
            }

            return parts.Count == 0
                ? "没有发现需要迁移的旧自动关机任务。"
                : string.Join("；", parts) + "。";
        }
    }

    public string WeeklySummaryText
    {
        get
        {
            DesktopWeeklyReportSummaryDto? report = _state?.WeeklyReport;
            return report is null || report.EligibleWorkNights == 0
                ? "本周还没有足够的工作夜记录。"
                : $"本周 {report.QualifyingWorkNights}/{report.EligibleWorkNights} 个合格工作夜按计划收尾。";
        }
    }

    public string WeeklyLockText
    {
        get
        {
            DesktopWeeklyReportSummaryDto? report = _state?.WeeklyReport;
            if (report?.MedianLockTime is not { } median)
            {
                return "锁屏中位时间会在有记录后显示。";
            }

            string trend = report.MedianLockChangeMinutes switch
            {
                > 0 => $"，比前一周晚 {report.MedianLockChangeMinutes} 分钟",
                < 0 => $"，比前一周早 {-report.MedianLockChangeMinutes} 分钟",
                0 => "，与前一周接近",
                _ => string.Empty,
            };
            return $"锁屏中位时间 {median.ToString("HH:mm", CultureInfo.InvariantCulture)}{trend}。";
        }
    }

    public string WeeklyOverrideText
    {
        get
        {
            DesktopOverrideReasonSummaryDto? reasons = _state?.WeeklyReport.OverrideReasons;
            if (reasons is null)
            {
                return "例外使用情况尚未读取。";
            }

            return $"团队救场 {reasons.TeamRescueCount} 次 · 娱乐再用 {reasons.EntertainmentCount} 次 · 紧急：健康 {reasons.EmergencyHealthCount} 次 · 安全 {reasons.EmergencySafetyCount} 次 · 紧急工作 {reasons.EmergencyUrgentWorkCount} 次 · 其他 {reasons.EmergencyOtherCount} 次";
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged(nameof(StatusMessage));
        }
    }

    public DesktopAppRuleItemViewModel? SelectedAppRule
    {
        get => _selectedAppRule;
        set
        {
            if (ReferenceEquals(_selectedAppRule, value))
            {
                return;
            }

            _selectedAppRule = value;
            OnPropertyChanged(nameof(SelectedAppRule));
            OnPropertyChanged(nameof(SelectedAppRuleSessionMinutes));
            OnPropertyChanged(nameof(CanEditSelectedGameSessionMinutes));
            OnPropertyChanged(nameof(CanEditSelectedAppRule));
        }
    }

    public int SelectedAppRuleSessionMinutes
    {
        get => SelectedAppRule?.SessionMinutes ?? 35;
        set
        {
            if (!CanEditSelectedGameSessionMinutes
                || !GameSessionMinuteOptionsValue.Contains(value)
                || SelectedAppRule is not { } selected
                || selected.SessionMinutes == value)
            {
                return;
            }

            if (!ConfiguredApps.Contains(selected))
            {
                return;
            }

            selected.SessionMinutes = value;
        }
    }

    public bool CanEditSelectedGameSessionMinutes => CanEditRules
        && SelectedAppRule?.Category == DesktopAppRuleCategory.Game;

    public bool CanEditSelectedAppRule => CanEditRules
        && SelectedAppRule is { } selected
        && ConfiguredApps.Contains(selected);

    public bool AcknowledgedProtectionLimit
    {
        get => _acknowledgedProtectionLimit;
        set
        {
            if (_acknowledgedProtectionLimit == value)
            {
                return;
            }

            _acknowledgedProtectionLimit = value;
            _onboardingAcknowledgementsDirty = true;
            OnPropertyChanged(nameof(AcknowledgedProtectionLimit));
            RaiseCommandStateChanged();
        }
    }

    public bool AcknowledgedIncognitoWarning
    {
        get => _acknowledgedIncognitoWarning;
        set
        {
            if (_acknowledgedIncognitoWarning == value)
            {
                return;
            }

            _acknowledgedIncognitoWarning = value;
            _onboardingAcknowledgementsDirty = true;
            OnPropertyChanged(nameof(AcknowledgedIncognitoWarning));
            RaiseCommandStateChanged();
        }
    }

    public bool AcknowledgedChromeDegraded
    {
        get => _acknowledgedChromeDegraded;
        set
        {
            if (_acknowledgedChromeDegraded == value)
            {
                return;
            }

            _acknowledgedChromeDegraded = value;
            _onboardingAcknowledgementsDirty = true;
            OnPropertyChanged(nameof(AcknowledgedChromeDegraded));
            RaiseCommandStateChanged();
        }
    }

    public bool CanAcknowledgeIncognitoWarning =>
        _state?.ChromeProtection is
        {
            IsHealthy: true,
            IncognitoProtected: false,
        };

    public bool IsChromeProtectionDegraded =>
        IsAvailable && _state?.ChromeProtection.IsHealthy == false;

    public bool? PhoneOutOfReach
    {
        get => _phoneOutOfReach;
        set
        {
            if (_phoneOutOfReach == value)
            {
                return;
            }

            _phoneOutOfReach = value;
            _selfReportDirty = true;
            OnPropertyChanged(nameof(PhoneOutOfReach));
        }
    }

    public bool? WakeWithinWindow
    {
        get => _wakeWithinWindow;
        set
        {
            if (_wakeWithinWindow == value)
            {
                return;
            }

            _wakeWithinWindow = value;
            _selfReportDirty = true;
            OnPropertyChanged(nameof(WakeWithinWindow));
        }
    }

    public IReadOnlyList<int> GameSessionMinuteOptions =>
        GameSessionMinuteOptionsValue;

    public int SelectedGameSessionMinutes
    {
        get => _selectedGameSessionMinutes;
        set
        {
            if (!GameSessionMinuteOptionsValue.Contains(value)
                || _selectedGameSessionMinutes == value)
            {
                return;
            }

            _selectedGameSessionMinutes = value;
            OnPropertyChanged(nameof(SelectedGameSessionMinutes));
        }
    }

    public string GameDiscoveryStatusText => _gameDiscoveryStatusText;

    public bool IsGameDiscoveryInFlight => _gameDiscoveryInFlight;

    public bool HasDiscoveredGames => DiscoveredGames.Count > 0;

    public bool CanScanInstalledGames => IsAvailable
        && _gameDiscovery is not null
        && !OperationInFlight
        && !IsGameDiscoveryInFlight;

    public bool CanAddSelectedDiscoveredGames => CanEditRules
        && DiscoveredGames.Any(game => game.IsSelected && game.CanSelect);

    public bool CanEditRules => IsAvailable && !OperationInFlight;

    public bool CanSaveRules => CanEditRules
        && (ConfiguredApps.Count > 0
            || SiteSelections.Any(site => site.IsSelected)
            || (IsOnboardingComplete && _rulesDirty));

    public bool CanClearHistory => IsAvailable && !OperationInFlight;

    public bool CanCompleteOnboardingStep => IsAvailable
        && !OperationInFlight
        && CompletedOnboardingStep < 5
        && SelectedOnboardingStep == FirstIncompleteOnboardingStep
        && OnboardingMissingRequirement is null;

    public string? OnboardingMissingRequirement
    {
        get
        {
            if (!IsAvailable
                || CompletedOnboardingStep >= 5
                || SelectedOnboardingStep < FirstIncompleteOnboardingStep)
            {
                return null;
            }

            return SelectedOnboardingStep switch
            {
                1 => null,
                2 when !_rulesSaved =>
                    "请先添加至少一个程序或网站并保存规则。",
                3 when _state!.ChromeProtection.IsHealthy
                    && !_state.ChromeProtection.IncognitoProtected
                    && !AcknowledgedIncognitoWarning
                    && !_state.Onboarding.IncognitoWarningAcknowledged =>
                    "请确认隐身模式保护状态。",
                3 when _state!.ChromeProtection.Status == "protectionDegraded"
                    && !AcknowledgedChromeDegraded =>
                    "Chrome 扩展已连接但网页保护尚未就绪；确认网页保护降级后才能继续。",
                3 when !_state!.ChromeProtection.IsHealthy
                    && !AcknowledgedChromeDegraded =>
                    "Chrome 扩展未连接；确认网页保护降级后才能继续。",
                4 when !IPhone.IsComplete =>
                    $"iPhone 清单还有 {IPhone.RemainingCount} 项未确认。",
                5 when !AcknowledgedProtectionLimit =>
                    "请先确认收尾的保护边界。",
                _ => null,
            };
        }
    }

    public bool CanConfirmProgression => IsAvailable
        && !OperationInFlight
        && _state!.Progress.PendingStep is not null
        && _state.Progress.PendingStepConfirmedAtUtc is null
        && IPhone.IsComplete;

    public bool CanDisableSelectedLegacyTasks =>
        IsAvailable
        && _legacyMigrationSnapshot?.Available == true
        && !OperationInFlight
        && LegacyShutdownTasks.Any(item => item.IsSelected);

    public bool CanRestoreLegacyTasks =>
        IsAvailable
        && _legacyMigrationSnapshot?.Available == true
        && !OperationInFlight
        && (_legacyMigrationSnapshot.DisabledMigrations.Count > 0
            || _legacyMigrationSnapshot.PendingRecoveryCount > 0
            || _legacyMigrationSnapshot.PendingRestoreCount > 0);

    private bool OperationInFlight
    {
        get => _operationInFlight;
        set
        {
            _operationInFlight = value;
            RaiseCommandStateChanged();
        }
    }

    private int FirstIncompleteOnboardingStep =>
        Math.Min(CompletedOnboardingStep + 1, 5);

    public async ValueTask<DesktopUserStateResult> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        if (OperationInFlight)
        {
            return DesktopUserStateResult.Unavailable("request-in-progress");
        }

        bool operationReleasedForGameDiscovery = false;
        OperationInFlight = true;
        try
        {
            DesktopUserStateResult result = await _gateway
                .GetUserStateAsync(cancellationToken);
            if (!result.Available || result.State is null)
            {
                SetConnection(new(
                    DesktopConnectionState.Unavailable,
                    "保护服务暂不可用",
                    "电脑保持可用。你可以立即重试，收尾也会在后台自动重新连接。"));
                StatusMessage = "暂时无法读取设置；电脑保持可用，稍后会自动重试。";
                return result;
            }

            ApplyState(result.State, reloadRules: true);
            await RefreshLegacyMigrationCoreAsync(cancellationToken);
            StatusMessage = "本机设置已同步。";
            if (!_gameDiscoveryStarted && _gameDiscovery is not null)
            {
                operationReleasedForGameDiscovery = true;
                OperationInFlight = false;
                await ScanInstalledGamesCoreAsync(cancellationToken);
            }
            return result;
        }
        finally
        {
            if (!operationReleasedForGameDiscovery)
            {
                OperationInFlight = false;
            }
        }
    }

    public void SelectOnboardingStep(int step)
    {
        if (!IsAvailable
            || step is < 1 or > 5
            || step > FirstIncompleteOnboardingStep
            || step == SelectedOnboardingStep)
        {
            return;
        }

        _selectedOnboardingStep = step;
        NotifyOnboardingPresentation();
    }

    internal async Task AdvanceOnboardingAsync()
    {
        if (!IsAvailable || OperationInFlight)
        {
            return;
        }

        int firstIncomplete = FirstIncompleteOnboardingStep;
        if (SelectedOnboardingStep < firstIncomplete)
        {
            SelectOnboardingStep(SelectedOnboardingStep + 1);
            return;
        }

        if (CompletedOnboardingStep < 5)
        {
            _ = await CompleteCurrentOnboardingStepAsync();
        }
    }

    private bool CanAdvanceOnboarding() => IsAvailable
        && !OperationInFlight
        && (SelectedOnboardingStep < FirstIncompleteOnboardingStep
            || CanCompleteOnboardingStep);

    public async ValueTask<DesktopLegacyMigrationSnapshot>
        DisableSelectedLegacyTasksAsync(
            CancellationToken cancellationToken = default)
    {
        if (!CanDisableSelectedLegacyTasks || _legacyMigrationCoordinator is null)
        {
            return _legacyMigrationSnapshot
                ?? DesktopLegacyMigrationSnapshot.Unavailable("not-configured");
        }

        LegacyShutdownTaskCandidate[] selected = LegacyShutdownTasks
            .Where(item => item.IsSelected)
            .Select(item => item.Candidate)
            .ToArray();
        int previousFailedCount = _legacyMigrationSnapshot?.FailedCount ?? 0;
        OperationInFlight = true;
        StatusMessage = "正在安全停用选中的旧自动关机任务……";
        try
        {
            DesktopLegacyMigrationSnapshot snapshot = await _legacyMigrationCoordinator
                .DisableSelectedAsync(selected, cancellationToken);
            int unprocessedCount = selected.Count(candidate =>
                snapshot.Candidates.Contains(candidate));
            ApplyLegacyMigrationSnapshot(snapshot);
            StatusMessage = !snapshot.Available
                ? "暂时无法完成旧任务处理；未确认的任务保持原样。"
                : snapshot.UnverifiedDisabledCount > 0
                    ? $"有 {snapshot.UnverifiedDisabledCount} 项记录不能确认仍处于停用状态；请重新勾选停用，Windows 可能要求一次管理员确认。"
                : snapshot.PendingRecoveryCount > 0
                    ? $"仍有 {snapshot.PendingRecoveryCount} 项没有真正停用；请重新勾选并再次停用，Windows 可能要求一次管理员确认。"
                    : unprocessedCount > 0
                        ? $"有 {unprocessedCount} 项未处理，仍保持原样；其余处理结果见下方。"
                    : snapshot.FailedCount > previousFailedCount
                        ? "部分任务的内容已经变化，因此没有改动；其余处理结果见下方。"
                        : snapshot.ScanAvailable
                            ? "选中任务的处理结果已保存；已停用的任务没有删除。"
                            : "处理结果已保存，但暂时无法重新扫描确认；稍后会自动重试。";
            return snapshot;
        }
        finally
        {
            OperationInFlight = false;
        }
    }

    public async ValueTask<DesktopLegacyMigrationSnapshot> RestoreLegacyTasksAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanRestoreLegacyTasks || _legacyMigrationCoordinator is null)
        {
            return _legacyMigrationSnapshot
                ?? DesktopLegacyMigrationSnapshot.Unavailable("not-configured");
        }

        OperationInFlight = true;
        StatusMessage = "正在恢复此前由收尾停用的旧任务……";
        int previousFailedCount = _legacyMigrationSnapshot?.FailedCount ?? 0;
        try
        {
            DesktopLegacyMigrationSnapshot snapshot = await _legacyMigrationCoordinator
                .RestoreDisabledAsync(cancellationToken);
            ApplyLegacyMigrationSnapshot(snapshot);
            StatusMessage = !snapshot.Available
                ? "暂时无法恢复旧任务；保存的恢复记录仍然保留。"
                : snapshot.PendingRestoreCount > 0
                    && snapshot.FailedCount > previousFailedCount
                    ? $"仍有 {snapshot.PendingRestoreCount} 项恢复尚未完成；请再次点击“恢复此前停用的旧任务”，Windows 可能要求一次管理员确认。另有 {snapshot.FailedCount - previousFailedCount} 项内容已变化或缺少可信旧记录；请在 Windows 任务计划程序中手动核对。"
                : snapshot.PendingRestoreCount > 0
                    ? $"仍有 {snapshot.PendingRestoreCount} 项恢复尚未完成；请再次点击“恢复此前停用的旧任务”，Windows 可能要求一次管理员确认。"
                : snapshot.UnverifiedDisabledCount > 0
                    ? $"有 {snapshot.UnverifiedDisabledCount} 项停用记录当前无法由 Windows 核对；恢复记录已保留，稍后可重试。"
                : snapshot.PendingRecoveryCount > 0
                    ? $"仍有 {snapshot.PendingRecoveryCount} 项停用记录无法转入恢复流程；任务保持原样，稍后可重试。"
                : snapshot.FailedCount > previousFailedCount
                    ? $"有 {snapshot.FailedCount - previousFailedCount} 项任务的内容已变化或缺少可信的旧记录，收尾为安全没有自动启用。请在 Windows 任务计划程序中手动核对；无需反复点击恢复。"
                : snapshot.DisabledMigrations.Count > 0
                    ? $"仍有 {snapshot.DisabledMigrations.Count} 项尚未恢复；恢复记录已保留，稍后可重试。"
                    : "可恢复的旧任务已经恢复。";
            return snapshot;
        }
        finally
        {
            OperationInFlight = false;
        }
    }

    public ValueTask EnsureGameDiscoveryAsync(
        CancellationToken cancellationToken = default) =>
        _gameDiscoveryStarted
            ? ValueTask.CompletedTask
            : ScanInstalledGamesAsync(cancellationToken);

    public async ValueTask ScanInstalledGamesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanScanInstalledGames)
        {
            return;
        }

        await ScanInstalledGamesCoreAsync(cancellationToken);
    }

    public void AddSelectedDiscoveredGames()
    {
        if (!CanAddSelectedDiscoveredGames)
        {
            return;
        }

        DiscoveredGameChoiceViewModel[] selected = DiscoveredGames
            .Where(game => game.IsSelected && game.CanSelect)
            .ToArray();
        foreach (DiscoveredGameChoiceViewModel choice in selected)
        {
            AddAppRule(
                choice.ExecutablePath,
                DesktopAppRuleCategory.Game,
                choice.SessionMinutes,
                choice.DisplayName);
            choice.SetConfigured(configured: true);
        }

        _gameDiscoveryStatusText = selected.Length == 0
            ? _gameDiscoveryStatusText
            : $"已把 {selected.Length} 个游戏加入规则；确认列表后请点击“保存规则”。";
        OnPropertyChanged(nameof(GameDiscoveryStatusText));
        RaiseCommandStateChanged();
    }

    private async ValueTask ScanInstalledGamesCoreAsync(
        CancellationToken cancellationToken)
    {
        if (_gameDiscovery is null || _gameDiscoveryInFlight)
        {
            return;
        }

        _gameDiscoveryStarted = true;
        _gameDiscoveryInFlight = true;
        _gameDiscoveryStatusText = "正在读取 Steam、Epic、Xbox 和常见游戏目录……";
        NotifyGameDiscoveryPresentation();
        try
        {
            GameDiscoverySnapshot snapshot = await _gameDiscovery
                .DiscoverAsync(cancellationToken);
            Dictionary<string, (bool IsSelected, int SessionMinutes)> pendingChoices =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (DiscoveredGameChoiceViewModel choice in DiscoveredGames.Where(
                         game => game.CanSelect))
            {
                pendingChoices[choice.ExecutablePath] =
                    (choice.IsSelected, choice.SessionMinutes);
            }
            DiscoveredGames.Clear();
            foreach (DiscoveredGame game in snapshot.Games)
            {
                bool configured = ConfiguredApps.Any(app => string.Equals(
                    app.RootExecutablePath,
                    game.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase));
                DiscoveredGameChoiceViewModel choice = new(
                    game,
                    configured,
                    RaiseCommandStateChanged);
                if (pendingChoices.TryGetValue(game.ExecutablePath, out var pending))
                {
                    choice.SessionMinutes = pending.SessionMinutes;
                    choice.IsSelected = !configured && pending.IsSelected;
                }
                DiscoveredGames.Add(choice);
            }

            _gameDiscoveryStatusText = GameDiscoveryPresentation.Summary(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _gameDiscoveryStarted = false;
            _gameDiscoveryStatusText = "扫描已取消；已有规则没有改变。";
            throw;
        }
        catch (Exception)
        {
            _gameDiscoveryStatusText =
                "自动扫描暂不可用；已有规则没有改变，你仍可手动选择 exe。";
        }
        finally
        {
            _gameDiscoveryInFlight = false;
            NotifyGameDiscoveryPresentation();
        }
    }

    private void NotifyGameDiscoveryPresentation()
    {
        OnPropertyChanged(nameof(GameDiscoveryStatusText));
        OnPropertyChanged(nameof(IsGameDiscoveryInFlight));
        OnPropertyChanged(nameof(HasDiscoveredGames));
        OnPropertyChanged(nameof(CanScanInstalledGames));
        OnPropertyChanged(nameof(CanAddSelectedDiscoveredGames));
        RaiseCommandStateChanged();
    }

    public void AddAppRule(
        string executablePath,
        DesktopAppRuleCategory category,
        int sessionMinutes,
        string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!CanEditRules)
        {
            return;
        }

        if (!Enum.IsDefined(category) || sessionMinutes is < 15 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionMinutes));
        }

        string baseId = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileNameWithoutExtension(executablePath)
                .Trim()
                .ToLowerInvariant()
            : displayName.Trim();
        if (baseId.Length > NightGate.Core.AppRule.MaximumIdLength)
        {
            baseId = baseId[..NightGate.Core.AppRule.MaximumIdLength].TrimEnd();
        }
        if (string.IsNullOrWhiteSpace(baseId))
        {
            throw new ArgumentException("Executable path has no file name.", nameof(executablePath));
        }

        DesktopAppRuleItemViewModel? existing = ConfiguredApps.FirstOrDefault(app =>
            string.Equals(
                app.RootExecutablePath,
                executablePath,
                StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedAppRule = existing;
            DiscoveredGames.FirstOrDefault(game => string.Equals(
                game.ExecutablePath,
                executablePath,
                StringComparison.OrdinalIgnoreCase))?.SetConfigured(configured: true);
            return;
        }

        string id = baseId;
        for (int suffix = 2; ConfiguredApps.Any(app =>
                 string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase)); suffix++)
        {
            string suffixText = $"-{suffix}";
            int stemLength = Math.Min(
                baseId.Length,
                NightGate.Core.AppRule.MaximumIdLength - suffixText.Length);
            id = baseId[..stemLength] + suffixText;
        }

        DesktopAppRuleItemViewModel item = new(
            id,
            executablePath,
            [],
            category,
            sessionMinutes,
            OnConfiguredAppRuleChanged);
        ConfiguredApps.Add(item);
        SelectedAppRule = item;
        DiscoveredGames.FirstOrDefault(game => string.Equals(
            game.ExecutablePath,
            executablePath,
            StringComparison.OrdinalIgnoreCase))?.SetConfigured(configured: true);
        _rulesSaved = false;
        _rulesDirty = true;
        NotifyAll();
    }

    public void RemoveSelectedAppRule()
    {
        if (!CanEditRules || SelectedAppRule is null)
        {
            return;
        }

        string removedPath = SelectedAppRule.RootExecutablePath;
        _ = ConfiguredApps.Remove(SelectedAppRule);
        SelectedAppRule = null;
        DiscoveredGames.FirstOrDefault(game => string.Equals(
            game.ExecutablePath,
            removedPath,
            StringComparison.OrdinalIgnoreCase))?.SetConfigured(configured: false);
        _rulesSaved = false;
        _rulesDirty = true;
        NotifyAll();
    }

    private void OnConfiguredAppRuleChanged()
    {
        _rulesSaved = false;
        _rulesDirty = true;
        NotifyAll();
    }

    public void AddHelperToSelectedAppRule(string helperExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperExecutablePath);
        if (!CanEditRules)
        {
            return;
        }

        if (SelectedAppRule is not { } selected)
        {
            throw new InvalidOperationException("Select an application rule first.");
        }

        if (selected.HelperExecutablePaths.Contains(
                helperExecutablePath,
                StringComparer.OrdinalIgnoreCase)
            || string.Equals(
                selected.RootExecutablePath,
                helperExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (selected.HelperExecutablePaths.Count >= 32)
        {
            throw new InvalidOperationException("Each application can have at most 32 helpers.");
        }

        int index = ConfiguredApps.IndexOf(selected);
        DesktopAppRuleItemViewModel replacement = selected.WithHelpers(
            selected.HelperExecutablePaths
                .Append(helperExecutablePath)
                .ToArray());
        ConfiguredApps[index] = replacement;
        SelectedAppRule = replacement;
        _rulesSaved = false;
        _rulesDirty = true;
        NotifyAll();
    }

    public void RemoveHelperFromAppRule(
        DesktopAppRuleItemViewModel appRule,
        string helperExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(appRule);
        ArgumentException.ThrowIfNullOrWhiteSpace(helperExecutablePath);
        if (!CanEditRules)
        {
            return;
        }

        int index = ConfiguredApps.IndexOf(appRule);
        if (index < 0
            || !appRule.HelperExecutablePaths.Contains(
                helperExecutablePath,
                StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        DesktopAppRuleItemViewModel replacement = appRule.WithHelpers(
            appRule.HelperExecutablePaths
                .Where(path => !string.Equals(
                    path,
                    helperExecutablePath,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray());
        ConfiguredApps[index] = replacement;
        if (ReferenceEquals(SelectedAppRule, appRule))
        {
            SelectedAppRule = replacement;
        }

        _rulesSaved = false;
        _rulesDirty = true;
        NotifyAll();
    }

    public async ValueTask<DesktopRuleSettingsMutationResult> SaveRulesAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanSaveRules)
        {
            return new(false, "rules-incomplete", null, false, false, null);
        }

        OperationInFlight = true;
        StatusMessage = "正在保存规则……";
        try
        {
            DesktopRuleSettingsMutationResult result = await _gateway.SaveRuleSettingsAsync(
                ConfiguredApps.Select(app => app.ToDraft()).ToArray(),
                SiteSelections.Where(site => site.IsSelected)
                    .Select(site => site.Domain)
                    .ToArray(),
                cancellationToken);
            if (!result.Saved || result.Rules is null)
            {
                StatusMessage = "规则暂时没有保存；原有保护保持不变。";
                return result;
            }

            _state = _state! with { Rules = result.Rules };
            _rulesSaved = true;
            _rulesDirty = false;
            StatusMessage = result.AppliesTonight
                ? "规则已保存，今晚生效。"
                : $"规则已保存，将从 {result.EffectiveNight:MM-dd} 对应的次日晚间生效。";
            NotifyAll();
            return result;
        }
        finally
        {
            OperationInFlight = false;
        }
    }

    public async ValueTask<DesktopOnboardingMutationResult>
        CompleteCurrentOnboardingStepAsync(
            CancellationToken cancellationToken = default)
    {
        if (!CanCompleteOnboardingStep || _state is null)
        {
            return new(false, "step-incomplete", null);
        }

        OperationInFlight = true;
        try
        {
            DesktopOnboardingStateDto current = _state.Onboarding;
            DesktopChromeProtectionStatusDto chromeProtection =
                _state.ChromeProtection;
            int target = current.CompletedStep + 1;
            int iPhoneConfirmedThrough = target == 4
                ? Math.Max(current.IPhoneConfirmedThroughStep, _state.Progress.CurrentStep)
                : current.IPhoneConfirmedThroughStep;
            DesktopOnboardingStepRequest request = new(
                target,
                chromeProtection.IsHealthy,
                chromeProtection.IncognitoProtected,
                current.IncognitoWarningAcknowledged
                    || AcknowledgedIncognitoWarning,
                iPhoneConfirmedThrough,
                AcknowledgedChromeDegraded);
            DesktopOnboardingMutationResult result = await _gateway
                .CompleteOnboardingStepAsync(request, cancellationToken);
            if (!result.Accepted || result.Onboarding is null)
            {
                StatusMessage = "这一步还没有完成；已完成的设置不会丢失。";
                return result;
            }

            _state = _state with { Onboarding = result.Onboarding };
            if (current.CompletedStep < 5 && result.Onboarding.CompletedStep == 5)
            {
                IPhone.Reset();
            }
            SynchronizeIPhoneChecklistTarget(currentState: _state);
            _acknowledgedIncognitoWarning =
                result.Onboarding.IncognitoWarningAcknowledged;
            _acknowledgedChromeDegraded =
                result.Onboarding.ChromeDegradedAcknowledged;
            _onboardingAcknowledgementsDirty = false;
            _selectedOnboardingStep = Math.Min(
                result.Onboarding.CompletedStep + 1,
                5);
            StatusMessage = target == 5 ? "首次设置完成。今晚按自己的节奏开始。" : "这一步已保存。";
            NotifyAll();
            return result;
        }
        finally
        {
            OperationInFlight = false;
        }
    }

    public async ValueTask<DesktopSelfReportMutationResult> SaveSelfReportAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || _state is null || OperationInFlight)
        {
            return new(false, "state-unavailable", null);
        }

        OperationInFlight = true;
        try
        {
            DesktopSelfReportMutationResult result = await _gateway.SaveNightSelfReportAsync(
                _state.CurrentNightDate,
                PhoneOutOfReach,
                WakeWithinWindow,
                cancellationToken);
            if (result.Saved && result.SelfReport is not null)
            {
                _state = _state with { SelfReport = result.SelfReport };
                _selfReportDirty = false;
                StatusMessage = "今晚的简短记录已保存。";
            }
            else
            {
                StatusMessage = "记录暂时没有保存，稍后可以再试。";
            }

            return result;
        }
        finally
        {
            OperationInFlight = false;
        }
    }

    public async ValueTask<DesktopNoticeClaimResult> PollNoticeAsync(
        CancellationToken cancellationToken = default)
    {
        DesktopNoticeClaimResult result = await _gateway.ClaimDueNoticeAsync(cancellationToken);
        if (result.Claimed && result.Kind is { } kind)
        {
            NoticeRaised?.Invoke(this, NoticeFor(kind));
        }

        return result;
    }

    public async ValueTask<DesktopIPhoneProgressionResult> ConfirmPendingProgressionAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanConfirmProgression || _state?.Progress.PendingStep is not { } pendingStep)
        {
            return new(false, "checklist-incomplete", null, null);
        }

        OperationInFlight = true;
        try
        {
            DesktopIPhoneProgressionResult result = await _gateway
                .ConfirmIPhoneProgressionAsync(pendingStep, IPhone.ToDto(), cancellationToken);
            if (result.Accepted && result.EffectiveNightDate is { } effectiveNight)
            {
                _state = _state! with
                {
                    Progress = _state.Progress with
                    {
                        PendingStep = result.PendingStep ?? pendingStep,
                        PendingStepConfirmedAtUtc = DateTimeOffset.UtcNow,
                        PendingStepEffectiveNightDate = effectiveNight,
                    },
                };
                SynchronizeIPhoneChecklistTarget(_state);
                NotifyAll();
            }

            StatusMessage = result.Accepted
                ? $"手机设置已确认；第 {pendingStep} 台阶将从 {result.EffectiveNightDate:MM-dd} 对应的晚间开始。"
                : "手机设置确认暂未保存，当前台阶保持不变。";
            return result;
        }
        finally
        {
            OperationInFlight = false;
        }
    }

    public async ValueTask<DesktopClearHistoryResult> ClearHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || OperationInFlight)
        {
            return new(false, IsAvailable ? "request-in-progress" : "state-unavailable");
        }

        OperationInFlight = true;
        try
        {
            DesktopClearHistoryResult result = await _gateway
                .ClearHistoryAsync(cancellationToken);
            if (!result.Cleared)
            {
                StatusMessage = "历史暂时没有清除；当前保护规则没有改变。";
                return result;
            }

            DesktopUserStateResult refreshed = await _gateway
                .GetUserStateAsync(cancellationToken);
            if (refreshed.Available && refreshed.State is not null)
            {
                _selfReportDirty = false;
                ApplyState(refreshed.State, reloadRules: false);
            }

            StatusMessage = "全部本机历史（包括原始事件、周报来源和自报记录）已清除；今晚状态和当前规则仍保留。";
            return result;
        }
        finally
        {
            OperationInFlight = false;
        }
    }

    private void ApplyState(DesktopUserStateDto state, bool reloadRules)
    {
        DesktopUserStateDto? previous = _state;
        bool sameOnboardingStep = previous?.Onboarding.CompletedStep
            == state.Onboarding.CompletedStep;
        bool sameNight = previous?.CurrentNightDate == state.CurrentNightDate;
        _state = state;
        if (previous?.Onboarding.CompletedStep is < 5
            && state.Onboarding.CompletedStep == 5)
        {
            IPhone.Reset();
        }
        SynchronizeIPhoneChecklistTarget(state);
        if (!_onboardingAcknowledgementsDirty || !sameOnboardingStep)
        {
            _acknowledgedIncognitoWarning =
                state.Onboarding.IncognitoWarningAcknowledged;
            _acknowledgedChromeDegraded =
                state.Onboarding.ChromeDegradedAcknowledged;
            _onboardingAcknowledgementsDirty = false;
        }

        if (!sameOnboardingStep)
        {
            _selectedOnboardingStep = Math.Min(
                state.Onboarding.CompletedStep + 1,
                5);
        }

        SetConnection(new(
            DesktopConnectionState.Available,
            "保护服务已连接",
            "本机设置与保护状态已同步。"), notify: false);
        if (!_selfReportDirty || !sameNight)
        {
            bool? phoneOutOfReach = state.SelfReport?.PhoneOutOfReach;
            bool? wakeWithinWindow = state.SelfReport?.WakeWithinWindow;
            if (_phoneOutOfReach != phoneOutOfReach)
            {
                _phoneOutOfReach = phoneOutOfReach;
                OnPropertyChanged(nameof(PhoneOutOfReach));
            }

            if (_wakeWithinWindow != wakeWithinWindow)
            {
                _wakeWithinWindow = wakeWithinWindow;
                OnPropertyChanged(nameof(WakeWithinWindow));
            }

            _selfReportDirty = false;
        }

        if (reloadRules && !_rulesDirty)
        {
            IReadOnlyList<DesktopAppRuleDto> apps = state.Rules.PendingAppRules
                ?? state.Rules.ActiveAppRules;
            IReadOnlyList<DesktopSiteRuleDto> sites = state.Rules.PendingSiteRules
                ?? state.Rules.ActiveSiteRules;
            ConfiguredApps.Clear();
            foreach (DesktopAppRuleDto app in apps)
            {
                ConfiguredApps.Add(new(
                    app.Id,
                    app.RootExecutablePath!,
                    app.HelperExecutablePaths,
                    app.Category!.Value,
                    app.SessionMinutes,
                    OnConfiguredAppRuleChanged));
            }

            HashSet<string> configuredPaths = ConfiguredApps
                .Select(app => app.RootExecutablePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (DiscoveredGameChoiceViewModel game in DiscoveredGames)
            {
                game.SetConfigured(configuredPaths.Contains(game.ExecutablePath));
            }

            HashSet<string> selected = sites.Select(site => site.Domain)
                .ToHashSet(StringComparer.Ordinal);
            foreach (DesktopSiteSelectionViewModel site in SiteSelections)
            {
                site.IsSelected = selected.Contains(site.Domain);
            }

            _rulesSaved = apps.Count > 0 || sites.Count > 0;
            _rulesDirty = false;
        }

        NotifyAll();
    }

    private void SetConnection(
        DesktopConnectionPresentation connection,
        bool notify = true)
    {
        if (_connection == connection)
        {
            return;
        }

        _connection = connection;
        if (notify)
        {
            NotifyAll();
        }
    }

    private async ValueTask RefreshLegacyMigrationCoreAsync(
        CancellationToken cancellationToken)
    {
        if (_legacyMigrationCoordinator is null)
        {
            return;
        }

        try
        {
            DesktopLegacyMigrationSnapshot snapshot = await _legacyMigrationCoordinator
                .RefreshAsync(cancellationToken);
            ApplyLegacyMigrationSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            ApplyLegacyMigrationSnapshot(
                DesktopLegacyMigrationSnapshot.Unavailable("migration-degraded"));
        }
    }

    private void ApplyLegacyMigrationSnapshot(
        DesktopLegacyMigrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        HashSet<LegacyShutdownTaskCandidate> selected = LegacyShutdownTasks
            .Where(item => item.IsSelected)
            .Select(item => item.Candidate)
            .ToHashSet();
        _legacyMigrationSnapshot = snapshot;
        LegacyShutdownTasks.Clear();
        foreach (LegacyShutdownTaskCandidate candidate in snapshot.Candidates)
        {
            LegacyShutdownTasks.Add(new(
                candidate,
                selected.Contains(candidate),
                RaiseCommandStateChanged));
        }

        OnPropertyChanged(nameof(LegacyMigrationStatusText));
        RaiseCommandStateChanged();
    }

    private static DesktopNoticePresentation NoticeFor(DesktopNightNoticeKind kind) => kind switch
    {
        DesktopNightNoticeKind.IfThenPlan => new(
            "今晚的如果—那么计划",
            "如果还想再开一个新娱乐，就先保存当前进度并开始善后。"),
        DesktopNightNoticeKind.LastStart => new(
            "游戏截止时间提醒",
            "正在进行的一局可以安全收尾；是否还能开新局，请看设置页中每个游戏自己的截止时间。"),
        DesktopNightNoticeKind.Grace10 => new(
            "还有 10 分钟锁屏",
            "保存进度、关掉自动连播，慢慢把今晚停在这里。"),
        DesktopNightNoticeKind.Grace2 => new(
            "还有 2 分钟锁屏",
            "完成最后的安全保存；锁屏不会主动断网或改变电源计划。"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private void NotifyAll()
    {
        RebuildOnboardingSteps();
        string[] names =
        [
            nameof(Connection),
            nameof(RuleSettings),
            nameof(IsLoading),
            nameof(IsAvailable),
            nameof(IsUnavailable),
            nameof(IsOnboardingComplete),
            nameof(HasPendingProgression),
            nameof(CanEditIPhoneChecklist),
            nameof(IPhoneChecklistTargetText),
            nameof(CompletedOnboardingStep),
            nameof(SelectedOnboardingStep),
            nameof(OnboardingSteps),
            nameof(SettingsTabTitle),
            nameof(ProgressStepText),
            nameof(CurrentScheduleText),
            nameof(ScheduleLadder),
            nameof(ProgressionInvitationText),
            nameof(OnboardingTitle),
            nameof(OnboardingBody),
            nameof(ChromeProtectionText),
            nameof(ChromeExtensionOptionsStatusText),
            nameof(LegacyMigrationStatusText),
            nameof(AcknowledgedIncognitoWarning),
            nameof(AcknowledgedChromeDegraded),
            nameof(CanAcknowledgeIncognitoWarning),
            nameof(IsChromeProtectionDegraded),
            nameof(OnboardingMissingRequirement),
            nameof(WeeklySummaryText),
            nameof(WeeklyLockText),
            nameof(WeeklyOverrideText),
            nameof(GameDiscoveryStatusText),
            nameof(IsGameDiscoveryInFlight),
            nameof(HasDiscoveredGames),
            nameof(CanScanInstalledGames),
            nameof(CanAddSelectedDiscoveredGames),
            nameof(CanEditRules),
            nameof(SelectedAppRuleSessionMinutes),
            nameof(CanEditSelectedGameSessionMinutes),
            nameof(CanEditSelectedAppRule),
            nameof(CanSaveRules),
            nameof(CanClearHistory),
            nameof(CanCompleteOnboardingStep),
            nameof(CanConfirmProgression),
            nameof(CanDisableSelectedLegacyTasks),
            nameof(CanRestoreLegacyTasks),
        ];
        foreach (string name in names)
        {
            OnPropertyChanged(name);
        }

        RaiseCommandStateChanged();
    }

    private void NotifyOnboardingPresentation()
    {
        RebuildOnboardingSteps();
        OnPropertyChanged(nameof(SelectedOnboardingStep));
        OnPropertyChanged(nameof(OnboardingSteps));
        OnPropertyChanged(nameof(OnboardingTitle));
        OnPropertyChanged(nameof(OnboardingBody));
        OnPropertyChanged(nameof(OnboardingMissingRequirement));
        RaiseCommandStateChanged();
    }

    private void RebuildOnboardingSteps()
    {
        string[] titles =
        [
            "作息台阶与旧关机任务",
            "游戏、语音程序与娱乐网站",
            "Chrome 网页保护",
            "iPhone 同步清单",
            "保护边界与最终确认",
        ];
        int firstIncomplete = FirstIncompleteOnboardingStep;
        _onboardingSteps = titles.Select((title, index) =>
        {
            int number = index + 1;
            DesktopOnboardingStepState state = number == SelectedOnboardingStep
                ? DesktopOnboardingStepState.Current
                : number <= CompletedOnboardingStep
                    ? DesktopOnboardingStepState.Completed
                    : DesktopOnboardingStepState.Upcoming;
            return new DesktopOnboardingStepPresentation(
                number,
                title,
                state,
                number <= firstIncomplete);
        }).ToArray();
    }

    private void RaiseCommandStateChanged()
    {
        _refreshCommand?.RaiseCanExecuteChanged();
        _saveRulesCommand?.RaiseCanExecuteChanged();
        _scanInstalledGamesCommand?.RaiseCanExecuteChanged();
        _addSelectedDiscoveredGamesCommand?.RaiseCanExecuteChanged();
        _previousOnboardingCommand?.RaiseCanExecuteChanged();
        _nextOnboardingCommand?.RaiseCanExecuteChanged();
        _saveSelfReportCommand?.RaiseCanExecuteChanged();
        _confirmProgressionCommand?.RaiseCanExecuteChanged();
        _disableSelectedLegacyTasksCommand?.RaiseCanExecuteChanged();
        _restoreLegacyTasksCommand?.RaiseCanExecuteChanged();
        _openChromeExtensionOptionsCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanSaveRules));
        OnPropertyChanged(nameof(CanEditRules));
        OnPropertyChanged(nameof(CanEditSelectedGameSessionMinutes));
        OnPropertyChanged(nameof(CanEditSelectedAppRule));
        OnPropertyChanged(nameof(CanClearHistory));
        OnPropertyChanged(nameof(CanEditIPhoneChecklist));
        OnPropertyChanged(nameof(CanScanInstalledGames));
        OnPropertyChanged(nameof(CanAddSelectedDiscoveredGames));
        OnPropertyChanged(nameof(CanCompleteOnboardingStep));
        OnPropertyChanged(nameof(OnboardingMissingRequirement));
        OnPropertyChanged(nameof(CanConfirmProgression));
        OnPropertyChanged(nameof(CanDisableSelectedLegacyTasks));
        OnPropertyChanged(nameof(CanRestoreLegacyTasks));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new(propertyName));

    private string StepTimes(int step)
    {
        ScheduleLadderStepPresentation schedule = ScheduleLadder.Steps.Single(
            candidate => candidate.Number == step);
        return $"工作日 {schedule.LastStartText} 最后开局 · {schedule.LockText} 锁屏 · {schedule.LightsOutText} 关灯 · {schedule.WakeText} 起床；周五、周六整体顺延 1 小时";
    }

    private void SynchronizeIPhoneChecklistTarget(DesktopUserStateDto currentState)
    {
        if (currentState.Onboarding.CompletedStep < 5)
        {
            _iPhoneChecklistTarget = null;
            return;
        }

        (int Step, DateOnly UnlockedByNightDate)? nextTarget =
            currentState.Progress.PendingStep is { } pending
            && currentState.Progress.PendingStepConfirmedAtUtc is null
            && currentState.Progress.PendingStepUnlockedByNightDate is { } unlockedBy
                ? (pending, unlockedBy)
                : null;
        if (_iPhoneChecklistTarget != nextTarget)
        {
            IPhone.Reset();
        }

        _iPhoneChecklistTarget = nextTarget;
    }
}
