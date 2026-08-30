namespace NightGate.Desktop;

public sealed record DesktopProgressStateDto(
    int CurrentStep,
    DateTimeOffset? LastTeamRescueAtUtc,
    DateOnly? LastProgressionNightDate,
    int? PendingStep,
    DateOnly? PendingStepUnlockedByNightDate,
    DateTimeOffset? PendingStepConfirmedAtUtc,
    DateOnly? PendingStepEffectiveNightDate);

public sealed record DesktopOnboardingStateDto(
    int WizardVersion,
    int CompletedStep,
    bool ChromeVerified,
    bool IncognitoProtected,
    bool IncognitoWarningAcknowledged,
    int IPhoneConfirmedThroughStep,
    DateTimeOffset? CompletedAtUtc,
    bool ChromeDegradedAcknowledged = false);

public sealed record DesktopRuleSettingsStateDto(
    IReadOnlyList<DesktopAppRuleDto> ActiveAppRules,
    IReadOnlyList<DesktopSiteRuleDto> ActiveSiteRules,
    IReadOnlyList<DesktopAppRuleDto>? PendingAppRules,
    IReadOnlyList<DesktopSiteRuleDto>? PendingSiteRules,
    DateOnly? PendingEffectiveNightDate,
    DateTimeOffset? PendingSavedAtUtc);

public sealed record DesktopOverrideReasonSummaryDto(
    int TeamRescueCount,
    int EntertainmentCount,
    int EmergencyHealthCount,
    int EmergencySafetyCount,
    int EmergencyUrgentWorkCount,
    int EmergencyOtherCount);

public sealed record DesktopWeeklyReportSummaryDto(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int ObservedWorkNights,
    int EligibleWorkNights,
    int QualifyingWorkNights,
    int LockObservations,
    TimeOnly? MedianLockTime,
    int? MedianLockChangeMinutes,
    DesktopOverrideReasonSummaryDto OverrideReasons);

public sealed record DesktopNightSelfReportDto(
    DateOnly NightDate,
    bool? PhoneOutOfReach,
    bool? WakeWithinWindow,
    DateTimeOffset UpdatedAtUtc);

public sealed record DesktopChromeProtectionStatusDto(
    string Status,
    bool IsHealthy,
    bool IncognitoProtected,
    DateTimeOffset? LastHeartbeatAtUtc,
    string? ExtensionVersion);

public sealed record DesktopUserStateDto(
    DesktopProgressStateDto Progress,
    DesktopOnboardingStateDto Onboarding,
    DesktopRuleSettingsStateDto Rules,
    DesktopWeeklyReportSummaryDto WeeklyReport,
    DateOnly CurrentNightDate,
    DesktopNightSelfReportDto? SelfReport,
    DesktopChromeProtectionStatusDto ChromeProtection);

public sealed record DesktopUserStateResult(
    bool Available,
    string? Error,
    DesktopUserStateDto? State)
{
    public static DesktopUserStateResult Unavailable(string error) =>
        new(false, error, null);
}

public sealed record DesktopOnboardingStepRequest(
    int Step,
    bool ChromeVerified,
    bool IncognitoProtected,
    bool IncognitoWarningAcknowledged,
    int IPhoneConfirmedThroughStep,
    bool ChromeDegradedAcknowledged = false);

public sealed record DesktopOnboardingMutationResult(
    bool Accepted,
    string? Error,
    DesktopOnboardingStateDto? Onboarding);

public sealed record DesktopSelfReportMutationResult(
    bool Saved,
    string? Error,
    DesktopNightSelfReportDto? SelfReport);

public sealed record DesktopAppRuleDraft(
    string Id,
    string RootExecutablePath,
    IReadOnlyList<string> HelperExecutablePaths,
    DesktopAppRuleCategory Category,
    int SessionMinutes);

public sealed record DesktopRuleSettingsMutationResult(
    bool Saved,
    string? Error,
    DesktopRuleSettingsStateDto? Rules,
    bool AppliesImmediately,
    bool AppliesTonight,
    DateOnly? EffectiveNight);

public enum DesktopNightNoticeKind
{
    IfThenPlan,
    LastStart,
    Grace10,
    Grace2,
}

public sealed record DesktopNoticeClaimResult(
    bool Claimed,
    string? Error,
    DesktopNightNoticeKind? Kind,
    DateOnly? NightDate);

public sealed record DesktopIPhoneChecklist(
    bool HealthSleepScheduleConfigured,
    bool SleepFocusConfigured,
    bool DowntimeConfigured,
    bool BlockAtDowntimeEnabled,
    bool RequiredAppsAllowed,
    bool SafariNotAllowlisted,
    bool DistinctRecoverableScreenTimePasscodeAcknowledged,
    bool OldAlarmsChecked,
    bool PhonePlacementPlanned,
    bool EntertainmentCategoriesRestricted = false)
{
    public static DesktopIPhoneChecklist AllConfirmed { get; } = new(
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        true,
        true);

    public bool IsComplete =>
        HealthSleepScheduleConfigured
        && SleepFocusConfigured
        && DowntimeConfigured
        && BlockAtDowntimeEnabled
        && EntertainmentCategoriesRestricted
        && RequiredAppsAllowed
        && SafariNotAllowlisted
        && DistinctRecoverableScreenTimePasscodeAcknowledged
        && OldAlarmsChecked
        && PhonePlacementPlanned;
}

public sealed record DesktopIPhoneProgressionResult(
    bool Accepted,
    string? Error,
    int? PendingStep,
    DateOnly? EffectiveNightDate);

public sealed record DesktopClearHistoryResult(bool Cleared, string? Error);
