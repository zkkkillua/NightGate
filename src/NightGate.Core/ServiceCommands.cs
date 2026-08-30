namespace NightGate.Core;

public abstract record ServiceCommand;

public sealed record GetPolicyCommand : ServiceCommand;

public sealed record GetDesktopPolicyCommand(string SessionId) : ServiceCommand;

public sealed record EndDesktopSessionCommand(string SessionId) : ServiceCommand;

public sealed record GetStatusCommand : ServiceCommand;

public sealed record GetUserStateCommand : ServiceCommand;

public sealed record RequestOverrideCommand(OverrideRequest Request) : ServiceCommand;

public sealed record RecordEventCommand(
    NightEventKind Kind,
    NightPhase? BasePhase,
    OverrideKind? OverrideKind) : ServiceCommand;

public sealed record ConfirmIPhoneStepCommand(
    int RequestedStep,
    IPhoneStepConfirmation Confirmation) : ServiceCommand;

public sealed record CompleteOnboardingStepCommand(
    int Step,
    bool ChromeVerified,
    bool IncognitoProtected,
    bool IncognitoWarningAcknowledged,
    int IPhoneConfirmedThroughStep,
    bool ChromeDegradedAcknowledged = false) : ServiceCommand;

public sealed record SaveNightSelfReportCommand(
    DateOnly NightDate,
    bool? PhoneOutOfReach,
    bool? WakeWithinWindow) : ServiceCommand;

public sealed record SaveRuleSettingsCommand(
    System.Collections.Immutable.ImmutableArray<AppRule> AppRules,
    System.Collections.Immutable.ImmutableArray<SiteRule> SiteRules) : ServiceCommand;

public sealed record ClaimDueNoticeCommand : ServiceCommand;

public sealed record ClearHistoryCommand : ServiceCommand;

public sealed record RecordBrowserEventCommand(
    BrowserPrivacyEvent Event) : ServiceCommand;

public sealed record RecordChromeHealthCommand(
    string ExtensionId,
    string ExtensionVersion,
    string ProfileTokenSha256,
    long PolicyRevision,
    bool IncognitoAllowed,
    bool ProtectionReady) : ServiceCommand;

public sealed record ListLegacyTaskMigrationsCommand(
    string? Cursor = null) : ServiceCommand;

public sealed record FindLegacyTaskMigrationRecoveryCandidateCommand(
    string TaskPath) : ServiceCommand;

public sealed record PrepareLegacyTaskMigrationCommand(
    string TaskPath,
    string ActionFingerprint,
    bool OriginalEnabled) : ServiceCommand;

public sealed record CompleteLegacyTaskMigrationCommand(
    string MigrationId,
    LegacyTaskMigrationStatus Status) : ServiceCommand;

public sealed record RecoverLegacyTaskMigrationDisabledCommand(
    string MigrationId,
    string TaskPath,
    string ActionFingerprint,
    bool OriginalEnabled,
    string RecoveryToken) : ServiceCommand;

public sealed record LoadProcessPersistenceCommand(
    ProcessPersistenceSlot Slot) : ServiceCommand;

public sealed record CompareExchangeProcessPersistenceCommand(
    ProcessPersistenceSlot Slot,
    long? ExpectedVersion,
    ProcessPersistenceRecord Replacement) : ServiceCommand;
