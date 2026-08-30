using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class PersistenceRecordsV2Tests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 8, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Night = new(2026, 7, 7);

    [Fact]
    public async Task SelfReport_UpsertsAndPersistsAcrossRestart()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository first = new(database.Path);
        Assert.IsAssignableFrom<INightSelfReportRepository>(first);
        NightSelfReport initial = new(Night, true, null, Now);
        NightSelfReport updated = new(Night, false, true, Now.AddMinutes(1));

        Assert.Equal(StorageMode.Success, (await first.SaveSelfReportAsync(initial)).Mode);
        Assert.Equal(StorageMode.Success, (await first.SaveSelfReportAsync(updated)).Mode);

        SqliteNightGateRepository reopened = new(database.Path);
        StorageResult<NightSelfReport?> result = await reopened.ReadSelfReportAsync(Night);

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal(updated, result.Value);
        Assert.Null((await reopened.ReadSelfReportAsync(Night.AddDays(1))).Value);
    }

    [Fact]
    public async Task CorruptSelfReportJsonOrMetadata_ReturnsDegradedFailOpen()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveSelfReportAsync(new(Night, true, true, Now));
        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(
                connection,
                "UPDATE night_self_reports SET updated_utc = '2026-07-08T14:01:00.0000000+00:00';");
        }

        StorageResult<NightSelfReport?> result = await repository.ReadSelfReportAsync(Night);

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task NoticeClaim_SameNightAndKindHasOneWinnerAcrossRestart()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository first = new(database.Path);
        Assert.IsAssignableFrom<INoticeClaimRepository>(first);
        NoticeClaim claim = new(Night, NightNoticeKind.LastStart, Now);

        StorageResult<bool> won = await first.TryClaimNoticeAsync(claim);
        StorageResult<bool> repeated = await first.TryClaimNoticeAsync(
            new(Night, NightNoticeKind.LastStart, Now.AddSeconds(1)));
        StorageResult<bool> afterRestart = await new SqliteNightGateRepository(database.Path)
            .TryClaimNoticeAsync(new(Night, NightNoticeKind.LastStart, Now.AddSeconds(2)));
        StorageResult<bool> otherKind = await first.TryClaimNoticeAsync(
            new(Night, NightNoticeKind.Grace10, Now));
        StorageResult<bool> otherNight = await first.TryClaimNoticeAsync(
            new(Night.AddDays(1), NightNoticeKind.LastStart, Now));

        Assert.Equal(StorageMode.Success, won.Mode);
        Assert.True(won.Value);
        Assert.False(repeated.Value);
        Assert.False(afterRestart.Value);
        Assert.True(otherKind.Value);
        Assert.True(otherNight.Value);
    }

    [Fact]
    public async Task NoticeClaim_ConcurrentCallersHaveExactlyOneWinner()
    {
        using TempDatabase database = new();
        const int callerCount = 32;
        Assert.Equal(
            StorageMode.Success,
            (await new SqliteNightGateRepository(database.Path).ReadOnboardingAsync()).Mode);
        using Barrier barrier = new(callerCount);
        Task<StorageResult<bool>>[] calls = Enumerable.Range(0, callerCount)
            .Select(index => Task.Factory.StartNew(
                async () =>
                {
                    barrier.SignalAndWait();
                    return await new SqliteNightGateRepository(database.Path)
                        .TryClaimNoticeAsync(
                            new(Night, NightNoticeKind.IfThenPlan, Now.AddTicks(index)));
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap())
            .ToArray();

        StorageResult<bool>[] results = await Task.WhenAll(calls)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.Equal(StorageMode.Success, result.Mode));
        Assert.Single(results, result => result.Value);
    }

    [Fact]
    public async Task NoticeClaimPurge_DeletesOnlyClaimsStrictlyOlderThanUtcCutoff()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        DateTimeOffset cutoff = Now.AddDays(-90);
        DateOnly oldNight = Night.AddDays(-91);
        DateOnly boundaryNight = Night.AddDays(-90);
        await repository.TryClaimNoticeAsync(
            new(oldNight, NightNoticeKind.LastStart, cutoff.AddTicks(-1)));
        await repository.TryClaimNoticeAsync(
            new(boundaryNight, NightNoticeKind.LastStart, cutoff));

        StorageWriteResult purge = await repository
            .PurgeNoticeClaimsOlderThanAsync(cutoff);
        StorageResult<bool> oldCanBeClaimedAgain = await repository.TryClaimNoticeAsync(
            new(oldNight, NightNoticeKind.LastStart, Now));
        StorageResult<bool> boundaryStillClaimed = await repository.TryClaimNoticeAsync(
            new(boundaryNight, NightNoticeKind.LastStart, Now));

        Assert.Equal(StorageMode.Success, purge.Mode);
        Assert.True(oldCanBeClaimedAgain.Value);
        Assert.False(boundaryStillClaimed.Value);
    }

    [Fact]
    public async Task LegacyMigration_PersistsLegalTransitionAndListsByTaskPath()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.IsAssignableFrom<ILegacyTaskMigrationRepository>(repository);
        LegacyTaskMigrationRecord laterPath = Prepared(
            "migration-b",
            @"\Zeta\Shutdown",
            new string('b', 64),
            originalEnabled: false);
        LegacyTaskMigrationRecord prepared = Prepared(
            "migration-a",
            @"\Alpha\Shutdown",
            new string('a', 64),
            originalEnabled: true);
        LegacyTaskMigrationRecord disabled = new(
            prepared.MigrationId,
            prepared.TaskPath,
            prepared.ActionFingerprint,
            prepared.OriginalEnabled,
            LegacyTaskMigrationStatus.Disabled,
            prepared.PreparedAtUtc,
            Now.AddMinutes(1),
            DisabledStateVerified: true);
        LegacyTaskMigrationRecord restored = new(
            prepared.MigrationId,
            prepared.TaskPath,
            prepared.ActionFingerprint,
            prepared.OriginalEnabled,
            LegacyTaskMigrationStatus.Restored,
            prepared.PreparedAtUtc,
            Now.AddMinutes(2),
            DisabledStateVerified: true);

        Assert.Equal(StorageMode.Success, (await repository.SaveLegacyTaskMigrationAsync(laterPath)).Mode);
        Assert.Equal(StorageMode.Success, (await repository.SaveLegacyTaskMigrationAsync(prepared)).Mode);
        Assert.Equal(StorageMode.Success, (await repository.SaveLegacyTaskMigrationAsync(disabled)).Mode);
        Assert.Equal(StorageMode.Success, (await repository.SaveLegacyTaskMigrationAsync(restored)).Mode);

        SqliteNightGateRepository reopened = new(database.Path);
        StorageResult<LegacyTaskMigrationRecord?> read =
            await reopened.ReadLegacyTaskMigrationAsync(prepared.MigrationId);
        StorageResult<IReadOnlyList<LegacyTaskMigrationRecord>> list =
            await reopened.ReadLegacyTaskMigrationsAsync();

        Assert.Equal(restored, read.Value);
        Assert.Equal([restored, laterPath], list.Value);
    }

    [Fact]
    public async Task LegacyMigration_ReadsPreVerificationJsonAsUnverified()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadOnboardingAsync()).Mode);
        DateTimeOffset preparedAtUtc = Now.AddMinutes(-10);
        DateTimeOffset completedAtUtc = Now.AddMinutes(-5);
        string fingerprint = new('a', 64);
        string json = JsonSerializer.Serialize(new
        {
            migrationId = "legacy-disabled",
            taskPath = @"\Alpha\Shutdown",
            actionFingerprint = fingerprint,
            originalEnabled = true,
            // 0.3.3 stored the enum as integer 1 and had no verification field.
            status = 1,
            preparedAtUtc,
            completedAtUtc,
        });
        await using (SqliteConnection connection = Open(database.Path))
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO legacy_task_migrations(
                    migration_id, task_path, status, prepared_utc, json)
                VALUES ($id, $path, $status, $preparedUtc, $json);
                """;
            command.Parameters.AddWithValue("$id", "legacy-disabled");
            command.Parameters.AddWithValue("$path", @"\Alpha\Shutdown");
            command.Parameters.AddWithValue("$status", 1);
            command.Parameters.AddWithValue(
                "$preparedUtc",
                preparedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$json", json);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        StorageResult<LegacyTaskMigrationRecord?> result =
            await new SqliteNightGateRepository(database.Path)
                .ReadLegacyTaskMigrationAsync("legacy-disabled");

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.NotNull(result.Value);
        LegacyTaskMigrationRecord record = result.Value!;
        Assert.Equal(LegacyTaskMigrationStatus.Disabled, record.Status);
        Assert.False(record.DisabledStateVerified);
    }

    [Fact]
    public async Task LegacyMigration_DisabledVerificationIsMonotonic()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        LegacyTaskMigrationRecord prepared = Prepared(
            "migration-a",
            @"\Alpha\Shutdown",
            new string('a', 64),
            originalEnabled: true);
        LegacyTaskMigrationRecord unverified = new(
            prepared.MigrationId,
            prepared.TaskPath,
            prepared.ActionFingerprint,
            prepared.OriginalEnabled,
            LegacyTaskMigrationStatus.Disabled,
            prepared.PreparedAtUtc,
            Now.AddMinutes(1),
            DisabledStateVerified: false);
        LegacyTaskMigrationRecord verified = new(
            unverified.MigrationId,
            unverified.TaskPath,
            unverified.ActionFingerprint,
            unverified.OriginalEnabled,
            unverified.Status,
            unverified.PreparedAtUtc,
            unverified.CompletedAtUtc,
            DisabledStateVerified: true);
        LegacyTaskMigrationRecord downgraded = new(
            verified.MigrationId,
            verified.TaskPath,
            verified.ActionFingerprint,
            verified.OriginalEnabled,
            verified.Status,
            verified.PreparedAtUtc,
            verified.CompletedAtUtc,
            DisabledStateVerified: false);
        LegacyTaskMigrationRecord restoredWithoutProof = new(
            verified.MigrationId,
            verified.TaskPath,
            verified.ActionFingerprint,
            verified.OriginalEnabled,
            LegacyTaskMigrationStatus.Restored,
            verified.PreparedAtUtc,
            Now.AddMinutes(2),
            DisabledStateVerified: false);

        Assert.Equal(StorageMode.Success, (await repository.SaveLegacyTaskMigrationAsync(prepared)).Mode);
        Assert.Equal(StorageMode.Success, (await repository.SaveLegacyTaskMigrationAsync(unverified)).Mode);
        Assert.Equal(StorageMode.Success, (await repository.SaveLegacyTaskMigrationAsync(verified)).Mode);
        Assert.Equal(StorageMode.Degraded, (await repository.SaveLegacyTaskMigrationAsync(downgraded)).Mode);
        Assert.Equal(
            StorageMode.Degraded,
            (await repository.SaveLegacyTaskMigrationAsync(restoredWithoutProof)).Mode);

        Assert.Equal(
            verified,
            (await repository.ReadLegacyTaskMigrationAsync(prepared.MigrationId)).Value);
    }

    [Fact]
    public async Task LegacyMigration_RejectsFingerprintEnabledFactAndIllegalTransitionChanges()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        LegacyTaskMigrationRecord prepared = Prepared(
            "migration-a",
            @"\Alpha\Shutdown",
            new string('a', 64),
            originalEnabled: true);
        await repository.SaveLegacyTaskMigrationAsync(prepared);

        LegacyTaskMigrationRecord changedFingerprint = new(
            prepared.MigrationId,
            prepared.TaskPath,
            new string('c', 64),
            prepared.OriginalEnabled,
            LegacyTaskMigrationStatus.Disabled,
            prepared.PreparedAtUtc,
            Now.AddMinutes(1));
        LegacyTaskMigrationRecord changedEnabled = new(
            prepared.MigrationId,
            prepared.TaskPath,
            prepared.ActionFingerprint,
            false,
            LegacyTaskMigrationStatus.Disabled,
            prepared.PreparedAtUtc,
            Now.AddMinutes(1));
        LegacyTaskMigrationRecord rewrittenPrepared = new(
            prepared.MigrationId,
            prepared.TaskPath,
            prepared.ActionFingerprint,
            prepared.OriginalEnabled,
            LegacyTaskMigrationStatus.Prepared,
            prepared.PreparedAtUtc,
            Now.AddMinutes(1));

        StorageWriteResult fingerprintResult =
            await repository.SaveLegacyTaskMigrationAsync(changedFingerprint);
        StorageWriteResult enabledResult =
            await repository.SaveLegacyTaskMigrationAsync(changedEnabled);
        StorageWriteResult transitionResult =
            await repository.SaveLegacyTaskMigrationAsync(rewrittenPrepared);

        Assert.Equal(StorageMode.Degraded, fingerprintResult.Mode);
        Assert.Equal(StorageMode.Degraded, enabledResult.Mode);
        Assert.Equal(StorageMode.Degraded, transitionResult.Mode);
        Assert.Equal(
            prepared,
            (await repository.ReadLegacyTaskMigrationAsync(prepared.MigrationId)).Value);
    }

    [Fact]
    public async Task CorruptLegacyMigrationJson_ReturnsDegradedFailOpen()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        LegacyTaskMigrationRecord prepared = Prepared(
            "migration-a",
            @"\Alpha\Shutdown",
            new string('a', 64),
            true);
        await repository.SaveLegacyTaskMigrationAsync(prepared);
        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(
                connection,
                "UPDATE legacy_task_migrations SET status = 1 WHERE migration_id = 'migration-a';");
        }

        StorageResult<LegacyTaskMigrationRecord?> result =
            await repository.ReadLegacyTaskMigrationAsync(prepared.MigrationId);

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ClearHistory_DeletesReportsAndOldNoticesButRetainsAllOperationalState()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState active = ActiveState();
        ProgressState progress = new(
            1,
            Now.AddHours(-24),
            Night,
            PendingStep: 2,
            PendingStepUnlockedByNightDate: Night,
            PendingStepConfirmedAtUtc: Now,
            PendingStepEffectiveNightDate: Night.AddDays(1));
        OnboardingState onboarding = new(1);
        RuleSettingsState rules = new(ActiveSiteRules: [new("video.example.com")]);
        LegacyTaskMigrationRecord migration = Prepared(
            "migration-a",
            @"\Alpha\Shutdown",
            new string('a', 64),
            true);
        await repository.SaveActiveStateWithEventAsync(active, Event(active.NightId));
        await repository.SaveProgressAsync(progress);
        await repository.SaveOnboardingAsync(onboarding, expectedVersion: 0);
        await repository.SaveRuleSettingsAsync(rules, expectedVersion: 0);
        await repository.SaveLegacyTaskMigrationAsync(migration);
        await repository.SaveSelfReportAsync(new(Night.AddDays(-1), true, true, Now));
        await repository.SaveSelfReportAsync(new(Night, true, true, Now));
        await repository.SaveOutcomeAsync(Outcome(Night.AddDays(-1)));
        await repository.TryClaimNoticeAsync(
            new(Night.AddDays(-1), NightNoticeKind.LastStart, Now));
        await repository.TryClaimNoticeAsync(new(Night, NightNoticeKind.LastStart, Now));
        await repository.TryClaimNoticeAsync(
            new(Night.AddDays(1), NightNoticeKind.LastStart, Now));
        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO process_persistence(slot,schema_version,record_version,payload_json) VALUES ('processGateEnvelope',1,1,'{\"schemaVersion\":1}');");
        }

        StorageWriteResult result = await repository.ClearHistoryAsync();

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal(active, (await repository.ReadActiveStateAsync()).Value);
        Assert.Equal(progress, (await repository.ReadProgressAsync()).Value);
        Assert.Equal(onboarding, (await repository.ReadOnboardingAsync()).Value);
        Assert.Equal("video.example.com", Assert.Single(
            (await repository.ReadRuleSettingsAsync()).Value.ActiveSiteRules).Domain);
        Assert.Equal(migration, (await repository.ReadLegacyTaskMigrationAsync("migration-a")).Value);
        Assert.Null((await repository.ReadSelfReportAsync(Night)).Value);
        Assert.Empty((await repository.ReadLatestOutcomesAsync(10)).Value);
        await using SqliteConnection verify = Open(database.Path);
        Assert.Equal(0, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM raw_events;"));
        Assert.Equal(0, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM night_self_reports;"));
        Assert.Equal(2, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM notice_claims;"));
        Assert.Equal(1, await ScalarLongAsync(
            verify,
            $"SELECT COUNT(*) FROM notice_claims WHERE night_date = '{Night:yyyy-MM-dd}';"));
        Assert.Equal(1, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM process_persistence;"));
    }

    [Fact]
    public async Task ClearHistory_WithoutOpenActiveNightDeletesEveryNoticeClaim()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.TryClaimNoticeAsync(new(Night, NightNoticeKind.Grace2, Now));
        await repository.TryClaimNoticeAsync(
            new(Night.AddDays(1), NightNoticeKind.Grace2, Now));

        Assert.Equal(StorageMode.Success, (await repository.ClearHistoryAsync()).Mode);

        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(0, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM notice_claims;"));
    }

    [Fact]
    public async Task V2SchemaContainsNoCredentialOrBrowsingDetailColumns()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadOnboardingAsync()).Mode);
        await using SqliteConnection connection = Open(database.Path);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(sql, ' ') FROM sqlite_master WHERE sql IS NOT NULL;";
        string schema = Convert.ToString(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture)!.ToLowerInvariant();

        foreach (string forbidden in new[] { "password", "credential", "apple", "url", "title" })
        {
            Assert.DoesNotContain(forbidden, schema, StringComparison.Ordinal);
        }
    }

    private static LegacyTaskMigrationRecord Prepared(
        string id,
        string taskPath,
        string fingerprint,
        bool originalEnabled) => new(
            id,
            taskPath,
            fingerprint,
            originalEnabled,
            LegacyTaskMigrationStatus.Prepared,
            Now);

    private static NightState ActiveState() => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        Night,
        Now,
        NightPhase.Grace,
        null,
        false,
        false,
        false,
        false,
        false,
        false);

    private static NightEvent Event(Guid nightId) => new(
        Guid.NewGuid(),
        nightId,
        Now,
        NightEventKind.StateObserved,
        NightPhase.Grace);

    private static NightOutcome Outcome(DateOnly nightDate) => new(
        Guid.NewGuid(),
        nightDate,
        Now,
        false,
        false,
        false,
        false,
        false,
        false);

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new($"Data Source={path};Pooling=False;Default Timeout=1");
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            DirectoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NightGate.Service.Tests",
                Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(DirectoryPath, "state.db");
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
