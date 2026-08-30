using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class BrowserEventCommandHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 12, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(BrowserEventType.MediaPlaying)]
    [InlineData(BrowserEventType.MediaPaused)]
    [InlineData(BrowserEventType.MediaEnded)]
    public async Task MediaEvents_RecordHistoryWithoutReadingOrMutatingNightState(
        BrowserEventType eventType)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        InMemoryServiceStatus status = new();
        NightGateProtocolCommandHandler handler = CreateHandler(
            new ThrowingStateRepository(),
            repository,
            status);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RecordBrowserEventCommand(
                new(Now, eventType, BrowserSiteCategory.Video)));

        AssertRecorded(result);
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(1, await CountEventsAsync(connection));
    }

    [Fact]
    public async Task NavigationBlocked_AtomicallyFlagsTheCurrentNightAndRecordsHistory()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState();
        await repository.SaveActiveStateWithEventAsync(state, CreateNightEvent());
        await repository.ClearHistoryAsync();
        InMemoryServiceStatus status = new();
        NightGateProtocolCommandHandler handler = CreateHandler(repository, repository, status);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RecordBrowserEventCommand(new(
                Now,
                BrowserEventType.NavigationBlocked,
                BrowserSiteCategory.Social)));

        AssertRecorded(result);
        Assert.True((await repository.ReadActiveStateAsync()).Value!.LateNewEntertainment);
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(1, await CountEventsAsync(connection));
        string json = await EventJsonAsync(connection);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(
            ["timestamp", "eventType", "category"],
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    [Fact]
    public async Task NavigationBlocked_WithoutAnActiveNightStillRecordsPrivacyHistory()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        InMemoryServiceStatus status = new();
        NightGateProtocolCommandHandler handler = CreateHandler(repository, repository, status);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RecordBrowserEventCommand(new(
                Now,
                BrowserEventType.NavigationBlocked,
                BrowserSiteCategory.Gaming)));

        AssertRecorded(result);
        Assert.Null((await repository.ReadActiveStateAsync()).Value);
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(1, await CountEventsAsync(connection));
    }

    [Fact]
    public async Task NavigationBlocked_RetriesAtomicCompareExchangeConflict()
    {
        ConflictOnceRepository repository = new(CreateState());
        InMemoryServiceStatus status = new();
        NightGateProtocolCommandHandler handler = CreateHandler(repository, repository, status);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RecordBrowserEventCommand(new(
                Now,
                BrowserEventType.NavigationBlocked,
                BrowserSiteCategory.Other)));

        AssertRecorded(result);
        Assert.Equal(2, repository.SaveAttempts);
        Assert.True(repository.State.LateNewEntertainment);
        Assert.Single(repository.RecordedEvents);
    }

    [Fact]
    public async Task StorageFailure_ReturnsExactDegradedPayloadAndFailsOpen()
    {
        using TempDatabase database = new();
        Directory.CreateDirectory(database.DirectoryPath);
        string blockingFile = Path.Combine(database.DirectoryPath, "not-a-directory");
        await File.WriteAllTextAsync(blockingFile, "block");
        SqliteNightGateRepository repository = new(Path.Combine(blockingFile, "state.db"));
        InMemoryServiceStatus status = new();
        NightGateProtocolCommandHandler handler = CreateHandler(repository, repository, status);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RecordBrowserEventCommand(new(
                Now,
                BrowserEventType.MediaPlaying,
                BrowserSiteCategory.Video)));

        AssertDegraded(result);
        Assert.True(status.Current.IsDegraded);
        Assert.False(status.Current.EnforcementEnabled);
        Assert.Equal("browser-event-storage-unavailable", status.Current.DegradationCode);
    }

    [Fact]
    public async Task RepositoryException_IsContainedAsDegradedInsteadOfEscapingCommand()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository ordinaryRepository = new(database.Path);
        InMemoryServiceStatus status = new();
        NightGateProtocolCommandHandler handler = CreateHandler(
            ordinaryRepository,
            new ThrowingBrowserEventRepository(),
            status);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RecordBrowserEventCommand(new(
                Now,
                BrowserEventType.MediaEnded,
                BrowserSiteCategory.Other)));

        AssertDegraded(result);
    }

    [Fact]
    public async Task MissingBrowserRepository_IsDegradedAndNeverUsesGenericEventStorage()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.ReadActiveStateAsync();
        InMemoryServiceStatus status = new();
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptySnapshotProvider()),
            new NightMutationGate(),
            new FixedClock(Now));

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RecordBrowserEventCommand(new(
                Now,
                BrowserEventType.MediaPaused,
                BrowserSiteCategory.Video)));

        AssertDegraded(result);
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(0, await CountEventsAsync(connection));
    }

    private static NightGateProtocolCommandHandler CreateHandler(
        INightStateRepository stateRepository,
        IBrowserEventRepository browserEventRepository,
        InMemoryServiceStatus status) => new(
            stateRepository,
            stateRepository as IProgressRepository ?? new NullProgressRepository(),
            stateRepository as IHistoryRepository ?? new NullHistoryRepository(),
            status,
            status,
            new OverridePolicy(new EmptySnapshotProvider()),
            new NightMutationGate(),
            new FixedClock(Now),
            processPersistenceRepository: null,
            browserEventRepository);

    private static void AssertRecorded(ProtocolCommandResult result)
    {
        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal(
            ["status"],
            result.Payload.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("recorded", result.Payload.GetProperty("status").GetString());
    }

    private static void AssertDegraded(ProtocolCommandResult result)
    {
        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.Equal(
            ["status"],
            result.Payload.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("degraded", result.Payload.GetProperty("status").GetString());
    }

    private static NightState CreateState() => new(
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
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
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
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

    private static async Task<long> CountEventsAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM raw_events;";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> EventJsonAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM raw_events;";
        return Convert.ToString(await command.ExecuteScalarAsync())
            ?? throw new InvalidDataException("Expected browser event JSON.");
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class EmptySnapshotProvider : IAllowedProcessSnapshotProvider
    {
        public ImmutableArray<string> GetSnapshot() => [];
    }

    private sealed class ThrowingStateRepository : INightStateRepository
    {
        public ValueTask<StorageResult<NightState?>> ReadActiveStateAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Media history must not read state.");

        public ValueTask<StorageWriteResult> SaveActiveStateWithEventAsync(
            NightState state,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public ValueTask<StorageWriteResult> SaveActiveStateProgressWithEventAsync(
            NightState state,
            ProgressState progress,
            NightEvent nightEvent,
            long? expectedStateVersion = null,
            long? expectedProgressVersion = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public ValueTask<StorageWriteResult> CloseActiveStateWithOutcomeAndEventAsync(
            NightState closedState,
            NightOutcome outcome,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }

    private sealed class ThrowingBrowserEventRepository : IBrowserEventRepository
    {
        public ValueTask<StorageWriteResult> RecordBrowserEventAsync(
            BrowserPrivacyEvent browserEvent,
            CancellationToken cancellationToken = default) =>
            throw new IOException("simulated browser event storage failure");

        public ValueTask<StorageWriteResult> SaveLateNewEntertainmentWithBrowserEventAsync(
            NightState state,
            BrowserPrivacyEvent browserEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            throw new IOException("simulated browser event storage failure");
    }

    private sealed class ConflictOnceRepository(NightState initial) :
        INightStateRepository,
        IProgressRepository,
        IHistoryRepository,
        IBrowserEventRepository
    {
        private long _version = 4;

        public NightState State { get; private set; } = initial;

        public int SaveAttempts { get; private set; }

        public List<BrowserPrivacyEvent> RecordedEvents { get; } = [];

        public ValueTask<StorageResult<NightState?>> ReadActiveStateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StorageResult<NightState?>(
                StorageMode.Success,
                State,
                Version: _version));

        public ValueTask<StorageWriteResult> SaveLateNewEntertainmentWithBrowserEventAsync(
            NightState state,
            BrowserPrivacyEvent browserEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            if (SaveAttempts == 1)
            {
                _version++;
                return ValueTask.FromResult(StorageWriteResult.Conflict);
            }

            Assert.Equal(_version, expectedVersion);
            State = state;
            RecordedEvents.Add(browserEvent);
            _version++;
            return ValueTask.FromResult(StorageWriteResult.Success);
        }

        public ValueTask<StorageWriteResult> RecordBrowserEventAsync(
            BrowserPrivacyEvent browserEvent,
            CancellationToken cancellationToken = default)
        {
            RecordedEvents.Add(browserEvent);
            return ValueTask.FromResult(StorageWriteResult.Success);
        }

        public ValueTask<StorageWriteResult> SaveActiveStateWithEventAsync(
            NightState state,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageWriteResult> SaveActiveStateProgressWithEventAsync(
            NightState state,
            ProgressState progress,
            NightEvent nightEvent,
            long? expectedStateVersion = null,
            long? expectedProgressVersion = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageWriteResult> CloseActiveStateWithOutcomeAndEventAsync(
            NightState closedState,
            NightOutcome outcome,
            NightEvent nightEvent,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageResult<ProgressState>> ReadProgressAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StorageResult<ProgressState>(
                StorageMode.Success,
                ProgressState.Initial));

        public ValueTask<StorageWriteResult> SaveProgressAsync(
            ProgressState progress,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestOutcomesAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StorageResult<IReadOnlyList<NightOutcome>>(
                StorageMode.Success,
                Array.Empty<NightOutcome>()));

        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestEligibleOutcomesAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            ReadLatestOutcomesAsync(count, cancellationToken);

        public ValueTask<StorageWriteResult> SaveOutcomeAsync(
            NightOutcome outcome,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageWriteResult> RecordEventAsync(
            NightEvent nightEvent,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageWriteResult> PurgeEventsOlderThanAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageWriteResult> ClearHistoryAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);
    }

    private sealed class NullProgressRepository : IProgressRepository
    {
        public ValueTask<StorageResult<ProgressState>> ReadProgressAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StorageResult<ProgressState>(
                StorageMode.Success,
                ProgressState.Initial));

        public ValueTask<StorageWriteResult> SaveProgressAsync(
            ProgressState progress,
            long? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);
    }

    private sealed class NullHistoryRepository : IHistoryRepository
    {
        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestOutcomesAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StorageResult<IReadOnlyList<NightOutcome>>(
                StorageMode.Success,
                Array.Empty<NightOutcome>()));

        public ValueTask<StorageResult<IReadOnlyList<NightOutcome>>> ReadLatestEligibleOutcomesAsync(
            int count,
            CancellationToken cancellationToken = default) =>
            ReadLatestOutcomesAsync(count, cancellationToken);

        public ValueTask<StorageWriteResult> SaveOutcomeAsync(
            NightOutcome outcome,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageWriteResult> RecordEventAsync(
            NightEvent nightEvent,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageWriteResult> PurgeEventsOlderThanAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);

        public ValueTask<StorageWriteResult> ClearHistoryAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(StorageWriteResult.Success);
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
