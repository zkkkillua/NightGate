using System.Windows;

namespace NightGate.Desktop.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    private readonly IConfirmationDialogService _confirmationDialogs;

    public SettingsView() : this(FluentConfirmationDialogService.Instance)
    {
    }

    internal SettingsView(IConfirmationDialogService confirmationDialogs)
    {
        ArgumentNullException.ThrowIfNull(confirmationDialogs);
        _confirmationDialogs = confirmationDialogs;
        InitializeComponent();
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UserExperienceViewModel viewModel)
        {
            return;
        }

        _ = await ConfirmAndClearHistoryAsync(
            viewModel,
            _confirmationDialogs,
            Window.GetWindow(this));
    }

    internal static async ValueTask<bool> ConfirmAndClearHistoryAsync(
        UserExperienceViewModel viewModel,
        IConfirmationDialogService dialogs,
        Window? owner)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(dialogs);
        if (!dialogs.Confirm(owner, ConfirmationDialogRequests.ClearHistory))
        {
            return false;
        }

        DesktopClearHistoryResult result = await viewModel.ClearHistoryAsync();
        return result.Cleared;
    }
}
