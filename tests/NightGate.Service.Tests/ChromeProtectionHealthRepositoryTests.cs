using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class ChromeProtectionHealthRepositoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Health_IsInitiallyMissingThenPersistsWithCompareAndSwap()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.IsAssignableFrom<IChromeProtectionHealthRepository>(repository);

        StorageResult<ChromeProtectionHealth?> missing =
            await repository.ReadChromeProtectionHealthAsync();
        ChromeProtectionHealth first = Health(1, Now);

        Assert.Equal(StorageMode.Success, missing.Mode);
        Assert.Null(missing.Value);
        Assert.Equal(0, missing.Version);
        Assert.Equal(
            StorageMode.Success,
            (await repository.SaveChromeProtectionHealthAsync(first, expectedVersion: 0)).Mode);

        StorageResult<ChromeProtectionHealth?> saved =
            await new SqliteNightGateRepository(database.Path)
                .ReadChromeProtectionHealthAsync();
        ChromeProtectionHealth replacement = Health(2, Now.AddSeconds(30));
        StorageWriteResult stale = await repository.SaveChromeProtectionHealthAsync(
            replacement,
            expectedVersion: 0);
        StorageWriteResult updated = await repository.SaveChromeProtectionHealthAsync(
            replacement,
            expectedVersion: saved.Version);

        Assert.Equal(first, saved.Value);
        Assert.Equal(1, saved.Version);
        Assert.True(stale.IsConflict);
        Assert.Equal(StorageMode.Success, updated.Mode);
        Assert.Equal(
            replacement,
            (await repository.ReadChromeProtectionHealthAsync()).Value);
    }

    [Fact]
    public async Task VersionTwoDatabase_UpgradesWithoutLosingExistingState()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        OnboardingState onboarding = new(1);
        await repository.SaveOnboardingAsync(onboarding, expectedVersion: 0);

        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(connection, "DROP TABLE chrome_protection_health;");
            await ExecuteAsync(connection, "PRAGMA user_version = 2;");
        }

        SqliteNightGateRepository reopened = new(database.Path);
        StorageResult<ChromeProtectionHealth?> health =
            await reopened.ReadChromeProtectionHealthAsync();

        Assert.Equal(StorageMode.Success, health.Mode);
        Assert.Null(health.Value);
        Assert.Equal(onboarding, (await reopened.ReadOnboardingAsync()).Value);
    }

    [Fact]
    public async Task CorruptHealthJson_FailsOpenWithoutReturningObservation()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveChromeProtectionHealthAsync(Health(1, Now));
        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(
                connection,
                "UPDATE chrome_protection_health SET json = '{\"extensionId\":\"bad\"}';");
        }

        StorageResult<ChromeProtectionHealth?> result =
            await repository.ReadChromeProtectionHealthAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task LegacyHealthJsonWithoutProtectionReady_ReadsAsNotReady()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveChromeProtectionHealthAsync(Health(1, Now));
        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(
                connection,
                "UPDATE chrome_protection_health SET json = '{\"extensionId\":\"eefgemhlhbdodhlgjmicnoifhclhdgmm\",\"extensionVersion\":\"1.0.0\",\"profileTokenSha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"policyRevision\":1,\"incognitoAllowed\":false,\"observedAtUtc\":\"2026-07-15T14:00:00+00:00\"}';");
        }

        StorageResult<ChromeProtectionHealth?> result =
            await repository.ReadChromeProtectionHealthAsync();

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.ProtectionReady);
    }

    private static ChromeProtectionHealth Health(long revision, DateTimeOffset observedAt) => new(
        ChromeProtectionHealth.ExpectedExtensionId,
        "1.0.0",
        new string('b', 64),
        revision,
        false,
        observedAt,
        true);

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new($"Data Source={path}");
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            string directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NightGate.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "state.db");
        }

        public string Path { get; }

        public void Dispose()
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (directory is null || !Directory.Exists(directory))
            {
                return;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
