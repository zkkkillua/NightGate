using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NightGate.Service.Tests;

public sealed class SqliteNightGateRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid BootSessionId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task CreateAndReopen_PersistsActiveStateProgressAndOutcomes()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository first = new(database.Path);
        NightState state = CreateState() with
        {
            LastObservedUptime = TimeSpan.FromHours(100),
            LastObservedBootSessionId = BootSessionId,
        };
        ProgressState progress = new(3, Now.AddHours(-200), new DateOnly(2026, 7, 6));
        NightOutcome outcome = CreateOutcome(new DateOnly(2026, 7, 6));

        Assert.Equal(StorageMode.Success, (await first.SaveActiveStateWithEventAsync(state, CreateEvent())).Mode);
        Assert.Equal(StorageMode.Success, (await first.SaveProgressAsync(progress)).Mode);
        Assert.Equal(StorageMode.Success, (await first.SaveOutcomeAsync(outcome)).Mode);

        SqliteNightGateRepository reopened = new(database.Path);
        StorageResult<NightState?> savedState = await reopened.ReadActiveStateAsync();
        StorageResult<ProgressState> savedProgress = await reopened.ReadProgressAsync();
        StorageResult<IReadOnlyList<NightOutcome>> savedOutcomes = await reopened.ReadLatestOutcomesAsync(4);

        Assert.Equal(StorageMode.Success, savedState.Mode);
        AssertEquivalent(state, savedState.Value);
        Assert.Equal(progress, savedProgress.Value);
        Assert.Equal([outcome], savedOutcomes.Value);
    }

    [Fact]
    public async Task LegacyActiveStateJsonWithoutBootSessionId_RemainsReadableAsUntrustedAnchor()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState() with
        {
            LastObservedUptime = TimeSpan.FromHours(100),
            LastObservedBootSessionId = BootSessionId,
        };
        await repository.SaveActiveStateWithEventAsync(state, CreateEvent());
        await using (SqliteConnection connection = Open(database.Path))
        {
            JsonObject json = JsonNode.Parse(await ScalarStringAsync(
                connection,
                "SELECT json FROM active_state WHERE singleton_id = 1;"))!.AsObject();
            Assert.True(json.Remove("lastObservedBootSessionId"));
            Assert.True(json.Remove("overrideReasons"));
            Assert.True(json.Remove("firstLockObservedAtUtc"));
            Assert.True(json.Remove("scheduledLockAtUtc"));
            Assert.True(json.Remove("protectionGapObserved"));
            Assert.True(json.Remove("scheduleTimeZoneSerialized"));
            await UpdateJsonAsync(connection, "active_state", json.ToJsonString());
        }

        StorageResult<NightState?> result = await repository.ReadActiveStateAsync();

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal(TimeSpan.FromHours(100), result.Value!.LastObservedUptime);
        Assert.Null(result.Value.LastObservedBootSessionId);
        Assert.False(result.Value.ProtectionGapObserved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidBootAnchor_ReturnsDegradedFailOpen(bool emptyIdentifier)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState() with
        {
            LastObservedUptime = TimeSpan.FromHours(100),
            LastObservedBootSessionId = BootSessionId,
        };
        await repository.SaveActiveStateWithEventAsync(state, CreateEvent());
        await using (SqliteConnection connection = Open(database.Path))
        {
            JsonObject json = JsonNode.Parse(await ScalarStringAsync(
                connection,
                "SELECT json FROM active_state WHERE singleton_id = 1;"))!.AsObject();
            if (emptyIdentifier)
            {
                json["lastObservedBootSessionId"] = Guid.Empty;
            }
            else
            {
                json["lastObservedUptime"] = null;
            }

            await UpdateJsonAsync(connection, "active_state", json.ToJsonString());
        }

        StorageResult<NightState?> result = await repository.ReadActiveStateAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Create_SetsSchemaVersionThreeAndRequiredTables()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadProgressAsync()).Mode);

        await using SqliteConnection connection = Open(database.Path);
        long version = await ScalarLongAsync(connection, "PRAGMA user_version;");
        string[] tables = await ReadStringsAsync(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;");

        Assert.Equal(3, version);
        Assert.Equal(
            [
                "active_state",
                "chrome_protection_health",
                "legacy_task_migrations",
                "night_outcomes",
                "night_self_reports",
                "notice_claims",
                "onboarding_state",
                "process_persistence",
                "progress",
                "raw_events",
                "rule_settings",
            ],
            tables);
    }

    [Fact]
    public async Task ConcurrentRepositories_MigrateLegacySchemaVersionOneWithoutDegradation()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            using TempDatabase database = new();
            await CreateLegacySchemaVersionOneAsync(database);
            SqliteNightGateRepository first = new(database.Path);
            SqliteNightGateRepository second = new(database.Path);
            using Barrier start = new(2);

            Task<StorageResult<ProgressState>> firstRead = Task.Run(async () =>
            {
                start.SignalAndWait();
                return await first.ReadProgressAsync();
            });
            Task<StorageResult<ProgressState>> secondRead = Task.Run(async () =>
            {
                start.SignalAndWait();
                return await second.ReadProgressAsync();
            });
            StorageResult<ProgressState>[] results = await Task
                .WhenAll(firstRead, secondRead)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.All(results, result =>
            {
                Assert.Equal(StorageMode.Success, result.Mode);
                Assert.Equal(ProgressState.Initial, result.Value);
                Assert.Equal(0, result.Version);
            });
            await using SqliteConnection connection = Open(database.Path);
            Assert.Equal(
                1,
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('active_state') WHERE name = 'row_version' AND type = 'INTEGER' AND [notnull] = 1 AND dflt_value = '0';"));
            Assert.Equal(
                1,
                await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM pragma_table_info('progress') WHERE name = 'row_version' AND type = 'INTEGER' AND [notnull] = 1 AND dflt_value = '0';"));
            Assert.Equal(3, await ScalarLongAsync(connection, "PRAGMA user_version;"));

            ProgressState advanced = ProgressState.Initial with { CurrentStep = 2 };
            StorageWriteResult write = await first.SaveProgressAsync(advanced, expectedVersion: 0);
            StorageResult<ProgressState> reread = await second.ReadProgressAsync();

            Assert.Equal(StorageMode.Success, write.Mode);
            Assert.Equal(StorageMode.Success, reread.Mode);
            Assert.Equal(advanced, reread.Value);
            Assert.Equal(1, reread.Version);
        }
    }

    [Fact]
    public async Task SaveActiveStateAndEvent_IsAtomicWhenEventInsertFails()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState original = CreateState();
        Assert.Equal(StorageMode.Success, (await repository.SaveActiveStateWithEventAsync(original, CreateEvent())).Mode);

        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(
                connection,
                "CREATE TRIGGER reject_events BEFORE INSERT ON raw_events BEGIN SELECT RAISE(ABORT, 'event rejected'); END;");
        }

        NightState changed = original with
        {
            LastObservedUtc = original.LastObservedUtc.AddMinutes(5),
            HighestBasePhaseReached = NightPhase.LandingLocked,
        };
        StorageWriteResult failed = await repository.SaveActiveStateWithEventAsync(changed, CreateEvent());
        StorageResult<NightState?> reloaded = await repository.ReadActiveStateAsync();

        Assert.Equal(StorageMode.Degraded, failed.Mode);
        Assert.False(failed.EnforcementEnabled);
        AssertEquivalent(original, reloaded.Value);
    }

    [Fact]
    public async Task SaveActiveStateProgressAndEvent_IsAtomicWhenEventInsertFails()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState originalState = CreateState() with { ActiveOverride = null, TeamRescueUsed = false };
        ProgressState originalProgress = ProgressState.Initial;
        await repository.SaveActiveStateWithEventAsync(originalState, CreateEvent());
        await repository.SaveProgressAsync(originalProgress);

        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(
                connection,
                "CREATE TRIGGER reject_override_event BEFORE INSERT ON raw_events BEGIN SELECT RAISE(ABORT, 'event rejected'); END;");
        }

        NightState changedState = originalState with { TeamRescueUsed = true };
        ProgressState changedProgress = originalProgress with { LastTeamRescueAtUtc = Now };
        StorageWriteResult result = await repository.SaveActiveStateProgressWithEventAsync(
            changedState,
            changedProgress,
            CreateEvent());

        Assert.Equal(StorageMode.Degraded, result.Mode);
        AssertEquivalent(originalState, (await repository.ReadActiveStateAsync()).Value);
        Assert.Equal(originalProgress, (await repository.ReadProgressAsync()).Value);
    }

    [Fact]
    public async Task CloseStateOutcomeAndEvent_IsAtomicWhenEventInsertFails()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState original = CreateState() with { ActiveOverride = null };
        await repository.SaveActiveStateWithEventAsync(original, CreateEvent());
        await using (SqliteConnection connection = Open(database.Path))
        {
            await ExecuteAsync(
                connection,
                "CREATE TRIGGER reject_close_event BEFORE INSERT ON raw_events BEGIN SELECT RAISE(ABORT, 'event rejected'); END;");
        }

        NightOutcome outcome = CreateOutcome(original.NightDate);
        StorageWriteResult result = await repository.CloseActiveStateWithOutcomeAndEventAsync(
            original with { IsClosed = true },
            outcome,
            CreateEvent());

        Assert.Equal(StorageMode.Degraded, result.Mode);
        AssertEquivalent(original, (await repository.ReadActiveStateAsync()).Value);
        Assert.Empty((await repository.ReadLatestOutcomesAsync(4)).Value);
    }

    [Fact]
    public async Task PurgeEventsOlderThan_DeletesBeforeButRetainsExactNinetyDayBoundary()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        DateTimeOffset cutoff = Now.AddDays(-90);
        NightEvent before = CreateEvent(cutoff.AddTicks(-1));
        NightEvent exact = CreateEvent(cutoff);
        NightEvent after = CreateEvent(cutoff.AddTicks(1));
        await repository.RecordEventAsync(before);
        await repository.RecordEventAsync(exact);
        await repository.RecordEventAsync(after);

        StorageWriteResult result = await repository.PurgeEventsOlderThanAsync(cutoff);

        await using SqliteConnection connection = Open(database.Path);
        string[] ids = await ReadStringsAsync(
            connection,
            "SELECT event_id FROM raw_events ORDER BY occurred_utc;");
        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.Equal([exact.EventId.ToString("D"), after.EventId.ToString("D")], ids);
    }

    [Fact]
    public async Task ClearHistory_RemovesReportsButPreservesActiveStateRescueTimestampAndStep()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState();
        ProgressState progress = new(4, Now.AddHours(-12), new DateOnly(2026, 7, 6));
        await repository.SaveActiveStateWithEventAsync(state, CreateEvent());
        await repository.SaveProgressAsync(progress);
        await repository.SaveOutcomeAsync(CreateOutcome(new DateOnly(2026, 7, 6)));
        await repository.RecordEventAsync(CreateEvent());

        StorageWriteResult result = await repository.ClearHistoryAsync();

        Assert.Equal(StorageMode.Success, result.Mode);
        AssertEquivalent(state, (await repository.ReadActiveStateAsync()).Value);
        Assert.Equal(progress, (await repository.ReadProgressAsync()).Value);
        Assert.Empty((await repository.ReadLatestOutcomesAsync(4)).Value);
        await using SqliteConnection connection = Open(database.Path);
        Assert.Equal(0, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_events;"));
    }

    [Fact]
    public async Task LockedDatabase_ReturnsDegradedWithoutThrowing()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.ReadProgressAsync();
        await using SqliteConnection lockConnection = Open(database.Path);
        await ExecuteAsync(lockConnection, "BEGIN EXCLUSIVE;");

        StorageWriteResult result = await repository.SaveProgressAsync(ProgressState.Initial with { CurrentStep = 2 });

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        await ExecuteAsync(lockConnection, "ROLLBACK;");
    }

    [Fact]
    public async Task CorruptDatabase_ReturnsDegradedWithoutThrowing()
    {
        using TempDatabase database = new();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(database.Path)!);
        await File.WriteAllBytesAsync(database.Path, [0x01, 0x02, 0x03, 0x04, 0x05]);
        SqliteNightGateRepository repository = new(database.Path);

        StorageResult<ProgressState> result = await repository.ReadProgressAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Equal(ProgressState.Initial, result.Value);
    }

    [Fact]
    public async Task ParseableProgressRowWithOutOfRangeStep_ReturnsDegradedFailOpen()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadProgressAsync()).Mode);
        await using (SqliteConnection connection = Open(database.Path))
        {
            await UpdateJsonAsync(
                connection,
                "progress",
                "{\"currentStep\":999,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":null}");
        }

        StorageResult<ProgressState> result = await repository.ReadProgressAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Equal(ProgressState.Initial, result.Value);
    }

    public static TheoryData<string> InvalidPendingProgressJson => new()
    {
        "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":null,\"pendingStep\":2}",
        "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"2026-07-08\",\"pendingStepUnlockedByNightDate\":\"2026-07-08\"}",
        "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"2026-07-08\",\"pendingStep\":3,\"pendingStepUnlockedByNightDate\":\"2026-07-08\"}",
        "{\"currentStep\":4,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"2026-07-08\",\"pendingStep\":5,\"pendingStepUnlockedByNightDate\":\"2026-07-08\"}",
        "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"2026-07-07\",\"pendingStep\":2,\"pendingStepUnlockedByNightDate\":\"2026-07-08\"}",
        "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"2026-07-08\",\"pendingStep\":2,\"pendingStepUnlockedByNightDate\":\"2026-07-08\",\"pendingStepConfirmedAtUtc\":\"2026-07-08T14:00:00Z\"}",
        "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"2026-07-08\",\"pendingStep\":2,\"pendingStepUnlockedByNightDate\":\"2026-07-08\",\"pendingStepEffectiveNightDate\":\"2026-07-09\"}",
        "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"2026-07-08\",\"pendingStep\":2,\"pendingStepUnlockedByNightDate\":\"2026-07-08\",\"pendingStepConfirmedAtUtc\":\"2026-07-08T22:00:00+08:00\",\"pendingStepEffectiveNightDate\":\"2026-07-09\"}",
        "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"2026-07-08\",\"pendingStep\":2,\"pendingStepUnlockedByNightDate\":\"2026-07-08\",\"pendingStepConfirmedAtUtc\":\"2026-07-08T14:00:00Z\",\"pendingStepEffectiveNightDate\":\"2026-07-07\"}",
        "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"0001-01-01\"}",
        "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"2026-07-08\",\"pendingStep\":2,\"pendingStepUnlockedByNightDate\":\"0001-01-01\"}",
    };

    [Theory]
    [MemberData(nameof(InvalidPendingProgressJson))]
    public async Task ParseableProgressRowWithImpossiblePendingState_ReturnsDegradedFailOpen(
        string invalidJson)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadProgressAsync()).Mode);
        await using (SqliteConnection connection = Open(database.Path))
        {
            await UpdateJsonAsync(connection, "progress", invalidJson);
        }

        StorageResult<ProgressState> result = await repository.ReadProgressAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Equal(ProgressState.Initial, result.Value);
    }

    [Fact]
    public async Task LegacyTask5ARowsWithoutPendingOrReportFieldsRemainReadable()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState();
        NightOutcome outcome = CreateOutcome(new DateOnly(2026, 7, 6));
        await repository.SaveActiveStateWithEventAsync(state, CreateEvent());
        await repository.SaveOutcomeAsync(outcome);
        await using (SqliteConnection connection = Open(database.Path))
        {
            await UpdateJsonAsync(
                connection,
                "progress",
                "{\"currentStep\":2,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":\"2026-07-06\"}");
            await RemoveJsonPropertiesAsync(
                connection,
                "active_state",
                "overrideReasons",
                "firstLockObservedAtUtc",
                "scheduledLockAtUtc",
                "protectionGapObserved",
                "scheduleTimeZoneSerialized");
            await RemoveJsonPropertiesAsync(
                connection,
                "night_outcomes",
                "overrideReasons",
                "firstLockObservedAtUtc",
                "scheduledLockAtUtc",
                "protectionGapObserved",
                "scheduleTimeZoneSerialized");
        }

        StorageResult<ProgressState> progress = await repository.ReadProgressAsync();
        StorageResult<NightState?> active = await repository.ReadActiveStateAsync();
        StorageResult<IReadOnlyList<NightOutcome>> outcomes =
            await repository.ReadLatestOutcomesAsync(1);

        Assert.Equal(StorageMode.Success, progress.Mode);
        Assert.Equal(2, progress.Value.CurrentStep);
        Assert.Null(progress.Value.PendingStep);
        Assert.Equal(StorageMode.Success, active.Mode);
        Assert.Equal(OverrideReasonSummary.Empty, active.Value!.OverrideReasons);
        Assert.Null(active.Value.FirstLockObservedAtUtc);
        Assert.Null(active.Value.ScheduledLockAtUtc);
        Assert.False(active.Value.ProtectionGapObserved);
        Assert.Equal(StorageMode.Success, outcomes.Mode);
        Assert.Equal(OverrideReasonSummary.Empty, Assert.Single(outcomes.Value).OverrideReasons);
        Assert.Null(Assert.Single(outcomes.Value).FirstLockObservedAtUtc);
        Assert.Null(Assert.Single(outcomes.Value).ScheduledLockAtUtc);
        Assert.False(Assert.Single(outcomes.Value).ProtectionGapObserved);
        Assert.Null(Assert.Single(outcomes.Value).ScheduleTimeZoneSerialized);
        Assert.False(Assert.Single(outcomes.Value).Qualifies);
    }

    [Theory]
    [InlineData("overrideReasons", "null")]
    [InlineData("OverrideReasons", "null")]
    [InlineData("overrideReasons", "{\"teamRescueCount\":-1}")]
    [InlineData("firstLockObservedAtUtc", "\"2026-07-07T00:01:00Z\"")]
    [InlineData("firstLockObservedAtUtc", "\"2026-07-07T08:00:00+08:00\"")]
    [InlineData("scheduledLockAtUtc", "\"2026-07-07T08:00:00+08:00\"")]
    [InlineData("scheduleTimeZoneSerialized", "\"not-a-time-zone\"")]
    public async Task CorruptTask5AActiveStateFacts_ReturnDegradedFailOpen(
        string propertyName,
        string rawJsonValue)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveActiveStateWithEventAsync(CreateState(), CreateEvent());
        await using (SqliteConnection connection = Open(database.Path))
        {
            await SetJsonPropertyAsync(connection, "active_state", propertyName, rawJsonValue);
        }

        StorageResult<NightState?> result = await repository.ReadActiveStateAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("overrideReasons", "null")]
    [InlineData("OVERRIDEREASONS", "null")]
    [InlineData("overrideReasons", "{\"emergencyHealthCount\":1000001}")]
    [InlineData("firstLockObservedAtUtc", "\"2026-07-07T00:01:00Z\"")]
    [InlineData("firstLockObservedAtUtc", "\"2026-07-07T08:00:00+08:00\"")]
    [InlineData("scheduledLockAtUtc", "\"2026-07-07T00:01:00Z\"")]
    [InlineData("scheduledLockAtUtc", "\"2026-07-07T08:00:00+08:00\"")]
    public async Task CorruptTask5AOutcomeFacts_ReturnDegradedFailOpen(
        string propertyName,
        string rawJsonValue)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveOutcomeAsync(CreateOutcome(new DateOnly(2026, 7, 6)));
        await using (SqliteConnection connection = Open(database.Path))
        {
            await SetJsonPropertyAsync(connection, "night_outcomes", propertyName, rawJsonValue);
        }

        StorageResult<IReadOnlyList<NightOutcome>> result =
            await repository.ReadLatestOutcomesAsync(1);

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task SavingNullTask5AReasonSummaries_IsRejectedFailOpen()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);

        StorageWriteResult stateWrite = await repository.SaveActiveStateWithEventAsync(
            CreateState() with { OverrideReasons = null! },
            CreateEvent());
        StorageWriteResult outcomeWrite = await repository.SaveOutcomeAsync(
            CreateOutcome(new DateOnly(2026, 7, 6)) with { OverrideReasons = null! });

        Assert.Equal(StorageMode.Degraded, stateWrite.Mode);
        Assert.False(stateWrite.EnforcementEnabled);
        Assert.Equal(StorageMode.Degraded, outcomeWrite.Mode);
        Assert.False(outcomeWrite.EnforcementEnabled);
    }

    [Fact]
    public async Task ParseableActiveStateRowWithTemporaryBasePhase_ReturnsDegradedFailOpen()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState();
        await repository.SaveActiveStateWithEventAsync(state, CreateEvent());
        string invalidJson = JsonSerializer.Serialize(
            state with { HighestBasePhaseReached = NightPhase.CoolingOff },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using (SqliteConnection connection = Open(database.Path))
        {
            await UpdateJsonAsync(connection, "active_state", invalidJson);
        }

        StorageResult<NightState?> result = await repository.ReadActiveStateAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("LastStart, Grace")]
    [InlineData("grace")]
    [InlineData(" Grace ")]
    public async Task ParseableActiveStateRowWithNoncanonicalPhaseToken_ReturnsDegradedFailOpen(
        string phaseToken)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveActiveStateWithEventAsync(CreateState(), CreateEvent());
        await using (SqliteConnection connection = Open(database.Path))
        {
            string json = await ScalarStringAsync(
                connection,
                "SELECT json FROM active_state WHERE singleton_id = 1;");
            string invalidJson = json.Replace(
                "\"highestBasePhaseReached\":\"Grace\"",
                $"\"highestBasePhaseReached\":\"{phaseToken}\"",
                StringComparison.Ordinal);
            Assert.NotEqual(json, invalidJson);
            await UpdateJsonAsync(connection, "active_state", invalidJson);
        }

        StorageResult<NightState?> result = await repository.ReadActiveStateAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(20)]
    public async Task ParseableActiveOverrideOutsideStateObservation_ReturnsDegradedFailOpen(
        int lastObservedOffsetMinutes)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = CreateState();
        await repository.SaveActiveStateWithEventAsync(state, CreateEvent());
        NightState invalidState = state with
        {
            LastObservedUtc = state.ActiveOverride!.RequestedAtUtc.AddMinutes(lastObservedOffsetMinutes),
        };
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        string invalidJson = JsonSerializer.Serialize(invalidState, options);
        await using (SqliteConnection connection = Open(database.Path))
        {
            await UpdateJsonAsync(connection, "active_state", invalidJson);
        }

        StorageResult<NightState?> result = await repository.ReadActiveStateAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ParseableOutcomeRowWithUnstableIdentity_ReturnsDegradedFailOpen()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightOutcome outcome = CreateOutcome(new DateOnly(2026, 7, 6));
        await repository.SaveOutcomeAsync(outcome);
        string invalidJson = JsonSerializer.Serialize(
            outcome with { NightId = Guid.Empty },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using (SqliteConnection connection = Open(database.Path))
        {
            await UpdateJsonAsync(connection, "night_outcomes", invalidJson);
        }

        StorageResult<IReadOnlyList<NightOutcome>> result =
            await repository.ReadLatestOutcomesAsync(4);

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task UnusableParentPath_ReturnsDegradedWithoutThrowing()
    {
        using TempDatabase database = new();
        Directory.CreateDirectory(database.DirectoryPath);
        string parentFile = System.IO.Path.Combine(database.DirectoryPath, "not-a-directory");
        await File.WriteAllTextAsync(parentFile, "blocking file");
        SqliteNightGateRepository repository = new(System.IO.Path.Combine(parentFile, "state.db"));

        StorageResult<NightState?> result = await repository.ReadActiveStateAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task ReadOnlyDatabaseFile_ReturnsDegradedWithoutHanging()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadProgressAsync()).Mode);
        FileAttributes originalAttributes = File.GetAttributes(database.Path);
        try
        {
            File.SetAttributes(database.Path, originalAttributes | FileAttributes.ReadOnly);

            StorageResult<ProgressState> result = await repository
                .ReadProgressAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(StorageMode.Degraded, result.Mode);
            Assert.False(result.EnforcementEnabled);
            Assert.Equal(ProgressState.Initial, result.Value);
        }
        finally
        {
            File.SetAttributes(database.Path, originalAttributes);
        }
    }

    [Fact]
    public void ProductionDatabasePath_IsUnderCommonProgramDataNightGate()
    {
        string expected = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NightGate",
            "state.db");

        Assert.Equal(expected, SqliteNightGateRepository.GetProductionDatabasePath());
    }

    private static NightState CreateState()
    {
        ActiveOverride activeOverride = new(
            OverrideKind.TeamRescue,
            Now,
            Now,
            Now.AddMinutes(20),
            ["game.exe", "voice.exe"]);
        return new(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            new DateOnly(2026, 7, 6),
            Now,
            NightPhase.Grace,
            activeOverride,
            false,
            true,
            false,
            false,
            false,
            false);
    }

    private static NightOutcome CreateOutcome(DateOnly date)
    {
        DateTimeOffset scheduledLock = Now.AddMinutes(-1);
        return new(
            Guid.NewGuid(),
            date,
            Now,
            false,
            false,
            false,
            false,
            false,
            false,
            FirstLockObservedAtUtc: scheduledLock,
            ScheduledLockAtUtc: scheduledLock);
    }

    private static NightEvent CreateEvent(DateTimeOffset? occurredAtUtc = null) => new(
        Guid.NewGuid(),
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        occurredAtUtc ?? Now,
        NightEventKind.StateObserved,
        NightPhase.Grace);

    private static void AssertEquivalent(NightState expected, NightState? actual)
    {
        Assert.NotNull(actual);
        if (expected.ActiveOverride is null)
        {
            Assert.Null(actual.ActiveOverride);
            Assert.Equal(expected, actual);
            return;
        }

        Assert.NotNull(actual.ActiveOverride);
        Assert.Equal(
            expected.ActiveOverride.AllowedProcessIdentifiers.ToArray(),
            actual.ActiveOverride.AllowedProcessIdentifiers.ToArray());
        NightState normalized = actual with
        {
            ActiveOverride = actual.ActiveOverride with
            {
                AllowedProcessIdentifiers = expected.ActiveOverride.AllowedProcessIdentifiers,
            },
        };
        Assert.Equal(expected, normalized);
    }

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

    private static async Task UpdateJsonAsync(
        SqliteConnection connection,
        string table,
        string json)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET json = $json;";
        command.Parameters.AddWithValue("$json", json);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveJsonPropertiesAsync(
        SqliteConnection connection,
        string table,
        params string[] propertyNames)
    {
        JsonObject json = JsonNode.Parse(await ScalarStringAsync(
            connection,
            $"SELECT json FROM {table} LIMIT 1;"))!.AsObject();
        foreach (string propertyName in propertyNames)
        {
            Assert.True(json.Remove(propertyName));
        }

        await UpdateJsonAsync(connection, table, json.ToJsonString());
    }

    private static async Task SetJsonPropertyAsync(
        SqliteConnection connection,
        string table,
        string propertyName,
        string rawJsonValue)
    {
        JsonObject json = JsonNode.Parse(await ScalarStringAsync(
            connection,
            $"SELECT json FROM {table} LIMIT 1;"))!.AsObject();
        json[propertyName] = JsonNode.Parse(rawJsonValue);
        await UpdateJsonAsync(connection, table, json.ToJsonString());
    }

    private static async Task CreateLegacySchemaVersionOneAsync(TempDatabase database)
    {
        Directory.CreateDirectory(database.DirectoryPath);
        await using SqliteConnection connection = Open(database.Path);
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE active_state (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                json TEXT NOT NULL
            );
            CREATE TABLE progress (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                json TEXT NOT NULL
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
            INSERT INTO progress (singleton_id, json)
            VALUES (1, '{"currentStep":1,"lastTeamRescueAtUtc":null,"lastProgressionNightDate":null}');
            PRAGMA user_version = 1;
            """);
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())
            ?? throw new InvalidDataException("Expected a stored string value.");
    }

    private static async Task<string[]> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        List<string> values = [];
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
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
