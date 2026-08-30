using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class WorkstationLockCommandTests
{
    private static readonly DateTimeOffset LastObserved =
        new(2026, 7, 7, 0, 40, 0, TimeSpan.Zero);
    private static readonly Guid BootSession =
        Guid.Parse("77777777-7777-7777-7777-777777777777");

    [Fact]
    public async Task ActualLock_UsesLogicalServiceTimeAndCopiesFactToOutcome()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = ActiveState();
        await repository.SaveActiveStateWithEventAsync(state, SeedEvent(state));
        ObservationClock clock = new(new(
            LastObserved.AddMinutes(-5),
            TimeSpan.FromMinutes(101),
            BootSession));
        NightGateProtocolCommandHandler handler = Handler(repository, clock);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RecordEventCommand(NightEventKind.WorkstationLocked, null, null));

        DateTimeOffset logicalLockTime = LastObserved.AddMinutes(1);
        NightState updated = (await repository.ReadActiveStateAsync()).Value!;
        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True(result.Payload.GetProperty("recorded").GetBoolean());
        Assert.Equal(logicalLockTime, updated.FirstLockObservedAtUtc);
        Assert.Equal(logicalLockTime, updated.LastObservedUtc);
        Assert.Equal(TimeSpan.FromMinutes(101), updated.LastObservedUptime);

        NightWindow window = Window(state.NightDate);
        await new NightStateCoordinator(repository, new NightMutationGate())
            .ObserveAsync(window, NightPhase.Morning, window.Wake);
        NightOutcome outcome = Assert.Single(
            (await repository.ReadLatestOutcomesAsync(4)).Value);
        Assert.Equal(logicalLockTime, outcome.FirstLockObservedAtUtc);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ActualLock_QualificationUsesDeadlineCapturedAtNightStart(
        int lockOffsetMinutes,
        bool expectedQualification)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightWindow window = Window(new DateOnly(2026, 7, 6));
        NightStateCoordinator coordinator = new(repository, new NightMutationGate());
        await coordinator.ObserveAsync(window, NightPhase.Free, window.ProtectedStart);
        ObservationClock clock = new(new(window.Lock.AddMinutes(lockOffsetMinutes)));

        ProtocolCommandResult result = await Handler(repository, clock).ExecuteAsync(
            new RecordEventCommand(NightEventKind.WorkstationLocked, null, null));
        await coordinator.ObserveAsync(window, NightPhase.Morning, window.Wake);

        Assert.True(result.Payload.GetProperty("recorded").GetBoolean());
        NightOutcome outcome = Assert.Single(
            (await repository.ReadLatestOutcomesAsync(1)).Value);
        Assert.Equal(window.Lock, outcome.ScheduledLockAtUtc);
        Assert.Equal(expectedQualification, outcome.Qualifies);
    }

    [Fact]
    public async Task NightWithoutActualLock_DoesNotQualify()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightWindow window = Window(new DateOnly(2026, 7, 6));
        NightStateCoordinator coordinator = new(repository, new NightMutationGate());
        await coordinator.ObserveAsync(window, NightPhase.Free, window.ProtectedStart);

        await coordinator.ObserveAsync(window, NightPhase.Morning, window.Wake);

        NightOutcome outcome = Assert.Single(
            (await repository.ReadLatestOutcomesAsync(1)).Value);
        Assert.Equal(window.Lock, outcome.ScheduledLockAtUtc);
        Assert.Null(outcome.FirstLockObservedAtUtc);
        Assert.False(outcome.Qualifies);
    }

    [Fact]
    public async Task RecordedLateLocksDoNotUnlockProgression()
    {
        DateOnly[] dates =
        [
            new(2026, 7, 5),
            new(2026, 7, 6),
            new(2026, 7, 7),
            new(2026, 7, 8),
        ];
        int[] lockOffsets = [0, 0, 1, 1];
        NightOutcome[] outcomes = new NightOutcome[dates.Length];
        for (int index = 0; index < dates.Length; index++)
        {
            outcomes[index] = await CaptureOutcomeAsync(dates[index], lockOffsets[index]);
        }

        ProgressState result = ProgressionEngine.Advance(ProgressState.Initial, outcomes);

        Assert.Null(result.PendingStep);
        Assert.Equal(new DateOnly(2026, 7, 8), result.LastProgressionNightDate);
    }

    [Fact]
    public async Task RepeatedActualLock_PreservesFirstFactAndDoesNotDuplicateRawEvent()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = ActiveState();
        await repository.SaveActiveStateWithEventAsync(state, SeedEvent(state));
        ObservationClock clock = new(new(
            LastObserved.AddMinutes(1),
            TimeSpan.FromMinutes(101),
            BootSession));
        NightGateProtocolCommandHandler handler = Handler(repository, clock);

        await handler.ExecuteAsync(
            new RecordEventCommand(NightEventKind.WorkstationLocked, null, null));
        clock.Observation = new(
            LastObserved.AddMinutes(2),
            TimeSpan.FromMinutes(102),
            BootSession);
        ProtocolCommandResult repeated = await handler.ExecuteAsync(
            new RecordEventCommand(NightEventKind.WorkstationLocked, null, null));

        NightState updated = (await repository.ReadActiveStateAsync()).Value!;
        Assert.True(repeated.Payload.GetProperty("recorded").GetBoolean());
        Assert.Equal(LastObserved.AddMinutes(1), updated.FirstLockObservedAtUtc);
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(1, await CountEventsAsync(connection, NightEventKind.WorkstationLocked));
    }

    [Fact]
    public async Task ActualLockWithoutActiveNight_IsRejectedWithoutRawEvent()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = Handler(
            repository,
            new ObservationClock(new(LastObserved)));

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new RecordEventCommand(NightEventKind.WorkstationLocked, null, null));

        Assert.False(result.Payload.GetProperty("recorded").GetBoolean());
        Assert.Equal("noActiveNight", result.Payload.GetProperty("error").GetString());
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(0, await CountEventsAsync(connection, NightEventKind.WorkstationLocked));
    }

    private static NightGateProtocolCommandHandler Handler(
        SqliteNightGateRepository repository,
        IClock clock)
    {
        InMemoryServiceStatus status = new();
        return new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new EmptyAllowedProcesses()),
            new NightMutationGate(),
            clock);
    }

    private static async Task<NightOutcome> CaptureOutcomeAsync(
        DateOnly nightDate,
        int lockOffsetMinutes)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightWindow window = Window(nightDate);
        NightStateCoordinator coordinator = new(repository, new NightMutationGate());
        await coordinator.ObserveAsync(window, NightPhase.Free, window.ProtectedStart);
        await Handler(
                repository,
                new ObservationClock(new(window.Lock.AddMinutes(lockOffsetMinutes))))
            .ExecuteAsync(new RecordEventCommand(NightEventKind.WorkstationLocked, null, null));
        await coordinator.ObserveAsync(window, NightPhase.Morning, window.Wake);
        return Assert.Single((await repository.ReadLatestOutcomesAsync(1)).Value);
    }

    private static NightState ActiveState() => new(
        Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
        new(2026, 7, 6),
        LastObserved,
        NightPhase.LandingLocked,
        null,
        false,
        false,
        false,
        false,
        false,
        false,
        LastObservedUptime: TimeSpan.FromMinutes(100),
        LastObservedBootSessionId: BootSession);

    private static NightEvent SeedEvent(NightState state) => new(
        Guid.NewGuid(),
        state.NightId,
        state.LastObservedUtc,
        NightEventKind.StateObserved,
        NightPhase.LandingLocked);

    private static NightWindow Window(DateOnly nightDate)
    {
        DateTimeOffset lockTime = new(
            nightDate.AddDays(1).ToDateTime(new TimeOnly(0, 40)),
            TimeSpan.Zero);
        return new(
            nightDate,
            new DateTimeOffset(
                nightDate.ToDateTime(new TimeOnly(21, 0)),
                TimeSpan.Zero),
            lockTime.AddMinutes(-100),
            lockTime,
            lockTime.AddMinutes(20),
            lockTime.AddHours(8).AddMinutes(20));
    }

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private static async Task<long> CountEventsAsync(
        SqliteConnection connection,
        NightEventKind kind)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM raw_events WHERE json_extract(json, '$.kind') = $kind;";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed class ObservationClock(ClockObservation observation) : IClock
    {
        public ClockObservation Observation { get; set; } = observation;

        public DateTimeOffset UtcNow => Observation.UtcNow;

        public ClockObservation Observe() => Observation;
    }

    private sealed class EmptyAllowedProcesses : IAllowedProcessSnapshotProvider
    {
        public ImmutableArray<string> GetSnapshot() => [];
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
