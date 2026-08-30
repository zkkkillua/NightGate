using System.Collections.Immutable;
using System.Text.Json;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class ProcessPersistenceCommandHandlerTests
{
    [Fact]
    public async Task LoadMissing_ReturnsTypedMissingResult()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = CreateHandler(repository, repository);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new LoadProcessPersistenceCommand(
                ProcessPersistenceSlot.ProcessGateEnvelope));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal("missing", result.Payload.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.Payload.GetProperty("record").ValueKind);
    }

    [Fact]
    public async Task CompareExchangeSaved_EmbedsPayloadAsJsonObject()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = CreateHandler(repository, repository);
        ProcessPersistenceRecord replacement = new(
            ProcessPersistenceSlot.ProcessGateEnvelope,
            1,
            1,
            "{\"schemaVersion\":1,\"message\":\"睡觉\"}");

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new CompareExchangeProcessPersistenceCommand(
                replacement.Slot,
                null,
                replacement));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal("saved", result.Payload.GetProperty("status").GetString());
        JsonElement record = result.Payload.GetProperty("record");
        Assert.Equal("processGateEnvelope", record.GetProperty("slot").GetString());
        Assert.Equal(1, record.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, record.GetProperty("version").GetInt64());
        Assert.Equal(
            JsonValueKind.Object,
            record.GetProperty("payload").ValueKind);
        Assert.Equal(
            "睡觉",
            record.GetProperty("payload").GetProperty("message").GetString());
    }

    [Fact]
    public async Task SavedProcessEnvelope_PublishesTheCommittedDesktopObservation()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        RecordingProcessSnapshotPublisher publisher = new();
        NightGateProtocolCommandHandler handler = CreateHandler(
            repository,
            repository,
            publisher);
        ProcessPersistenceRecord replacement = new(
            ProcessPersistenceSlot.ProcessGateEnvelope,
            1,
            1,
            "{\"schemaVersion\":1,\"envelope\":{\"reducerState\":{}}}");

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new CompareExchangeProcessPersistenceCommand(
                replacement.Slot,
                null,
                replacement));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal(replacement, Assert.Single(publisher.Published));
        Assert.Equal(0, publisher.Invalidations);
    }

    [Fact]
    public async Task CompareExchangeConflict_ReturnsCurrentWinner()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = CreateHandler(repository, repository);
        ProcessPersistenceRecord winner = new(
            ProcessPersistenceSlot.ProcessSourceContinuity,
            1,
            1,
            "{\"schemaVersion\":1,\"epoch\":\"winner\"}");
        await repository.CompareExchangeProcessPersistenceAsync(
            winner.Slot,
            null,
            winner);
        ProcessPersistenceRecord loser = winner with
        {
            Version = 3,
            PayloadJson = "{\"schemaVersion\":1,\"epoch\":\"loser\"}",
        };

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new CompareExchangeProcessPersistenceCommand(
                loser.Slot,
                ExpectedVersion: 2,
                loser));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal("conflict", result.Payload.GetProperty("status").GetString());
        Assert.Equal(
            "winner",
            result.Payload
                .GetProperty("record")
                .GetProperty("payload")
                .GetProperty("epoch")
                .GetString());
    }

    [Theory]
    [InlineData(ProcessPersistenceLoadStatus.Unavailable, StorageMode.Degraded, "unavailable")]
    [InlineData(ProcessPersistenceLoadStatus.Corrupt, StorageMode.Degraded, "corrupt")]
    public async Task LoadFailure_PreservesExactStatusAndFailsOpen(
        ProcessPersistenceLoadStatus status,
        StorageMode expectedMode,
        string expectedToken)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository domainRepository = new(database.Path);
        StubProcessRepository processRepository = new(
            new(status, null));
        NightGateProtocolCommandHandler handler = CreateHandler(
            domainRepository,
            processRepository);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new LoadProcessPersistenceCommand(
                ProcessPersistenceSlot.ProcessGateEnvelope));

        Assert.Equal(expectedMode, result.Mode);
        Assert.Equal(expectedToken, result.Payload.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, result.Payload.GetProperty("record").ValueKind);
    }

    private static NightGateProtocolCommandHandler CreateHandler(
        SqliteNightGateRepository domainRepository,
        IProcessPersistenceRepository processRepository,
        IActiveProcessSnapshotPublisher? processSnapshotPublisher = null)
    {
        InMemoryServiceStatus status = new();
        return new(
            domainRepository,
            domainRepository,
            domainRepository,
            status,
            status,
            new OverridePolicy(new EmptySnapshotProvider()),
            new NightMutationGate(),
            new FixedClock(),
            processRepository,
            activeProcessSnapshotPublisher: processSnapshotPublisher);
    }

    private sealed class RecordingProcessSnapshotPublisher :
        IActiveProcessSnapshotPublisher
    {
        public List<ProcessPersistenceRecord> Published { get; } = [];

        public int Invalidations { get; private set; }

        public void PublishProcessSnapshot(ProcessPersistenceRecord record) =>
            Published.Add(record);

        public void InvalidateProcessSnapshot() => Invalidations++;
    }

    private sealed class StubProcessRepository(
        ProcessPersistenceLoadResult loadResult) : IProcessPersistenceRepository
    {
        public ValueTask<ProcessPersistenceLoadResult> LoadProcessPersistenceAsync(
            ProcessPersistenceSlot slot,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(loadResult);

        public ValueTask<ProcessPersistenceSaveResult>
            CompareExchangeProcessPersistenceAsync(
                ProcessPersistenceSlot slot,
                long? expectedVersion,
                ProcessPersistenceRecord replacement,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ProcessPersistenceSaveResult(
                ProcessPersistenceSaveStatus.Unavailable,
                null));
    }

    private sealed class EmptySnapshotProvider : IAllowedProcessSnapshotProvider
    {
        public ImmutableArray<string> GetSnapshot() => [];
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);
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
