namespace NightGate.Desktop;

public enum DesktopLegacyTaskMigrationStatus
{
    Prepared = 0,
    Disabled = 1,
    Restored = 2,
    Failed = 3,
    RestorePrepared = 4,
}

public sealed record DesktopLegacyTaskMigration(
    string MigrationId,
    string TaskPath,
    string ActionFingerprint,
    bool OriginalEnabled,
    DesktopLegacyTaskMigrationStatus Status,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    bool DisabledStateVerified = false);

public sealed record DesktopLegacyMigrationListResult(
    bool Available,
    string? Error,
    IReadOnlyList<DesktopLegacyTaskMigration> Migrations,
    int FailedCount = 0)
{
    public static DesktopLegacyMigrationListResult Unavailable(string error) =>
        new(false, error, Array.Empty<DesktopLegacyTaskMigration>(), 0);
}

public sealed record DesktopLegacyMigrationMutationResult(
    bool Accepted,
    string? Error,
    DesktopLegacyTaskMigration? Migration);

public sealed record DesktopLegacyMigrationLookupResult(
    bool Available,
    string? Error,
    DesktopLegacyTaskMigration? Migration,
    string? RecoveryToken = null)
{
    public static DesktopLegacyMigrationLookupResult Unavailable(string error) =>
        new(false, error, null, null);
}
