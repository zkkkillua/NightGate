namespace NightGate.Core;

public sealed record OnboardingState
{
    public const int CurrentWizardVersion = 1;

    public OnboardingState(
        int CompletedStep = 0,
        bool ChromeVerified = false,
        bool IncognitoProtected = false,
        bool IncognitoWarningAcknowledged = false,
        int IPhoneConfirmedThroughStep = 0,
        DateTimeOffset? CompletedAtUtc = null,
        int WizardVersion = CurrentWizardVersion,
        bool ChromeDegradedAcknowledged = false)
    {
        if (WizardVersion != CurrentWizardVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(WizardVersion));
        }

        if (CompletedStep is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(CompletedStep));
        }

        if (IPhoneConfirmedThroughStep is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(IPhoneConfirmedThroughStep));
        }

        bool chromeStepSatisfied =
            (ChromeVerified
                && (IncognitoProtected || IncognitoWarningAcknowledged))
            || ChromeDegradedAcknowledged;
        if (CompletedStep >= 3 && !chromeStepSatisfied)
        {
            throw new ArgumentException(
                "Completing Chrome setup requires protection or an explicit degraded decision.");
        }

        if (CompletedAtUtc is { } completedAt
            && (completedAt == default
                || completedAt.Offset != TimeSpan.Zero
                || CompletedStep != 5))
        {
            throw new ArgumentException(
                "The optional onboarding completion time requires step five and UTC.",
                nameof(CompletedAtUtc));
        }

        this.WizardVersion = WizardVersion;
        this.CompletedStep = CompletedStep;
        this.ChromeVerified = ChromeVerified;
        this.IncognitoProtected = IncognitoProtected;
        this.IncognitoWarningAcknowledged = IncognitoWarningAcknowledged;
        this.IPhoneConfirmedThroughStep = IPhoneConfirmedThroughStep;
        this.CompletedAtUtc = CompletedAtUtc;
        this.ChromeDegradedAcknowledged = ChromeDegradedAcknowledged;
    }

    public static OnboardingState Initial { get; } = new();

    public int WizardVersion { get; }

    public int CompletedStep { get; }

    public bool ChromeVerified { get; }

    public bool IncognitoProtected { get; }

    public bool IncognitoWarningAcknowledged { get; }

    public int IPhoneConfirmedThroughStep { get; }

    public DateTimeOffset? CompletedAtUtc { get; }

    public bool ChromeDegradedAcknowledged { get; }
}
