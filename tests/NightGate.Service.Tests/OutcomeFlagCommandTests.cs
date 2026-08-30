using System.Collections.Immutable;
using NightGate.Core;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class OutcomeFlagCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(NightEventKind.DeliberateBypass)]
    [InlineData(NightEventKind.LateNewEntertainment)]
    [InlineData(NightEventKind.MissedLock)]
    public async Task RecordEvent_StateOutcomeFlagsPersistTransactionallyIntoClosedOutcome(
        NightEventKind kind)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState();
        await repository.SaveActiveStateWithEventAsync(state, CreateEvent());
        InMemoryServiceStatus status = new();
        NightMutationGate gate = new();
        FixedClock clock = new(Now.AddMinutes(1));
        NightGateProtocolCommandHandler handler = new(
            repository,
            repository,
            repository,
            status,
            status,
            new OverridePolicy(new FixedSnapshotProvider([])),
            gate,
            clock);

        ProtocolCommandResult recorded = await handler.ExecuteAsync(
            new RecordEventCommand(kind, NightPhase.LandingLocked, null));
        NightWindow window = new(
            state.NightDate,
            Now.AddHours(-3),
            Now.AddHours(-2),
            Now.AddHours(-1),
            Now.AddMinutes(-30),
            Now.AddHours(1));
        CoordinatorObservation closed = await new NightStateCoordinator(repository, gate)
            .ObserveAsync(window, NightPhase.Morning, window.Wake);
        NightOutcome outcome = Assert.Single((await repository.ReadLatestOutcomesAsync(4)).Value);

        Assert.Equal(StorageMode.Success, recorded.Mode);
        Assert.Null(closed.State);
        Assert.Equal(kind == NightEventKind.DeliberateBypass, outcome.DeliberateBypass);
        Assert.Equal(kind == NightEventKind.LateNewEntertainment, outcome.LateNewEntertainment);
        Assert.Equal(kind == NightEventKind.MissedLock, outcome.MissedLock);
    }

    private static NightState CreateState() => new(
        Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
        new DateOnly(2026, 7, 6),
        Now,
        NightPhase.LandingLocked,
        null,
        false,
        false,
        false,
        false,
        false,
        false);

    private static NightEvent CreateEvent() => new(
        Guid.NewGuid(),
        Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
        Now,
        NightEventKind.StateObserved,
        NightPhase.LandingLocked);

    private sealed class FixedSnapshotProvider(ImmutableArray<string> snapshot) :
        IAllowedProcessSnapshotProvider
    {
        public ImmutableArray<string> GetSnapshot() => snapshot;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
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
