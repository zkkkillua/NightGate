using System.Text;
using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class ProcessPersistenceRepositoryTests
{
    [Fact]
    public async Task LoadAsync_MissingSlot_ReturnsMissing()
    {
        using TempDatabase database = new();
        IProcessPersistenceRepository repository = new SqliteNightGateRepository(database.Path);

        ProcessPersistenceLoadResult result = await repository.LoadProcessPersistenceAsync(
            ProcessPersistenceSlot.ProcessGateEnvelope);

        Assert.Equal(ProcessPersistenceLoadStatus.Missing, result.Status);
        Assert.Null(result.Record);
    }

    [Fact]
    public async Task CompareExchangeAsync_MissingSlot_SavesImmediateSuccessorAndSurvivesRestart()
    {
        using TempDatabase database = new();
        ProcessPersistenceRecord proposed = Record(
            ProcessPersistenceSlot.ProcessGateEnvelope,
            1,
            "{\"schemaVersion\":1,\"value\":\"saved\"}");
        IProcessPersistenceRepository first = new SqliteNightGateRepository(database.Path);

        ProcessPersistenceSaveResult saved = await first.CompareExchangeProcessPersistenceAsync(
            ProcessPersistenceSlot.ProcessGateEnvelope,
            expectedVersion: null,
            proposed);
        IProcessPersistenceRepository reopened = new SqliteNightGateRepository(database.Path);
        ProcessPersistenceLoadResult loaded = await reopened.LoadProcessPersistenceAsync(
            ProcessPersistenceSlot.ProcessGateEnvelope);

        Assert.Equal(ProcessPersistenceSaveStatus.Saved, saved.Status);
        Assert.Equal(proposed, saved.Record);
        Assert.Equal(ProcessPersistenceLoadStatus.Found, loaded.Status);
        Assert.Equal(proposed, loaded.Record);
    }

    [Fact]
    public async Task CompareExchangeAsync_StaleVersion_ReturnsCurrentWinnerWithoutMutation()
    {
        using TempDatabase database = new();
        IProcessPersistenceRepository repository = new SqliteNightGateRepository(database.Path);
        ProcessPersistenceRecord winner = Record(
            ProcessPersistenceSlot.ProcessSourceContinuity,
            1,
            "{\"schemaVersion\":1,\"epoch\":\"winner\"}");
        ProcessPersistenceRecord loser = Record(
            ProcessPersistenceSlot.ProcessSourceContinuity,
            100,
            "{\"schemaVersion\":1,\"epoch\":\"loser\"}");
        Assert.Equal(
            ProcessPersistenceSaveStatus.Saved,
            (await repository.CompareExchangeProcessPersistenceAsync(
                ProcessPersistenceSlot.ProcessSourceContinuity,
                null,
                winner)).Status);

        ProcessPersistenceSaveResult conflict =
            await repository.CompareExchangeProcessPersistenceAsync(
                ProcessPersistenceSlot.ProcessSourceContinuity,
                expectedVersion: 99,
                loser);
        ProcessPersistenceLoadResult loaded = await repository.LoadProcessPersistenceAsync(
            ProcessPersistenceSlot.ProcessSourceContinuity);

        Assert.Equal(ProcessPersistenceSaveStatus.Conflict, conflict.Status);
        Assert.Equal(winner, conflict.Record);
        Assert.Equal(winner, loaded.Record);
    }

    [Fact]
    public async Task CompareExchangeAsync_NonSuccessorReplacement_ReturnsCorruptWithoutMutation()
    {
        using TempDatabase database = new();
        IProcessPersistenceRepository repository = new SqliteNightGateRepository(database.Path);
        ProcessPersistenceRecord invalid = Record(
            ProcessPersistenceSlot.ProcessGateEnvelope,
            2,
            "{\"schemaVersion\":1}");

        ProcessPersistenceSaveResult result = await repository.CompareExchangeProcessPersistenceAsync(
            ProcessPersistenceSlot.ProcessGateEnvelope,
            expectedVersion: null,
            invalid);

        Assert.Equal(ProcessPersistenceSaveStatus.Corrupt, result.Status);
        Assert.Null(result.Record);
        Assert.Equal(
            ProcessPersistenceLoadStatus.Missing,
            (await repository.LoadProcessPersistenceAsync(
                ProcessPersistenceSlot.ProcessGateEnvelope)).Status);
    }

    [Fact]
    public async Task LoadAsync_ParseableButInvalidStoredRow_ReturnsCorruptAndDoesNotClearIt()
    {
        using TempDatabase database = new();
        IProcessPersistenceRepository repository = new SqliteNightGateRepository(database.Path);
        Assert.Equal(
            ProcessPersistenceLoadStatus.Missing,
            (await repository.LoadProcessPersistenceAsync(
                ProcessPersistenceSlot.ProcessGateEnvelope)).Status);
        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO process_persistence(slot, schema_version, record_version, payload_json) VALUES ('processGateEnvelope', 99, 1, '{\"schemaVersion\":1}');");
        }

        ProcessPersistenceLoadResult first = await repository.LoadProcessPersistenceAsync(
            ProcessPersistenceSlot.ProcessGateEnvelope);
        ProcessPersistenceLoadResult second = await repository.LoadProcessPersistenceAsync(
            ProcessPersistenceSlot.ProcessGateEnvelope);

        Assert.Equal(ProcessPersistenceLoadStatus.Corrupt, first.Status);
        Assert.Equal(ProcessPersistenceLoadStatus.Corrupt, second.Status);
    }

    [Fact]
    public async Task CompareExchangeAsync_OversizedPayload_ReturnsCorruptWithoutOpeningDatabase()
    {
        using TempDatabase database = new();
        IProcessPersistenceRepository repository = new SqliteNightGateRepository(database.Path);
        string payload = "{\"schemaVersion\":1,\"value\":\""
            + new string('睡', ProcessPersistenceLimits.MaximumPayloadBytes / 3)
            + "\"}";

        ProcessPersistenceSaveResult result = await repository.CompareExchangeProcessPersistenceAsync(
            ProcessPersistenceSlot.ProcessGateEnvelope,
            null,
            Record(ProcessPersistenceSlot.ProcessGateEnvelope, 1, payload));

        Assert.Equal(ProcessPersistenceSaveStatus.Corrupt, result.Status);
        Assert.False(File.Exists(database.Path));
    }

    [Fact]
    public async Task CompareExchangeAsync_PayloadWhoseResponseEncodingWouldExceedCap_IsRejected()
    {
        using TempDatabase database = new();
        IProcessPersistenceRepository repository = new SqliteNightGateRepository(database.Path);
        string payload = "{\"schemaVersion\":1,\"value\":\""
            + new string('&', 10_100)
            + "\"}";
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(payload)
            < ProcessPersistenceLimits.MaximumPayloadBytes);

        ProcessPersistenceSaveResult result = await repository
            .CompareExchangeProcessPersistenceAsync(
                ProcessPersistenceSlot.ProcessGateEnvelope,
                null,
                Record(ProcessPersistenceSlot.ProcessGateEnvelope, 1, payload));

        Assert.Equal(ProcessPersistenceSaveStatus.Corrupt, result.Status);
        Assert.False(File.Exists(database.Path));
    }

    [Fact]
    public async Task LoadAsync_MaximumRecordVersion_ReturnsCorrupt()
    {
        using TempDatabase database = new();
        IProcessPersistenceRepository repository = new SqliteNightGateRepository(database.Path);
        Assert.Equal(
            ProcessPersistenceLoadStatus.Missing,
            (await repository.LoadProcessPersistenceAsync(
                ProcessPersistenceSlot.ProcessGateEnvelope)).Status);
        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(
                connection,
                $"INSERT INTO process_persistence(slot, schema_version, record_version, payload_json) VALUES ('processGateEnvelope', 1, {long.MaxValue}, '{{\"schemaVersion\":1}}');");
        }

        ProcessPersistenceLoadResult result = await repository.LoadProcessPersistenceAsync(
            ProcessPersistenceSlot.ProcessGateEnvelope);

        Assert.Equal(ProcessPersistenceLoadStatus.Corrupt, result.Status);
    }

    [Fact]
    public async Task ClearHistoryAndPurgeEvents_DoNotTouchProcessSlots()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        ProcessPersistenceRecord gate = Record(
            ProcessPersistenceSlot.ProcessGateEnvelope,
            1,
            "{\"schemaVersion\":1,\"kind\":\"gate\"}");
        ProcessPersistenceRecord continuity = Record(
            ProcessPersistenceSlot.ProcessSourceContinuity,
            1,
            "{\"schemaVersion\":1,\"kind\":\"continuity\"}");
        await repository.CompareExchangeProcessPersistenceAsync(
            gate.Slot, null, gate);
        await repository.CompareExchangeProcessPersistenceAsync(
            continuity.Slot, null, continuity);

        await repository.ClearHistoryAsync();
        await repository.PurgeEventsOlderThanAsync(DateTimeOffset.UtcNow);

        Assert.Equal(
            gate,
            (await repository.LoadProcessPersistenceAsync(gate.Slot)).Record);
        Assert.Equal(
            continuity,
            (await repository.LoadProcessPersistenceAsync(continuity.Slot)).Record);
    }

    [Fact]
    public async Task LoadAsync_CallerCancellation_Propagates()
    {
        using TempDatabase database = new();
        IProcessPersistenceRepository repository = new SqliteNightGateRepository(database.Path);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository
            .LoadProcessPersistenceAsync(
                ProcessPersistenceSlot.ProcessGateEnvelope,
                cancellation.Token)
            .AsTask());
    }

    private static ProcessPersistenceRecord Record(
        ProcessPersistenceSlot slot,
        long version,
        string payload) => new(
            slot,
            ProcessPersistenceLimits.CurrentSchemaVersion,
            version,
            payload);

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new($"Data Source={path};Pooling=False;Default Timeout=0");
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
