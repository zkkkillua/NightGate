using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NightGate.Core;

namespace NightGate.Service;

public sealed class SqliteNightGateRepository :
    INightStateRepository,
    IProgressRepository,
    IHistoryRepository,
    IBrowserEventRepository,
    IProcessPersistenceRepository,
    IOnboardingRepository,
    IChromeProtectionHealthRepository,
    IRuleSettingsRepository,
    INightSelfReportRepository,
    INoticeClaimRepository,
    ILegacyTaskMigrationRepository
{
    private const int SchemaVersion = 3;
    private const string DegradationCode = "storage-unavailable";
    private const int MaximumPersistedJsonBytes = 256 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly AsyncKeyedGate<string> NoticeClaimGate =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _databasePath;

    public SqliteNightGateRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public static string GetProductionDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NightGate",
        "state.db");

    public ValueTask<StorageResult<OnboardingState>> ReadOnboardingAsync(
        CancellationToken cancellationToken = default) =>
        ReadRequiredSingletonAsync(
            "onboarding_state",
            OnboardingState.Initial,
            cancellationToken);

    public async ValueTask<StorageWriteResult> SaveOnboardingAsync(
        OnboardingState state,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            string json = Serialize(state);
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            OnboardingState current;
            long currentVersion;
            await using (SqliteCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText =
                    "SELECT length(CAST(json AS BLOB)), json, row_version FROM onboarding_state WHERE singleton_id = 1;";
                await using SqliteDataReader reader = await read
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The onboarding singleton is missing.");
                }

                current = Deserialize<OnboardingState>(
                    ReadBoundedJson<OnboardingState>(reader, 1, 0));
                currentVersion = ReadSingletonRowVersion(reader, 2);
            }

            if (expectedVersion is { } expected && expected != currentVersion)
            {
                transaction.Rollback();
                return StorageWriteResult.Conflict;
            }

            if (state.CompletedStep < current.CompletedStep
                || state.CompletedStep > current.CompletedStep + 1
                || state.IPhoneConfirmedThroughStep < current.IPhoneConfirmedThroughStep)
            {
                transaction.Rollback();
                return DegradedWrite();
            }

            bool written = await TryWriteSingletonAsync(
                    connection,
                    transaction,
                    "onboarding_state",
                    json,
                    expectedVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!written)
            {
                transaction.Rollback();
                return StorageWriteResult.Conflict;
            }

            transaction.Commit();
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public ValueTask<StorageResult<RuleSettingsState>> ReadRuleSettingsAsync(
        CancellationToken cancellationToken = default) =>
        ReadRequiredSingletonAsync(
            "rule_settings",
            RuleSettingsState.Initial,
            cancellationToken);

    public ValueTask<StorageWriteResult> SaveRuleSettingsAsync(
        RuleSettingsState state,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default) =>
        SaveSingletonAsync("rule_settings", state, expectedVersion, cancellationToken);

    public async ValueTask<StorageResult<ChromeProtectionHealth?>>
        ReadChromeProtectionHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT length(CAST(json AS BLOB)), json, row_version FROM chrome_protection_health WHERE singleton_id = 1;";
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new(StorageMode.Success, null, Version: 0);
            }

            ChromeProtectionHealth health = Deserialize<ChromeProtectionHealth>(
                ReadBoundedJson<ChromeProtectionHealth>(reader, 1, 0));
            return new(
                StorageMode.Success,
                health,
                Version: ReadSingletonRowVersion(reader, 2));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(StorageMode.Degraded, null, DegradationCode);
        }
    }

    public ValueTask<StorageWriteResult> SaveChromeProtectionHealthAsync(
        ChromeProtectionHealth health,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default) =>
        SaveSingletonAsync(
            "chrome_protection_health",
            health,
            expectedVersion,
            cancellationToken);

    public async ValueTask<StorageResult<NightSelfReport?>> ReadSelfReportAsync(
        DateOnly nightDate,
        CancellationToken cancellationToken = default)
    {
        if (nightDate == default)
        {
            throw new ArgumentOutOfRangeException(nameof(nightDate));
        }

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT night_date, updated_utc, length(CAST(json AS BLOB)), json
                FROM night_self_reports
                WHERE night_date = $nightDate;
                """;
            command.Parameters.AddWithValue("$nightDate", ToStorageNightDate(nightDate));
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new(StorageMode.Success, null);
            }

            NightSelfReport report = Deserialize<NightSelfReport>(
                ReadBoundedJson<NightSelfReport>(reader, 3, 2));
            ValidateSelfReportMetadata(report, reader.GetString(0), reader.GetString(1));
            return new(StorageMode.Success, report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(StorageMode.Degraded, null, DegradationCode);
        }
    }

    public async ValueTask<StorageWriteResult> SaveSelfReportAsync(
        NightSelfReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO night_self_reports(night_date, updated_utc, json)
                VALUES ($nightDate, $updatedUtc, $json)
                ON CONFLICT(night_date) DO UPDATE SET
                    updated_utc = excluded.updated_utc,
                    json = excluded.json;
                """;
            command.Parameters.AddWithValue("$nightDate", ToStorageNightDate(report.NightDate));
            command.Parameters.AddWithValue("$updatedUtc", ToStorageTimestamp(report.UpdatedAtUtc));
            command.Parameters.AddWithValue("$json", Serialize(report));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
                ? StorageWriteResult.Success
                : DegradedWrite();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<StorageResult<bool>> TryClaimNoticeAsync(
        NoticeClaim claim,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        using IDisposable claimLease = await NoticeClaimGate
            .EnterAsync(_databasePath, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            PersistedDomainValidator.Validate(claim);
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO notice_claims(night_date, notice_kind, claimed_utc)
                VALUES ($nightDate, $noticeKind, $claimedUtc);
                """;
            command.Parameters.AddWithValue("$nightDate", ToStorageNightDate(claim.NightDate));
            command.Parameters.AddWithValue("$noticeKind", (int)claim.Kind);
            command.Parameters.AddWithValue("$claimedUtc", ToStorageTimestamp(claim.ClaimedAtUtc));
            bool won = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            if (!won)
            {
                await ValidateExistingNoticeClaimAsync(connection, claim, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new(StorageMode.Success, won);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(StorageMode.Degraded, false, DegradationCode);
        }
    }

    public async ValueTask<StorageWriteResult> PurgeNoticeClaimsOlderThanAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM notice_claims WHERE claimed_utc < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", ToStorageTimestamp(cutoffUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<StorageResult<LegacyTaskMigrationRecord?>>
        ReadLegacyTaskMigrationAsync(
            string migrationId,
            CancellationToken cancellationToken = default)
    {
        ValidateMigrationIdForLookup(migrationId);
        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            LegacyTaskMigrationRecord? record = await ReadLegacyTaskMigrationCoreAsync(
                    connection,
                    null,
                    migrationId,
                    cancellationToken)
                .ConfigureAwait(false);
            return new(StorageMode.Success, record);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(StorageMode.Degraded, null, DegradationCode);
        }
    }

    public async ValueTask<StorageResult<IReadOnlyList<LegacyTaskMigrationRecord>>>
        ReadLegacyTaskMigrationsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT migration_id, task_path, status, prepared_utc,
                       length(CAST(json AS BLOB)), json
                FROM legacy_task_migrations
                ORDER BY task_path COLLATE NOCASE, migration_id;
                """;
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            List<LegacyTaskMigrationRecord> records = [];
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                LegacyTaskMigrationRecord record = Deserialize<LegacyTaskMigrationRecord>(
                    ReadBoundedJson<LegacyTaskMigrationRecord>(reader, 5, 4));
                ValidateMigrationMetadata(
                    record,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3));
                records.Add(record);
            }

            return new(StorageMode.Success, records);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(
                StorageMode.Degraded,
                Array.Empty<LegacyTaskMigrationRecord>(),
                DegradationCode);
        }
    }

    public async ValueTask<StorageWriteResult> SaveLegacyTaskMigrationAsync(
        LegacyTaskMigrationRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            string json = Serialize(record);
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            LegacyTaskMigrationRecord? current = await ReadLegacyTaskMigrationCoreAsync(
                    connection,
                    transaction,
                    record.MigrationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                if (record.Status != LegacyTaskMigrationStatus.Prepared)
                {
                    transaction.Rollback();
                    return DegradedWrite();
                }

                await using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO legacy_task_migrations(
                        migration_id, task_path, status, prepared_utc, json)
                    VALUES ($migrationId, $taskPath, $status, $preparedUtc, $json);
                    """;
                AddMigrationParameters(insert, record, json);
                if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    transaction.Rollback();
                    return DegradedWrite();
                }
            }
            else if (current != record)
            {
                if (!HasSameMigrationIdentity(current, record)
                    || !IsLegalMigrationUpdate(current, record))
                {
                    transaction.Rollback();
                    return DegradedWrite();
                }

                await using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE legacy_task_migrations
                    SET status = $status, json = $json
                    WHERE migration_id = $migrationId;
                    """;
                update.Parameters.AddWithValue("$status", (int)record.Status);
                update.Parameters.AddWithValue("$json", json);
                update.Parameters.AddWithValue("$migrationId", record.MigrationId);
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    transaction.Rollback();
                    return DegradedWrite();
                }
            }

            transaction.Commit();
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<ProcessPersistenceLoadResult> LoadProcessPersistenceAsync(
        ProcessPersistenceSlot slot,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(slot))
        {
            return new(ProcessPersistenceLoadStatus.Corrupt, null);
        }

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            return await ReadProcessPersistenceAsync(
                    connection,
                    transaction: null,
                    slot,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(ProcessPersistenceLoadStatus.Unavailable, null);
        }
    }

    public async ValueTask<ProcessPersistenceSaveResult>
        CompareExchangeProcessPersistenceAsync(
            ProcessPersistenceSlot slot,
            long? expectedVersion,
            ProcessPersistenceRecord replacement,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!IsValidProcessPersistenceReplacement(slot, expectedVersion, replacement))
        {
            return new(ProcessPersistenceSaveStatus.Corrupt, null);
        }

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            ProcessPersistenceLoadResult current = await ReadProcessPersistenceAsync(
                    connection,
                    transaction,
                    slot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.Status == ProcessPersistenceLoadStatus.Corrupt)
            {
                transaction.Rollback();
                return new(ProcessPersistenceSaveStatus.Corrupt, null);
            }

            if (current.Status == ProcessPersistenceLoadStatus.Unavailable)
            {
                transaction.Rollback();
                return new(ProcessPersistenceSaveStatus.Unavailable, null);
            }

            bool expectedMatches = expectedVersion is null
                ? current.Status == ProcessPersistenceLoadStatus.Missing
                : current.Status == ProcessPersistenceLoadStatus.Found
                    && current.Record!.Version == expectedVersion.Value;
            if (!expectedMatches)
            {
                transaction.Rollback();
                return new(ProcessPersistenceSaveStatus.Conflict, current.Record);
            }

            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = current.Status == ProcessPersistenceLoadStatus.Missing
                    ? """
                        INSERT INTO process_persistence(
                            slot, schema_version, record_version, payload_json)
                        VALUES ($slot, $schemaVersion, $recordVersion, $payloadJson);
                        """
                    : """
                        UPDATE process_persistence
                        SET schema_version = $schemaVersion,
                            record_version = $recordVersion,
                            payload_json = $payloadJson
                        WHERE slot = $slot AND record_version = $expectedVersion;
                        """;
                command.Parameters.AddWithValue(
                    "$slot",
                    ProcessPersistenceLimits.GetSlotToken(slot));
                command.Parameters.AddWithValue("$schemaVersion", replacement.SchemaVersion);
                command.Parameters.AddWithValue("$recordVersion", replacement.Version);
                command.Parameters.AddWithValue("$payloadJson", replacement.PayloadJson);
                if (expectedVersion is not null)
                {
                    command.Parameters.AddWithValue("$expectedVersion", expectedVersion.Value);
                }

                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    transaction.Rollback();
                    return new(ProcessPersistenceSaveStatus.Unavailable, null);
                }
            }

            ProcessPersistenceLoadResult committed = await ReadProcessPersistenceAsync(
                    connection,
                    transaction,
                    slot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (committed.Status != ProcessPersistenceLoadStatus.Found
                || committed.Record != replacement)
            {
                transaction.Rollback();
                return new(ProcessPersistenceSaveStatus.Corrupt, committed.Record);
            }

            transaction.Commit();
            return new(ProcessPersistenceSaveStatus.Saved, committed.Record);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(ProcessPersistenceSaveStatus.Unavailable, null);
        }
    }

    public async ValueTask<StorageResult<NightState?>> ReadActiveStateAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT length(CAST(json AS BLOB)), json, row_version FROM active_state WHERE singleton_id = 1;";
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new(StorageMode.Success, null);
            }

            NightState state = Deserialize<NightState>(
                ReadBoundedJson<NightState>(reader, 1, 0));
            return new(StorageMode.Success, state, Version: ReadSingletonRowVersion(reader, 2));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(StorageMode.Degraded, null, DegradationCode);
        }
    }

    public async ValueTask<StorageWriteResult> SaveActiveStateWithEventAsync(
        NightState state,
        NightEvent nightEvent,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(nightEvent);

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            bool stateWritten = await TryWriteSingletonAsync(
                connection,
                transaction,
                "active_state",
                Serialize(state),
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
            if (!stateWritten)
            {
                transaction.Rollback();
                return StorageWriteResult.Conflict;
            }

            await InsertEventAsync(connection, transaction, nightEvent, cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<StorageWriteResult> CloseActiveStateWithOutcomeAndEventAsync(
        NightState closedState,
        NightOutcome outcome,
        NightEvent nightEvent,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(closedState);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(nightEvent);

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            bool stateWritten = await TryWriteSingletonAsync(
                connection,
                transaction,
                "active_state",
                Serialize(closedState),
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
            if (!stateWritten)
            {
                transaction.Rollback();
                return StorageWriteResult.Conflict;
            }

            await UpsertOutcomeAsync(connection, transaction, outcome, cancellationToken)
                .ConfigureAwait(false);
            await InsertEventAsync(connection, transaction, nightEvent, cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<StorageWriteResult> SaveActiveStateProgressWithEventAsync(
        NightState state,
        ProgressState progress,
        NightEvent nightEvent,
        long? expectedStateVersion = null,
        long? expectedProgressVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(nightEvent);

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            bool stateWritten = await TryWriteSingletonAsync(
                connection,
                transaction,
                "active_state",
                Serialize(state),
                expectedStateVersion,
                cancellationToken).ConfigureAwait(false);
            bool progressWritten = stateWritten && await TryWriteSingletonAsync(
                connection,
                transaction,
                "progress",
                Serialize(progress),
                expectedProgressVersion,
                cancellationToken).ConfigureAwait(false);
            if (!progressWritten)
            {
                transaction.Rollback();
                return StorageWriteResult.Conflict;
            }

            await InsertEventAsync(connection, transaction, nightEvent, cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<StorageResult<ProgressState>> ReadProgressAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT length(CAST(json AS BLOB)), json, row_version FROM progress WHERE singleton_id = 1;";
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("Progress singleton is missing.");
            }

            ProgressState progress = Deserialize<ProgressState>(
                ReadBoundedJson<ProgressState>(reader, 1, 0));
            return new(StorageMode.Success, progress, Version: ReadSingletonRowVersion(reader, 2));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(StorageMode.Degraded, ProgressState.Initial, DegradationCode);
        }
    }

    public async ValueTask<StorageWriteResult> SaveProgressAsync(
        ProgressState progress,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            bool progressWritten = await TryWriteSingletonAsync(
                connection,
                transaction,
                "progress",
                Serialize(progress),
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
            if (!progressWritten)
            {
                transaction.Rollback();
                return StorageWriteResult.Conflict;
            }

            transaction.Commit();
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestOutcomesAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT night_id, closed_utc, length(CAST(json AS BLOB)), json FROM night_outcomes ORDER BY closed_utc DESC LIMIT $count;";
            command.Parameters.AddWithValue("$count", count);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            List<NightOutcome> outcomes = [];
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                NightOutcome outcome = Deserialize<NightOutcome>(
                    ReadBoundedJson<NightOutcome>(reader, 3, 2));
                ValidateOutcomeMetadata(outcome, reader.GetString(0), reader.GetString(1));
                outcomes.Add(outcome);
            }

            return new(StorageMode.Success, outcomes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(StorageMode.Degraded, Array.Empty<NightOutcome>(), DegradationCode);
        }
    }

    public async ValueTask<StorageWriteResult> SaveOutcomeAsync(
        NightOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await UpsertOutcomeAsync(connection, null, outcome, cancellationToken)
                .ConfigureAwait(false);
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestEligibleOutcomesAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT night_id, closed_utc, length(CAST(json AS BLOB)), json FROM night_outcomes ORDER BY closed_utc DESC;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            List<NightOutcome> outcomes = [];
            while (outcomes.Count < count
                && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                NightOutcome outcome = Deserialize<NightOutcome>(
                    ReadBoundedJson<NightOutcome>(reader, 3, 2));
                ValidateOutcomeMetadata(outcome, reader.GetString(0), reader.GetString(1));
                if (outcome.IsEligible)
                {
                    outcomes.Add(outcome);
                }
            }

            return new(StorageMode.Success, outcomes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(StorageMode.Degraded, Array.Empty<NightOutcome>(), DegradationCode);
        }
    }

    public async ValueTask<StorageWriteResult> RecordEventAsync(
        NightEvent nightEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nightEvent);

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await InsertEventAsync(connection, null, nightEvent, cancellationToken)
                .ConfigureAwait(false);
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<StorageWriteResult> RecordBrowserEventAsync(
        BrowserPrivacyEvent browserEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browserEvent);

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await InsertBrowserEventAsync(connection, null, browserEvent, cancellationToken)
                .ConfigureAwait(false);
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<StorageWriteResult>
        SaveLateNewEntertainmentWithBrowserEventAsync(
            NightState state,
            BrowserPrivacyEvent browserEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(browserEvent);

        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            bool stateWritten = await TryWriteSingletonAsync(
                connection,
                transaction,
                "active_state",
                Serialize(state),
                expectedVersion,
                cancellationToken).ConfigureAwait(false);
            if (!stateWritten)
            {
                transaction.Rollback();
                return StorageWriteResult.Conflict;
            }

            await InsertBrowserEventAsync(
                    connection,
                    transaction,
                    browserEvent,
                    cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<StorageWriteResult> PurgeEventsOlderThanAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM raw_events WHERE occurred_utc < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", ToStorageTimestamp(cutoffUtc));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    public async ValueTask<StorageWriteResult> ClearHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            DateOnly? activeNightDate = null;
            await using (SqliteCommand activeState = connection.CreateCommand())
            {
                activeState.Transaction = transaction;
                activeState.CommandText =
                    "SELECT length(CAST(json AS BLOB)), json FROM active_state WHERE singleton_id = 1;";
                await using SqliteDataReader reader = await activeState
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    NightState state = Deserialize<NightState>(
                        ReadBoundedJson<NightState>(reader, 1, 0));
                    if (!state.IsClosed)
                    {
                        activeNightDate = state.NightDate;
                    }
                }
            }

            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = activeNightDate is not null
                    ? """
                        DELETE FROM raw_events;
                        DELETE FROM night_outcomes;
                        DELETE FROM night_self_reports;
                        DELETE FROM notice_claims WHERE night_date < $activeNightDate;
                        """
                    : """
                        DELETE FROM raw_events;
                        DELETE FROM night_outcomes;
                        DELETE FROM night_self_reports;
                        DELETE FROM notice_claims;
                        """;
                if (activeNightDate is { } retainedNightDate)
                {
                    command.Parameters.AddWithValue(
                        "$activeNightDate",
                        ToStorageNightDate(retainedNightDate));
                }

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction.Commit();
            return StorageWriteResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    private async Task<SqliteConnection> OpenInitializedAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_databasePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException("The database path must include a directory.");
        }

        Directory.CreateDirectory(directory);
        EnsureExistingDatabaseIsWritable(_databasePath);
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        };
        SqliteConnection connection = new(builder.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void EnsureExistingDatabaseIsWritable(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        using var writableHandle = File.OpenHandle(
            databasePath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.RandomAccess);
    }

    private static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        long observedVersion = await ReadSchemaVersionAsync(
                connection,
                null,
                cancellationToken)
            .ConfigureAwait(false);
        if (observedVersion == SchemaVersion)
        {
            return;
        }

        if (observedVersion is < 0 or > SchemaVersion)
        {
            throw new InvalidDataException($"Unsupported schema version {observedVersion}.");
        }

        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        long version = await ReadSchemaVersionAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (version is < 0 or > SchemaVersion)
        {
            throw new InvalidDataException($"Unsupported schema version {version}.");
        }

        if (version == SchemaVersion)
        {
            transaction.Commit();
            return;
        }

        await using (SqliteCommand schemaCommand = connection.CreateCommand())
        {
            schemaCommand.Transaction = transaction;
            schemaCommand.CommandText = """
                CREATE TABLE IF NOT EXISTS active_state (
                    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                    json TEXT NOT NULL,
                    row_version INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS progress (
                    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                    json TEXT NOT NULL,
                    row_version INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS night_outcomes (
                    night_id TEXT PRIMARY KEY,
                    closed_utc TEXT NOT NULL,
                    json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS raw_events (
                    event_id TEXT PRIMARY KEY,
                    occurred_utc TEXT NOT NULL,
                    json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS process_persistence (
                    slot TEXT PRIMARY KEY,
                    schema_version INTEGER NOT NULL,
                    record_version INTEGER NOT NULL,
                    payload_json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS onboarding_state (
                    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                    json TEXT NOT NULL,
                    row_version INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS chrome_protection_health (
                    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                    json TEXT NOT NULL,
                    row_version INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS rule_settings (
                    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                    json TEXT NOT NULL,
                    row_version INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS night_self_reports (
                    night_date TEXT PRIMARY KEY,
                    updated_utc TEXT NOT NULL,
                    json TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS notice_claims (
                    night_date TEXT NOT NULL,
                    notice_kind INTEGER NOT NULL,
                    claimed_utc TEXT NOT NULL,
                    PRIMARY KEY(night_date, notice_kind)
                );
                CREATE TABLE IF NOT EXISTS legacy_task_migrations (
                    migration_id TEXT PRIMARY KEY,
                    task_path TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    prepared_utc TEXT NOT NULL,
                    json TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_night_outcomes_closed_utc ON night_outcomes(closed_utc DESC);
                CREATE INDEX IF NOT EXISTS ix_raw_events_occurred_utc ON raw_events(occurred_utc);
                CREATE INDEX IF NOT EXISTS ix_legacy_task_migrations_task_path
                    ON legacy_task_migrations(task_path COLLATE NOCASE, migration_id);
                """;
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureRowVersionColumnsAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        await UpsertSingletonIfMissingAsync(
            connection,
            transaction,
            "progress",
            Serialize(ProgressState.Initial),
            cancellationToken).ConfigureAwait(false);
        await UpsertSingletonIfMissingAsync(
            connection,
            transaction,
            "onboarding_state",
            Serialize(OnboardingState.Initial),
            cancellationToken).ConfigureAwait(false);
        await UpsertSingletonIfMissingAsync(
            connection,
            transaction,
            "rule_settings",
            Serialize(RuleSettingsState.Initial),
            cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand setVersion = connection.CreateCommand())
        {
            setVersion.Transaction = transaction;
            setVersion.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            await setVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private static async Task<long> ReadSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static bool IsValidProcessPersistenceReplacement(
        ProcessPersistenceSlot requestedSlot,
        long? expectedVersion,
        ProcessPersistenceRecord replacement)
    {
        if (!Enum.IsDefined(requestedSlot)
            || replacement.Slot != requestedSlot
            || replacement.SchemaVersion != ProcessPersistenceLimits.CurrentSchemaVersion
            || replacement.Version < 1
            || expectedVersion is < 1
            || expectedVersion == long.MaxValue
            || replacement.Version != (expectedVersion ?? 0) + 1)
        {
            return false;
        }

        return ProcessPersistenceLimits.IsValidPayload(
            replacement.PayloadJson,
            replacement.SchemaVersion);
    }

    private static async ValueTask<ProcessPersistenceLoadResult>
        ReadProcessPersistenceAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            ProcessPersistenceSlot requestedSlot,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT slot, schema_version, record_version, payload_json
            FROM process_persistence
            WHERE slot = $slot;
            """;
        command.Parameters.AddWithValue(
            "$slot",
            ProcessPersistenceLimits.GetSlotToken(requestedSlot));
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new(ProcessPersistenceLoadStatus.Missing, null);
        }

        if (reader.FieldCount != 4
            || reader.IsDBNull(0)
            || reader.IsDBNull(1)
            || reader.IsDBNull(2)
            || reader.IsDBNull(3)
            || !ProcessPersistenceLimits.TryParseSlotToken(
                reader.GetString(0),
                out ProcessPersistenceSlot storedSlot)
            || storedSlot != requestedSlot
            || reader.GetValue(1) is not long schemaVersionValue
            || schemaVersionValue is < int.MinValue or > int.MaxValue
            || reader.GetValue(2) is not long recordVersion
            || recordVersion is < 1 or long.MaxValue)
        {
            return new(ProcessPersistenceLoadStatus.Corrupt, null);
        }

        int schemaVersion = (int)schemaVersionValue;
        if (reader.GetValue(3) is not string payloadJson)
        {
            return new(ProcessPersistenceLoadStatus.Corrupt, null);
        }
        if (!ProcessPersistenceLimits.IsValidPayload(payloadJson, schemaVersion))
        {
            return new(ProcessPersistenceLoadStatus.Corrupt, null);
        }

        return new(
            ProcessPersistenceLoadStatus.Found,
            new(storedSlot, schemaVersion, recordVersion, payloadJson));
    }

    private static async Task<bool> TryWriteSingletonAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string json,
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (expectedVersion is < 0 or >= long.MaxValue)
        {
            throw new InvalidDataException("The expected singleton row version is invalid.");
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        if (expectedVersion is null)
        {
            command.CommandText = $"""
                INSERT INTO {table} (singleton_id, json, row_version) VALUES (1, $json, 1)
                ON CONFLICT(singleton_id) DO UPDATE SET
                    json = excluded.json,
                    row_version = {table}.row_version + 1
                WHERE {table}.row_version >= 0
                  AND {table}.row_version < $maximumWritableVersion
                  AND typeof({table}.row_version) = 'integer';
                """;
        }
        else
        {
            command.CommandText = $"""
                UPDATE {table}
                SET json = $json, row_version = row_version + 1
                WHERE singleton_id = 1
                  AND row_version = $expectedVersion
                  AND row_version >= 0
                  AND row_version < $maximumWritableVersion
                  AND typeof(row_version) = 'integer';
                """;
            command.Parameters.AddWithValue("$expectedVersion", expectedVersion.Value);
        }

        command.Parameters.AddWithValue("$maximumWritableVersion", long.MaxValue - 1);
        command.Parameters.AddWithValue("$json", json);
        int affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected == 1)
        {
            return true;
        }

        if (expectedVersion is null)
        {
            _ = await ReadSingletonRowVersionForWriteAsync(
                    connection,
                    transaction,
                    table,
                    cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidDataException("A blind singleton write did not affect its row.");
        }

        if (expectedVersion != 0)
        {
            _ = await ReadSingletonRowVersionForWriteAsync(
                    connection,
                    transaction,
                    table,
                    cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"""
            INSERT INTO {table} (singleton_id, json, row_version)
            VALUES (1, $json, 1)
            ON CONFLICT(singleton_id) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$json", json);
        if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
        {
            return true;
        }

        _ = await ReadSingletonRowVersionForWriteAsync(
                connection,
                transaction,
                table,
                cancellationToken)
            .ConfigureAwait(false);
        return false;
    }

    private static async Task UpsertSingletonIfMissingAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string json,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT OR IGNORE INTO {table} (singleton_id, json, row_version) VALUES (1, $json, 0);";
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureRowVersionColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await HasRowVersionColumnAsync(
                connection, transaction, "active_state", cancellationToken).ConfigureAwait(false)
            && await HasRowVersionColumnAsync(
                connection, transaction, "progress", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        foreach (string table in new[] { "active_state", "progress" })
        {
            if (await HasRowVersionColumnAsync(
                    connection, transaction, table, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            await using SqliteCommand alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN row_version INTEGER NOT NULL DEFAULT 0;";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<StorageResult<T>> ReadRequiredSingletonAsync<T>(
        string table,
        T degradedValue,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT length(CAST(json AS BLOB)), json, row_version FROM {table} WHERE singleton_id = 1;";
            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException($"The {table} singleton is missing.");
            }

            T value = Deserialize<T>(ReadBoundedJson<T>(reader, 1, 0));
            return new(StorageMode.Success, value, Version: ReadSingletonRowVersion(reader, 2));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(StorageMode.Degraded, degradedValue, DegradationCode);
        }
    }

    private async ValueTask<StorageWriteResult> SaveSingletonAsync<T>(
        string table,
        T value,
        long? expectedVersion,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            await using SqliteConnection connection = await OpenInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
            bool written = await TryWriteSingletonAsync(
                    connection,
                    null,
                    table,
                    Serialize(value),
                    expectedVersion,
                    cancellationToken)
                .ConfigureAwait(false);
            return written ? StorageWriteResult.Success : StorageWriteResult.Conflict;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return DegradedWrite();
        }
    }

    private static async Task<bool> HasRowVersionColumnAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand columns = connection.CreateCommand();
        columns.Transaction = transaction;
        columns.CommandText = $"PRAGMA table_info({table});";
        await using SqliteDataReader reader = await columns
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), "row_version", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask<long?> ReadSingletonRowVersionForWriteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT row_version FROM {table} WHERE singleton_id = 1;";
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        long rowVersion = ReadSingletonRowVersion(reader, 0);
        if (rowVersion >= long.MaxValue - 1)
        {
            throw new InvalidDataException("The singleton row version is exhausted.");
        }

        return rowVersion;
    }

    private static long ReadSingletonRowVersion(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not long rowVersion
            || rowVersion is < 0 or >= long.MaxValue)
        {
            throw new InvalidDataException("The singleton row version is invalid.");
        }

        return rowVersion;
    }

    private static async Task UpsertOutcomeAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        NightOutcome outcome,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO night_outcomes (night_id, closed_utc, json)
            VALUES ($nightId, $closedUtc, $json)
            ON CONFLICT(night_id) DO UPDATE SET
                closed_utc = excluded.closed_utc,
                json = excluded.json;
            """;
        command.Parameters.AddWithValue("$nightId", outcome.NightId.ToString("D"));
        command.Parameters.AddWithValue("$closedUtc", ToStorageTimestamp(outcome.ClosedAtUtc));
        command.Parameters.AddWithValue("$json", Serialize(outcome));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        NightEvent nightEvent,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO raw_events (event_id, occurred_utc, json)
            VALUES ($eventId, $occurredUtc, $json);
            """;
        command.Parameters.AddWithValue("$eventId", nightEvent.EventId.ToString("D"));
        command.Parameters.AddWithValue("$occurredUtc", ToStorageTimestamp(nightEvent.OccurredAtUtc));
        command.Parameters.AddWithValue("$json", Serialize(nightEvent));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertBrowserEventAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        BrowserPrivacyEvent browserEvent,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO raw_events (event_id, occurred_utc, json)
            VALUES ($eventId, $occurredUtc, $json);
            """;
        command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue(
            "$occurredUtc",
            ToStorageTimestamp(browserEvent.TimestampUtc));
        command.Parameters.AddWithValue(
            "$json",
            JsonSerializer.Serialize(
                new BrowserEventPayload(
                    browserEvent.TimestampUtc.ToUniversalTime()
                        .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    BrowserEventTypeToken(browserEvent.EventType),
                    BrowserSiteCategoryToken(browserEvent.Category)),
                SerializerOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BrowserEventTypeToken(BrowserEventType eventType) => eventType switch
    {
        BrowserEventType.MediaPlaying => "mediaPlaying",
        BrowserEventType.MediaPaused => "mediaPaused",
        BrowserEventType.MediaEnded => "mediaEnded",
        BrowserEventType.NavigationBlocked => "navigationBlocked",
        _ => throw new ArgumentOutOfRangeException(nameof(eventType)),
    };

    private static string BrowserSiteCategoryToken(BrowserSiteCategory category) => category switch
    {
        BrowserSiteCategory.Gaming => "gaming",
        BrowserSiteCategory.Video => "video",
        BrowserSiteCategory.Social => "social",
        BrowserSiteCategory.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    private static StorageWriteResult DegradedWrite() => new(StorageMode.Degraded, DegradationCode);

    private static string Serialize<T>(T value)
        where T : class
    {
        byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(
            PersistedDomainValidator.Validate(value),
            SerializerOptions);
        if (utf8.Length > MaximumJsonBytesFor<T>())
        {
            throw new InvalidDataException($"Stored {typeof(T).Name} exceeds its JSON byte limit.");
        }

        return Encoding.UTF8.GetString(utf8);
    }

    private static T Deserialize<T>(string json)
        where T : class
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumJsonBytesFor<T>())
        {
            throw new InvalidDataException($"Stored {typeof(T).Name} exceeds its JSON byte limit.");
        }

        PersistedDomainValidator.ValidateSerializedShape<T>(json);
        T value = JsonSerializer.Deserialize<T>(json, SerializerOptions)
            ?? throw new InvalidDataException($"Stored {typeof(T).Name} was null.");
        return PersistedDomainValidator.Validate(value);
    }

    private static string ReadBoundedJson<T>(
        SqliteDataReader reader,
        int jsonOrdinal,
        int byteLengthOrdinal)
        where T : class
    {
        int maximumBytes = MaximumJsonBytesFor<T>();
        if (reader.GetValue(byteLengthOrdinal) is not long storedByteLength
            || storedByteLength is < 1
            || storedByteLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"Stored {typeof(T).Name} has an invalid JSON byte length.");
        }

        if (reader.GetValue(jsonOrdinal) is not string json
            || Encoding.UTF8.GetByteCount(json) != storedByteLength)
        {
            throw new InvalidDataException(
                $"Stored {typeof(T).Name} JSON does not match its SQLite byte length.");
        }

        return json;
    }

    private static int MaximumJsonBytesFor<T>()
        where T : class => typeof(T) == typeof(RuleSettingsState)
            ? RuleSettingsState.MaximumUtf8PayloadBytes
            : MaximumPersistedJsonBytes;

    private static void ValidateOutcomeMetadata(
        NightOutcome outcome,
        string storedNightId,
        string storedClosedUtc)
    {
        if (!Guid.TryParseExact(storedNightId, "D", out Guid nightId)
            || nightId != outcome.NightId
            || !DateTimeOffset.TryParseExact(
                storedClosedUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset closedAtUtc)
            || closedAtUtc != outcome.ClosedAtUtc)
        {
            throw new InvalidDataException("NightOutcome metadata does not match its persisted value.");
        }
    }

    private static void ValidateSelfReportMetadata(
        NightSelfReport report,
        string storedNightDate,
        string storedUpdatedUtc)
    {
        if (!DateOnly.TryParseExact(
                storedNightDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly nightDate)
            || nightDate != report.NightDate
            || !DateTimeOffset.TryParseExact(
                storedUpdatedUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset updatedUtc)
            || updatedUtc != report.UpdatedAtUtc)
        {
            throw new InvalidDataException(
                "NightSelfReport metadata does not match its persisted value.");
        }
    }

    private static async ValueTask ValidateExistingNoticeClaimAsync(
        SqliteConnection connection,
        NoticeClaim requestedClaim,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT night_date, notice_kind, claimed_utc
            FROM notice_claims
            WHERE night_date = $nightDate AND notice_kind = $noticeKind;
            """;
        command.Parameters.AddWithValue(
            "$nightDate",
            ToStorageNightDate(requestedClaim.NightDate));
        command.Parameters.AddWithValue("$noticeKind", (int)requestedClaim.Kind);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.FieldCount != 3
            || reader.GetValue(0) is not string storedNightDate
            || reader.GetValue(1) is not long storedKind
            || reader.GetValue(2) is not string storedClaimedUtc
            || !string.Equals(
                storedNightDate,
                ToStorageNightDate(requestedClaim.NightDate),
                StringComparison.Ordinal)
            || storedKind != (int)requestedClaim.Kind
            || !DateTimeOffset.TryParseExact(
                storedClaimedUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset claimedAtUtc)
            || claimedAtUtc.Offset != TimeSpan.Zero
            || !string.Equals(
                storedClaimedUtc,
                ToStorageTimestamp(claimedAtUtc),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The existing notice claim row is malformed.");
        }

        _ = new NoticeClaim(requestedClaim.NightDate, requestedClaim.Kind, claimedAtUtc);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The existing notice claim is duplicated.");
        }
    }

    private static async ValueTask<LegacyTaskMigrationRecord?>
        ReadLegacyTaskMigrationCoreAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string migrationId,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT migration_id, task_path, status, prepared_utc,
                   length(CAST(json AS BLOB)), json
            FROM legacy_task_migrations
            WHERE migration_id = $migrationId;
            """;
        command.Parameters.AddWithValue("$migrationId", migrationId);
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        LegacyTaskMigrationRecord record = Deserialize<LegacyTaskMigrationRecord>(
            ReadBoundedJson<LegacyTaskMigrationRecord>(reader, 5, 4));
        ValidateMigrationMetadata(
            record,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3));
        return record;
    }

    private static void ValidateMigrationMetadata(
        LegacyTaskMigrationRecord record,
        string storedMigrationId,
        string storedTaskPath,
        int storedStatus,
        string storedPreparedUtc)
    {
        if (!string.Equals(storedMigrationId, record.MigrationId, StringComparison.Ordinal)
            || !string.Equals(storedTaskPath, record.TaskPath, StringComparison.Ordinal)
            || storedStatus != (int)record.Status
            || !DateTimeOffset.TryParseExact(
                storedPreparedUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset preparedUtc)
            || preparedUtc != record.PreparedAtUtc)
        {
            throw new InvalidDataException(
                "LegacyTaskMigration metadata does not match its persisted value.");
        }
    }

    private static void AddMigrationParameters(
        SqliteCommand command,
        LegacyTaskMigrationRecord record,
        string json)
    {
        command.Parameters.AddWithValue("$migrationId", record.MigrationId);
        command.Parameters.AddWithValue("$taskPath", record.TaskPath);
        command.Parameters.AddWithValue("$status", (int)record.Status);
        command.Parameters.AddWithValue("$preparedUtc", ToStorageTimestamp(record.PreparedAtUtc));
        command.Parameters.AddWithValue("$json", json);
    }

    private static bool HasSameMigrationIdentity(
        LegacyTaskMigrationRecord current,
        LegacyTaskMigrationRecord replacement) =>
        string.Equals(current.MigrationId, replacement.MigrationId, StringComparison.Ordinal)
        && string.Equals(current.TaskPath, replacement.TaskPath, StringComparison.Ordinal)
        && string.Equals(
            current.ActionFingerprint,
            replacement.ActionFingerprint,
            StringComparison.Ordinal)
        && current.OriginalEnabled == replacement.OriginalEnabled
        && current.PreparedAtUtc == replacement.PreparedAtUtc;

    private static bool IsLegalMigrationUpdate(
        LegacyTaskMigrationRecord current,
        LegacyTaskMigrationRecord replacement) =>
        // Direct Prepared/Disabled -> Restored mirrors the command handler only
        // for a 0.3.3 Desktop overlapping a service-first rolling upgrade. New
        // clients must persist RestorePrepared before the external side effect.
        (!current.DisabledStateVerified || replacement.DisabledStateVerified)
        && (current.Status == LegacyTaskMigrationStatus.Disabled
                && replacement.Status == LegacyTaskMigrationStatus.Disabled
                && !current.DisabledStateVerified
                && replacement.DisabledStateVerified
            || current.Status == LegacyTaskMigrationStatus.Failed
                && replacement.Status == LegacyTaskMigrationStatus.Disabled
                && !current.DisabledStateVerified
                && replacement.DisabledStateVerified
                && replacement.CompletedAtUtc == current.CompletedAtUtc
            || current.Status switch
            {
                LegacyTaskMigrationStatus.Prepared => replacement.Status is
                    LegacyTaskMigrationStatus.Disabled or
                    LegacyTaskMigrationStatus.RestorePrepared or
                    LegacyTaskMigrationStatus.Restored or
                    LegacyTaskMigrationStatus.Failed,
                LegacyTaskMigrationStatus.Disabled => replacement.Status is
                    LegacyTaskMigrationStatus.RestorePrepared or
                    LegacyTaskMigrationStatus.Restored or
                    LegacyTaskMigrationStatus.Failed,
                LegacyTaskMigrationStatus.RestorePrepared => replacement.Status is
                    LegacyTaskMigrationStatus.Restored or
                    LegacyTaskMigrationStatus.Failed,
                LegacyTaskMigrationStatus.Restored or
                    LegacyTaskMigrationStatus.Failed => false,
                _ => false,
            });

    private static void ValidateMigrationIdForLookup(string migrationId)
    {
        if (string.IsNullOrWhiteSpace(migrationId)
            || migrationId.Length > LegacyTaskMigrationRecord.MaximumMigrationIdLength
            || !string.Equals(migrationId, migrationId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The migration ID is invalid.", nameof(migrationId));
        }
    }

    private static string ToStorageTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string ToStorageNightDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new ExactNightPhaseJsonConverter());
        options.Converters.Add(new ExactOverrideKindJsonConverter());
        options.Converters.Add(new ExactNightEventKindJsonConverter());
        return options;
    }

    private sealed record BrowserEventPayload(
        string Timestamp,
        string EventType,
        string Category);
}
