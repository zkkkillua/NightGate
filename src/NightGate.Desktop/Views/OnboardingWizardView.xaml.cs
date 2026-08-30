using System.Windows;

namespace NightGate.Desktop.Views;

public partial class OnboardingWizardView : System.Windows.Controls.UserControl
{
    public OnboardingWizardView()
    {
        InitializeComponent();
    }

    private void Step_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is UserExperienceViewModel viewModel
            && sender is System.Windows.Controls.Button { Tag: int step })
        {
            viewModel.SelectOnboardingStep(step);
        }
    }
}
