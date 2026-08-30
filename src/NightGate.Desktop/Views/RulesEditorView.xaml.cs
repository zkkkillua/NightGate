using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace NightGate.Desktop.Views;

public partial class RulesEditorView : System.Windows.Controls.UserControl
{
    private readonly Dictionary<ItemsControl, int> _wheelDeltaRemainders = [];

    public RulesEditorView()
    {
        InitializeComponent();
    }

    private UserExperienceViewModel? ViewModel =>
        DataContext as UserExperienceViewModel;

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseExecutable("选择要保护的游戏", out string path))
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.AddAppRule(
                    path,
                    DesktopAppRuleCategory.Game,
                    viewModel.SelectedGameSessionMinutes);
            }
        }
    }

    private void AddVoice_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseExecutable("选择团队救场时允许的语音工具", out string path))
        {
            ViewModel?.AddAppRule(path, DesktopAppRuleCategory.Voice, 35);
        }
    }

    private void AddHelper_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedAppRule is null)
        {
            return;
        }

        if (ChooseExecutable("选择关联辅助程序", out string path))
        {
            ViewModel.AddHelperToSelectedAppRule(path);
        }
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.RemoveSelectedAppRule();

    private void RemoveHelper_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button
            {
                DataContext: string helperExecutablePath,
                Tag: DesktopAppRuleItemViewModel appRule,
            })
        {
            ViewModel?.RemoveHelperFromAppRule(appRule, helperExecutablePath);
            e.Handled = true;
        }
    }

    private void VirtualizedGameList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ItemsControl list)
        {
            return;
        }

        // The list owns wheel input even when the containing page is already at
        // its boundary. Letting the event continue would allow a collapsed
        // duration ComboBox under the pointer to change its selected value.
        e.Handled = true;
        int accumulated = _wheelDeltaRemainders.GetValueOrDefault(list) + e.Delta;
        int wheelSteps = accumulated / Mouse.MouseWheelDeltaForOneLine;
        _wheelDeltaRemainders[list] =
            accumulated - (wheelSteps * Mouse.MouseWheelDeltaForOneLine);
        if (wheelSteps == 0)
        {
            return;
        }

        ScrollViewer? listScroller = FindVisualDescendant<ScrollViewer>(list);
        bool scrollUp = wheelSteps > 0;
        int scrollLines = SystemParameters.WheelScrollLines;
        if (scrollLines < 0)
        {
            for (int index = 0; index < Math.Abs(wheelSteps); index++)
            {
                ScrollViewer? target = CanScroll(listScroller, scrollUp)
                    ? listScroller
                    : FindScrollableAncestor(list, scrollUp);
                if (target is null)
                {
                    break;
                }

                if (scrollUp)
                {
                    target.PageUp();
                }
                else
                {
                    target.PageDown();
                }
            }

            return;
        }

        for (int index = 0; index < scrollLines * Math.Abs(wheelSteps); index++)
        {
            ScrollViewer? target = CanScroll(listScroller, scrollUp)
                ? listScroller
                : FindScrollableAncestor(list, scrollUp);
            if (target is null)
            {
                break;
            }

            if (scrollUp)
            {
                target.LineUp();
            }
            else
            {
                target.LineDown();
            }
        }
    }

    private static ScrollViewer? FindScrollableAncestor(
        DependencyObject child,
        bool scrollUp)
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(child);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is ScrollViewer scroller && CanScroll(scroller, scrollUp))
            {
                return scroller;
            }
        }

        return null;
    }

    private static bool CanScroll(ScrollViewer? scroller, bool scrollUp) =>
        scroller is not null
        && (scrollUp
            ? scroller.VerticalOffset > 0
            : scroller.VerticalOffset < scroller.ScrollableHeight);

    private static T? FindVisualDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static bool ChooseExecutable(string title, out string path)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = title,
            Filter = "Windows 程序 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
            ValidateNames = true,
        };
        bool accepted = dialog.ShowDialog() == true;
        path = accepted ? dialog.FileName : string.Empty;
        return accepted;
    }
}
