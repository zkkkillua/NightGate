using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WpfBrush = System.Windows.Media.Brush;
using Forms = System.Windows.Forms;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace NightGate.Desktop;

public partial class CommitmentCountdownWindow : Window
{
    private const int DefaultMarginPixels = 24;
    private const int WindowHitTest = 0x0084;
    private const int WindowMouseActivate = 0x0021;
    private static readonly nint HitTestTransparent = new(-1);
    private static readonly nint MouseActivateNoActivate = new(3);
    private bool _passiveHookInstalled;
    private HwndSource? _source;
    private readonly Stopwatch _movementClock = Stopwatch.StartNew();
    private readonly CommitmentCountdownMovementModel _movement = new();
    private readonly Stopwatch _radianceClock = new();
    private readonly DispatcherTimer _radianceTimer;
    private readonly Func<(bool ReduceMotion, bool HighContrast)> _readRadiancePreferences;
    private readonly int _radianceSeed = Random.Shared.Next();
    private bool _radianceTickAttached;
    private bool _radiancePreferencesAttached;
    private bool _reduceRadianceMotion;
    private bool _highContrast;
    private bool _closed;
    private bool _radianceFaulted;
    private CommitmentCountdownPresentation? _presentation;
    private CountdownRadianceLayout _radianceLayout =
        CountdownRadianceModel.LayoutFor(CommitmentCountdownKind.GraceToLock);

    public CommitmentCountdownWindow()
        : this(() => (!SystemParameters.ClientAreaAnimation, SystemParameters.HighContrast))
    {
    }

    internal CommitmentCountdownWindow(
        Func<(bool ReduceMotion, bool HighContrast)> readRadiancePreferences)
    {
        ArgumentNullException.ThrowIfNull(readRadiancePreferences);
        _readRadiancePreferences = readRadiancePreferences;
        InitializeComponent();
        _radianceTimer = new(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        IsVisibleChanged += OnCountdownVisibilityChanged;
        RadianceLayer.RenderingFailed += OnRadianceRenderingFailed;
        Opacity = 0;
    }

    internal bool IsRadianceAnimating => _radianceTimer.IsEnabled;

    internal bool HasRadiancePreferenceSubscription => _radiancePreferencesAttached;

    internal int RadianceSeedForTesting => _radianceSeed;

    internal CountdownRadianceFrame? RadianceFrameForTesting => RadianceLayer.CurrentFrame;

    internal void UpdatePresentation(
        CommitmentCountdownPresentation presentation,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _presentation = presentation;
        _radianceLayout = CountdownRadianceModel.LayoutFor(presentation.Kind);
        bool game = _radianceLayout.IsGame;
        Width = _radianceLayout.Width;
        Height = _radianceLayout.Height;
        RootCard.Margin = new Thickness(_radianceLayout.Halo);
        RootCard.Padding = game ? new Thickness(24, 20, 24, 20) : new Thickness(18, 12, 18, 12);
        RootCard.BorderThickness = new Thickness(game ? 2 : 1);
        TitleText.FontSize = game ? 24 : 17;
        CurrentTimeText.FontSize = game ? 23 : 14;
        DeadlineText.FontSize = game ? 16 : 12;
        RemainingText.FontSize = game ? 52 : 31;
        DetailText.FontSize = game ? 16 : 12;
        CountdownLabelText.Text = game ? "距离锁屏" : "剩余时间";
        TitleText.Text = TitleFor(presentation.Kind);
        CurrentTimeText.Text = $"现在 {(now ?? DateTimeOffset.Now).ToLocalTime():HH:mm:ss}";
        DeadlineText.Text = DeadlineFor(
            presentation.Kind,
            presentation.ServiceDeadline);
        RemainingText.Text = FormatRemaining(presentation.Remaining);
        DetailText.Text = DetailFor(presentation.Kind);
        RefreshRadiancePreferences();
        AutomationProperties.SetName(
            this,
            $"{TitleText.Text}，{CurrentTimeText.Text}，{DeadlineText.Text}，剩余 {RemainingText.Text}");
    }

    internal bool TryConfigureAndPlace(
        IMonitorLayoutProvider monitorLayoutProvider,
        TimeSpan? monotonicNow = null,
        string? preferredMonitorId = null)
    {
        ArgumentNullException.ThrowIfNull(monitorLayoutProvider);
        UpdateLayout();
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return false;
        }

        HwndSource? source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget is null)
        {
            return false;
        }

        if (!_passiveHookInstalled)
        {
            if (!Win32MonitorDpiNative.TryConfigurePassiveWindow(handle))
            {
                return false;
            }

            source.AddHook(PassiveWindowProcedure);
            _source = source;
            _passiveHookInstalled = true;
        }

        Matrix toPixels = source.CompositionTarget.TransformToDevice;
        int width = Math.Max(1, (int)Math.Ceiling(ActualWidth * toPixels.M11));
        int height = Math.Max(1, (int)Math.Ceiling(ActualHeight * toPixels.M22));
        CommitmentCountdownPlacement placement =
            _movement.Update(
                monitorLayoutProvider.ReadMonitors(),
                width,
                height,
                DefaultMarginPixels,
                monotonicNow ?? _movementClock.Elapsed,
                preferredMonitorId ?? ForegroundMonitorId());
        if (!Win32MonitorDpiNative.TryPlaceTopmostWindow(
                handle,
                placement.PixelBounds))
        {
            return false;
        }

        Opacity = 1;
        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        IsVisibleChanged -= OnCountdownVisibilityChanged;
        RadianceLayer.RenderingFailed -= OnRadianceRenderingFailed;
        StopRadianceActivity();
        _radianceClock.Reset();
        if (_passiveHookInstalled && _source is { IsDisposed: false })
        {
            _source?.RemoveHook(PassiveWindowProcedure);
        }

        _source = null;
        _passiveHookInstalled = false;
        _movement.Reset();
        _movementClock.Stop();
        base.OnClosed(e);
    }

    internal void SampleRadianceFrameForTesting(
        TimeSpan elapsed,
        bool reduceMotion = false,
        bool highContrast = false)
    {
        try
        {
            ApplyRadianceFrame(elapsed, reduceMotion || highContrast, highContrast);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            DisableRadiance();
        }
    }

    internal void RefreshRadiancePreferences()
    {
        if (_closed)
        {
            return;
        }

        try
        {
            RefreshRadiancePreferencesCore();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            DisableRadiance();
            try
            {
                // A decorative/preference fault must not freeze the countdown's
                // existing urgent colors. Use the last readable system preference.
                ApplyCardColors(_highContrast);
            }
            catch (Exception colorException) when (IsRecoverable(colorException))
            {
                // Missing theme resources cannot stop the live numeric countdown.
            }
        }
    }

    private void RefreshRadiancePreferencesCore()
    {
        (bool reduceMotion, bool highContrast) = _readRadiancePreferences();
        _reduceRadianceMotion = reduceMotion || highContrast;
        _highContrast = highContrast;
        ApplyCardColors(highContrast);
        if (_radianceFaulted)
        {
            return;
        }
        ApplyRadianceFrame(_radianceClock.Elapsed, _reduceRadianceMotion, _highContrast);
        if (IsVisible && !_reduceRadianceMotion)
        {
            if (!_radianceTickAttached)
            {
                _radianceTimer.Tick += OnRadianceTick;
                _radianceTickAttached = true;
            }

            _radianceClock.Start();
            _radianceTimer.Start();
        }
        else
        {
            StopRadianceTimer();
        }
    }

    private void OnCountdownVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_closed || !IsVisible || _radianceFaulted)
        {
            StopRadianceActivity();
            return;
        }

        if (!_radiancePreferencesAttached)
        {
            SystemParameters.StaticPropertyChanged += OnSystemPreferencesChanged;
            _radiancePreferencesAttached = true;
        }

        RefreshRadiancePreferences();
    }

    private void OnSystemPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SystemParameters.ClientAreaAnimation)
            or nameof(SystemParameters.HighContrast)))
        {
            return;
        }

        try
        {
            if (Dispatcher.CheckAccess())
            {
                RefreshRadiancePreferences();
            }
            else if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshRadiancePreferences);
            }
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (Dispatcher.CheckAccess())
            {
                DisableRadiance();
            }
        }
    }

    private void OnRadianceTick(object? sender, EventArgs e)
    {
        if (_closed || !IsVisible || _radianceFaulted)
        {
            StopRadianceActivity();
            return;
        }

        try
        {
            ApplyRadianceFrame(_radianceClock.Elapsed, _reduceRadianceMotion, _highContrast);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            DisableRadiance();
        }
    }

    private void OnRadianceRenderingFailed(object? sender, EventArgs e) => DisableRadiance();

    private void DisableRadiance()
    {
        _radianceFaulted = true;
        StopRadianceActivity();
        RadianceLayer.Clear();
    }

    private void ApplyRadianceFrame(TimeSpan elapsed, bool reduceMotion, bool highContrast)
    {
        WpfBrush accent = highContrast ? SystemColors.WindowTextBrush : (WpfBrush)FindResource(
            _presentation?.IsUrgent == true ? "DangerBrush" : "AccentBrush");
        Color color = accent is SolidColorBrush solid ? solid.Color : Colors.SeaGreen;
        RadianceLayer.Apply(_radianceLayout,
            CountdownRadianceModel.Sample(_radianceLayout, elapsed, reduceMotion, _radianceSeed),
            color, highContrast);
    }

    private void ApplyCardColors(bool highContrast)
    {
        bool urgent = _presentation?.IsUrgent == true;
        WpfBrush accent = highContrast ? SystemColors.WindowTextBrush : (WpfBrush)FindResource(urgent ? "DangerBrush" : "AccentBrush");
        WpfBrush primary = highContrast ? SystemColors.WindowTextBrush : (WpfBrush)FindResource("PrimaryTextBrush");
        WpfBrush secondary = highContrast ? SystemColors.WindowTextBrush : (WpfBrush)FindResource("SecondaryTextBrush");
        RootCard.Background = highContrast ? SystemColors.WindowBrush : (WpfBrush)FindResource(urgent ? "WarningCardBrush" : "CardBackgroundBrush");
        RootCard.BorderBrush = accent;
        RemainingText.Foreground = accent;
        TitleText.Foreground = primary;
        CurrentTimeText.Foreground = primary;
        DeadlineText.Foreground = secondary;
        DetailText.Foreground = secondary;
        CountdownLabelText.Foreground = secondary;
    }

    private void StopRadianceTimer()
    {
        _radianceTimer.Stop();
        _radianceClock.Stop();
        if (_radianceTickAttached)
        {
            _radianceTimer.Tick -= OnRadianceTick;
            _radianceTickAttached = false;
        }
    }

    private void StopRadianceActivity()
    {
        StopRadianceTimer();
        if (_radiancePreferencesAttached)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemPreferencesChanged;
            _radiancePreferencesAttached = false;
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static string? ForegroundMonitorId()
    {
        nint foreground = Win32MonitorDpiNative.GetForegroundWindowHandle();
        return foreground == nint.Zero ? null : Forms.Screen.FromHandle(foreground).DeviceName;
    }

    private static nint PassiveWindowProcedure(
        nint window,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (message == WindowHitTest)
        {
            handled = true;
            return HitTestTransparent;
        }

        if (message == WindowMouseActivate)
        {
            handled = true;
            return MouseActivateNoActivate;
        }

        return nint.Zero;
    }

    private static string TitleFor(CommitmentCountdownKind kind) => kind switch
    {
        CommitmentCountdownKind.GraceToLock => "今晚收尾",
        CommitmentCountdownKind.GameGraceToLock => "游戏还在运行，该收尾了",
        CommitmentCountdownKind.EntertainmentCoolingOff => "娱乐再用冷静期",
        CommitmentCountdownKind.TeamRescue => "团队救场",
        CommitmentCountdownKind.Emergency => "紧急解锁",
        CommitmentCountdownKind.EntertainmentActive => "娱乐再用（不可续期）",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string DetailFor(CommitmentCountdownKind kind) => kind switch
    {
        CommitmentCountdownKind.GraceToLock => "到点自动锁屏；现在适合保存进度、结束这一局。",
        CommitmentCountdownKind.GameGraceToLock => "不再开新局，保存进度并和队友告别。到点自动锁屏。",
        CommitmentCountdownKind.EntertainmentCoolingOff =>
            "冷静结束后进入一次不可续期的 20 分钟娱乐窗口。",
        CommitmentCountdownKind.EntertainmentActive =>
            "本次不可续期；到时自动恢复收尾保护。",
        _ => "到时自动恢复收尾保护。",
    };

    private static string DeadlineFor(
        CommitmentCountdownKind kind,
        DateTimeOffset serviceDeadline)
    {
        string localTime = serviceDeadline.ToLocalTime().ToString("HH:mm");
        return kind switch
        {
            CommitmentCountdownKind.GraceToLock or CommitmentCountdownKind.GameGraceToLock => $"锁屏 {localTime}",
            CommitmentCountdownKind.EntertainmentCoolingOff =>
                $"冷静至 {localTime}",
            _ => $"结束 {localTime}",
        };
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        long totalSeconds = Math.Max(
            0,
            (long)Math.Ceiling(remaining.TotalSeconds));
        long hours = totalSeconds / 3600;
        long minutes = totalSeconds % 3600 / 60;
        long seconds = totalSeconds % 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}";
    }
}
