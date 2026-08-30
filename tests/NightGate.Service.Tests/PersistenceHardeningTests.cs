using System.Globalization;
using Microsoft.Data.Sqlite;
using NightGate.Core;
using NightGate.Service;
using System.Text.Json.Nodes;

namespace NightGate.Service.Tests;

public sealed class PersistenceHardeningTests
{
    private const int MaximumRuleSettingsJsonBytes = 512 * 1024;
    private static readonly DateTimeOffset Now =
        new(2026, 7, 8, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Night = new(2026, 7, 7);

    public static TheoryData<string, string> MalformedSingletonShapes => new()
    {
        { "onboarding_state", "{}" },
        {
            "onboarding_state",
            "{\"wizardVersion\":1,\"CompletedStep\":0,\"chromeVerified\":false,\"incognitoProtected\":false,\"incognitoWarningAcknowledged\":false,\"iPhoneConfirmedThroughStep\":0,\"completedAtUtc\":null}"
        },
        {
            "onboarding_state",
            "{\"wizardVersion\":1,\"completedStep\":0,\"completedStep\":0,\"chromeVerified\":false,\"incognitoProtected\":false,\"incognitoWarningAcknowledged\":false,\"iPhoneConfirmedThroughStep\":0,\"completedAtUtc\":null}"
        },
        {
            "onboarding_state",
            "{\"wizardVersion\":1,\"completedStep\":0,\"chromeVerified\":false,\"incognitoProtected\":false,\"incognitoWarningAcknowledged\":false,\"iPhoneConfirmedThroughStep\":0,\"completedAtUtc\":null,\"unknown\":true}"
        },
        { "rule_settings", "{}" },
        {
            "rule_settings",
            "{\"ActiveAppRules\":[],\"activeSiteRules\":[],\"pendingAppRules\":null,\"pendingSiteRules\":null,\"pendingEffectiveNightDate\":null,\"pendingSavedAtUtc\":null}"
        },
        {
            "rule_settings",
            "{\"activeAppRules\":[],\"activeAppRules\":[],\"activeSiteRules\":[],\"pendingAppRules\":null,\"pendingSiteRules\":null,\"pendingEffectiveNightDate\":null,\"pendingSavedAtUtc\":null}"
        },
        {
            "rule_settings",
            "{\"activeAppRules\":[],\"activeSiteRules\":[],\"pendingAppRules\":null,\"pendingSiteRules\":null,\"pendingEffectiveNightDate\":null}"
        },
    };

    [Theory]
    [MemberData(nameof(MalformedSingletonShapes))]
    public async Task SingletonJson_RequiresExactCanonicalShape(string table, string json)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadOnboardingAsync()).Mode);
        await UpdateAsync(database.Path, $"UPDATE {table} SET json = $value;", json);

        StorageMode mode = table == "onboarding_state"
            ? (await repository.ReadOnboardingAsync()).Mode
            : (await repository.ReadRuleSettingsAsync()).Mode;

        Assert.Equal(StorageMode.Degraded, mode);
    }

    [Fact]
    public async Task LegacyProgressShape_RemainsReadableButDuplicateCanonicalFieldIsCorrupt()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadProgressAsync()).Mode);
        await UpdateAsync(
            database.Path,
            "UPDATE progress SET json = $value;",
            "{\"currentStep\":2,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":null}");

        Assert.Equal(StorageMode.Success, (await repository.ReadProgressAsync()).Mode);

        await UpdateAsync(
            database.Path,
            "UPDATE progress SET json = $value;",
            "{\"currentStep\":2,\"currentStep\":2,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":null}");

        Assert.Equal(StorageMode.Degraded, (await repository.ReadProgressAsync()).Mode);
    }

    [Fact]
    public async Task NightState_RecognizesEveryHistoricalSerializedFieldVersion()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = ActiveState();
        await repository.SaveActiveStateWithEventAsync(state, StateEvent(state.NightId));
        JsonObject current = await ReadJsonObjectAsync(database.Path, "active_state");

        JsonObject initial = CloneWithout(
            current,
            "lastObservedUptime",
            "lastObservedBootSessionId",
            "overrideReasons",
            "firstLockObservedAtUtc",
            "scheduledLockAtUtc",
            "protectionGapObserved",
            "scheduleTimeZoneSerialized");
        JsonObject uptimeOnly = CloneWithout(
            current,
            "lastObservedBootSessionId",
            "overrideReasons",
            "firstLockObservedAtUtc",
            "scheduledLockAtUtc",
            "protectionGapObserved",
            "scheduleTimeZoneSerialized");
        JsonObject uptimeAndBoot = CloneWithout(
            current,
            "overrideReasons",
            "firstLockObservedAtUtc",
            "scheduledLockAtUtc",
            "protectionGapObserved",
            "scheduleTimeZoneSerialized");
        JsonObject task5A = CloneWithout(
            current,
            "scheduledLockAtUtc",
            "protectionGapObserved",
            "scheduleTimeZoneSerialized");
        JsonObject beforeProtectionGap = CloneWithout(
            current,
            "protectionGapObserved",
            "scheduleTimeZoneSerialized");
        JsonObject beforeScheduleTimeZone = CloneWithout(
            current,
            "scheduleTimeZoneSerialized");

        foreach (JsonObject legalVersion in new[]
                 {
                     initial,
                     uptimeOnly,
                     uptimeAndBoot,
                     task5A,
                     beforeProtectionGap,
                     beforeScheduleTimeZone,
                     current,
                 })
        {
            await UpdateAsync(
                database.Path,
                "UPDATE active_state SET json = $value;",
                legalVersion.ToJsonString());
            Assert.Equal(StorageMode.Success, (await repository.ReadActiveStateAsync()).Mode);
        }
    }

    [Fact]
    public async Task NightState_RejectsHybridHistoricalFieldGroups()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightState state = ActiveState();
        await repository.SaveActiveStateWithEventAsync(state, StateEvent(state.NightId));
        JsonObject current = await ReadJsonObjectAsync(database.Path, "active_state");
        JsonObject task5AFactsWithoutBoot = CloneWithout(
            current,
            "lastObservedBootSessionId");
        JsonObject incompleteTask5AFacts = CloneWithout(current, "overrideReasons");

        foreach (JsonObject corrupt in new[] { task5AFactsWithoutBoot, incompleteTask5AFacts })
        {
            await UpdateAsync(
                database.Path,
                "UPDATE active_state SET json = $value;",
                corrupt.ToJsonString());
            Assert.Equal(StorageMode.Degraded, (await repository.ReadActiveStateAsync()).Mode);
        }
    }

    [Fact]
    public async Task Progress_RequiresThePendingVersionFieldsAsOneCompleteGroup()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadProgressAsync()).Mode);
        const string legacy =
            "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":null}";
        const string current =
            "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":null,\"pendingStep\":null,\"pendingStepUnlockedByNightDate\":null,\"pendingStepConfirmedAtUtc\":null,\"pendingStepEffectiveNightDate\":null}";
        const string partial =
            "{\"currentStep\":1,\"lastTeamRescueAtUtc\":null,\"lastProgressionNightDate\":null,\"pendingStep\":null}";

        foreach (string legalVersion in new[] { legacy, current })
        {
            await UpdateAsync(database.Path, "UPDATE progress SET json = $value;", legalVersion);
            Assert.Equal(StorageMode.Success, (await repository.ReadProgressAsync()).Mode);
        }

        await UpdateAsync(database.Path, "UPDATE progress SET json = $value;", partial);
        Assert.Equal(StorageMode.Degraded, (await repository.ReadProgressAsync()).Mode);
    }

    [Fact]
    public async Task Onboarding_AcceptsLegacyAndAppendedChromeDegradedField()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadOnboardingAsync()).Mode);
        const string legacy =
            "{\"wizardVersion\":1,\"completedStep\":3,\"chromeVerified\":true,\"incognitoProtected\":false,\"incognitoWarningAcknowledged\":true,\"iPhoneConfirmedThroughStep\":0,\"completedAtUtc\":null}";
        const string current =
            "{\"wizardVersion\":1,\"completedStep\":3,\"chromeVerified\":false,\"incognitoProtected\":false,\"incognitoWarningAcknowledged\":false,\"iPhoneConfirmedThroughStep\":0,\"completedAtUtc\":null,\"chromeDegradedAcknowledged\":true}";

        await UpdateAsync(database.Path, "UPDATE onboarding_state SET json = $value;", legacy);
        StorageResult<OnboardingState> legacyResult = await repository.ReadOnboardingAsync();
        await UpdateAsync(database.Path, "UPDATE onboarding_state SET json = $value;", current);
        StorageResult<OnboardingState> currentResult = await repository.ReadOnboardingAsync();

        Assert.Equal(StorageMode.Success, legacyResult.Mode);
        Assert.False(legacyResult.Value.ChromeDegradedAcknowledged);
        Assert.Equal(StorageMode.Success, currentResult.Mode);
        Assert.True(currentResult.Value.ChromeDegradedAcknowledged);
    }

    [Fact]
    public async Task NightOutcome_RequiresTask5AFactsAsOneCompleteVersionGroup()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveOutcomeAsync(Outcome());
        JsonObject current = await ReadJsonObjectAsync(database.Path, "night_outcomes");
        JsonObject legacy = CloneWithout(
            current,
            "overrideReasons",
            "firstLockObservedAtUtc",
            "scheduledLockAtUtc",
            "protectionGapObserved",
            "scheduleTimeZoneSerialized");
        JsonObject task5A = CloneWithout(
            current,
            "scheduledLockAtUtc",
            "protectionGapObserved",
            "scheduleTimeZoneSerialized");
        JsonObject beforeProtectionGap = CloneWithout(
            current,
            "protectionGapObserved",
            "scheduleTimeZoneSerialized");
        JsonObject beforeTimeZoneSnapshot = CloneWithout(
            current,
            "scheduleTimeZoneSerialized");
        JsonObject partial = CloneWithout(current, "firstLockObservedAtUtc");

        await UpdateAsync(
            database.Path,
            "UPDATE night_outcomes SET json = $value;",
            legacy.ToJsonString());
        StorageResult<IReadOnlyList<NightOutcome>> legacyResult =
            await repository.ReadLatestOutcomesAsync(1);
        Assert.Equal(StorageMode.Success, legacyResult.Mode);
        Assert.False(Assert.Single(legacyResult.Value).Qualifies);

        await UpdateAsync(
            database.Path,
            "UPDATE night_outcomes SET json = $value;",
            task5A.ToJsonString());
        StorageResult<IReadOnlyList<NightOutcome>> task5AResult =
            await repository.ReadLatestOutcomesAsync(1);
        Assert.Equal(StorageMode.Success, task5AResult.Mode);
        Assert.False(Assert.Single(task5AResult.Value).Qualifies);

        await UpdateAsync(
            database.Path,
            "UPDATE night_outcomes SET json = $value;",
            beforeProtectionGap.ToJsonString());
        Assert.Equal(StorageMode.Success, (await repository.ReadLatestOutcomesAsync(1)).Mode);

        await UpdateAsync(
            database.Path,
            "UPDATE night_outcomes SET json = $value;",
            beforeTimeZoneSnapshot.ToJsonString());
        StorageResult<IReadOnlyList<NightOutcome>> beforeSnapshotResult =
            await repository.ReadLatestOutcomesAsync(1);
        Assert.Equal(StorageMode.Success, beforeSnapshotResult.Mode);
        Assert.Null(Assert.Single(beforeSnapshotResult.Value).ScheduleTimeZoneSerialized);

        await UpdateAsync(
            database.Path,
            "UPDATE night_outcomes SET json = $value;",
            partial.ToJsonString());
        Assert.Equal(StorageMode.Degraded, (await repository.ReadLatestOutcomesAsync(1)).Mode);
    }

    [Fact]
    public async Task NightOutcome_InvalidOrOversizedScheduleTimeZoneDegradesOnRead()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveOutcomeAsync(Outcome());
        JsonObject json = await ReadJsonObjectAsync(database.Path, "night_outcomes");

        foreach (string corruptValue in new[]
        {
            "not-a-serialized-time-zone",
            new string('x', NightScheduleTimeZone.MaximumSerializedLength + 1),
        })
        {
            json["scheduleTimeZoneSerialized"] = corruptValue;
            await UpdateAsync(
                database.Path,
                "UPDATE night_outcomes SET json = $value;",
                json.ToJsonString());

            Assert.Equal(
                StorageMode.Degraded,
                (await repository.ReadLatestOutcomesAsync(1)).Mode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData("not-a-boolean")]
    public async Task AppRule_IsConfiguredMustBeBooleanTrue(object corruptValue)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveRuleSettingsAsync(new RuleSettingsState(
            ActiveAppRules: [ConfiguredApp()]));
        JsonObject json = await ReadJsonObjectAsync(database.Path, "rule_settings");
        JsonObject app = json["activeAppRules"]!.AsArray()[0]!.AsObject();
        app["isConfigured"] = JsonValue.Create(corruptValue);
        await UpdateAsync(
            database.Path,
            "UPDATE rule_settings SET json = $value;",
            json.ToJsonString());

        Assert.Equal(StorageMode.Degraded, (await repository.ReadRuleSettingsAsync()).Mode);
    }

    [Theory]
    [InlineData("isWorkNight", false)]
    [InlineData("isEligible", false)]
    [InlineData("qualifies", false)]
    [InlineData("qualifies", "not-a-boolean")]
    public async Task NightOutcome_ComputedFactsMustBeBooleanAndMatchDomainValue(
        string propertyName,
        object corruptValue)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveOutcomeAsync(Outcome());
        JsonObject json = await ReadJsonObjectAsync(database.Path, "night_outcomes");
        json[propertyName] = JsonValue.Create(corruptValue);
        await UpdateAsync(
            database.Path,
            "UPDATE night_outcomes SET json = $value;",
            json.ToJsonString());

        Assert.Equal(StorageMode.Degraded, (await repository.ReadLatestOutcomesAsync(1)).Mode);
    }

    [Theory]
    [InlineData("active_state")]
    [InlineData("night_outcomes")]
    public async Task ProtectionGapField_MustBeBooleanWhenPresent(string table)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        if (table == "active_state")
        {
            NightState state = ActiveState();
            await repository.SaveActiveStateWithEventAsync(state, StateEvent(state.NightId));
        }
        else
        {
            await repository.SaveOutcomeAsync(Outcome());
        }

        JsonObject json = await ReadJsonObjectAsync(database.Path, table);
        json["protectionGapObserved"] = "not-a-boolean";
        await UpdateAsync(
            database.Path,
            $"UPDATE {table} SET json = $value;",
            json.ToJsonString());

        StorageMode mode = table == "active_state"
            ? (await repository.ReadActiveStateAsync()).Mode
            : (await repository.ReadLatestOutcomesAsync(1)).Mode;
        Assert.Equal(StorageMode.Degraded, mode);
    }

    [Fact]
    public async Task NightOutcome_ProtectionGapParticipatesInComputedQualification()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        await repository.SaveOutcomeAsync(Outcome());
        JsonObject json = await ReadJsonObjectAsync(database.Path, "night_outcomes");
        json["protectionGapObserved"] = true;
        await UpdateAsync(
            database.Path,
            "UPDATE night_outcomes SET json = $value;",
            json.ToJsonString());

        Assert.Equal(StorageMode.Degraded, (await repository.ReadLatestOutcomesAsync(1)).Mode);

        json["qualifies"] = false;
        await UpdateAsync(
            database.Path,
            "UPDATE night_outcomes SET json = $value;",
            json.ToJsonString());
        StorageResult<IReadOnlyList<NightOutcome>> result =
            await repository.ReadLatestOutcomesAsync(1);

        Assert.Equal(StorageMode.Success, result.Mode);
        NightOutcome outcome = Assert.Single(result.Value);
        Assert.True(outcome.ProtectionGapObserved);
        Assert.True(outcome.IsEligible);
        Assert.False(outcome.Qualifies);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public async Task SingletonRead_RejectsUnsafeRowVersion(long rowVersion)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadOnboardingAsync()).Mode);
        await UpdateAsync(
            database.Path,
            "UPDATE onboarding_state SET row_version = $value;",
            rowVersion);

        StorageResult<OnboardingState> result = await repository.ReadOnboardingAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
    }

    [Fact]
    public async Task SingletonWrite_RefusesVersionExhaustionWithoutChangingTheRow()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadOnboardingAsync()).Mode);
        await UpdateAsync(
            database.Path,
            "UPDATE onboarding_state SET row_version = $value;",
            long.MaxValue - 1);

        Assert.Equal(
            long.MaxValue - 1,
            (await repository.ReadOnboardingAsync()).Version);

        StorageWriteResult result = await repository.SaveOnboardingAsync(
            new OnboardingState(1),
            expectedVersion: long.MaxValue - 1);

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.IsConflict);
        Assert.Equal(
            long.MaxValue - 1,
            await ScalarLongAsync(database.Path, "SELECT row_version FROM onboarding_state;"));
    }

    [Fact]
    public async Task SingletonBlindWrite_TreatsCorruptVersionAsDegradedNotConflict()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadRuleSettingsAsync()).Mode);
        await UpdateAsync(
            database.Path,
            "UPDATE rule_settings SET row_version = $value;",
            long.MaxValue);

        StorageWriteResult result = await repository.SaveRuleSettingsAsync(
            RuleSettingsState.Initial);

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.IsConflict);
        Assert.Equal(
            long.MaxValue,
            await ScalarLongAsync(database.Path, "SELECT row_version FROM rule_settings;"));
    }

    [Fact]
    public async Task BlindSingletonWrite_RejectsRealRowVersionStorageClass()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadRuleSettingsAsync()).Mode);
        await ExecuteAsync(
            database.Path,
            "UPDATE rule_settings SET row_version = CAST(1.5 AS REAL);");

        StorageWriteResult result = await repository.SaveRuleSettingsAsync(
            RuleSettingsState.Initial);

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.Equal(
            "real",
            await ScalarStringAsync(
                database.Path,
                "SELECT typeof(row_version) FROM rule_settings;"));
    }

    [Fact]
    public async Task CasSingletonWrite_RejectsIntegralRealRowVersionStorageClass()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadProgressAsync()).Mode);
        await ExecuteAsync(
            database.Path,
            """
            ALTER TABLE progress RENAME TO progress_old;
            CREATE TABLE progress (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                json TEXT NOT NULL,
                row_version REAL NOT NULL
            );
            INSERT INTO progress(singleton_id, json, row_version)
            SELECT singleton_id, json, CAST(0 AS REAL) FROM progress_old;
            DROP TABLE progress_old;
            """);
        Assert.Equal(
            "real",
            await ScalarStringAsync(
                database.Path,
                "SELECT typeof(row_version) FROM progress;"));

        StorageWriteResult result = await repository.SaveProgressAsync(
            ProgressState.Initial with { CurrentStep = 2 },
            expectedVersion: 0);

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.IsConflict);
        Assert.Equal(
            "real",
            await ScalarStringAsync(
                database.Path,
                "SELECT typeof(row_version) FROM progress;"));
    }

    [Fact]
    public async Task OversizedRuleSettingsJson_IsRejectedBeforeDeserialization()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.Equal(StorageMode.Success, (await repository.ReadRuleSettingsAsync()).Mode);
        await UpdateAsync(
            database.Path,
            "UPDATE rule_settings SET json = json || $value;",
            new string(' ', MaximumRuleSettingsJsonBytes));

        StorageResult<RuleSettingsState> result = await repository.ReadRuleSettingsAsync();

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.EnforcementEnabled);
    }

    [Fact]
    public async Task ExistingNoticeClaim_IsStrictlyValidatedAfterInsertConflict()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        Assert.True((await repository.TryClaimNoticeAsync(
            new NoticeClaim(Night, NightNoticeKind.LastStart, Now))).Value);
        await UpdateAsync(
            database.Path,
            "UPDATE notice_claims SET claimed_utc = $value;",
            "not-a-timestamp");

        StorageResult<bool> result = await repository.TryClaimNoticeAsync(
            new NoticeClaim(Night, NightNoticeKind.LastStart, Now.AddMinutes(1)));

        Assert.Equal(StorageMode.Degraded, result.Mode);
        Assert.False(result.Value);
        Assert.False(result.EnforcementEnabled);
    }

    private static async Task UpdateAsync(string path, string sql, object value)
    {
        await using SqliteConnection connection = Open(path);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using SqliteConnection connection = Open(path);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(string path, string sql)
    {
        await using SqliteConnection connection = Open(path);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(string path, string sql)
    {
        await using SqliteConnection connection = Open(path);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture)!;
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(string path, string table) =>
        JsonNode.Parse(await ScalarStringAsync(path, $"SELECT json FROM {table};"))!.AsObject();

    private static JsonObject CloneWithout(JsonObject source, params string[] propertyNames)
    {
        JsonObject clone = source.DeepClone().AsObject();
        foreach (string propertyName in propertyNames)
        {
            Assert.True(clone.Remove(propertyName));
        }

        return clone;
    }

    private static AppRule ConfiguredApp() => new(
        "game",
        @"C:\Games\game.exe",
        [],
        AppRuleCategory.Game);

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
        false,
        LastObservedUptime: TimeSpan.FromHours(100),
        LastObservedBootSessionId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    private static NightEvent StateEvent(Guid nightId) => new(
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        nightId,
        Now,
        NightEventKind.StateObserved,
        NightPhase.Grace);

    private static NightOutcome Outcome()
    {
        DateTimeOffset scheduledLock = Now.AddMinutes(-1);
        return new(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            Night,
            Now,
            false,
            false,
            false,
            false,
            false,
            false,
            FirstLockObservedAtUtc: scheduledLock,
            ScheduledLockAtUtc: scheduledLock,
            ScheduleTimeZoneSerialized: NightScheduleTimeZone.Capture(
                TimeZoneInfo.CreateCustomTimeZone(
                    "NightGate-Persistence-History-UTC+8",
                    TimeSpan.FromHours(8),
                    "NightGate Persistence History UTC+8",
                    "NightGate Persistence History UTC+8")));
    }

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new(
            $"Data Source={path};Pooling=False;Default Timeout=1");
        connection.Open();
        return connection;
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
