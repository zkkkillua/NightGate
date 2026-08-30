using System.Windows;

namespace NightGate.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(DashboardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => Hide();
}
