namespace NightGate.Desktop.Tests;

public sealed class DesktopThemeContractTests
{
    [Fact]
    public void App_ActivatesBuiltInFluentThenMergesBrandResources()
    {
        string app = File.ReadAllText(Repo("src", "NightGate.Desktop", "App.xaml"));
        int brushes = app.IndexOf(
            "Themes/NightGate.Brushes.xaml",
            StringComparison.Ordinal);
        int controls = app.IndexOf(
            "Themes/NightGate.Controls.xaml",
            StringComparison.Ordinal);

        Assert.Contains("ThemeMode=\"Light\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PresentationFramework.Fluent;component/Themes/Fluent.xaml",
            app,
            StringComparison.Ordinal);
        Assert.True(brushes >= 0 && brushes < controls);
    }

    [Fact]
    public void BrandResources_DefineRequiredBrushesAndTemplatePreservingStyles()
    {
        string brushes = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Themes", "NightGate.Brushes.xaml"));
        string controls = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Themes", "NightGate.Controls.xaml"));

        string[] brushKeys =
        [
            "AppBackgroundBrush",
            "CardBackgroundBrush",
            "SuccessCardBrush",
            "InfoCardBrush",
            "WarningCardBrush",
            "PrimaryTextBrush",
            "SecondaryTextBrush",
            "AccentBrush",
        ];
        string[] styleKeys =
        [
            "PrimaryButtonStyle",
            "SecondaryButtonStyle",
            "TextButtonStyle",
            "DangerButtonStyle",
            "CardStyle",
            "StatusPillStyle",
        ];

        foreach (string key in brushKeys)
        {
            Assert.Contains($"x:Key=\"{key}\"", brushes, StringComparison.Ordinal);
        }

        foreach (string key in styleKeys)
        {
            Assert.Contains($"x:Key=\"{key}\"", controls, StringComparison.Ordinal);
        }

        Assert.Contains(
            "BasedOn=\"{StaticResource {x:Type Button}}\"",
            controls,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ControlTemplate", controls, StringComparison.Ordinal);
    }

    [Fact]
    public void UserExperience_SeparatesConnectionWizardAndSettingsModes()
    {
        string source = File.ReadAllText(Repo(
            "src",
            "NightGate.Desktop",
            "Views",
            "UserExperienceShellView.xaml"));

        Assert.Contains("IsLoading", source, StringComparison.Ordinal);
        Assert.Contains("IsUnavailable", source, StringComparison.Ordinal);
        Assert.Contains("OnboardingWizardView", source, StringComparison.Ordinal);
        Assert.Contains("SettingsView", source, StringComparison.Ordinal);
        Assert.Contains("DataTrigger", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WizardSettingsAndRulesViews_KeepNavigationAndEditingContracts()
    {
        string wizard = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "OnboardingWizardView.xaml"));
        string settings = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "SettingsView.xaml"));
        string rulesCode = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "RulesEditorView.xaml.cs"));

        Assert.Contains("OnboardingSteps", wizard, StringComparison.Ordinal);
        Assert.Contains("PreviousOnboardingCommand", wizard, StringComparison.Ordinal);
        Assert.Contains("NextOnboardingCommand", wizard, StringComparison.Ordinal);
        Assert.Contains("OnboardingMissingRequirement", wizard, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", wizard, StringComparison.Ordinal);
        Assert.Contains("AcknowledgedChromeDegraded", wizard, StringComparison.Ordinal);
        Assert.Contains("SettingsCategories", settings, StringComparison.Ordinal);
        Assert.Contains("SelectedSettingsCategory", settings, StringComparison.Ordinal);
        Assert.Contains("RulesEditorView", settings, StringComparison.Ordinal);
        Assert.Contains("OpenFileDialog", rulesCode, StringComparison.Ordinal);
        Assert.Contains("AddAppRule", rulesCode, StringComparison.Ordinal);
        Assert.Contains("AddHelperToSelectedAppRule", rulesCode, StringComparison.Ordinal);
        Assert.Contains("RemoveSelectedAppRule", rulesCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UserExperienceViews_ExposeCompleteOperationalSetupContract()
    {
        string shell = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "UserExperienceShellView.xaml"));
        string wizard = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "OnboardingWizardView.xaml"));
        string settings = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "SettingsView.xaml"));
        string ladder = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "ScheduleLadderView.xaml"));
        string rules = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "RulesEditorView.xaml"));

        Assert.DoesNotContain("Connection.Message", shell, StringComparison.Ordinal);
        Assert.Equal(2, Count(shell, "Connection.Body"));
        Assert.Contains("Text=\"{Binding StatusMessage}\"", shell, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.LiveSetting=\"Polite\"",
            shell,
            StringComparison.Ordinal);

        Assert.Contains("StringFormat=第 {0}/5 步", wizard, StringComparison.Ordinal);
        Assert.DoesNotContain("StringFormat=第 {0} 步", wizard, StringComparison.Ordinal);
        Assert.Contains("ScheduleLadderView", wizard, StringComparison.Ordinal);
        Assert.Contains("ScheduleLadderView", settings, StringComparison.Ordinal);
        Assert.Contains("ScheduleLadder.CurrentStepHeading", ladder, StringComparison.Ordinal);
        Assert.Contains("ScheduleLadder.TonightMilestones", ladder, StringComparison.Ordinal);
        Assert.Contains("ScheduleLadder.Steps", ladder, StringComparison.Ordinal);
        Assert.Contains("ScheduleLadder.ProgressionRuleText", ladder, StringComparison.Ordinal);
        Assert.Contains("默认最晚开新一局", ladder, StringComparison.Ordinal);
        Assert.Contains("chrome://extensions", wizard, StringComparison.Ordinal);
        Assert.Contains("开发者模式", wizard, StringComparison.Ordinal);
        Assert.Contains("加载已解压的扩展程序", wizard, StringComparison.Ordinal);
        Assert.Contains(
            @"C:\Program Files\NightGate\chrome-extension",
            wizard,
            StringComparison.Ordinal);
        Assert.Contains("在无痕模式下启用", wizard, StringComparison.Ordinal);
        Assert.Contains("OpenChromeExtensionOptionsCommand", wizard, StringComparison.Ordinal);
        Assert.Contains("OpenChromeExtensionOptionsCommand", settings, StringComparison.Ordinal);
        Assert.Contains("选择与“程序与网站”页面相同的网站", wizard, StringComparison.Ordinal);
        Assert.Contains("选择与“程序与网站”页面相同的网站", settings, StringComparison.Ordinal);
        Assert.Contains("chrome://extensions", settings, StringComparison.Ordinal);
        Assert.Contains("扩展程序选项", settings, StringComparison.Ordinal);

        int rulesCategory = settings.IndexOf(
            "DesktopSettingsCategory.Rules",
            StringComparison.Ordinal);
        int privacyCategory = settings.IndexOf(
            "DesktopSettingsCategory.Privacy",
            StringComparison.Ordinal);
        Assert.True(rulesCategory > 0 && privacyCategory > rulesCategory);
        Assert.DoesNotContain(
            "LegacyShutdownTasks",
            settings[..rulesCategory],
            StringComparison.Ordinal);
        Assert.Contains(
            "LegacyShutdownTasks",
            settings[privacyCategory..],
            StringComparison.Ordinal);
        Assert.Contains("旧任务、历史与隐私", settings, StringComparison.Ordinal);
        Assert.Contains(
            "IsEnabled=\"{Binding CanEditRules}\"",
            rules,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_EnableDpiRoundingAndKeepInteractiveTargetsAccessible()
    {
        string main = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "MainWindow.xaml"));
        string overlay = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "RestrictedOverlayWindow.xaml"));
        string controls = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Themes", "NightGate.Controls.xaml"));

        foreach (string source in new[] { main, overlay })
        {
            Assert.Contains("UseLayoutRounding=\"True\"", source, StringComparison.Ordinal);
            Assert.Contains("SnapsToDevicePixels=\"True\"", source, StringComparison.Ordinal);
        }

        Assert.Contains("MinHeight\" Value=\"40\"", controls, StringComparison.Ordinal);
    }

    [Fact]
    public void CommonControls_ExposeClearBrandHierarchyForActionsAndChoices()
    {
        string controls = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Themes", "NightGate.Controls.xaml"));
        string overlay = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "RestrictedOverlayWindow.xaml"));
        string settings = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "SettingsView.xaml"));
        string rules = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "RulesEditorView.xaml"));

        Assert.Contains(
            "<Setter Property=\"Background\" Value=\"{StaticResource AccentBrush}\" />",
            controls,
            StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ChoiceComboBoxStyle\"", controls, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ChecklistItemStyle\"", controls, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SettingsNavigationItemStyle\"", controls, StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource ChoiceComboBoxStyle}\"",
            rules,
            StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource SecondaryButtonStyle}\"",
            overlay,
            StringComparison.Ordinal);
        Assert.Contains(
            "BasedOn=\"{StaticResource SettingsNavigationItemStyle}\"",
            settings,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PageScrollers_ShareVerticalPanningAndImmediateFeedbackContract()
    {
        string controls = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Themes", "NightGate.Controls.xaml"));
        string main = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "MainWindow.xaml"));
        string wizard = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "OnboardingWizardView.xaml"));
        string settings = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "SettingsView.xaml"));

        Assert.Contains("x:Key=\"PageScrollViewerStyle\"", controls, StringComparison.Ordinal);
        Assert.Contains("Property=\"PanningMode\" Value=\"VerticalOnly\"", controls, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsDeferredScrollingEnabled\" Value=\"False\"", controls, StringComparison.Ordinal);
        Assert.Equal(2, Count(main, "Style=\"{StaticResource PageScrollViewerStyle}\""));
        Assert.Contains(
            "Style=\"{StaticResource PageScrollViewerStyle}\"",
            wizard,
            StringComparison.Ordinal);
        Assert.Contains(
            "BasedOn=\"{StaticResource PageScrollViewerStyle}\"",
            settings,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduleLadder_DoesNotCompressFourCardsIntoOneRow()
    {
        string ladder = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "ScheduleLadderView.xaml"));

        Assert.DoesNotContain("<primitives:UniformGrid Rows=\"1\" />", ladder, StringComparison.Ordinal);
        Assert.Equal(2, Count(ladder, "<primitives:UniformGrid Columns=\"2\" />"));
    }

    [Fact]
    public void HeaderAndFooterDockPanels_DoNotStretchTrailingActionsAcrossThePage()
    {
        string main = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "MainWindow.xaml"));
        string wizard = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "OnboardingWizardView.xaml"));
        string settings = File.ReadAllText(Repo(
            "src", "NightGate.Desktop", "Views", "SettingsView.xaml"));

        Assert.Contains(
            "<DockPanel Grid.Row=\"0\" Margin=\"4,0,4,16\" LastChildFill=\"False\">",
            main,
            StringComparison.Ordinal);
        Assert.Contains(
            "<DockPanel Grid.Row=\"2\" Margin=\"4,12,4,0\" LastChildFill=\"False\">",
            main,
            StringComparison.Ordinal);
        Assert.Contains(
            "<DockPanel Grid.Row=\"0\" Margin=\"0,0,0,12\" LastChildFill=\"False\">",
            wizard,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            Count(settings, "<DockPanel Margin=\"0,0,0,14\" LastChildFill=\"False\">"));
    }

    private static string Repo(params string[] segments)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null
            && !File.Exists(Path.Combine(current.FullName, "NightGate.slnx")))
        {
            current = current.Parent;
        }

        return Path.Combine(
            current?.FullName
                ?? throw new DirectoryNotFoundException(
                    "Could not locate NightGate.slnx."),
            Path.Combine(segments));
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
