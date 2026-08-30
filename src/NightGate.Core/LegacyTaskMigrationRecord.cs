namespace NightGate.Core;

public enum LegacyTaskMigrationStatus
{
    Prepared = 0,
    Disabled = 1,
    Restored = 2,
    Failed = 3,
    RestorePrepared = 4,
}

public sealed record LegacyTaskMigrationRecord
{
    public const int MaximumMigrationIdLength = 128;
    public const int MaximumTaskPathLength = 1024;
    public const int ActionFingerprintLength = 64;
    public const int MaximumActionFingerprintLength = ActionFingerprintLength;
    public const int RecoveryTokenLength = 64;
    public const int MaximumActiveRecords = 1_024;

    public LegacyTaskMigrationRecord(
        string MigrationId,
        string TaskPath,
        string ActionFingerprint,
        bool OriginalEnabled,
        LegacyTaskMigrationStatus Status,
        DateTimeOffset PreparedAtUtc,
        DateTimeOffset? CompletedAtUtc = null,
        bool DisabledStateVerified = false)
    {
        this.MigrationId = ValidateText(
            MigrationId,
            MaximumMigrationIdLength,
            nameof(MigrationId));
        this.TaskPath = ValidateText(TaskPath, MaximumTaskPathLength, nameof(TaskPath));
        this.ActionFingerprint = ValidateActionFingerprint(ActionFingerprint);
        if (!Enum.IsDefined(Status))
        {
            throw new ArgumentOutOfRangeException(nameof(Status));
        }

        if (Status == LegacyTaskMigrationStatus.Prepared && DisabledStateVerified)
        {
            throw new ArgumentException(
                "A prepared migration cannot claim a verified disabled state.",
                nameof(DisabledStateVerified));
        }

        if (Status == LegacyTaskMigrationStatus.RestorePrepared
            && CompletedAtUtc is not null)
        {
            throw new ArgumentException(
                "A prepared restore cannot already have a completion time.",
                nameof(CompletedAtUtc));
        }

        ValidateUtc(PreparedAtUtc, nameof(PreparedAtUtc));
        if (CompletedAtUtc is { } completedAt)
        {
            ValidateUtc(completedAt, nameof(CompletedAtUtc));
            if (completedAt < PreparedAtUtc)
            {
                throw new ArgumentException(
                    "Migration completion cannot predate preparation.",
                    nameof(CompletedAtUtc));
            }
        }

        this.OriginalEnabled = OriginalEnabled;
        this.Status = Status;
        this.PreparedAtUtc = PreparedAtUtc;
        this.CompletedAtUtc = CompletedAtUtc;
        this.DisabledStateVerified = DisabledStateVerified;
    }

    public string MigrationId { get; }

    public string TaskPath { get; }

    public string ActionFingerprint { get; }

    public bool OriginalEnabled { get; }

    public LegacyTaskMigrationStatus Status { get; }

    public DateTimeOffset PreparedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; }

    public bool DisabledStateVerified { get; }

    private static string ValidateText(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The value is empty, padded, or too long.", parameterName);
        }

        return value;
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must be nondefault UTC.", parameterName);
        }
    }

    private static string ValidateActionFingerprint(string value)
    {
        if (value is null
            || value.Length != ActionFingerprintLength
            || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "The action fingerprint must be the adapter's 64-character lowercase SHA-256 hex digest.",
                nameof(ActionFingerprint));
        }

        return value;
    }
}
