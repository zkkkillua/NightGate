using System.Text.Json;
using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class BrowserEventRepositoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordBrowserEvent_PersistsOnlyTimestampEventTypeAndCategory()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);

        StorageWriteResult result = await repository.RecordBrowserEventAsync(
            BrowserEvent(BrowserEventType.MediaPlaying, BrowserSiteCategory.Video));

        Assert.Equal(StorageMode.Success, result.Mode);
        await using SqliteConnection connection = Open(database.Path);
        (string occurredUtc, string json) = await ReadOnlyEventAsync(connection);
        Assert.Equal(Now.ToString("O"), occurredUtc);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal(
            ["timestamp", "eventType", "category"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("2026-07-12T15:00:00.000Z", root.GetProperty("timestamp").GetString());
        Assert.Equal("mediaPlaying", root.GetProperty("eventType").GetString());
        Assert.Equal("video", root.GetProperty("category").GetString());
        Assert.DoesNotContain("ruleId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("title", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(BrowserEventType.MediaPlaying, "mediaPlaying")]
    [InlineData(BrowserEventType.MediaPaused, "mediaPaused")]
    [InlineData(BrowserEventType.MediaEnded, "mediaEnded")]
    [InlineData(BrowserEventType.NavigationBlocked, "navigationBlocked")]
    public async Task RecordBrowserEvent_UsesCanonicalEventTypeTokens(
        BrowserEventType eventType,
        string expectedToken)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);

        await repository.RecordBrowserEventAsync(
            BrowserEvent(eventType, BrowserSiteCategory.Other));

        await using SqliteConnection connection = Open(database.Path);
        (_, string json) = await ReadOnlyEventAsync(connection);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(expectedToken, document.RootElement.GetProperty("eventType").GetString());
    }

    [Theory]
    [InlineData(BrowserSiteCategory.Gaming, "gaming")]
    [InlineData(BrowserSiteCategory.Video, "video")]
    [InlineData(BrowserSiteCategory.Social, "social")]
    [InlineData(BrowserSiteCategory.Other, "other")]
    public async Task RecordBrowserEvent_UsesCanonicalCategoryTokens(
        BrowserSiteCategory category,
        string expectedToken)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);

        await repository.RecordBrowserEventAsync(
            BrowserEvent(BrowserEventType.MediaPaused, category));

        await using SqliteConnection connection = Open(database.Path);
        (_, string json) = await ReadOnlyEventAsync(connection);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(expectedToken, document.RootElement.GetProperty("category").GetString());
    }

    [Fact]
    public async Task SaveLateNewEntertainmentWithBrowserEvent_AtomicallyFlagsActiveNight()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState();
        await repository.SaveActiveStateWithEventAsync(state, CreateNightEvent());
        await repository.ClearHistoryAsync();
        StorageResult<NightState?> before = await repository.ReadActiveStateAsync();

        StorageWriteResult result = await repository
            .SaveLateNewEntertainmentWithBrowserEventAsync(
                state with { LateNewEntertainment = true },
                BrowserEvent(
                    BrowserEventType.NavigationBlocked,
                    BrowserSiteCategory.Social),
                before.Version);

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True((await repository.ReadActiveStateAsync()).Value!.LateNewEntertainment);
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(1, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_events;"));
        (_, string json) = await ReadOnlyEventAsync(connection);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("navigationBlocked", document.RootElement.GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task SaveLateNewEntertainmentWithBrowserEvent_EventFailureRollsBackFlag()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState();
        await repository.SaveActiveStateWithEventAsync(state, CreateNightEvent());
        await repository.ClearHistoryAsync();
        StorageResult<NightState?> before = await repository.ReadActiveStateAsync();
        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(
                connection,
                "CREATE TRIGGER reject_browser_event BEFORE INSERT ON raw_events BEGIN SELECT RAISE(ABORT, 'event rejected'); END;");
        }

        StorageWriteResult result = await repository
            .SaveLateNewEntertainmentWithBrowserEventAsync(
                state with { LateNewEntertainment = true },
                BrowserEvent(
                    BrowserEventType.NavigationBlocked,
                    BrowserSiteCategory.Social),
                before.Version);

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False((await repository.ReadActiveStateAsync()).Value!.LateNewEntertainment);
        await using SqliteConnection verify = Open(database.Path);
        Assert.Equal(0, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM raw_events;"));
    }

    [Fact]
    public async Task SaveLateNewEntertainmentWithBrowserEvent_StaleVersionChangesNothing()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState();
        await repository.SaveActiveStateWithEventAsync(state, CreateNightEvent());
        await repository.ClearHistoryAsync();
        StorageResult<NightState?> before = await repository.ReadActiveStateAsync();

        StorageWriteResult result = await repository
            .SaveLateNewEntertainmentWithBrowserEventAsync(
                state with { LateNewEntertainment = true },
                BrowserEvent(
                    BrowserEventType.NavigationBlocked,
                    BrowserSiteCategory.Video),
                before.Version + 1);

        Assert.True(result.IsConflict);
        Assert.False((await repository.ReadActiveStateAsync()).Value!.LateNewEntertainment);
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(0, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_events;"));
    }

    [Fact]
    public async Task BrowserEvents_AreIncludedInNinetyDayPurgeAndHistoryClear()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        DateTimeOffset cutoff = Now.AddDays(-90);
        await repository.RecordBrowserEventAsync(new(
            cutoff.AddTicks(-1),
            BrowserEventType.MediaPlaying,
            BrowserSiteCategory.Gaming));
        await repository.RecordBrowserEventAsync(new(
            cutoff,
            BrowserEventType.MediaPaused,
            BrowserSiteCategory.Video));
        await repository.RecordBrowserEventAsync(new(
            cutoff.AddTicks(1),
            BrowserEventType.MediaEnded,
            BrowserSiteCategory.Social));

        StorageWriteResult purge = await repository.PurgeEventsOlderThanAsync(cutoff);

        Assert.Equal(StorageMode.Success, purge.Mode);
        await using (SqliteConnection connection = Open(database.Path))
        {
            Assert.Equal(2, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_events;"));
            Assert.Equal(
                0,
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM raw_events WHERE json_extract(json, '$.category') = 'gaming';"));
        }

        StorageWriteResult clear = await repository.ClearHistoryAsync();

        Assert.Equal(StorageMode.Success, clear.Mode);
        await using SqliteConnection verify = Open(database.Path);
        Assert.Equal(0, await ScalarLongAsync(verify, "SELECT COUNT(*) FROM raw_events;"));
    }

    private static BrowserPrivacyEvent BrowserEvent(
        BrowserEventType eventType,
        BrowserSiteCategory category) => new(Now, eventType, category);

    private static NightState CreateState() => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        new DateOnly(2026, 7, 12),
        Now,
        NightPhase.Grace,
        null,
        false,
        false,
        false,
        false,
        false,
        false);

    private static NightEvent CreateNightEvent() => new(
        Guid.NewGuid(),
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        Now,
        NightEventKind.StateObserved,
        NightPhase.Grace);

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new(
            $"Data Source={path};Pooling=False;Default Timeout=1");
        connection.Open();
        return connection;
    }

    private static async Task<(string OccurredUtc, string Json)> ReadOnlyEventAsync(
        SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT occurred_utc, json FROM raw_events;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        (string, string) value = (reader.GetString(0), reader.GetString(1));
        Assert.False(await reader.ReadAsync());
        return value;
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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
                Directory.Delete(DirectoryPath, true);
            }
        }
    }
}
