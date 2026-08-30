namespace NightGate.Desktop;

public enum DesktopOnboardingStepState
{
    Completed,
    Current,
    Upcoming,
}

public sealed record DesktopOnboardingStepPresentation(
    int Number,
    string Title,
    DesktopOnboardingStepState State,
    bool CanNavigate);
