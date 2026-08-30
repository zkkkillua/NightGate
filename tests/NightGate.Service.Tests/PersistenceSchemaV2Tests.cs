using System.Globalization;
using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class PersistenceSchemaV3Tests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 8, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NewDatabase_SeedsSingletonDefaultsAndImplementsV3Repositories()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);

        StorageResult<OnboardingState> onboarding = await repository.ReadOnboardingAsync();
        StorageResult<RuleSettingsState> rules = await repository.ReadRuleSettingsAsync();

        Assert.IsAssignableFrom<IOnboardingRepository>(repository);
        Assert.IsAssignableFrom<IChromeProtectionHealthRepository>(repository);
        Assert.IsAssignableFrom<IRuleSettingsRepository>(repository);
        Assert.Equal(StorageMode.Success, onboarding.Mode);
        Assert.Equal(OnboardingState.Initial, onboarding.Value);
        Assert.Equal(0, onboarding.Version);
        Assert.Equal(StorageMode.Success, rules.Mode);
        Assert.True(rules.Value.ActiveAppRules.IsEmpty);
        Assert.True(rules.Value.ActiveSiteRules.IsEmpty);
        Assert.Equal(0, rules.Version);

        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(3, await ScalarLongAsync(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task SingletonSettings_CasConflictAndRestartPreserveCommittedValues()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository first = new(database.Path);
        OnboardingState onboarding = new(1);
        RuleSettingsState rules = new(
            ActiveAppRules: [ConfiguredApp()],
            ActiveSiteRules: [new("video.example.com")],
            PendingAppRules: [],
            PendingSiteRules: [],
            PendingEffectiveNightDate: new(2026, 7, 9),
            PendingSavedAtUtc: Now);

        StorageWriteResult onboardingWrite = await first.SaveOnboardingAsync(
            onboarding,
            expectedVersion: 0);
        StorageWriteResult rulesWrite = await first.SaveRuleSettingsAsync(
            rules,
            expectedVersion: 0);
        StorageWriteResult staleWrite = await first.SaveOnboardingAsync(
            new(2),
            expectedVersion: 0);
        SqliteNightGateRepository reopened = new(database.Path);
        StorageResult<OnboardingState> savedOnboarding = await reopened.ReadOnboardingAsync();
        StorageResult<RuleSettingsState> savedRules = await reopened.ReadRuleSettingsAsync();

        Assert.Equal(StorageMode.Success, onboardingWrite.Mode);
        Assert.False(onboardingWrite.IsConflict);
        Assert.Equal(StorageMode.Success, rulesWrite.Mode);
        Assert.True(staleWrite.IsConflict);
        Assert.Equal(onboarding, savedOnboarding.Value);
        Assert.Equal(1, savedOnboarding.Version);
        Assert.Equal("game", Assert.Single(savedRules.Value.ActiveAppRules).Id);
        Assert.Equal("video.example.com", Assert.Single(savedRules.Value.ActiveSiteRules).Domain);
        Assert.NotNull(savedRules.Value.PendingAppRules);
        Assert.Equal(1, savedRules.Version);
    }

    [Fact]
    public async Task OnboardingCompletedStep_CannotSkipOrRegress()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);

        StorageWriteResult skipped = await repository.SaveOnboardingAsync(
            new(2),
            expectedVersion: 0);
        StorageWriteResult first = await repository.SaveOnboardingAsync(
            new(1),
            expectedVersion: 0);
        StorageWriteResult regressed = await repository.SaveOnboardingAsync(
            OnboardingState.Initial,
            expectedVersion: 1);

        Assert.Equal(StorageMode.Degraded, skipped.Mode);
        Assert.False(skipped.EnforcementEnabled);
        Assert.Equal(StorageMode.Success, first.Mode);
        Assert.Equal(StorageMode.Degraded, regressed.Mode);
        Assert.Equal(new OnboardingState(1), (await repository.ReadOnboardingAsync()).Value);
    }

    [Theory]
    [InlineData("onboarding_state", "{\"completedStep\":0,\"wizardVersion\":99}")]
    [InlineData("rule_settings", "{\"activeAppRules\":[],\"activeSiteRules\":[],\"pendingAppRules\":[]}")]
    public async Task MalformedSingletonJson_ReturnsDegradedFailOpen(string table, string json)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadOnboardingAsync()).Mode);
        await using (SqliteConnection connection = Open(database.Path))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"UPDATE {table} SET json = $json;";
            command.Parameters.AddWithValue("$json", json);
            await command.ExecuteNonQueryAsync();
        }

        if (table == "onboarding_state")
        {
            StorageResult<OnboardingState> result = await repository.ReadOnboardingAsync();
            Assert.Equal(StorageMode.Degraded, result.Mode);
            Assert.False(result.EnforcementEnabled);
            Assert.Equal(OnboardingState.Initial, result.Value);
        }
        else
        {
            StorageResult<RuleSettingsState> result = await repository.ReadRuleSettingsAsync();
            Assert.Equal(StorageMode.Degraded, result.Mode);
            Assert.False(result.EnforcementEnabled);
            Assert.Equal(RuleSettingsState.Initial, result.Value);
        }
    }

    [Fact]
    public async Task VersionOneMigration_PreservesExistingTablesRowsVersionsAndIndexes()
    {
        using TempDatabase database = new();
        await CreateVersionOneDatabaseAsync(database.Path);
        SqliteNightGateRepository repository = new(database.Path);

        StorageResult<OnboardingState> result = await repository.ReadOnboardingAsync();

        Assert.Equal(StorageMode.Success, result.Mode);
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(3, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_events;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM night_outcomes;"));
        Assert.Equal(
            7,
            await ScalarLongAsync(
                connection,
                "SELECT row_version FROM progress WHERE singleton_id = 1;"));
        Assert.Equal(
            "{\"currentStep\":2,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":null}",
            await ScalarStringAsync(
                connection,
                "SELECT json FROM progress WHERE singleton_id = 1;"));
        Assert.Equal(
            1,
            await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='ix_night_outcomes_closed_utc';"));
        Assert.Equal(
            1,
            await ScalarLongAsync(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='process_persistence';"));
    }

    [Fact]
    public async Task RepeatedOpenOfVersionThree_IsIdempotent()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository first = new(database.Path);
        Assert.Equal(StorageMode.Success, (await first.ReadOnboardingAsync()).Mode);

        for (int attempt = 0; attempt < 4; attempt++)
        {
            SqliteNightGateRepository reopened = new(database.Path);
            Assert.Equal(StorageMode.Success, (await reopened.ReadRuleSettingsAsync()).Mode);
        }

        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(3, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM onboarding_state;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM rule_settings;"));
    }

    [Fact]
    public async Task FutureSchemaVersion_ReturnsDegradedFailOpen()
    {
        using TempDatabase database = new();
        Directory.CreateDirectory(database.DirectoryPath);
        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(connection, "PRAGMA user_version = 4;");
        }

        SqliteNightGateRepository repository = new(database.Path);
        StorageResult<OnboardingState> result = await repository.ReadOnboardingAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Equal(OnboardingState.Initial, result.Value);
    }

    private static AppRule ConfiguredApp() => new(
        "game",
        @"C:\Games\game.exe",
        [],
        AppRuleCategory.Game);

    private static async Task CreateVersionOneDatabaseAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using SqliteConnection connection = Open(path);
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE active_state (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                json TEXT NOT NULL,
                row_version INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE progress (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                json TEXT NOT NULL,
                row_version INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE night_outcomes (
                night_id TEXT PRIMARY KEY,
                closed_utc TEXT NOT NULL,
                json TEXT NOT NULL
            );
            CREATE TABLE raw_events (
                event_id TEXT PRIMARY KEY,
                occurred_utc TEXT NOT NULL,
                json TEXT NOT NULL
            );
            CREATE INDEX ix_night_outcomes_closed_utc ON night_outcomes(closed_utc DESC);
            CREATE INDEX ix_raw_events_occurred_utc ON raw_events(occurred_utc);
            INSERT INTO progress(singleton_id, json, row_version)
            VALUES (1, '{"currentStep":2,"lastTeamRescueAtUtc":null,"lastProgressionNightDate":null}', 7);
            INSERT INTO night_outcomes(night_id, closed_utc, json)
            VALUES (
                'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                '2026-07-08T14:00:00.0000000+00:00',
                '{"legacy":true}');
            INSERT INTO raw_events(event_id, occurred_utc, json)
            VALUES (
                'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
                '2026-07-08T14:00:00.0000000+00:00',
                '{"legacy":true}');
            PRAGMA user_version = 1;
            """);
    }

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

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture)!;
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
