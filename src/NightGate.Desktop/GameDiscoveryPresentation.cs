using System.ComponentModel;

namespace NightGate.Desktop;

public sealed class DiscoveredGameChoiceViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<int> SessionMinuteOptionsValue =
        [15, 25, 35, 45, 60, 90];
    private readonly Action _selectionChanged;
    private bool _isSelected;
    private bool _isAlreadyConfigured;
    private int _sessionMinutes = 35;

    internal DiscoveredGameChoiceViewModel(
        DiscoveredGame game,
        bool isAlreadyConfigured,
        Action selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(selectionChanged);
        Game = game;
        _isAlreadyConfigured = isAlreadyConfigured;
        _selectionChanged = selectionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal DiscoveredGame Game { get; }

    public string DisplayName => Game.DisplayName;

    public string ExecutablePath => Game.ExecutablePath;

    public string SourceText => Game.Source switch
    {
        GameDiscoverySource.Epic => "Epic",
        GameDiscoverySource.XboxGamingServices => "Xbox",
        GameDiscoverySource.Steam => "Steam",
        GameDiscoverySource.UninstallRegistry => "已安装程序",
        GameDiscoverySource.FixedDirectory => "常见目录",
        _ => "本机",
    };

    public string ConfidenceText => Game.Confidence switch
    {
        GameDiscoveryConfidence.High => "启动文件已确认",
        GameDiscoveryConfidence.Medium => "建议确认路径",
        _ => "请确认路径",
    };

    public bool IsAlreadyConfigured => _isAlreadyConfigured;

    public string SelectionStatusText => IsAlreadyConfigured ? "已添加" : ConfidenceText;

    public bool CanSelect => !IsAlreadyConfigured;

    public IReadOnlyList<int> SessionMinuteOptions => SessionMinuteOptionsValue;

    public int SessionMinutes
    {
        get => _sessionMinutes;
        set
        {
            if (!SessionMinuteOptionsValue.Contains(value) || _sessionMinutes == value)
            {
                return;
            }

            _sessionMinutes = value;
            OnPropertyChanged(nameof(SessionMinutes));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            bool next = CanSelect && value;
            if (_isSelected == next)
            {
                return;
            }

            _isSelected = next;
            OnPropertyChanged(nameof(IsSelected));
            _selectionChanged();
        }
    }

    internal void SetConfigured(bool configured)
    {
        if (_isAlreadyConfigured == configured)
        {
            return;
        }

        _isAlreadyConfigured = configured;
        if (configured)
        {
            _isSelected = false;
        }
        OnPropertyChanged(nameof(IsAlreadyConfigured));
        OnPropertyChanged(nameof(SelectionStatusText));
        OnPropertyChanged(nameof(CanSelect));
        OnPropertyChanged(nameof(IsSelected));
        _selectionChanged();
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}

internal static class GameDiscoveryPresentation
{
    internal static string Summary(GameDiscoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Games.IsEmpty)
        {
            return "扫描完成，暂未找到可确认的游戏；你仍可手动选择 exe。";
        }

        string sources = string.Join(
            " · ",
            snapshot.Games
                .GroupBy(game => SourceName(game.Source))
                .OrderBy(group => group.Key, StringComparer.CurrentCulture)
                .Select(group => $"{group.Key} {group.Count()}"));
        int degraded = snapshot.Sources.Count(source =>
            source.State == GameDiscoverySourceState.Degraded);
        string suffix = degraded > 0
            ? "。个别目录不可读，不影响已找到的结果。"
            : "。";
        return $"找到 {snapshot.Games.Length} 个游戏：{sources}{suffix}";
    }

    private static string SourceName(GameDiscoverySource source) => source switch
    {
        GameDiscoverySource.Epic => "Epic",
        GameDiscoverySource.XboxGamingServices => "Xbox",
        GameDiscoverySource.Steam => "Steam",
        GameDiscoverySource.UninstallRegistry => "其他",
        GameDiscoverySource.FixedDirectory => "常见目录",
        _ => "本机",
    };
}
