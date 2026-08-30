using System.Text.Json;
using NightGate.Core;

namespace NightGate.Desktop;

public sealed partial class NightGateDesktopClient
{
    private const int MaximumLegacyMigrations =
        LegacyTaskMigrationRecord.MaximumActiveRecords;
    private const int MaximumLegacyMigrationPageSize = 8;
    private const int MaximumLegacyMigrationPages =
        MaximumLegacyMigrations / MaximumLegacyMigrationPageSize;

    public async ValueTask<DesktopLegacyMigrationListResult>
        ListLegacyTaskMigrationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var projected = new List<DesktopLegacyTaskMigration>();
            HashSet<string> ids = new(StringComparer.Ordinal);
            HashSet<string> cursors = new(StringComparer.Ordinal);
            string? cursor = null;
            int? failedCount = null;
            for (int pageNumber = 0;
                 pageNumber < MaximumLegacyMigrationPages;
                 pageNumber++)
            {
                string requestId = NextRequestId();
                object payload = cursor is null ? new { } : new { cursor };
                ReadOnlyMemory<byte> response = await _transport.ExchangeAsync(
                    CreateRequest("listLegacyTaskMigrations", requestId, payload),
                    cancellationToken).ConfigureAwait(false);
                ResponseWrapper<LegacyMigrationListResponseDto> wrapper =
                    DecodeResponse<LegacyMigrationListResponseDto>(
                        response,
                        "listLegacyTaskMigrationsResult",
                        requestId);
                LegacyMigrationListResponseDto data = wrapper.Data;
                if (wrapper.Status != "success"
                    || data.Migrations is null
                    || data.Migrations.Count > MaximumLegacyMigrationPageSize
                    || data.FailedCount < 0
                    || (failedCount is not null && failedCount != data.FailedCount))
                {
                    return DesktopLegacyMigrationListResult.Unavailable(
                        "service-degraded");
                }

                failedCount ??= data.FailedCount;
                foreach (LegacyMigrationResponseDto migration in data.Migrations)
                {
                    DesktopLegacyTaskMigration value = ProjectMigration(migration);
                    if (value.Status is not (
                            DesktopLegacyTaskMigrationStatus.Prepared or
                            DesktopLegacyTaskMigrationStatus.Disabled or
                            DesktopLegacyTaskMigrationStatus.RestorePrepared)
                        || !ids.Add(value.MigrationId)
                        || projected.Count >= MaximumLegacyMigrations)
                    {
                        throw new JsonException("Legacy migration page is malformed.");
                    }

                    projected.Add(value);
                }

                if (data.NextCursor is null)
                {
                    return new(true, null, projected, failedCount ?? 0);
                }

                ValidateMigrationId(data.NextCursor);
                if (data.Migrations.Count == 0
                    || !string.Equals(
                        data.Migrations[^1].MigrationId,
                        data.NextCursor,
                        StringComparison.Ordinal)
                    || !cursors.Add(data.NextCursor))
                {
                    throw new JsonException("Legacy migration cursor is malformed.");
                }

                cursor = data.NextCursor;
            }

            return DesktopLegacyMigrationListResult.Unavailable("service-degraded");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DesktopLegacyMigrationListResult.Unavailable(FailOpenCode);
        }
    }

    public async ValueTask<DesktopLegacyMigrationMutationResult>
        PrepareLegacyTaskMigrationAsync(
            LegacyShutdownTaskCandidate candidate,
            CancellationToken cancellationToken = default)
    {
        ValidateCandidate(candidate);
        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> response = await _transport.ExchangeAsync(
                CreateRequest(
                    "prepareLegacyTaskMigration",
                    requestId,
                    new
                    {
                        taskPath = candidate.TaskPath,
                        actionFingerprint = candidate.ActionFingerprint,
                        originalEnabled = candidate.WasEnabled,
                    }),
                cancellationToken).ConfigureAwait(false);
            return DecodeLegacyMutation(
                response,
                "prepareLegacyTaskMigrationResult",
                requestId,
                migration => (migration.Status is
                        DesktopLegacyTaskMigrationStatus.Prepared or
                        DesktopLegacyTaskMigrationStatus.Disabled)
                    && MatchesCandidate(migration, candidate));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, FailOpenCode, null);
        }
    }

    public async ValueTask<DesktopLegacyMigrationLookupResult>
        FindLegacyTaskMigrationRecoveryCandidateAsync(
            string taskPath,
            CancellationToken cancellationToken = default)
    {
        ValidateTaskPath(taskPath);
        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> response = await _transport.ExchangeAsync(
                CreateRequest(
                    "findLegacyTaskMigrationRecoveryCandidate",
                    requestId,
                    new { taskPath }),
                cancellationToken).ConfigureAwait(false);
            ResponseWrapper<LegacyMigrationLookupResponseDto> wrapper =
                DecodeResponse<LegacyMigrationLookupResponseDto>(
                    response,
                    "findLegacyTaskMigrationRecoveryCandidateResult",
                    requestId);
            if (wrapper.Status != "success")
            {
                return DesktopLegacyMigrationLookupResult.Unavailable(
                    "service-degraded");
            }

            LegacyMigrationLookupResponseDto data = wrapper.Data;
            if (!data.Found)
            {
                if (data.Migration is not null || data.RecoveryToken is not null)
                {
                    throw new JsonException("Absent recovery candidate has migration data.");
                }

                return new(true, null, null);
            }

            if (data.Migration is null || !IsValidRecoveryToken(data.RecoveryToken))
            {
                throw new JsonException("Recovery candidate is missing migration data.");
            }

            DesktopLegacyTaskMigration migration = ProjectMigration(data.Migration);
            if (migration.Status != DesktopLegacyTaskMigrationStatus.Failed
                || migration.DisabledStateVerified
                || !migration.OriginalEnabled
                || !string.Equals(
                    migration.TaskPath,
                    taskPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException("Recovery candidate facts are inconsistent.");
            }

            return new(true, null, migration, data.RecoveryToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // An older service does not know this optional recovery query.
            return DesktopLegacyMigrationLookupResult.Unavailable(FailOpenCode);
        }
    }

    public async ValueTask<DesktopLegacyMigrationMutationResult>
        RecoverLegacyTaskMigrationDisabledAsync(
            DesktopLegacyTaskMigration candidate,
            string recoveryToken,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateMigrationId(candidate.MigrationId);
        ValidateTaskPath(candidate.TaskPath);
        if (candidate.Status != DesktopLegacyTaskMigrationStatus.Failed
            || candidate.DisabledStateVerified
            || !candidate.OriginalEnabled
            || !IsValidRecoveryToken(recoveryToken))
        {
            throw new ArgumentException("The recovery proof is invalid.");
        }

        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> response = await _transport.ExchangeAsync(
                CreateRequest(
                    "recoverLegacyTaskMigrationDisabled",
                    requestId,
                    new
                    {
                        migrationId = candidate.MigrationId,
                        taskPath = candidate.TaskPath,
                        actionFingerprint = candidate.ActionFingerprint,
                        originalEnabled = candidate.OriginalEnabled,
                        recoveryToken,
                    }),
                cancellationToken).ConfigureAwait(false);
            return DecodeLegacyMutation(
                response,
                "recoverLegacyTaskMigrationDisabledResult",
                requestId,
                result => string.Equals(
                        result.MigrationId,
                        candidate.MigrationId,
                        StringComparison.Ordinal)
                    && string.Equals(
                        result.TaskPath,
                        candidate.TaskPath,
                        StringComparison.Ordinal)
                    && string.Equals(
                        result.ActionFingerprint,
                        candidate.ActionFingerprint,
                        StringComparison.Ordinal)
                    && result.OriginalEnabled == candidate.OriginalEnabled
                    && result.Status == DesktopLegacyTaskMigrationStatus.Disabled
                    && result.DisabledStateVerified);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, FailOpenCode, null);
        }
    }

    public async ValueTask<DesktopLegacyMigrationMutationResult>
        CompleteLegacyTaskMigrationAsync(
            string migrationId,
            DesktopLegacyTaskMigrationStatus status,
            CancellationToken cancellationToken = default)
    {
        ValidateMigrationId(migrationId);
        string statusToken = status switch
        {
            DesktopLegacyTaskMigrationStatus.Disabled => "disabled",
            DesktopLegacyTaskMigrationStatus.RestorePrepared => "restorePrepared",
            DesktopLegacyTaskMigrationStatus.Restored => "restored",
            DesktopLegacyTaskMigrationStatus.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        try
        {
            string requestId = NextRequestId();
            ReadOnlyMemory<byte> response = await _transport.ExchangeAsync(
                CreateRequest(
                    "completeLegacyTaskMigration",
                    requestId,
                    new { migrationId, status = statusToken }),
                cancellationToken).ConfigureAwait(false);
            return DecodeLegacyMutation(
                response,
                "completeLegacyTaskMigrationResult",
                requestId,
                migration => string.Equals(
                        migration.MigrationId,
                        migrationId,
                        StringComparison.Ordinal)
                    && migration.Status == status
                    && (status != DesktopLegacyTaskMigrationStatus.Disabled
                        || migration.DisabledStateVerified));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(false, FailOpenCode, null);
        }
    }

    private DesktopLegacyMigrationMutationResult DecodeLegacyMutation(
        ReadOnlyMemory<byte> response,
        string expectedType,
        string requestId,
        Func<DesktopLegacyTaskMigration, bool> acceptedPredicate)
    {
        ResponseWrapper<LegacyMigrationMutationResponseDto> wrapper =
            DecodeResponse<LegacyMigrationMutationResponseDto>(
                response,
                expectedType,
                requestId);
        if (wrapper.Status != "success")
        {
            return new(false, "service-degraded", null);
        }

        LegacyMigrationMutationResponseDto data = wrapper.Data;
        if (!data.Accepted)
        {
            if (string.IsNullOrWhiteSpace(data.Error) || data.Migration is not null)
            {
                throw new JsonException("Rejected legacy migration response is malformed.");
            }

            return new(false, data.Error, null);
        }

        if (data.Error is not null || data.Migration is null)
        {
            throw new JsonException("Accepted legacy migration response is malformed.");
        }

        DesktopLegacyTaskMigration migration = ProjectMigration(data.Migration);
        if (!acceptedPredicate(migration))
        {
            throw new JsonException("Legacy migration response facts do not match the request.");
        }

        return new(true, null, migration);
    }

    private static DesktopLegacyTaskMigration ProjectMigration(
        LegacyMigrationResponseDto migration)
    {
        ValidateMigrationId(migration.MigrationId);
        _ = new LegacyTaskMigrationRecord(
            migration.MigrationId,
            migration.TaskPath,
            migration.ActionFingerprint,
            migration.OriginalEnabled,
            ParseStatus(migration.Status),
            migration.PreparedAtUtc,
            migration.CompletedAtUtc,
            migration.DisabledStateVerified);
        DesktopLegacyTaskMigrationStatus status = migration.Status switch
        {
            "prepared" => DesktopLegacyTaskMigrationStatus.Prepared,
            "disabled" => DesktopLegacyTaskMigrationStatus.Disabled,
            "restorePrepared" => DesktopLegacyTaskMigrationStatus.RestorePrepared,
            "restored" => DesktopLegacyTaskMigrationStatus.Restored,
            "failed" => DesktopLegacyTaskMigrationStatus.Failed,
            _ => throw new JsonException("Unknown legacy migration status."),
        };
        if ((status is DesktopLegacyTaskMigrationStatus.Prepared or
                DesktopLegacyTaskMigrationStatus.RestorePrepared)
                != (migration.CompletedAtUtc is null))
        {
            throw new JsonException("Legacy migration completion time is inconsistent.");
        }

        return new(
            migration.MigrationId,
            migration.TaskPath,
            migration.ActionFingerprint,
            migration.OriginalEnabled,
            status,
            migration.PreparedAtUtc,
            migration.CompletedAtUtc,
            migration.DisabledStateVerified);
    }

    private static LegacyTaskMigrationStatus ParseStatus(string? status) => status switch
    {
        "prepared" => LegacyTaskMigrationStatus.Prepared,
        "disabled" => LegacyTaskMigrationStatus.Disabled,
        "restorePrepared" => LegacyTaskMigrationStatus.RestorePrepared,
        "restored" => LegacyTaskMigrationStatus.Restored,
        "failed" => LegacyTaskMigrationStatus.Failed,
        _ => throw new JsonException("Unknown legacy migration status."),
    };

    private static bool MatchesCandidate(
        DesktopLegacyTaskMigration migration,
        LegacyShutdownTaskCandidate candidate) =>
        string.Equals(
            migration.TaskPath,
            candidate.TaskPath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            migration.ActionFingerprint,
            candidate.ActionFingerprint,
            StringComparison.Ordinal)
        && migration.OriginalEnabled == candidate.WasEnabled;

    private static void ValidateCandidate(LegacyShutdownTaskCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        _ = new LegacyTaskMigrationRecord(
            "validation",
            candidate.TaskPath,
            candidate.ActionFingerprint,
            candidate.WasEnabled,
            LegacyTaskMigrationStatus.Prepared,
            DateTimeOffset.UnixEpoch);
    }

    private static void ValidateTaskPath(string taskPath)
    {
        _ = new LegacyTaskMigrationRecord(
            "validation",
            taskPath,
            new string('0', LegacyTaskMigrationRecord.ActionFingerprintLength),
            true,
            LegacyTaskMigrationStatus.Prepared,
            DateTimeOffset.UnixEpoch);
        if (!taskPath.StartsWith('\\') || taskPath.Contains('\0'))
        {
            throw new ArgumentException("The legacy task path is invalid.", nameof(taskPath));
        }
    }

    private static void ValidateMigrationId(string migrationId)
    {
        if (string.IsNullOrWhiteSpace(migrationId)
            || migrationId.Length > LegacyTaskMigrationRecord.MaximumMigrationIdLength
            || !string.Equals(migrationId, migrationId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The legacy migration ID is invalid.", nameof(migrationId));
        }
    }

    private static bool IsValidRecoveryToken(string? recoveryToken) =>
        recoveryToken is { Length: LegacyTaskMigrationRecord.RecoveryTokenLength }
        && recoveryToken.All(character => character is (>= '0' and <= '9')
            or (>= 'a' and <= 'f'));

    private sealed record LegacyMigrationListResponseDto(
        IReadOnlyList<LegacyMigrationResponseDto>? Migrations = null,
        string? NextCursor = null,
        int FailedCount = 0);

    private sealed record LegacyMigrationMutationResponseDto(
        bool Accepted,
        LegacyMigrationResponseDto? Migration = null,
        string? Error = null);

    private sealed record LegacyMigrationLookupResponseDto(
        bool Found,
        LegacyMigrationResponseDto? Migration = null,
        string? RecoveryToken = null);

    private sealed record LegacyMigrationResponseDto(
        string MigrationId,
        string TaskPath,
        string ActionFingerprint,
        bool OriginalEnabled,
        string Status,
        DateTimeOffset PreparedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        bool DisabledStateVerified = false);
}
