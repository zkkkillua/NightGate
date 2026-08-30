using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NightGate.Desktop;
using NightGate.Desktop.Views;

namespace NightGate.Desktop.Tests;

public sealed class DesktopUiContractTests
{
    [Fact]
    public void CommitmentCountdown_IsPassiveAccessibleAndWiredIntoProduction()
    {
        string desktop = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "NightGate.Desktop");
        string xaml = File.ReadAllText(Path.Combine(
            desktop,
            "CommitmentCountdownWindow.xaml"));
        string window = File.ReadAllText(Path.Combine(
            desktop,
            "CommitmentCountdownWindow.xaml.cs"));
        string native = File.ReadAllText(Path.Combine(
            desktop,
            "Win32MonitorDpiNative.cs"));
        string composition = File.ReadAllText(Path.Combine(
            desktop,
            "DesktopProductionComposition.cs"));

        Assert.Contains("ShowInTaskbar=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowActivated=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Topmost=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Focusable=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AllowsTransparency=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=", xaml, StringComparison.Ordinal);
        Assert.Contains("TryConfigurePassiveWindow", window, StringComparison.Ordinal);
        Assert.Contains("HitTestTransparent", window, StringComparison.Ordinal);
        Assert.Contains("MouseActivateNoActivate", window, StringComparison.Ordinal);
        Assert.Contains("娱乐再用（不可续期）", window, StringComparison.Ordinal);
        Assert.Contains("冷静至", window, StringComparison.Ordinal);
        Assert.Contains("锁屏 {localTime}", window, StringComparison.Ordinal);
        Assert.Contains("结束 {localTime}", window, StringComparison.Ordinal);
        Assert.Contains("ExtendedNoActivate", native, StringComparison.Ordinal);
        Assert.Contains("ExtendedTransparent", native, StringComparison.Ordinal);
        Assert.Contains("ExtendedLayered", native, StringComparison.Ordinal);
        Assert.Contains("ExtendedToolWindow", native, StringComparison.Ordinal);
        Assert.Contains("WpfCommitmentCountdownPresenter", composition, StringComparison.Ordinal);
        Assert.Contains("TraceCommitmentCountdownDiagnostics", composition, StringComparison.Ordinal);
    }

    private static readonly Lazy<Dispatcher> UiDispatcher = new(CreateUiDispatcher);

    [Fact]
    public async Task GameCountdown_IsLargerAndShowsCurrentTimeAndLockDeadline()
    {
        await RunOnStaThreadAsync(() =>
        {
            EnsureApplicationResources();
            CommitmentCountdownWindow window = new();
            try
            {
                DateTimeOffset now = new(2026, 8, 30, 0, 15, 23, TimeSpan.FromHours(8));
                DateTimeOffset deadline = now.AddMinutes(20);
                window.UpdatePresentation(new(
                    CommitmentCountdownKind.EntertainmentActive,
                    TimeSpan.FromMinutes(20), deadline, false), now);
                double smallWidth = window.Width;
                double smallHeight = window.Height;
                double smallCountdownFont = ((TextBlock)window.FindName("RemainingText")).FontSize;

                window.UpdatePresentation(new(
                    CommitmentCountdownKind.GameGraceToLock,
                    TimeSpan.FromMinutes(20), deadline, false), now);
                FrameworkElement content = (FrameworkElement)window.Content;
                content.Measure(new Size(window.Width, window.Height));
                content.Arrange(new Rect(0, 0, window.Width, window.Height));
                content.UpdateLayout();

                Assert.Equal(688, window.Width);
                Assert.Equal(368, window.Height);
                Assert.Equal(560, ((Border)window.FindName("RootCard")).ActualWidth);
                Assert.Equal(240, ((Border)window.FindName("RootCard")).ActualHeight);
                Assert.True(window.Width > smallWidth);
                Assert.True(window.Height > smallHeight);
                Assert.True(((TextBlock)window.FindName("RemainingText")).FontSize > smallCountdownFont);
                Assert.Equal("游戏还在运行，该收尾了", ((TextBlock)window.FindName("TitleText")).Text);
                Assert.Equal($"现在 {now.ToLocalTime():HH:mm:ss}", ((TextBlock)window.FindName("CurrentTimeText")).Text);
                Assert.Equal($"锁屏 {deadline.ToLocalTime():HH:mm}", ((TextBlock)window.FindName("DeadlineText")).Text);
                Assert.Equal("00:20:00", ((TextBlock)window.FindName("RemainingText")).Text);
                foreach (string name in new[] { "TitleText", "CurrentTimeText", "DeadlineText", "RemainingText", "DetailText" })
                {
                    FrameworkElement element = (FrameworkElement)window.FindName(name);
                    Assert.True(element.ActualWidth > 0 && element.ActualHeight > 0, name);
                    Assert.True(element.DesiredSize.Width <= element.ActualWidth + 1, name);
                }

                Assert.False(window.IsVisible);
                Assert.False(window.ShowActivated);
                Assert.False(window.IsHitTestVisible);
                Assert.False(window.Focusable);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task ExistingCountdownsRemainCompactAndUpdateCurrentTimeWithoutChangingDeadline()
    {
        await RunOnStaThreadAsync(() =>
        {
            EnsureApplicationResources();
            CommitmentCountdownWindow window = new();
            try
            {
                DateTimeOffset now = new(2026, 8, 30, 0, 15, 23, TimeSpan.FromHours(8));
                foreach (CommitmentCountdownKind kind in Enum.GetValues<CommitmentCountdownKind>()
                    .Where(kind => kind != CommitmentCountdownKind.GameGraceToLock))
                {
                    CommitmentCountdownPresentation presentation = new(kind, TimeSpan.FromMinutes(10), now.AddMinutes(10), false);
                    window.UpdatePresentation(presentation, now);
                    string deadlineText = ((TextBlock)window.FindName("DeadlineText")).Text;
                    window.UpdatePresentation(presentation, now.AddSeconds(1));

                    Assert.Equal(476, window.Width);
                    Assert.Equal(244, window.Height);
                    Assert.Equal(new Thickness(48), ((Border)window.FindName("RootCard")).Margin);
                    Assert.Equal($"现在 {now.AddSeconds(1).ToLocalTime():HH:mm:ss}", ((TextBlock)window.FindName("CurrentTimeText")).Text);
                    Assert.Equal(deadlineText, ((TextBlock)window.FindName("DeadlineText")).Text);
                }

                Assert.False(window.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task CountdownPreviewsRenderOffscreenWithUnclippedText()
    {
        await RunOnStaThreadAsync(() =>
        {
            EnsureApplicationResources();
            DateTimeOffset now = new(2026, 8, 30, 0, 15, 23, TimeSpan.FromHours(8));
            foreach ((CommitmentCountdownKind kind, string filename) in new[]
                {
                    (CommitmentCountdownKind.GameGraceToLock, "countdown-game.png"),
                    (CommitmentCountdownKind.EntertainmentActive, "countdown-entertainment.png"),
                })
            {
                CommitmentCountdownWindow window = new();
                try
                {
                    window.UpdatePresentation(new(kind, TimeSpan.FromMinutes(19).Add(TimeSpan.FromSeconds(37)), now.AddMinutes(20), false), now);
                    double previewSeconds = double.TryParse(
                        Environment.GetEnvironmentVariable("NIGHTGATE_COUNTDOWN_PREVIEW_SECONDS"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double configuredSeconds) && double.IsFinite(configuredSeconds)
                        ? Math.Clamp(configuredSeconds, 0, 3600) : 1.4;
                    window.SampleRadianceFrameForTesting(TimeSpan.FromSeconds(previewSeconds));
                    FrameworkElement content = (FrameworkElement)window.Content;
                    content.Measure(new Size(window.Width, window.Height));
                    content.Arrange(new Rect(0, 0, window.Width, window.Height));
                    content.UpdateLayout();
                    foreach (string name in new[] { "TitleText", "CurrentTimeText", "DeadlineText", "CountdownLabelText", "RemainingText", "DetailText" })
                    {
                        TextBlock element = (TextBlock)window.FindName(name);
                        Rect bounds = element.TransformToAncestor(content).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                        Assert.True(element.DesiredSize.Width <= element.ActualWidth + 1, $"{kind}:{name} width");
                        Assert.True(element.DesiredSize.Height <= element.ActualHeight + element.Margin.Top + element.Margin.Bottom + 1, $"{kind}:{name} height");
                        Assert.True(bounds.Left >= 0 && bounds.Top >= 0
                            && bounds.Right <= window.Width && bounds.Bottom <= window.Height, $"{kind}:{name} inside card");
                    }

                    RenderTargetBitmap bitmap = new((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    Assert.Contains(pixels, value => value != 0);
                    Assert.False(window.IsVisible);

                    string? previewDirectory = Environment.GetEnvironmentVariable("NIGHTGATE_COUNTDOWN_PREVIEW_DIRECTORY");
                    if (!string.IsNullOrWhiteSpace(previewDirectory))
                    {
                        Directory.CreateDirectory(previewDirectory);
                        PngBitmapEncoder encoder = new();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using FileStream output = File.Create(Path.Combine(previewDirectory, filename));
                        encoder.Save(output);
                    }
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    [Fact]
    public async Task EveryCountdownRendersRadianceOutsideAllFourCardEdgesWithoutStartingHiddenTimers()
    {
        await RunOnStaThreadAsync(() =>
        {
            EnsureApplicationResources();
            DateTimeOffset now = new(2026, 8, 30, 0, 15, 23, TimeSpan.FromHours(8));
            foreach (CommitmentCountdownKind kind in Enum.GetValues<CommitmentCountdownKind>())
            {
                CommitmentCountdownWindow window = new(() => (false, false));
                try
                {
                    window.UpdatePresentation(new(kind, TimeSpan.FromMinutes(10), now.AddMinutes(10), false), now);
                    int seed = window.RadianceSeedForTesting;
                    window.SampleRadianceFrameForTesting(TimeSpan.FromSeconds(1.4));
                    CountdownRadianceFrame first = Assert.IsType<CountdownRadianceFrame>(window.RadianceFrameForTesting);
                    window.UpdatePresentation(new(kind, TimeSpan.FromMinutes(9), now.AddMinutes(10), false), now.AddMinutes(1));
                    window.SampleRadianceFrameForTesting(TimeSpan.FromSeconds(1.4));
                    CountdownRadianceFrame repeated = Assert.IsType<CountdownRadianceFrame>(window.RadianceFrameForTesting);
                    Assert.Equal(seed, window.RadianceSeedForTesting);
                    Assert.Equal(first.Waves, repeated.Waves);
                    Assert.Equal(first.Particles, repeated.Particles);
                    Assert.False(window.IsRadianceAnimating);
                    Assert.False(window.HasRadiancePreferenceSubscription);

                    FrameworkElement content = (FrameworkElement)window.Content;
                    content.Measure(new Size(window.Width, window.Height));
                    content.Arrange(new Rect(0, 0, window.Width, window.Height));
                    content.UpdateLayout();
                    RenderTargetBitmap bitmap = new((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    int halo = (int)((Border)window.FindName("RootCard")).Margin.Left;
                    Assert.True(HasOpaquePixels(pixels, bitmap.PixelWidth, 0, 0, halo, bitmap.PixelHeight), $"{kind}: left glow");
                    Assert.True(HasOpaquePixels(pixels, bitmap.PixelWidth, bitmap.PixelWidth - halo, 0, halo, bitmap.PixelHeight), $"{kind}: right glow");
                    Assert.True(HasOpaquePixels(pixels, bitmap.PixelWidth, 0, 0, bitmap.PixelWidth, halo), $"{kind}: top glow");
                    Assert.True(HasOpaquePixels(pixels, bitmap.PixelWidth, 0, bitmap.PixelHeight - halo, bitmap.PixelWidth, halo), $"{kind}: bottom glow");
                }
                finally
                {
                    window.Close();
                }

                Assert.False(window.IsRadianceAnimating);
                Assert.False(window.HasRadiancePreferenceSubscription);
            }
        });
    }

    [Fact]
    public async Task ReducedMotionAndHighContrastUseStaticParticleFreeFrames()
    {
        await RunOnStaThreadAsync(() =>
        {
            EnsureApplicationResources();
            bool reducedMotion = true;
            bool highContrast = false;
            CommitmentCountdownWindow window = new(() => (reducedMotion, highContrast));
            try
            {
                DateTimeOffset now = DateTimeOffset.Now;
                window.UpdatePresentation(new(CommitmentCountdownKind.GameGraceToLock,
                    TimeSpan.FromMinutes(10), now.AddMinutes(10), false), now);
                Assert.Empty(Assert.IsType<CountdownRadianceFrame>(window.RadianceFrameForTesting).Particles);
                Assert.False(window.IsRadianceAnimating);
                window.SampleRadianceFrameForTesting(TimeSpan.FromSeconds(1), reduceMotion: true);
                CountdownRadianceFrame first = Assert.IsType<CountdownRadianceFrame>(window.RadianceFrameForTesting);
                window.SampleRadianceFrameForTesting(TimeSpan.FromSeconds(100), reduceMotion: true);
                CountdownRadianceFrame later = Assert.IsType<CountdownRadianceFrame>(window.RadianceFrameForTesting);
                Assert.Equal(first.Waves, later.Waves);
                Assert.Equal(first.GlowOpacity, later.GlowOpacity);

                reducedMotion = false;
                highContrast = true;
                window.RefreshRadiancePreferences();
                Assert.Empty(Assert.IsType<CountdownRadianceFrame>(window.RadianceFrameForTesting).Particles);
                Assert.Same(SystemColors.WindowBrush, ((Border)window.FindName("RootCard")).Background);
                Assert.Same(SystemColors.WindowTextBrush, ((TextBlock)window.FindName("RemainingText")).Foreground);
                Assert.False(window.IsRadianceAnimating);
                Assert.False(window.HasRadiancePreferenceSubscription);
            }
            finally
            {
                window.Close();
            }

            window.RefreshRadiancePreferences();
            Assert.False(window.IsRadianceAnimating);
            Assert.False(window.HasRadiancePreferenceSubscription);
        });
    }

    private static bool HasOpaquePixels(byte[] pixels, int stridePixels, int x, int y, int width, int height)
    {
        for (int row = y; row < y + height; row++)
        {
            for (int column = x; column < x + width; column++)
            {
                if (pixels[(row * stridePixels + column) * 4 + 3] > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public async Task RadiancePreferenceFailureStopsOnlyDecorationAndKeepsCountdownUsable()
    {
        await RunOnStaThreadAsync(() =>
        {
            EnsureApplicationResources();
            CommitmentCountdownWindow window = new(() => throw new InvalidOperationException("preferences unavailable"));
            try
            {
                DateTimeOffset now = DateTimeOffset.Now;
                window.UpdatePresentation(new(CommitmentCountdownKind.GameGraceToLock,
                    TimeSpan.FromMinutes(10), now.AddMinutes(10), false), now);
                Assert.Equal("00:10:00", ((TextBlock)window.FindName("RemainingText")).Text);
                Assert.Null(window.RadianceFrameForTesting);
                Assert.False(window.IsRadianceAnimating);
                Assert.False(window.HasRadiancePreferenceSubscription);

                window.UpdatePresentation(new(CommitmentCountdownKind.GameGraceToLock,
                    TimeSpan.FromMinutes(9), now.AddMinutes(10), false), now.AddMinutes(1));
                Assert.Equal("00:09:00", ((TextBlock)window.FindName("RemainingText")).Text);
                Assert.Null(window.RadianceFrameForTesting);
                Assert.False(window.IsRadianceAnimating);

                window.UpdatePresentation(new(CommitmentCountdownKind.GameGraceToLock,
                    TimeSpan.FromMinutes(2), now.AddMinutes(10), true), now.AddMinutes(8));
                Assert.Equal("00:02:00", ((TextBlock)window.FindName("RemainingText")).Text);
                Assert.Same(window.FindResource("DangerBrush"),
                    ((TextBlock)window.FindName("RemainingText")).Foreground);
                Assert.Same(window.FindResource("WarningCardBrush"),
                    ((Border)window.FindName("RootCard")).Background);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task RadianceFailureDoesNotFreezeLaterHighContrastCardColors()
    {
        await RunOnStaThreadAsync(() =>
        {
            EnsureApplicationResources();
            bool fail = true;
            CommitmentCountdownWindow window = new(() => fail
                ? throw new InvalidOperationException("preferences unavailable")
                : (true, true));
            try
            {
                DateTimeOffset now = DateTimeOffset.Now;
                CommitmentCountdownPresentation presentation = new(
                    CommitmentCountdownKind.TeamRescue, TimeSpan.FromMinutes(5), now.AddMinutes(5), false);
                window.UpdatePresentation(presentation, now);
                fail = false;
                window.UpdatePresentation(presentation, now.AddSeconds(1));
                Assert.Same(System.Windows.SystemColors.WindowTextBrush,
                    ((TextBlock)window.FindName("RemainingText")).Foreground);
                Assert.Same(System.Windows.SystemColors.WindowBrush,
                    ((Border)window.FindName("RootCard")).Background);
                Assert.Null(window.RadianceFrameForTesting);
                Assert.False(window.IsRadianceAnimating);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DesktopAssembly_ExposesDashboardOverlayAndTrayContracts()
    {
        Assembly desktop = typeof(DesktopPolicyResult).Assembly;
        string[] requiredTypes =
        [
            "NightGate.Desktop.DashboardPresentation",
            "NightGate.Desktop.DashboardViewModel",
            "NightGate.Desktop.IDesktopOverrideGateway",
            "NightGate.Desktop.DesktopClientOverrideGateway",
            "NightGate.Desktop.RestrictedOverlayWindow",
            "NightGate.Desktop.WpfRestrictedOverlayPresenter",
            "NightGate.Desktop.TrayApplicationShell",
            "NightGate.Desktop.TrayExitPrompt",
        ];

        foreach (string requiredType in requiredTypes)
        {
            Assert.True(
                desktop.GetType(requiredType) is not null,
                $"Missing required desktop type: {requiredType}");
        }
    }

    [Fact]
    public void DashboardViewModel_ExposesPolicyAndTypedOverrideSurface()
    {
        Type type = typeof(DesktopPolicyResult).Assembly.GetType(
            "NightGate.Desktop.DashboardViewModel",
            throwOnError: true)!;

        Assert.NotNull(type.GetConstructor([typeof(IDesktopOverrideGateway)]));
        Assert.NotNull(type.GetMethod("ApplyPolicy"));
        Assert.NotNull(type.GetMethod("RequestTeamRescueAsync"));
        Assert.NotNull(type.GetMethod("RequestEmergencyAsync"));
        Assert.NotNull(type.GetMethod("RequestEntertainmentAsync"));
        Assert.NotNull(type.GetProperty("Presentation"));
        Assert.NotNull(type.GetProperty("TeamRescueCommand"));
        Assert.NotNull(type.GetProperty("EmergencyHealthCommand"));
        Assert.NotNull(type.GetProperty("EmergencySafetyCommand"));
        Assert.NotNull(type.GetProperty("EmergencyUrgentWorkCommand"));
        Assert.NotNull(type.GetProperty("EntertainmentCommand"));
        Assert.Null(type.GetProperty("SelectedEmergencyReason"));
        Assert.Null(type.GetProperty("EmergencyCommand"));
    }

    [Fact]
    public void ChineseXaml_IsAccessibleAndFreeOfCommonMojibake()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string overlayPath = Path.Combine(desktop, "RestrictedOverlayWindow.xaml");

        Assert.True(File.Exists(overlayPath), "Restricted overlay XAML is missing.");
        string source = ReadDesktopXaml(desktop);

        Assert.Contains("收尾", source, StringComparison.Ordinal);
        Assert.Contains("紧急情况", source, StringComparison.Ordinal);
        Assert.Contains("健康：立即完整解锁 30 分钟", source, StringComparison.Ordinal);
        Assert.Contains("安全：立即完整解锁 30 分钟", source, StringComparison.Ordinal);
        Assert.Contains("紧急工作：立即完整解锁 30 分钟", source, StringComparison.Ordinal);
        Assert.DoesNotContain("其他紧急情况", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedEmergencyReason", source, StringComparison.Ordinal);
        Assert.Contains("娱乐再用", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name", source, StringComparison.Ordinal);
        Assert.Contains("TabIndex", source, StringComparison.Ordinal);
        string overlay = File.ReadAllText(overlayPath);
        Assert.Contains(
            "Text=\"{Binding Presentation.StatusText, Mode=OneWay}\"",
            overlay,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"例外请求结果\"",
            overlay,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.LiveSetting=\"Polite\"",
            overlay,
            StringComparison.Ordinal);
        foreach (string mojibake in new[] { "�", "鏀", "鍒", "绔", "闃", "鈥" })
        {
            Assert.DoesNotContain(mojibake, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MainWindow_ExposesRealOnboardingRulesIPhoneAndWeeklySurfaces()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string source = ReadDesktopXaml(desktop);
        string[] required =
        [
            "设置与向导",
            "自动发现本机游戏",
            "添加勾选的游戏",
            "ScanInstalledGamesCommand",
            "AddSelectedDiscoveredGamesCommand",
            "GameDiscoveryStatusText",
            "不联网，也不会启动游戏",
            "添加关联辅助程序",
            "Chrome 网页保护",
            "旧自动关机任务",
            "只停用，不删除",
            "DisableSelectedLegacyTasksCommand",
            "RestoreLegacyTasksCommand",
            "屏幕使用时间 → 停用时间",
            "停用期间阻止使用",
            "Safari 没有加入始终允许",
            "Apple 恢复路径",
            "每周回顾",
            "不显示羞辱性红叉",
            "SaveRulesCommand",
            "NextOnboardingCommand",
            "SaveSelfReportCommand",
        ];

        foreach (string token in required)
        {
            Assert.Contains(token, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RulesEditor_GivesEachDiscoveredGameItsOwnDurationAndOnePageScrollOwner()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string rules = File.ReadAllText(Path.Combine(desktop, "Views", "RulesEditorView.xaml"));

        Assert.NotNull(typeof(DiscoveredGameChoiceViewModel).GetProperty("SessionMinutes"));
        Assert.Contains(
            "SelectedItem=\"{Binding SessionMinutes, Mode=TwoWay}\"",
            rules,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<ScrollViewer", rules, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding DiscoveredGames}\"", rules, StringComparison.Ordinal);
    }

    [Fact]
    public void RulesEditor_ConfiguredGamesEditDurationInsideModernRows()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string rules = File.ReadAllText(Path.Combine(desktop, "Views", "RulesEditorView.xaml"));

        Assert.DoesNotContain("<DataGrid", rules, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ConfiguredApps}\"", rules, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource RuleRowListBoxStyle}\"", rules, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource InlineDurationComboBoxStyle}\"", rules, StringComparison.Ordinal);
        Assert.Contains(
            "SelectedItem=\"{Binding SessionMinutes, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"",
            rules,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScrollViewer.VerticalScrollBarVisibility=\"Auto\"",
            rules,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedAppRuleSessionMinutes", rules, StringComparison.Ordinal);
    }

    [Fact]
    public void RulesEditor_ConfiguredRowsShowHelpersWithAnIndividualRemoveAction()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string rules = File.ReadAllText(Path.Combine(desktop, "Views", "RulesEditorView.xaml"));
        string code = File.ReadAllText(Path.Combine(
            desktop,
            "Views",
            "RulesEditorView.xaml.cs"));

        Assert.Contains(
            "ItemsSource=\"{Binding HelperExecutablePaths}\"",
            rules,
            StringComparison.Ordinal);
        Assert.Contains("Content=\"移除\"", rules, StringComparison.Ordinal);
        Assert.Contains("Click=\"RemoveHelper_Click\"", rules, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.Name=\"移除这个关联辅助程序\"",
            rules,
            StringComparison.Ordinal);
        Assert.Contains("RemoveHelperFromAppRule", code, StringComparison.Ordinal);
    }

    [Fact]
    public void RulesEditor_SelectedRuleActionsAreDisabledUntilARowIsSelected()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string rules = File.ReadAllText(Path.Combine(desktop, "Views", "RulesEditorView.xaml"));

        Assert.NotNull(typeof(UserExperienceViewModel).GetProperty("CanEditSelectedAppRule"));
        Assert.Equal(
            2,
            Count(rules, "IsEnabled=\"{Binding CanEditSelectedAppRule}\""));
    }

    [Fact]
    public void Settings_ClearHistoryActionReflectsWhetherMutationsAreAvailable()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string settings = File.ReadAllText(Path.Combine(desktop, "Views", "SettingsView.xaml"));

        Assert.NotNull(typeof(UserExperienceViewModel).GetProperty("CanClearHistory"));
        Assert.Contains(
            "IsEnabled=\"{Binding CanClearHistory}\"",
            settings,
            StringComparison.Ordinal);
        Assert.Contains("全部本机历史", settings, StringComparison.Ordinal);
        Assert.Contains("保留今晚", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void DestructiveConfirmationsUseFluentDialogInsteadOfSystemMessageBoxes()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string tray = File.ReadAllText(Path.Combine(desktop, "TrayApplicationShell.cs"));
        string settings = File.ReadAllText(Path.Combine(desktop, "Views", "SettingsView.xaml.cs"));
        string dialog = File.ReadAllText(Path.Combine(desktop, "FluentConfirmationDialog.xaml"));

        Assert.DoesNotContain("System.Windows.MessageBox", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox.Show", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows.MessageBox", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox.Show", settings, StringComparison.Ordinal);
        string productionSource = string.Join(
            "\n",
            Directory.EnumerateFiles(desktop, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) is ".cs" or ".xaml")
                .Select(File.ReadAllText));
        Assert.DoesNotContain("MessageBox.Show", productionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows.MessageBox", productionSource,
            StringComparison.Ordinal);
        Assert.Contains("IConfirmationDialogService", tray, StringComparison.Ordinal);
        Assert.Contains("IConfirmationDialogService", settings, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CardStyle}\"", dialog, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource SecondaryButtonStyle}\"", dialog,
            StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DangerButtonStyle}\"", dialog,
            StringComparison.Ordinal);
        Assert.Contains("IsDefault=\"True\"", dialog, StringComparison.Ordinal);
        Assert.Contains("IsCancel=\"True\"", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void IPhoneChecklist_RequiresEntertainmentRestrictionsAndDisablesPreselection()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string settings = File.ReadAllText(Path.Combine(desktop, "Views", "SettingsView.xaml"));
        string onboarding = File.ReadAllText(Path.Combine(
            desktop,
            "Views",
            "OnboardingWizardView.xaml"));

        Assert.Contains("IPhone.EntertainmentCategoriesRestricted", settings, StringComparison.Ordinal);
        Assert.Contains("IPhone.EntertainmentCategoriesRestricted", onboarding, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanEditIPhoneChecklist}\"", settings,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding IPhoneChecklistTargetText}\"", settings,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesEditor_ConfiguredGameRowsForwardMouseWheelToPageScroller()
    {
        await RunOnStaThreadAsync(() =>
        {
            EnsureApplicationResources();
            DesktopAppRuleItemViewModel[] configuredApps = Enumerable
                .Range(1, 8)
                .Select(index => new DesktopAppRuleItemViewModel(
                    $"Game {index}",
                    $@"C:\Games\Game{index}\game.exe",
                    [],
                    DesktopAppRuleCategory.Game,
                    35))
                .ToArray();
            RulesEditorView editor = new()
            {
                DataContext = new RulesEditorTestContext
                {
                    CanEditRules = true,
                    ConfiguredApps = configuredApps,
                },
            };
            ScrollViewer pageScroller = new()
            {
                Width = 900,
                Height = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = editor,
            };
            Window host = new()
            {
                Width = 900,
                Height = 320,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = pageScroller,
            };

            try
            {
                host.Show();
                host.UpdateLayout();
                ListBox configuredList = FindVisualDescendant<ListBox>(
                    editor,
                    list => list.Items.Count == configuredApps.Length)
                    ?? throw new InvalidOperationException("Configured app list was not created.");
                configuredList.BringIntoView();
                host.UpdateLayout();
                ListBoxItem firstRow = (ListBoxItem?)configuredList
                    .ItemContainerGenerator.ContainerFromIndex(0)
                    ?? throw new InvalidOperationException("Configured app row was not created.");
                ScrollViewer listScroller = FindVisualDescendant<ScrollViewer>(configuredList)
                    ?? throw new InvalidOperationException("Configured app scroller was not created.");
                Assert.True(pageScroller.ScrollableHeight > 0);
                double pageBefore = pageScroller.VerticalOffset;
                double listBefore = listScroller.VerticalOffset;

                MouseWheelEventArgs previewWheel = new(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    -120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                };
                firstRow.RaiseEvent(previewWheel);
                if (!previewWheel.Handled)
                {
                    firstRow.RaiseEvent(new MouseWheelEventArgs(
                        Mouse.PrimaryDevice,
                        Environment.TickCount,
                        -120)
                    {
                        RoutedEvent = UIElement.MouseWheelEvent,
                    });
                }
                Assert.True(
                    previewWheel.Handled,
                    "The configured app list must intercept preview wheel input before its inner scroller.");
                host.UpdateLayout();

                Assert.True(
                    pageScroller.VerticalOffset > pageBefore
                        || listScroller.VerticalOffset > listBefore,
                    "Wheel input over a configured game row must scroll the virtualized list or containing page.");

                listScroller.ScrollToTop();
                pageScroller.ScrollToTop();
                host.UpdateLayout();
                ComboBox durationEditor = FindVisualDescendant<ComboBox>(firstRow)
                    ?? throw new InvalidOperationException("Duration editor was not created.");
                Assert.Equal(35, configuredApps[0].SessionMinutes);
                MouseWheelEventArgs boundaryWheel = new(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                };
                durationEditor.RaiseEvent(boundaryWheel);

                Assert.True(
                    boundaryWheel.Handled,
                    "Wheel input must stay consumed when the settings page is already at its boundary.");
                Assert.Equal(35, configuredApps[0].SessionMinutes);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public async Task RulesEditor_HighResolutionWheelWaitsForACompleteDetent()
    {
        await RunOnStaThreadAsync(() =>
        {
            EnsureApplicationResources();
            DesktopAppRuleItemViewModel[] configuredApps = Enumerable
                .Range(1, 40)
                .Select(index => new DesktopAppRuleItemViewModel(
                    $"Game {index}",
                    $@"C:\Games\Game{index}\game.exe",
                    [],
                    DesktopAppRuleCategory.Game,
                    35))
                .ToArray();
            RulesEditorView editor = new()
            {
                DataContext = new RulesEditorTestContext
                {
                    CanEditRules = true,
                    ConfiguredApps = configuredApps,
                },
            };
            ScrollViewer pageScroller = new()
            {
                Width = 900,
                Height = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = editor,
            };
            Window host = new()
            {
                Width = 900,
                Height = 320,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = pageScroller,
            };

            try
            {
                host.Show();
                host.UpdateLayout();
                ListBox configuredList = FindVisualDescendant<ListBox>(
                    editor,
                    list => list.Items.Count == configuredApps.Length)
                    ?? throw new InvalidOperationException("Configured app list was not created.");
                configuredList.BringIntoView();
                host.UpdateLayout();
                ListBoxItem firstRow = (ListBoxItem?)configuredList
                    .ItemContainerGenerator.ContainerFromIndex(0)
                    ?? throw new InvalidOperationException("Configured app row was not created.");
                ScrollViewer listScroller = FindVisualDescendant<ScrollViewer>(configuredList)
                    ?? throw new InvalidOperationException("Configured app scroller was not created.");
                double pageBefore = pageScroller.VerticalOffset;
                double listBefore = listScroller.VerticalOffset;

                firstRow.RaiseEvent(new MouseWheelEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    -119)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                });
                host.UpdateLayout();

                Assert.Equal(pageBefore, pageScroller.VerticalOffset);
                Assert.Equal(listBefore, listScroller.VerticalOffset);

                firstRow.RaiseEvent(new MouseWheelEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    -1)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                });
                host.UpdateLayout();

                Assert.True(
                    pageScroller.VerticalOffset > pageBefore
                        || listScroller.VerticalOffset > listBefore);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void RulesEditor_LargeGameListsRequireRecyclingVirtualization()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string rules = File.ReadAllText(Path.Combine(desktop, "Views", "RulesEditorView.xaml"));

        Assert.True(
            Count(rules, "VirtualizingPanel.IsVirtualizing=\"True\"") >= 2,
            "Both discovered and configured game lists must virtualize rows.");
        Assert.True(
            Count(rules, "VirtualizingPanel.VirtualizationMode=\"Recycling\"") >= 2,
            "Both game lists must recycle row containers.");
        Assert.True(
            Count(rules, "ScrollViewer.CanContentScroll=\"True\"") >= 2,
            "Virtualized game lists must use logical scrolling.");
    }

    [Fact]
    public async Task RulesEditor_LargeDiscoveredGameListRealizesOnlyVisibleRows()
    {
        await RunOnStaThreadAsync(() =>
        {
            EnsureApplicationResources();
            object[] discoveredGames = Enumerable
                .Range(1, 500)
                .Select(index => (object)new DiscoveredGameChoiceViewModel(
                    new DiscoveredGame(
                        $"Game {index}",
                        $@"C:\Games\Game{index}\game.exe",
                        GameDiscoverySource.Steam,
                        GameDiscoveryConfidence.High),
                    isAlreadyConfigured: false,
                    () => { }))
                .ToArray();
            RulesEditorView editor = new()
            {
                DataContext = new RulesEditorTestContext
                {
                    CanEditRules = true,
                    DiscoveredGames = discoveredGames,
                },
            };
            ScrollViewer pageScroller = new()
            {
                Width = 900,
                Height = 700,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = editor,
            };
            Window host = new()
            {
                Width = 900,
                Height = 700,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = pageScroller,
            };

            try
            {
                host.Show();
                host.UpdateLayout();
                ListBox discoveredList = FindVisualDescendant<ListBox>(
                    editor,
                    list => list.Items.Count == discoveredGames.Length)
                    ?? throw new InvalidOperationException("Discovered game list was not created.");
                int realized = Enumerable
                    .Range(0, discoveredGames.Length)
                    .Count(index => discoveredList.ItemContainerGenerator
                        .ContainerFromIndex(index) is not null);

                Assert.InRange(realized, 1, 40);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void RulesEditor_DiscoveredGameRowsStayResponsiveAndUseModernEditors()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string rules = File.ReadAllText(Path.Combine(desktop, "Views", "RulesEditorView.xaml"));

        Assert.DoesNotContain("Width=\"76\"", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("MinHeight=\"32\"", rules, StringComparison.Ordinal);
        Assert.True(
            Count(rules, "Style=\"{StaticResource InlineDurationComboBoxStyle}\"") >= 2,
            "Both discovered and configured game rows should use the modern duration editor.");
        Assert.True(
            Count(rules, "Style=\"{StaticResource ChecklistItemStyle}\"") >= 2,
            "Both game selection and site selection should expose a full-size modern target.");
        Assert.Contains(
            "Style=\"{StaticResource ChoiceComboBoxStyle}\"",
            rules,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.HelpText=\"勾选后会在添加时使用这一局时长\"",
            rules,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<WrapPanel Margin=\"0,4,0,0\">", rules, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationStartsHiddenAndLetsTheTrayOpenTheDashboard()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string appXaml = File.ReadAllText(Path.Combine(desktop, "App.xaml"));
        string appCode = File.ReadAllText(Path.Combine(desktop, "App.xaml.cs"));
        string composition = File.ReadAllText(Path.Combine(
            desktop,
            "DesktopProductionComposition.cs"));

        Assert.DoesNotContain("StartupUri", appXaml, StringComparison.Ordinal);
        Assert.Contains("DesktopProductionComposition", appCode, StringComparison.Ordinal);
        Assert.Contains("TrayApplicationShell", composition, StringComparison.Ordinal);
        Assert.Contains("WindowsGameDiscovery", composition, StringComparison.Ordinal);
        Assert.Contains("ShutdownMode.OnExplicitShutdown", appCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedTrayExit_RecordsBypassButOrdinaryApplicationExitDoesNot()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string composition = File.ReadAllText(Path.Combine(
            desktop,
            "DesktopProductionComposition.cs"));
        string tray = File.ReadAllText(Path.Combine(desktop, "TrayApplicationShell.cs"));

        Assert.Contains(
            "new TrayApplicationShell(dashboard, application, BeforeUserExitAsync)",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReportDeliberateBypassAsync",
            composition,
            StringComparison.Ordinal);
        Assert.Contains("await _beforeExitAsync();", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("DeliberateBypass", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void LockAndUiSlice_ContainsNoPowerNetworkShutdownOrProcessMutation()
    {
        string desktop = Path.Combine(FindRepositoryRoot(), "src", "NightGate.Desktop");
        string[] files = Directory
            .EnumerateFiles(desktop, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is ".cs" or ".xaml")
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                return name.Contains("Lock", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Overlay", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Dashboard", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Tray", StringComparison.OrdinalIgnoreCase)
                    || name is "App.xaml" or "App.xaml.cs" or "MainWindow.xaml" or "MainWindow.xaml.cs";
            })
            .ToArray();
        string source = string.Join("\n", files.Select(File.ReadAllText));
        string[] forbidden =
        [
            "PowerWrite",
            "PowerSetActiveScheme",
            "SetActiveScheme",
            "shutdown.exe",
            "netsh",
            "TerminateProcess",
            ".Kill(",
            "taskkill",
            "CurrentVersion\\Run",
            "Registry.SetValue",
        ];

        foreach (string token in forbidden)
        {
            Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "NightGate.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate NightGate.slnx.");
    }

    private static string ReadDesktopXaml(string desktop) => string.Join(
        "\n",
        Directory
            .EnumerateFiles(desktop, "*.xaml", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(File.ReadAllText));

    private static void EnsureApplicationResources()
    {
        System.Windows.Application application =
            System.Windows.Application.Current ?? new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
        ResourceDictionary resources = application.Resources;
        if (resources.MergedDictionaries.Count > 0)
        {
            return;
        }

        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/NightGate.Desktop;component/Themes/NightGate.Brushes.xaml",
                UriKind.Relative),
        });
        resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "/NightGate.Desktop;component/Themes/NightGate.Controls.xaml",
                UriKind.Relative),
        });
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            T? descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindVisualDescendant<T>(
        DependencyObject parent,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && predicate(match))
            {
                return match;
            }

            T? descendant = FindVisualDescendant(child, predicate);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private static async Task RunOnStaThreadAsync(Action action)
    {
        DispatcherOperation operation = UiDispatcher.Value.InvokeAsync(action);
        await operation.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Dispatcher CreateUiDispatcher()
    {
        TaskCompletionSource<Dispatcher> ready = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            ready.TrySetResult(dispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "NightGate desktop UI test dispatcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    private sealed class RulesEditorTestContext
    {
        public bool CanEditRules { get; set; }

        public IReadOnlyList<DesktopAppRuleItemViewModel> ConfiguredApps { get; set; } = [];

        public IReadOnlyList<object> DiscoveredGames { get; set; } = [];

        public IReadOnlyList<object> SiteSelections { get; } = [];

        public IReadOnlyList<int> GameSessionMinuteOptions { get; } =
            [15, 25, 35, 45, 60, 90];

        public int SelectedGameSessionMinutes { get; set; } = 35;

        public DesktopAppRuleItemViewModel? SelectedAppRule { get; set; }
    }

    private static int Count(string value, string token)
    {
        int count = 0;
        for (int index = 0;
             (index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0;
             index += token.Length)
        {
            count++;
        }

        return count;
    }
}
