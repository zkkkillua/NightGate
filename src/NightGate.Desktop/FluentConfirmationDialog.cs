using System.Windows;

namespace NightGate.Desktop;

internal sealed record ConfirmationDialogRequest(
    string Title,
    string Message,
    string ConfirmText,
    string CancelText)
{
    public ConfirmationDialogRequest Validate()
    {
        if (string.IsNullOrWhiteSpace(Title)
            || string.IsNullOrWhiteSpace(Message)
            || string.IsNullOrWhiteSpace(ConfirmText)
            || string.IsNullOrWhiteSpace(CancelText))
        {
            throw new ArgumentException("Confirmation dialog text cannot be empty.");
        }

        return this;
    }
}

internal static class ConfirmationDialogRequests
{
    public static ConfirmationDialogRequest ExitApplication { get; } = new(
        "退出收尾",
        TrayExitPrompt.Message,
        "退出并停止保护",
        "继续运行");

    public static ConfirmationDialogRequest ClearHistory { get; } = new(
        "清除全部本机历史",
        "清除后，全部原始事件、周报来源和简短自报记录都会被删除；今晚正在执行的状态、规则、通知防重复记录和例外机会会保留。",
        "确认清除",
        "取消");
}

internal interface IConfirmationDialogService
{
    bool Confirm(Window? owner, ConfirmationDialogRequest request);
}

internal sealed class FluentConfirmationDialogService : IConfirmationDialogService
{
    public static FluentConfirmationDialogService Instance { get; } = new();

    private FluentConfirmationDialogService()
    {
    }

    public bool Confirm(Window? owner, ConfirmationDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            FluentConfirmationDialog dialog = new(request.Validate());
            if (owner?.IsVisible == true)
            {
                dialog.Owner = owner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            return dialog.ShowDialog() == true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            // A prompt failure must behave like cancel, never like destructive confirmation.
            return false;
        }
    }
}

public partial class FluentConfirmationDialog : Window
{
    internal FluentConfirmationDialog(ConfirmationDialogRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        InitializeComponent();
        DataContext = request.Validate();
        Loaded += FocusConfirmButton;
    }

    private void FocusConfirmButton(object sender, RoutedEventArgs e)
    {
        ConfirmButton.Focus();
        Loaded -= FocusConfirmButton;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
