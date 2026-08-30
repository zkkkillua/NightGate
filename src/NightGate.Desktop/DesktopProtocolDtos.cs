namespace NightGate.Desktop;

public enum DesktopNightPhase
{
    Free,
    LastStart,
    Grace,
    LandingLocked,
    Morning,
    CoolingOff,
    OverrideActive,
}

public enum DesktopOverrideKind
{
    TeamRescue,
    Emergency,
    Entertainment,
}

public enum DesktopEmergencyReason
{
    Health,
    Safety,
    UrgentWork,
}

public enum DesktopAppRuleCategory
{
    Game,
    Voice,
}

public enum PrivacySafeEventKind
{
    MissedLock,
    WorkstationLocked,
    LateNewEntertainment,
    DeliberateBypass,
}

public sealed record DesktopNightWindowDto(
    DateOnly NightDate,
    DateTimeOffset ProtectedStart,
    DateTimeOffset LastStart,
    DateTimeOffset Lock,
    DateTimeOffset LightsOut,
    DateTimeOffset Wake);

public sealed record DesktopAppRuleDto(
    string Id,
    string? RootExecutablePath,
    IReadOnlyList<string> HelperExecutablePaths,
    DesktopAppRuleCategory? Category,
    int SessionMinutes,
    bool IsConfigured);

public sealed record DesktopSiteRuleDto(string Domain);

public sealed record DesktopActiveOverrideDto(
    DesktopOverrideKind Kind,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    IReadOnlyList<string> AllowedProcessIdentifiers);

public sealed record DesktopPolicySnapshotDto(
    DateTimeOffset EvaluatedAt,
    DesktopNightPhase Phase,
    DesktopNightWindowDto Window,
    IReadOnlyList<DesktopAppRuleDto> AppRules,
    IReadOnlyList<DesktopSiteRuleDto> SiteRules,
    bool EnforcementEnabled,
    bool IsDegraded,
    DesktopActiveOverrideDto? ActiveOverride,
    [property: System.Text.Json.Serialization.JsonRequired] long Revision = 0);

public sealed record DesktopServiceRuntimeStatusDto(
    bool EnforcementEnabled,
    bool IsDegraded,
    string? DegradationCode,
    DesktopPolicySnapshotDto? Policy);

public sealed record DesktopPolicyResult(
    bool CanEnforce,
    bool IsDegraded,
    string? DegradationCode,
    DesktopServiceRuntimeStatusDto? Status)
{
    public DesktopPolicySnapshotDto? ExecutablePolicy =>
        CanEnforce ? Status?.Policy : null;

    public static DesktopPolicyResult FailOpen(string code) =>
        new(false, true, code, null);
}

public sealed record DesktopOverrideRequest(
    DesktopOverrideKind Kind,
    DesktopEmergencyReason? EmergencyReason = null);

public sealed record DesktopOverrideWindowDto(
    DesktopOverrideKind Kind,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc);

public sealed record DesktopOverrideResult(
    bool Accepted,
    string? Error,
    DesktopOverrideWindowDto? ActiveWindow,
    DesktopPolicyResult PolicyAfterRequest);

public sealed record DesktopRecordEventResult(bool Recorded, string? Error);

public sealed record DesktopEndSessionResult(bool Accepted, string? Error);
