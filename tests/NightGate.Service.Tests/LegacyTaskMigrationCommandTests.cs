using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NightGate.Core;
using NightGate.Protocol;
using NightGate.Service;

namespace NightGate.Service.Tests;

public sealed class LegacyTaskMigrationCommandTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 15, 14, 0, 0, TimeSpan.Zero);
    private const string TaskPath = @"\NightGate tests\old shutdown";
    private const string Fingerprint =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Prepare_IsIdempotentAndRejectsChangedTaskWhileActive()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = Handler(repository, new FixedClock(Now));
        PrepareLegacyTaskMigrationCommand command = new(TaskPath, Fingerprint, true);

        ProtocolCommandResult first = await handler.ExecuteAsync(command);
        ProtocolCommandResult retry = await handler.ExecuteAsync(command);
        ProtocolCommandResult changed = await handler.ExecuteAsync(
            command with { ActionFingerprint = new string('b', 64) });

        Assert.True(first.Payload.GetProperty("accepted").GetBoolean());
        string migrationId = first.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        Assert.Equal(
            migrationId,
            retry.Payload.GetProperty("migration").GetProperty("migrationId").GetString());
        Assert.False(changed.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "taskAlreadyTracked",
            changed.Payload.GetProperty("error").GetString());
        Assert.Single((await repository.ReadLegacyTaskMigrationsAsync()).Value);
    }

    [Fact]
    public async Task Complete_UsesServiceTimeAndListProjectsStableStringStatus()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        MutableClock clock = new(Now);
        NightGateProtocolCommandHandler handler = Handler(repository, clock);
        ProtocolCommandResult prepared = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));
        string id = prepared.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        clock.UtcNow = Now.AddMinutes(1);

        ProtocolCommandResult disabled = await handler.ExecuteAsync(
            new CompleteLegacyTaskMigrationCommand(
                id,
                LegacyTaskMigrationStatus.Disabled));
        ProtocolCommandResult retry = await handler.ExecuteAsync(
            new CompleteLegacyTaskMigrationCommand(
                id,
                LegacyTaskMigrationStatus.Disabled));
        ProtocolCommandResult listed = await handler.ExecuteAsync(
            new ListLegacyTaskMigrationsCommand());

        Assert.True(disabled.Payload.GetProperty("accepted").GetBoolean());
        Assert.True(retry.Payload.GetProperty("accepted").GetBoolean());
        JsonElement migration = Assert.Single(
            listed.Payload.GetProperty("migrations").EnumerateArray().ToArray());
        Assert.Equal("disabled", migration.GetProperty("status").GetString());
        Assert.True(migration.GetProperty("disabledStateVerified").GetBoolean());
        Assert.Equal(
            Now.AddMinutes(1),
            migration.GetProperty("completedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task Complete_UpgradesLegacyUnverifiedDisabledRecordWithoutChangingCompletionTime()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        DateTimeOffset originallyCompleted = Now.AddMinutes(-5);
        LegacyTaskMigrationRecord legacy = new(
            "legacy-disabled",
            TaskPath,
            Fingerprint,
            true,
            LegacyTaskMigrationStatus.Disabled,
            Now.AddMinutes(-10),
            originallyCompleted,
            DisabledStateVerified: false);
        Assert.Equal(
            StorageMode.Success,
            (await repository.SaveLegacyTaskMigrationAsync(new(
                legacy.MigrationId,
                legacy.TaskPath,
                legacy.ActionFingerprint,
                legacy.OriginalEnabled,
                LegacyTaskMigrationStatus.Prepared,
                legacy.PreparedAtUtc))).Mode);
        Assert.Equal(
            StorageMode.Success,
            (await repository.SaveLegacyTaskMigrationAsync(legacy)).Mode);
        NightGateProtocolCommandHandler handler = Handler(repository, new FixedClock(Now));

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new CompleteLegacyTaskMigrationCommand(
                legacy.MigrationId,
                LegacyTaskMigrationStatus.Disabled));

        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        LegacyTaskMigrationRecord? persisted =
            (await repository.ReadLegacyTaskMigrationAsync(legacy.MigrationId)).Value;
        Assert.NotNull(persisted);
        LegacyTaskMigrationRecord upgraded = persisted!;
        Assert.True(upgraded.DisabledStateVerified);
        Assert.Equal(originallyCompleted, upgraded.CompletedAtUtc);
    }

    [Fact]
    public async Task FailedRecovery_RequiresDedicatedLookupAndCommandAndPreservesCompletionTime()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        MutableClock clock = new(Now);
        NightGateProtocolCommandHandler handler = Handler(repository, clock);
        ProtocolCommandResult prepared = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));
        string id = prepared.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        clock.UtcNow = Now.AddMinutes(1);
        ProtocolCommandResult failed = await handler.ExecuteAsync(
            new CompleteLegacyTaskMigrationCommand(
                id,
                LegacyTaskMigrationStatus.Failed));
        DateTimeOffset failedAt = failed.Payload.GetProperty("migration")
            .GetProperty("completedAtUtc").GetDateTimeOffset();
        clock.UtcNow = Now.AddMinutes(2);

        ProtocolCommandResult found = await handler.ExecuteAsync(
            new FindLegacyTaskMigrationRecoveryCandidateCommand(TaskPath));
        ProtocolCommandResult genericCompletion = await handler.ExecuteAsync(
            new CompleteLegacyTaskMigrationCommand(
                id,
                LegacyTaskMigrationStatus.Disabled));
        ProtocolCommandResult recovered = await handler.ExecuteAsync(
            RecoveryCommand(id, found));

        Assert.True(found.Payload.GetProperty("found").GetBoolean());
        Assert.Equal(
            id,
            found.Payload.GetProperty("migration")
                .GetProperty("migrationId").GetString());
        Assert.False(genericCompletion.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "invalidTransition",
            genericCompletion.Payload.GetProperty("error").GetString());
        Assert.True(recovered.Payload.GetProperty("accepted").GetBoolean());
        JsonElement migration = recovered.Payload.GetProperty("migration");
        Assert.Equal("disabled", migration.GetProperty("status").GetString());
        Assert.True(migration.GetProperty("disabledStateVerified").GetBoolean());
        Assert.Equal(
            failedAt,
            migration.GetProperty("completedAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task FailedRecovery_RejectsRecordWhenANewerSamePathMigrationExists()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        MutableClock clock = new(Now);
        NightGateProtocolCommandHandler handler = Handler(repository, clock);
        ProtocolCommandResult first = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));
        string failedId = first.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        _ = await handler.ExecuteAsync(new CompleteLegacyTaskMigrationCommand(
            failedId,
            LegacyTaskMigrationStatus.Failed));
        ProtocolCommandResult proof = await handler.ExecuteAsync(
            new FindLegacyTaskMigrationRecoveryCandidateCommand(TaskPath));
        Assert.True(proof.Payload.GetProperty("found").GetBoolean());
        clock.UtcNow = Now.AddMinutes(1);
        ProtocolCommandResult newer = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(
                TaskPath,
                new string('b', 64),
                true));

        ProtocolCommandResult lookup = await handler.ExecuteAsync(
            new FindLegacyTaskMigrationRecoveryCandidateCommand(TaskPath));
        ProtocolCommandResult recovery = await handler.ExecuteAsync(
            RecoveryCommand(failedId, proof));

        Assert.True(newer.Payload.GetProperty("accepted").GetBoolean());
        Assert.False(lookup.Payload.GetProperty("found").GetBoolean());
        Assert.False(recovery.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "invalidTransition",
            recovery.Payload.GetProperty("error").GetString());
        Assert.Equal(
            LegacyTaskMigrationStatus.Failed,
            (await repository.ReadLegacyTaskMigrationAsync(failedId)).Value!.Status);
    }

    [Fact]
    public async Task FailedRecovery_RejectsAnyOtherActiveSamePathRecordRegardlessOfTimestamp()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        MutableClock clock = new(Now);
        NightGateProtocolCommandHandler handler = Handler(repository, clock);
        ProtocolCommandResult first = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));
        string failedId = first.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        _ = await handler.ExecuteAsync(new CompleteLegacyTaskMigrationCommand(
            failedId,
            LegacyTaskMigrationStatus.Failed));
        ProtocolCommandResult lookup = await handler.ExecuteAsync(
            new FindLegacyTaskMigrationRecoveryCandidateCommand(TaskPath));
        Assert.True(lookup.Payload.GetProperty("found").GetBoolean());

        LegacyTaskMigrationRecord olderActive = new(
            "older-active-migration",
            TaskPath,
            new string('b', 64),
            true,
            LegacyTaskMigrationStatus.Prepared,
            Now.AddHours(-1));
        Assert.Equal(
            StorageMode.Success,
            (await repository.SaveLegacyTaskMigrationAsync(olderActive)).Mode);

        ProtocolCommandResult recovery = await handler.ExecuteAsync(
            RecoveryCommand(failedId, lookup));

        Assert.False(recovery.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "invalidTransition",
            recovery.Payload.GetProperty("error").GetString());
        Assert.Equal(
            LegacyTaskMigrationStatus.Failed,
            (await repository.ReadLegacyTaskMigrationAsync(failedId)).Value!.Status);
    }

    [Fact]
    public async Task FailedRecovery_RejectsAmbiguousSamePathFailedRecords()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = Handler(repository, new FixedClock(Now));
        ProtocolCommandResult first = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));
        string firstId = first.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        _ = await handler.ExecuteAsync(new CompleteLegacyTaskMigrationCommand(
            firstId,
            LegacyTaskMigrationStatus.Failed));
        LegacyTaskMigrationRecord secondPrepared = new(
            "older-failed-migration",
            TaskPath,
            new string('b', 64),
            true,
            LegacyTaskMigrationStatus.Prepared,
            Now.AddHours(-2));
        Assert.Equal(
            StorageMode.Success,
            (await repository.SaveLegacyTaskMigrationAsync(secondPrepared)).Mode);
        Assert.Equal(
            StorageMode.Success,
            (await repository.SaveLegacyTaskMigrationAsync(new(
                secondPrepared.MigrationId,
                secondPrepared.TaskPath,
                secondPrepared.ActionFingerprint,
                secondPrepared.OriginalEnabled,
                LegacyTaskMigrationStatus.Failed,
                secondPrepared.PreparedAtUtc,
                Now.AddHours(-1)))).Mode);

        ProtocolCommandResult lookup = await handler.ExecuteAsync(
            new FindLegacyTaskMigrationRecoveryCandidateCommand(TaskPath));

        Assert.False(lookup.Payload.GetProperty("found").GetBoolean());
    }

    [Theory]
    [InlineData("migrationId")]
    [InlineData("taskPath")]
    [InlineData("actionFingerprint")]
    [InlineData("originalEnabled")]
    public async Task FailedRecovery_RejectsAndConsumesProofWhenABoundFactDiffers(
        string changedFact)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = Handler(repository, new FixedClock(Now));
        ProtocolCommandResult prepared = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));
        string id = prepared.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        _ = await handler.ExecuteAsync(new CompleteLegacyTaskMigrationCommand(
            id,
            LegacyTaskMigrationStatus.Failed));
        ProtocolCommandResult lookup = await handler.ExecuteAsync(
            new FindLegacyTaskMigrationRecoveryCandidateCommand(TaskPath));
        RecoverLegacyTaskMigrationDisabledCommand valid = RecoveryCommand(id, lookup);
        RecoverLegacyTaskMigrationDisabledCommand changed = changedFact switch
        {
            "migrationId" => valid with { MigrationId = "another-migration" },
            "taskPath" => valid with { TaskPath = @"\NightGate tests\another task" },
            "actionFingerprint" => valid with { ActionFingerprint = new string('b', 64) },
            "originalEnabled" => valid with { OriginalEnabled = false },
            _ => throw new InvalidOperationException(),
        };

        ProtocolCommandResult rejected = await handler.ExecuteAsync(changed);
        ProtocolCommandResult replay = await handler.ExecuteAsync(valid);

        Assert.False(rejected.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "invalidRecoveryProof",
            rejected.Payload.GetProperty("error").GetString());
        Assert.False(replay.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "invalidRecoveryProof",
            replay.Payload.GetProperty("error").GetString());
        Assert.Equal(
            LegacyTaskMigrationStatus.Failed,
            (await repository.ReadLegacyTaskMigrationAsync(id)).Value!.Status);
    }

    [Fact]
    public async Task FailedRecovery_ProofIsSingleUse()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = Handler(repository, new FixedClock(Now));
        ProtocolCommandResult prepared = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));
        string id = prepared.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        _ = await handler.ExecuteAsync(new CompleteLegacyTaskMigrationCommand(
            id,
            LegacyTaskMigrationStatus.Failed));
        ProtocolCommandResult lookup = await handler.ExecuteAsync(
            new FindLegacyTaskMigrationRecoveryCandidateCommand(TaskPath));
        RecoverLegacyTaskMigrationDisabledCommand command = RecoveryCommand(id, lookup);

        ProtocolCommandResult first = await handler.ExecuteAsync(command);
        ProtocolCommandResult replay = await handler.ExecuteAsync(command);

        Assert.True(first.Payload.GetProperty("accepted").GetBoolean());
        Assert.False(replay.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "invalidRecoveryProof",
            replay.Payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task FailedRecovery_ProofExpiresUsingMonotonicTime()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        MutableMonotonicTimeProvider monotonicTime = new();
        NightGateProtocolCommandHandler handler = Handler(
            repository,
            new FixedClock(Now),
            recoveryTimeProvider: monotonicTime);
        ProtocolCommandResult prepared = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));
        string id = prepared.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        _ = await handler.ExecuteAsync(new CompleteLegacyTaskMigrationCommand(
            id,
            LegacyTaskMigrationStatus.Failed));
        ProtocolCommandResult lookup = await handler.ExecuteAsync(
            new FindLegacyTaskMigrationRecoveryCandidateCommand(TaskPath));
        monotonicTime.Advance(TimeSpan.FromSeconds(11));

        ProtocolCommandResult recovery = await handler.ExecuteAsync(
            RecoveryCommand(id, lookup));

        Assert.False(recovery.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "invalidRecoveryProof",
            recovery.Payload.GetProperty("error").GetString());
    }

    [Fact]
    public async Task FailedRecovery_WallClockRollbackDoesNotExpireFreshProof()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        MutableClock wallClock = new(Now);
        MutableMonotonicTimeProvider monotonicTime = new();
        NightGateProtocolCommandHandler handler = Handler(
            repository,
            wallClock,
            recoveryTimeProvider: monotonicTime);
        ProtocolCommandResult prepared = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));
        string id = prepared.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        _ = await handler.ExecuteAsync(new CompleteLegacyTaskMigrationCommand(
            id,
            LegacyTaskMigrationStatus.Failed));
        ProtocolCommandResult lookup = await handler.ExecuteAsync(
            new FindLegacyTaskMigrationRecoveryCandidateCommand(TaskPath));
        wallClock.UtcNow = Now.AddYears(-1);

        ProtocolCommandResult recovery = await handler.ExecuteAsync(
            RecoveryCommand(id, lookup));

        Assert.True(recovery.Payload.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task FailedRecovery_RejectsOriginallyDisabledOrPreviouslyVerifiedRecords()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        MutableClock clock = new(Now);
        NightGateProtocolCommandHandler handler = Handler(repository, clock);
        ProtocolCommandResult originallyDisabled = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, false));
        string originallyDisabledId = originallyDisabled.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        _ = await handler.ExecuteAsync(new CompleteLegacyTaskMigrationCommand(
            originallyDisabledId,
            LegacyTaskMigrationStatus.Failed));
        clock.UtcNow = Now.AddMinutes(1);
        const string verifiedPath = @"\NightGate tests\verified shutdown";
        ProtocolCommandResult verified = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(verifiedPath, Fingerprint, true));
        string verifiedId = verified.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        _ = await handler.ExecuteAsync(new CompleteLegacyTaskMigrationCommand(
            verifiedId,
            LegacyTaskMigrationStatus.Disabled));
        _ = await handler.ExecuteAsync(new CompleteLegacyTaskMigrationCommand(
            verifiedId,
            LegacyTaskMigrationStatus.Failed));

        ProtocolCommandResult disabledLookup = await handler.ExecuteAsync(
            new FindLegacyTaskMigrationRecoveryCandidateCommand(TaskPath));
        ProtocolCommandResult verifiedLookup = await handler.ExecuteAsync(
            new FindLegacyTaskMigrationRecoveryCandidateCommand(verifiedPath));
        ProtocolCommandResult disabledRecovery = await handler.ExecuteAsync(
            new RecoverLegacyTaskMigrationDisabledCommand(
                originallyDisabledId,
                TaskPath,
                Fingerprint,
                false,
                new string('b', LegacyTaskMigrationRecord.RecoveryTokenLength)));
        ProtocolCommandResult verifiedRecovery = await handler.ExecuteAsync(
            new RecoverLegacyTaskMigrationDisabledCommand(
                verifiedId,
                verifiedPath,
                Fingerprint,
                true,
                new string('b', LegacyTaskMigrationRecord.RecoveryTokenLength)));

        Assert.False(disabledLookup.Payload.GetProperty("found").GetBoolean());
        Assert.False(verifiedLookup.Payload.GetProperty("found").GetBoolean());
        Assert.False(disabledRecovery.Payload.GetProperty("accepted").GetBoolean());
        Assert.False(verifiedRecovery.Payload.GetProperty("accepted").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_AllowsDirectRestoredOnlyForLegacyDesktopRollingUpgradeCompatibility(
        bool disableFirst)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = Handler(repository, new FixedClock(Now));
        ProtocolCommandResult prepared = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));
        string id = prepared.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        if (disableFirst)
        {
            ProtocolCommandResult disabled = await handler.ExecuteAsync(
                new CompleteLegacyTaskMigrationCommand(
                    id,
                    LegacyTaskMigrationStatus.Disabled));
            Assert.True(disabled.Payload.GetProperty("accepted").GetBoolean());
        }

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new CompleteLegacyTaskMigrationCommand(
                id,
                LegacyTaskMigrationStatus.Restored));

        Assert.Equal(StorageMode.Success, result.Mode);
        Assert.True(result.Payload.GetProperty("accepted").GetBoolean());
        JsonElement terminal = result.Payload.GetProperty("migration");
        Assert.Equal(
            "restored",
            terminal.GetProperty("status").GetString());
        Assert.Equal(
            disableFirst,
            terminal.GetProperty("disabledStateVerified").GetBoolean());
        Assert.NotEqual(
            JsonValueKind.Null,
            terminal.GetProperty("completedAtUtc").ValueKind);
        Assert.DoesNotContain(
            (await repository.ReadLegacyTaskMigrationsAsync()).Value,
            record => record.Status is LegacyTaskMigrationStatus.Prepared or
                LegacyTaskMigrationStatus.Disabled or
                LegacyTaskMigrationStatus.RestorePrepared);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestorePreparation_IsDurableAcrossRestartAndCannotReturnToDisabled(
        bool disableFirst)
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        NightGateProtocolCommandHandler handler = Handler(
            repository,
            new FixedClock(Now));
        ProtocolCommandResult prepared = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));
        string id = prepared.Payload.GetProperty("migration")
            .GetProperty("migrationId").GetString()!;
        if (disableFirst)
        {
            ProtocolCommandResult disabled = await handler.ExecuteAsync(
                new CompleteLegacyTaskMigrationCommand(
                    id,
                    LegacyTaskMigrationStatus.Disabled));
            Assert.True(disabled.Payload.GetProperty("accepted").GetBoolean());
        }

        ProtocolCommandResult restorePrepared = await handler.ExecuteAsync(
            new CompleteLegacyTaskMigrationCommand(
                id,
                LegacyTaskMigrationStatus.RestorePrepared));

        Assert.True(restorePrepared.Payload.GetProperty("accepted").GetBoolean());
        JsonElement persistedIntent = restorePrepared.Payload.GetProperty("migration");
        Assert.Equal(
            "restorePrepared",
            persistedIntent.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, persistedIntent.GetProperty("completedAtUtc").ValueKind);
        Assert.Equal(
            disableFirst,
            persistedIntent.GetProperty("disabledStateVerified").GetBoolean());

        SqliteNightGateRepository reopened = new(database.Path);
        NightGateProtocolCommandHandler restarted = Handler(
            reopened,
            new FixedClock(Now.AddMinutes(1)));
        ProtocolCommandResult listed = await restarted.ExecuteAsync(
            new ListLegacyTaskMigrationsCommand());
        JsonElement reloaded = Assert.Single(
            listed.Payload.GetProperty("migrations").EnumerateArray().ToArray());
        Assert.Equal("restorePrepared", reloaded.GetProperty("status").GetString());

        ProtocolCommandResult cannotDisable = await restarted.ExecuteAsync(
            new CompleteLegacyTaskMigrationCommand(
                id,
                LegacyTaskMigrationStatus.Disabled));
        Assert.False(cannotDisable.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "invalidTransition",
            cannotDisable.Payload.GetProperty("error").GetString());

        ProtocolCommandResult restored = await restarted.ExecuteAsync(
            new CompleteLegacyTaskMigrationCommand(
                id,
                LegacyTaskMigrationStatus.Restored));
        Assert.True(restored.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            LegacyTaskMigrationStatus.Restored,
            (await reopened.ReadLegacyTaskMigrationAsync(id)).Value!.Status);
    }

    [Fact]
    public async Task Dispatcher_AcceptsExactMigrationCommandsAndRejectsPrivateExtras()
    {
        CapturingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string prepare = """
            {"version":1,"type":"prepareLegacyTaskMigration","requestId":"prepare","payload":{"taskPath":"\\old shutdown","actionFingerprint":"$FINGERPRINT$","originalEnabled":true}}
            """.Replace("$FINGERPRINT$", Fingerprint, StringComparison.Ordinal);

        ProtocolDispatchResult accepted = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes(prepare));
        ProtocolDispatchResult rejected = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes(prepare.Replace(
                "\"originalEnabled\":true",
                "\"originalEnabled\":true,\"taskXml\":\"secret\"",
                StringComparison.Ordinal)));

        Assert.True(accepted.CommandExecuted);
        Assert.IsType<PrepareLegacyTaskMigrationCommand>(handler.Commands[0]);
        Assert.False(rejected.CommandExecuted);
        Assert.Single(handler.Commands);
    }

    [Fact]
    public async Task Dispatcher_AcceptsDurableRestorePreparationStatus()
    {
        CapturingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string request = """
            {"version":1,"type":"completeLegacyTaskMigration","requestId":"restore-prepare","payload":{"migrationId":"migration-a","status":"restorePrepared"}}
            """;

        ProtocolDispatchResult result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes(request));

        Assert.True(result.CommandExecuted);
        CompleteLegacyTaskMigrationCommand command = Assert.IsType<
            CompleteLegacyTaskMigrationCommand>(Assert.Single(handler.Commands));
        Assert.Equal(LegacyTaskMigrationStatus.RestorePrepared, command.Status);
    }

    [Fact]
    public async Task Dispatcher_AcceptsExactFailedRecoveryCommandsAndRejectsExtras()
    {
        CapturingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);
        string lookup = """
            {"version":1,"type":"findLegacyTaskMigrationRecoveryCandidate","requestId":"lookup","payload":{"taskPath":"\\old shutdown"}}
            """;
        string recover = """
            {"version":1,"type":"recoverLegacyTaskMigrationDisabled","requestId":"recover","payload":{"migrationId":"migration-a","taskPath":"\\old shutdown","actionFingerprint":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","originalEnabled":true,"recoveryToken":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}
            """;
        string staleRecover = """
            {"version":1,"type":"recoverLegacyTaskMigrationDisabled","requestId":"stale","payload":{"migrationId":"migration-a"}}
            """;

        ProtocolDispatchResult lookupResult = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes(lookup));
        ProtocolDispatchResult recoverResult = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes(recover));
        ProtocolDispatchResult staleRecoverResult = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes(staleRecover));
        ProtocolDispatchResult rejected = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes(lookup.Replace(
                "}}",
                ",\"unexpected\":true}}",
                StringComparison.Ordinal)));

        Assert.True(lookupResult.CommandExecuted);
        Assert.True(recoverResult.CommandExecuted);
        Assert.False(staleRecoverResult.CommandExecuted);
        Assert.False(rejected.CommandExecuted);
        Assert.IsType<FindLegacyTaskMigrationRecoveryCandidateCommand>(handler.Commands[0]);
        Assert.IsType<RecoverLegacyTaskMigrationDisabledCommand>(handler.Commands[1]);
        Assert.Equal(2, handler.Commands.Count);
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("unknown")]
    public async Task Dispatcher_RejectsUnsupportedCompletionStatus(string status)
    {
        CapturingHandler handler = new();
        ServiceCommandDispatcher dispatcher = new(new JsonProtocolCodec(), handler);

        string request = """
            {"version":1,"type":"completeLegacyTaskMigration","requestId":"complete","payload":{"migrationId":"migration-a","status":"$STATUS$"}}
            """.Replace("$STATUS$", status, StringComparison.Ordinal);
        ProtocolDispatchResult result = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes(request));

        Assert.False(result.CommandExecuted);
    }

    [Fact]
    public async Task List_PaginatesOnlyActiveRecordsWithinProtocolFrame()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository repository = new(database.Path);
        for (int index = 0; index < 10; index++)
        {
            LegacyTaskMigrationRecord prepared = Record(
                LongMigrationId(index),
                LongTaskPath(index),
                LegacyTaskMigrationStatus.Prepared);
            Assert.False((await repository.SaveLegacyTaskMigrationAsync(prepared)).IsDegraded);
        }

        for (int index = 0; index < 12; index++)
        {
            LegacyTaskMigrationRecord prepared = Record(
                $"failed-{index:D2}",
                $@"\failed\{index:D2}",
                LegacyTaskMigrationStatus.Prepared);
            Assert.False((await repository.SaveLegacyTaskMigrationAsync(prepared)).IsDegraded);
            LegacyTaskMigrationRecord failed = new(
                prepared.MigrationId,
                prepared.TaskPath,
                prepared.ActionFingerprint,
                prepared.OriginalEnabled,
                LegacyTaskMigrationStatus.Failed,
                prepared.PreparedAtUtc,
                Now.AddSeconds(1));
            Assert.False((await repository.SaveLegacyTaskMigrationAsync(failed)).IsDegraded);
        }

        ServiceCommandDispatcher dispatcher = new(
            new JsonProtocolCodec(),
            Handler(repository, new FixedClock(Now)));
        ProtocolDispatchResult first = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes(Request("page-1", null)));
        Assert.True(first.CommandExecuted);
        Assert.InRange(first.ResponseUtf8.Length, 1, NightGateProtocol.MaximumBodyBytes);
        using JsonDocument firstJson = JsonDocument.Parse(first.ResponseUtf8);
        JsonElement firstData = firstJson.RootElement.GetProperty("payload")
            .GetProperty("data");
        JsonElement[] firstMigrations = firstData.GetProperty("migrations")
            .EnumerateArray().ToArray();
        Assert.Equal(8, firstMigrations.Length);
        Assert.All(firstMigrations, item =>
            Assert.Equal("prepared", item.GetProperty("status").GetString()));
        Assert.Equal(12, firstData.GetProperty("failedCount").GetInt32());
        string cursor = firstData.GetProperty("nextCursor").GetString()!;

        ProtocolDispatchResult second = await dispatcher.DispatchAsync(
            Encoding.UTF8.GetBytes(Request("page-2", cursor)));
        Assert.True(second.CommandExecuted);
        Assert.InRange(second.ResponseUtf8.Length, 1, NightGateProtocol.MaximumBodyBytes);
        using JsonDocument secondJson = JsonDocument.Parse(second.ResponseUtf8);
        JsonElement secondData = secondJson.RootElement.GetProperty("payload")
            .GetProperty("data");
        Assert.Equal(
            2,
            secondData.GetProperty("migrations").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, secondData.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Prepare_RejectsBeforeServiceCanExceedSharedActiveRecordLimit()
    {
        using TempDatabase database = new();
        SqliteNightGateRepository baseRepository = new(database.Path);
        InMemoryLegacyMigrationRepository migrations = new(
            Enumerable.Range(0, LegacyTaskMigrationRecord.MaximumActiveRecords)
                .Select(index => Record(
                    $"active-{index:D4}",
                    $@"\existing\{index:D4}",
                    LegacyTaskMigrationStatus.Prepared))
                .ToArray());
        NightGateProtocolCommandHandler handler = Handler(
            baseRepository,
            new FixedClock(Now),
            migrations);

        ProtocolCommandResult result = await handler.ExecuteAsync(
            new PrepareLegacyTaskMigrationCommand(TaskPath, Fingerprint, true));

        Assert.False(result.Payload.GetProperty("accepted").GetBoolean());
        Assert.Equal(
            "migrationCapacityReached",
            result.Payload.GetProperty("error").GetString());
        Assert.Equal(0, migrations.SaveCalls);
    }

    private static LegacyTaskMigrationRecord Record(
        string id,
        string taskPath,
        LegacyTaskMigrationStatus status) => new(
        id,
        taskPath,
        Fingerprint,
        true,
        status,
        Now,
        status is LegacyTaskMigrationStatus.Prepared or
            LegacyTaskMigrationStatus.RestorePrepared
                ? null
                : Now.AddSeconds(1));

    private static string LongTaskPath(int index)
    {
        string suffix = $"-{index:D2}";
        return "\\" + new string('\u0080', 1_023 - suffix.Length) + suffix;
    }

    private static string LongMigrationId(int index)
    {
        string suffix = $"{index:D3}";
        return new string('\u0080',
            LegacyTaskMigrationRecord.MaximumMigrationIdLength - suffix.Length) + suffix;
    }

    private static string Request(string requestId, string? cursor) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            type = "listLegacyTaskMigrations",
            requestId,
            payload = cursor is null ? new { } : (object)new { cursor },
        });

    private static RecoverLegacyTaskMigrationDisabledCommand RecoveryCommand(
        string migrationId,
        ProtocolCommandResult lookup) => new(
        migrationId,
        lookup.Payload.GetProperty("migration").GetProperty("taskPath").GetString()!,
        lookup.Payload.GetProperty("migration").GetProperty("actionFingerprint").GetString()!,
        lookup.Payload.GetProperty("migration").GetProperty("originalEnabled").GetBoolean(),
        lookup.Payload.GetProperty("recoveryToken").GetString()!);

    private static NightGateProtocolCommandHandler Handler(
        SqliteNightGateRepository repository,
        IClock clock,
        ILegacyTaskMigrationRepository? legacyRepository = null,
        TimeProvider? recoveryTimeProvider = null)
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
            clock,
            legacyTaskMigrationRepository: legacyRepository ?? repository,
            legacyRecoveryTimeProvider: recoveryTimeProvider);
    }

    private sealed class EmptyAllowedProcesses : IAllowedProcessSnapshotProvider
    {
        public ImmutableArray<string> GetSnapshot() => [];
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class MutableMonotonicTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class CapturingHandler : IProtocolCommandHandler
    {
        public List<ServiceCommand> Commands { get; } = [];

        public ValueTask<ProtocolCommandResult> ExecuteAsync(
            ServiceCommand command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return ValueTask.FromResult(ProtocolCommandResult.Success(new { accepted = true }));
        }
    }

    private sealed class InMemoryLegacyMigrationRepository(
        IReadOnlyList<LegacyTaskMigrationRecord> records) :
        ILegacyTaskMigrationRepository
    {
        private readonly List<LegacyTaskMigrationRecord> _records = [.. records];

        public int SaveCalls { get; private set; }

        public ValueTask<StorageResult<LegacyTaskMigrationRecord?>>
            ReadLegacyTaskMigrationAsync(
                string migrationId,
                CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new StorageResult<LegacyTaskMigrationRecord?>(
                StorageMode.Success,
                _records.SingleOrDefault(record => record.MigrationId == migrationId)));

        public ValueTask<StorageResult<IReadOnlyList<LegacyTaskMigrationRecord>>>
            ReadLegacyTaskMigrationsAsync(
                CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new StorageResult<IReadOnlyList<LegacyTaskMigrationRecord>>(
                StorageMode.Success,
                _records.ToArray()));

        public ValueTask<StorageWriteResult> SaveLegacyTaskMigrationAsync(
            LegacyTaskMigrationRecord record,
            CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            _records.Add(record);
            return ValueTask.FromResult(StorageWriteResult.Success);
        }
    }

    private sealed class TempDatabase : IDisposable
    {
        public TempDatabase()
        {
            DirectoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NightGate.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            Path = System.IO.Path.Combine(DirectoryPath, "state.db");
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
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
