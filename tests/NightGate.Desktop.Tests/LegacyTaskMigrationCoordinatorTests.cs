using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class LegacyTaskMigrationCoordinatorTests
{
    private static readonly LegacyShutdownTaskCandidate CandidateA = new(
        @"\NightGate tests\shutdown-a",
        new string('a', 64),
        true);

    private static readonly LegacyShutdownTaskCandidate CandidateB = new(
        @"\NightGate tests\shutdown-b",
        new string('b', 64),
        true);

    [Fact]
    public async Task Refresh_ReconcilesPreparedRecordAfterCrashAndMarksItDisabled()
    {
        FakeLegacyMigrationService service = new(
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Prepared));
        FakeLegacyTaskAdapter adapter = new([CandidateA])
        {
            ReconcileStatus = LegacyTaskMutationStatus.Unchanged,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.True(snapshot.Available);
        Assert.Equal(
            ["reconcile:\\NightGate tests\\shutdown-a", "scan"],
            adapter.Events);
        Assert.Equal(
            ["complete:disabled:\\NightGate tests\\shutdown-a"],
            service.Events);
        Assert.Empty(snapshot.Candidates);
        Assert.Single(snapshot.DisabledMigrations);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Disabled,
            snapshot.DisabledMigrations[0].Status);
    }

    [Fact]
    public async Task Refresh_RevalidatesPersistedDisabledRecordBeforeClaimingItIsDisabled()
    {
        FakeLegacyMigrationService service = new(
            Migration(
                CandidateA,
                DesktopLegacyTaskMigrationStatus.Disabled,
                disabledStateVerified: false));
        FakeLegacyTaskAdapter adapter = new([CandidateA])
        {
            ReconcileStatus = LegacyTaskMutationStatus.Disabled,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.True(snapshot.Available);
        Assert.Equal(
            ["reconcile:\\NightGate tests\\shutdown-a", "scan"],
            adapter.Events);
        Assert.Empty(snapshot.Candidates);
        Assert.Single(snapshot.DisabledMigrations);
        Assert.True(service.Migrations.Single().DisabledStateVerified);
    }

    [Fact]
    public async Task Refresh_UnavailablePersistedDisabledRecordIsReportedUnverified()
    {
        FakeLegacyMigrationService service = new(
            Migration(
                CandidateA,
                DesktopLegacyTaskMigrationStatus.Disabled,
                disabledStateVerified: false));
        FakeLegacyTaskAdapter adapter = new([])
        {
            ReconcileStatus = LegacyTaskMutationStatus.Unavailable,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.True(snapshot.Available);
        Assert.Equal(1, snapshot.UnverifiedDisabledCount);
        Assert.Single(snapshot.DisabledMigrations);
        Assert.Equal(
            ["reconcile:\\NightGate tests\\shutdown-a", "scan"],
            adapter.Events);
    }

    [Fact]
    public async Task Refresh_LiveEnabledScanOverridesHistoricallyVerifiedUnavailableRecord()
    {
        FakeLegacyMigrationService service = new(
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Disabled));
        FakeLegacyTaskAdapter adapter = new([CandidateA])
        {
            ReconcileStatus = LegacyTaskMutationStatus.Unavailable,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.Equal(CandidateA, Assert.Single(snapshot.Candidates));
        Assert.Single(snapshot.DisabledMigrations);
        Assert.Equal(1, snapshot.UnverifiedDisabledCount);
    }

    [Fact]
    public async Task Refresh_LiveReplacementClosesHistoricallyVerifiedRecordAndExposesReplacement()
    {
        LegacyShutdownTaskCandidate replacement = CandidateA with
        {
            ActionFingerprint = new string('c', 64),
        };
        FakeLegacyMigrationService service = new(
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Disabled));
        FakeLegacyTaskAdapter adapter = new([replacement])
        {
            ReconcileStatus = LegacyTaskMutationStatus.Unavailable,
            ObservationStatus = LegacyTaskObservationStatus.Changed,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.Equal(replacement, Assert.Single(snapshot.Candidates));
        Assert.Empty(snapshot.DisabledMigrations);
        Assert.Equal(0, snapshot.UnverifiedDisabledCount);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Failed,
            Assert.Single(service.Migrations).Status);
    }

    [Fact]
    public async Task Refresh_LiveReplacementClosesPreparedRetryRecordSoItCanBeSelected()
    {
        LegacyShutdownTaskCandidate replacement = CandidateA with
        {
            ActionFingerprint = new string('c', 64),
        };
        FakeLegacyMigrationService service = new(
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Prepared));
        FakeLegacyTaskAdapter adapter = new([replacement])
        {
            ReconcileStatus = LegacyTaskMutationStatus.Unavailable,
            ObservationStatus = LegacyTaskObservationStatus.Changed,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.Equal(replacement, Assert.Single(snapshot.Candidates));
        Assert.Equal(0, snapshot.PendingRecoveryCount);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Failed,
            Assert.Single(service.Migrations).Status);
    }

    [Fact]
    public async Task Refresh_Version033PreparedRecordSurvivesStrictScanFingerprintUpgrade()
    {
        LegacyShutdownTaskCandidate fullFingerprintScan = CandidateA with
        {
            ActionFingerprint = new string('c', 64),
        };
        FakeLegacyMigrationService service = new(
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Prepared));
        FakeLegacyTaskAdapter adapter = new([fullFingerprintScan])
        {
            ReconcileStatus = LegacyTaskMutationStatus.Unavailable,
            ObservationStatus = LegacyTaskObservationStatus.MatchingEnabled,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Prepared,
            Assert.Single(service.Migrations).Status);
        Assert.Equal(1, snapshot.PendingRecoveryCount);
        Assert.Equal(CandidateA, Assert.Single(snapshot.Candidates));
        Assert.Equal(
            [
                "reconcile:\\NightGate tests\\shutdown-a",
                "scan",
                "observe:\\NightGate tests\\shutdown-a",
            ],
            adapter.Events);
        Assert.DoesNotContain(
            service.Events,
            item => item.StartsWith("complete:failed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_RecoversFalseFailedRecordOnlyAfterReadOnlyDisabledProof()
    {
        List<string> eventOrder = [];
        DesktopLegacyTaskMigration failed = Migration(
            CandidateA,
            DesktopLegacyTaskMigrationStatus.Failed,
            disabledStateVerified: false);
        FakeLegacyMigrationService service = new(eventOrder, failed);
        FakeLegacyTaskAdapter adapter = new(
            [CandidateA with { WasEnabled = false }],
            eventOrder)
        {
            ObservationStatus = LegacyTaskObservationStatus.MatchingDisabled,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        DesktopLegacyTaskMigration recovered = Assert.Single(service.Migrations);
        Assert.Equal(DesktopLegacyTaskMigrationStatus.Disabled, recovered.Status);
        Assert.True(recovered.DisabledStateVerified);
        Assert.Equal(0, snapshot.FailedCount);
        Assert.True(Assert.Single(snapshot.DisabledMigrations).DisabledStateVerified);
        Assert.Equal(
            [
                "scan",
                "find-recovery:\\NightGate tests\\shutdown-a",
                "observe:\\NightGate tests\\shutdown-a",
                "recover-disabled:\\NightGate tests\\shutdown-a",
                "scan",
            ],
            eventOrder);
    }

    [Theory]
    [InlineData(LegacyTaskObservationStatus.MatchingEnabled)]
    [InlineData(LegacyTaskObservationStatus.Changed)]
    [InlineData(LegacyTaskObservationStatus.Missing)]
    [InlineData(LegacyTaskObservationStatus.Unavailable)]
    [InlineData(LegacyTaskObservationStatus.Invalid)]
    public async Task Refresh_DoesNotRecoverFailedRecordWithoutMatchingDisabledProof(
        LegacyTaskObservationStatus observationStatus)
    {
        DesktopLegacyTaskMigration failed = Migration(
            CandidateA,
            DesktopLegacyTaskMigrationStatus.Failed,
            disabledStateVerified: false);
        FakeLegacyMigrationService service = new(failed);
        FakeLegacyTaskAdapter adapter = new([CandidateA with { WasEnabled = false }])
        {
            ObservationStatus = observationStatus,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Failed,
            Assert.Single(service.Migrations).Status);
        Assert.Equal(1, snapshot.FailedCount);
        Assert.Empty(snapshot.DisabledMigrations);
        Assert.DoesNotContain(
            service.Events,
            item => item.StartsWith("recover-disabled:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_NeverQueriesRecoveryForCurrentlyEnabledFailedTask()
    {
        DesktopLegacyTaskMigration failed = Migration(
            CandidateA,
            DesktopLegacyTaskMigrationStatus.Failed,
            disabledStateVerified: false);
        FakeLegacyMigrationService service = new(failed);
        FakeLegacyTaskAdapter adapter = new([CandidateA])
        {
            ObservationStatus = LegacyTaskObservationStatus.MatchingDisabled,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.Equal(1, snapshot.FailedCount);
        Assert.Equal(CandidateA, Assert.Single(snapshot.Candidates));
        Assert.DoesNotContain(
            service.Events,
            item => item.StartsWith("find-recovery:", StringComparison.Ordinal));
        Assert.DoesNotContain(adapter.Events, item => item.StartsWith("observe:"));
    }

    [Fact]
    public async Task Refresh_ChangedPersistedDisabledRecordStopsClaimingDisabledAndExposesReplacement()
    {
        LegacyShutdownTaskCandidate replacement = CandidateA with
        {
            ActionFingerprint = new string('c', 64),
        };
        FakeLegacyMigrationService service = new(
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Disabled));
        FakeLegacyTaskAdapter adapter = new([replacement])
        {
            ReconcileStatus = LegacyTaskMutationStatus.Changed,
            ObservationStatus = LegacyTaskObservationStatus.Changed,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.Empty(snapshot.DisabledMigrations);
        Assert.Equal(0, snapshot.UnverifiedDisabledCount);
        Assert.Equal(replacement, Assert.Single(snapshot.Candidates));
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Failed,
            service.Migrations.Single().Status);
    }

    [Fact]
    public async Task DisableSelected_ExistingDisabledRecordIsRevalidatedInsteadOfSkipped()
    {
        List<string> eventOrder = [];
        FakeLegacyMigrationService service = new(
            eventOrder,
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Disabled));
        FakeLegacyTaskAdapter adapter = new([CandidateA], eventOrder)
        {
            ReconcileStatus = LegacyTaskMutationStatus.Disabled,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .DisableSelectedAsync([CandidateA]);

        Assert.Equal(
            [
                "prepare:\\NightGate tests\\shutdown-a",
                "reconcile:\\NightGate tests\\shutdown-a",
                "scan",
            ],
            eventOrder);
        Assert.Single(snapshot.DisabledMigrations);
    }

    [Fact]
    public async Task DisableSelected_PreparesBeforeMutationAndTouchesOnlyExplicitSelection()
    {
        List<string> eventOrder = [];
        FakeLegacyMigrationService service = new(eventOrder);
        FakeLegacyTaskAdapter adapter = new([CandidateA, CandidateB], eventOrder)
        {
            ReconcileStatus = LegacyTaskMutationStatus.Disabled,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);
        _ = await coordinator.RefreshAsync();
        eventOrder.Clear();

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .DisableSelectedAsync([CandidateB]);

        Assert.Equal(
            [
                "prepare:\\NightGate tests\\shutdown-b",
                "reconcile:\\NightGate tests\\shutdown-b",
                "complete:disabled:\\NightGate tests\\shutdown-b",
                "scan",
            ],
            eventOrder);
        Assert.Equal(CandidateA, Assert.Single(snapshot.Candidates));
        Assert.Equal(
            CandidateB.TaskPath,
            Assert.Single(snapshot.DisabledMigrations).TaskPath);
    }

    [Fact]
    public async Task DisableSelected_UnelevatedUnavailable_ElevationSuccessCompletesVerified()
    {
        List<string> eventOrder = [];
        FakeLegacyMigrationService service = new(eventOrder);
        FakeLegacyTaskAdapter adapter = new(
            [CandidateA with { WasEnabled = false }],
            eventOrder)
        {
            ReconcileStatus = LegacyTaskMutationStatus.Unavailable,
        };
        FakeLegacyTaskElevationService elevation = new(eventOrder)
        {
            DisableStatus = LegacyTaskMutationStatus.Disabled,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service, elevation);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .DisableSelectedAsync([CandidateA]);

        Assert.Equal(
            [
                "prepare:\\NightGate tests\\shutdown-a",
                "reconcile:\\NightGate tests\\shutdown-a",
                "elevate-disable:\\NightGate tests\\shutdown-a",
                "complete:disabled:\\NightGate tests\\shutdown-a",
                "scan",
            ],
            eventOrder);
        DesktopLegacyTaskMigration migration = Assert.Single(service.Migrations);
        Assert.Equal(DesktopLegacyTaskMigrationStatus.Disabled, migration.Status);
        Assert.True(migration.DisabledStateVerified);
        Assert.Equal(0, snapshot.PendingRecoveryCount);
        Assert.Equal(0, snapshot.UnverifiedDisabledCount);
        Assert.Empty(snapshot.Candidates);
    }

    [Fact]
    public async Task DisableSelected_UacCancelled_LeavesPreparedCandidateRetryable()
    {
        List<string> eventOrder = [];
        FakeLegacyMigrationService service = new(eventOrder);
        FakeLegacyTaskAdapter adapter = new([CandidateA], eventOrder)
        {
            ReconcileStatus = LegacyTaskMutationStatus.Unavailable,
        };
        FakeLegacyTaskElevationService elevation = new(eventOrder)
        {
            DisableStatus = LegacyTaskMutationStatus.Unavailable,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service, elevation);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .DisableSelectedAsync([CandidateA]);

        Assert.DoesNotContain(
            eventOrder,
            item => item.StartsWith("complete:", StringComparison.Ordinal));
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Prepared,
            Assert.Single(service.Migrations).Status);
        Assert.Equal(1, snapshot.PendingRecoveryCount);
        Assert.Empty(snapshot.DisabledMigrations);
        Assert.Equal(CandidateA, Assert.Single(snapshot.Candidates));
    }

    [Fact]
    public async Task DisableSelected_RecordsPermanentFingerprintFailureWithoutHidingCandidate()
    {
        FakeLegacyMigrationService service = new();
        FakeLegacyTaskAdapter adapter = new([CandidateA])
        {
            ReconcileStatus = LegacyTaskMutationStatus.Changed,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);
        _ = await coordinator.RefreshAsync();

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .DisableSelectedAsync([CandidateA]);

        Assert.Contains(
            service.Migrations,
            migration => migration.Status == DesktopLegacyTaskMigrationStatus.Failed);
        Assert.Equal(CandidateA, Assert.Single(snapshot.Candidates));
        Assert.Empty(snapshot.DisabledMigrations);
    }

    [Fact]
    public async Task RestoreDisabled_RestoresOnlyPersistedDisabledRecords()
    {
        DesktopLegacyTaskMigration disabled = Migration(
            CandidateA,
            DesktopLegacyTaskMigrationStatus.Disabled);
        DesktopLegacyTaskMigration failed = Migration(
            CandidateB,
            DesktopLegacyTaskMigrationStatus.Failed);
        FakeLegacyMigrationService service = new(disabled, failed);
        FakeLegacyTaskAdapter adapter = new([])
        {
            RestoreStatus = LegacyTaskMutationStatus.Restored,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .RestoreDisabledAsync();

        Assert.Equal(
            ["restore:\\NightGate tests\\shutdown-a", "scan"],
            adapter.Events);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Restored,
            service.Migrations.Single(item => item.MigrationId == disabled.MigrationId).Status);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Failed,
            service.Migrations.Single(item => item.MigrationId == failed.MigrationId).Status);
        Assert.Empty(snapshot.DisabledMigrations);
    }

    [Fact]
    public async Task RestoreDisabled_UnelevatedUnavailable_ElevationSuccessClosesRecord()
    {
        List<string> eventOrder = [];
        FakeLegacyMigrationService service = new(
            eventOrder,
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Disabled));
        FakeLegacyTaskAdapter adapter = new([], eventOrder)
        {
            RestoreStatus = LegacyTaskMutationStatus.Unavailable,
        };
        FakeLegacyTaskElevationService elevation = new(eventOrder)
        {
            RestoreStatus = LegacyTaskMutationStatus.Restored,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service, elevation);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .RestoreDisabledAsync();

        Assert.Equal(
            [
                "complete:restoreprepared:\\NightGate tests\\shutdown-a",
                "restore:\\NightGate tests\\shutdown-a",
                "elevate-restore:\\NightGate tests\\shutdown-a",
                "complete:restored:\\NightGate tests\\shutdown-a",
                "scan",
            ],
            eventOrder);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Restored,
            Assert.Single(service.Migrations).Status);
        Assert.Empty(snapshot.DisabledMigrations);
    }

    [Fact]
    public async Task RestoreDisabled_RecoversPreparedRecordWithoutWaitingForDisableReconciliation()
    {
        DesktopLegacyTaskMigration prepared = Migration(
            CandidateA,
            DesktopLegacyTaskMigrationStatus.Prepared);
        FakeLegacyMigrationService service = new(prepared);
        FakeLegacyTaskAdapter adapter = new([])
        {
            RestoreStatus = LegacyTaskMutationStatus.Restored,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .RestoreDisabledAsync();

        Assert.Equal(
            ["restore:\\NightGate tests\\shutdown-a", "scan"],
            adapter.Events);
        Assert.Equal(
            [
                "complete:restoreprepared:\\NightGate tests\\shutdown-a",
                "complete:restored:\\NightGate tests\\shutdown-a",
            ],
            service.Events);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Restored,
            service.Migrations.Single().Status);
        Assert.Equal(0, snapshot.PendingRecoveryCount);
        Assert.Empty(snapshot.DisabledMigrations);
    }

    [Fact]
    public async Task Refresh_RestorePreparedByPriorProcess_ContinuesRestoreWithoutDisabling()
    {
        List<string> eventOrder = [];
        FakeLegacyMigrationService service = new(
            eventOrder,
            Migration(
                CandidateA,
                DesktopLegacyTaskMigrationStatus.RestorePrepared,
                disabledStateVerified: true));
        FakeLegacyTaskAdapter adapter = new(
            [CandidateA with { WasEnabled = false }],
            eventOrder)
        {
            RestoreStatus = LegacyTaskMutationStatus.Restored,
            ReconcileStatus = LegacyTaskMutationStatus.Disabled,
        };
        LegacyTaskMigrationCoordinator restarted = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await restarted.RefreshAsync();

        Assert.Equal(
            [
                "restore:\\NightGate tests\\shutdown-a",
                "complete:restored:\\NightGate tests\\shutdown-a",
                "scan",
            ],
            eventOrder);
        Assert.DoesNotContain(
            eventOrder,
            item => item.StartsWith("reconcile:", StringComparison.Ordinal));
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Restored,
            Assert.Single(service.Migrations).Status);
        Assert.Empty(snapshot.DisabledMigrations);
        Assert.Equal(0, snapshot.PendingRecoveryCount);
        Assert.Equal(0, snapshot.PendingRestoreCount);
    }

    [Fact]
    public async Task Refresh_RestorePreparedThatNeedsElevation_DoesNotPromptInBackground()
    {
        List<string> eventOrder = [];
        FakeLegacyMigrationService service = new(
            eventOrder,
            Migration(
                CandidateA,
                DesktopLegacyTaskMigrationStatus.RestorePrepared,
                disabledStateVerified: true));
        FakeLegacyTaskAdapter adapter = new(
            [CandidateA with { WasEnabled = false }],
            eventOrder)
        {
            RestoreStatus = LegacyTaskMutationStatus.Unavailable,
        };
        FakeLegacyTaskElevationService elevation = new(eventOrder)
        {
            RestoreStatus = LegacyTaskMutationStatus.Restored,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service, elevation);

        DesktopLegacyMigrationSnapshot first = await coordinator.RefreshAsync();
        DesktopLegacyMigrationSnapshot second = await coordinator.RefreshAsync();

        Assert.Equal(
            [
                "restore:\\NightGate tests\\shutdown-a",
                "scan",
                "restore:\\NightGate tests\\shutdown-a",
                "scan",
            ],
            eventOrder);
        Assert.DoesNotContain(
            eventOrder,
            item => item.StartsWith("elevate-restore:", StringComparison.Ordinal));
        Assert.Equal(0, first.PendingRecoveryCount);
        Assert.Equal(0, second.PendingRecoveryCount);
        Assert.Equal(1, first.PendingRestoreCount);
        Assert.Equal(1, second.PendingRestoreCount);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.RestorePrepared,
            Assert.Single(service.Migrations).Status);
    }

    [Fact]
    public async Task RestoreDisabled_ExistingRestorePreparedMayRequestElevationExplicitly()
    {
        List<string> eventOrder = [];
        FakeLegacyMigrationService service = new(
            eventOrder,
            Migration(
                CandidateA,
                DesktopLegacyTaskMigrationStatus.RestorePrepared,
                disabledStateVerified: true));
        FakeLegacyTaskAdapter adapter = new(
            [CandidateA with { WasEnabled = false }],
            eventOrder)
        {
            RestoreStatus = LegacyTaskMutationStatus.Unavailable,
        };
        FakeLegacyTaskElevationService elevation = new(eventOrder)
        {
            RestoreStatus = LegacyTaskMutationStatus.Restored,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service, elevation);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .RestoreDisabledAsync();

        Assert.Equal(
            [
                "restore:\\NightGate tests\\shutdown-a",
                "elevate-restore:\\NightGate tests\\shutdown-a",
                "complete:restored:\\NightGate tests\\shutdown-a",
                "scan",
            ],
            eventOrder);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Restored,
            Assert.Single(service.Migrations).Status);
        Assert.Equal(0, snapshot.PendingRecoveryCount);
        Assert.Equal(0, snapshot.PendingRestoreCount);
    }

    [Fact]
    public async Task RestoreDisabled_PermanentTaskMismatchClosesRetryRecordAsFailed()
    {
        FakeLegacyMigrationService service = new(
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Disabled));
        FakeLegacyTaskAdapter adapter = new([])
        {
            RestoreStatus = LegacyTaskMutationStatus.Changed,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .RestoreDisabledAsync();

        Assert.Equal(
            ["restore:\\NightGate tests\\shutdown-a", "scan"],
            adapter.Events);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Failed,
            service.Migrations.Single().Status);
        Assert.Empty(snapshot.DisabledMigrations);
        Assert.Equal(0, snapshot.PendingRecoveryCount);
    }

    [Fact]
    public async Task RestoreDisabled_WhenPreparedCompletionCannotBeRecorded_LeavesRetryRecord()
    {
        FakeLegacyMigrationService service = new(
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Prepared))
        {
            RejectedCompletionStatuses =
            [
                DesktopLegacyTaskMigrationStatus.Restored,
            ],
        };
        FakeLegacyTaskAdapter adapter = new([])
        {
            ReconcileStatus = LegacyTaskMutationStatus.Disabled,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .RestoreDisabledAsync();

        Assert.Equal(
            ["restore:\\NightGate tests\\shutdown-a", "scan"],
            adapter.Events);
        Assert.Equal(0, snapshot.PendingRecoveryCount);
        Assert.Equal(1, snapshot.PendingRestoreCount);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.RestorePrepared,
            Assert.Single(service.Migrations).Status);
    }

    [Fact]
    public async Task RestoreDisabled_WhenCompletionCannotBeRecorded_DoesNotDisableTaskAgain()
    {
        FakeLegacyMigrationService service = new(
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Disabled))
        {
            RejectedCompletionStatuses =
            [
                DesktopLegacyTaskMigrationStatus.Restored,
            ],
        };
        FakeLegacyTaskAdapter adapter = new(
            [CandidateA with { WasEnabled = false }])
        {
            RestoreStatus = LegacyTaskMutationStatus.Restored,
            ReconcileStatus = LegacyTaskMutationStatus.Disabled,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .RestoreDisabledAsync();

        Assert.Equal(
            ["restore:\\NightGate tests\\shutdown-a", "scan"],
            adapter.Events);
        Assert.Empty(snapshot.DisabledMigrations);
        Assert.Equal(0, snapshot.PendingRecoveryCount);
        Assert.Equal(1, snapshot.PendingRestoreCount);
        Assert.Equal(0, snapshot.UnverifiedDisabledCount);
        Assert.Equal(CandidateA, Assert.Single(snapshot.Candidates));
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.RestorePrepared,
            Assert.Single(service.Migrations).Status);
        Assert.DoesNotContain(
            adapter.Events,
            item => item.StartsWith("reconcile:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DesktopLegacyTaskMigrationStatus.Prepared)]
    [InlineData(DesktopLegacyTaskMigrationStatus.Disabled)]
    public async Task RestoreDisabled_WhenRestoredCompletionCannotBeRecorded_RepeatedRefreshesContinueRestoreInsteadOfDisabling(
        DesktopLegacyTaskMigrationStatus persistedStatus)
    {
        List<string> eventOrder = [];
        FakeLegacyMigrationService service = new(
            eventOrder,
            Migration(
                CandidateA,
                persistedStatus,
                disabledStateVerified: false))
        {
            RejectedCompletionStatuses =
            [
                DesktopLegacyTaskMigrationStatus.Restored,
            ],
        };
        FakeLegacyTaskAdapter adapter = new(
            [CandidateA with { WasEnabled = false }],
            eventOrder)
        {
            RestoreStatus = LegacyTaskMutationStatus.Restored,
            ReconcileStatus = LegacyTaskMutationStatus.Disabled,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        _ = await coordinator.RestoreDisabledAsync();
        LegacyTaskMigrationCoordinator restarted = new(adapter, service);
        DesktopLegacyMigrationSnapshot firstRefresh = await restarted.RefreshAsync();
        DesktopLegacyMigrationSnapshot secondRefresh = await restarted.RefreshAsync();

        Assert.Equal(
            [
                "complete:restoreprepared:\\NightGate tests\\shutdown-a",
                "restore:\\NightGate tests\\shutdown-a",
                "complete-rejected:restored:\\NightGate tests\\shutdown-a",
                "scan",
                "restore:\\NightGate tests\\shutdown-a",
                "complete-rejected:restored:\\NightGate tests\\shutdown-a",
                "scan",
                "restore:\\NightGate tests\\shutdown-a",
                "complete-rejected:restored:\\NightGate tests\\shutdown-a",
                "scan",
            ],
            eventOrder);
        Assert.DoesNotContain(
            eventOrder,
            item => item.StartsWith("reconcile:", StringComparison.Ordinal));
        Assert.Equal(CandidateA, Assert.Single(firstRefresh.Candidates));
        Assert.Equal(CandidateA, Assert.Single(secondRefresh.Candidates));
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.RestorePrepared,
            Assert.Single(service.Migrations).Status);
        Assert.Equal(0, firstRefresh.PendingRecoveryCount);
        Assert.Equal(0, secondRefresh.PendingRecoveryCount);
        Assert.Equal(1, firstRefresh.PendingRestoreCount);
        Assert.Equal(1, secondRefresh.PendingRestoreCount);
    }

    [Fact]
    public async Task RestoreDisabled_WhenRestoreIntentCannotBePersisted_DoesNotEnableTask()
    {
        FakeLegacyMigrationService service = new(
            Migration(CandidateA, DesktopLegacyTaskMigrationStatus.Disabled))
        {
            RejectedCompletionStatuses =
            [
                DesktopLegacyTaskMigrationStatus.RestorePrepared,
            ],
        };
        FakeLegacyTaskAdapter adapter = new(
            [CandidateA with { WasEnabled = false }]);
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator
            .RestoreDisabledAsync();

        Assert.DoesNotContain(
            adapter.Events,
            item => item.StartsWith("restore:", StringComparison.Ordinal));
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Disabled,
            Assert.Single(service.Migrations).Status);
        Assert.Single(snapshot.DisabledMigrations);
    }

    [Fact]
    public async Task UnavailableService_NeverChangesScheduledTasks()
    {
        FakeLegacyMigrationService service = new()
        {
            Available = false,
        };
        FakeLegacyTaskAdapter adapter = new([CandidateA]);
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.False(snapshot.Available);
        Assert.Empty(adapter.Events);
    }

    [Fact]
    public async Task UnavailableTaskEnumeration_IsReportedWithoutBlockingRestoreRecords()
    {
        DesktopLegacyTaskMigration disabled = Migration(
            CandidateA,
            DesktopLegacyTaskMigrationStatus.Disabled);
        FakeLegacyMigrationService service = new(disabled);
        FakeLegacyTaskAdapter adapter = new([])
        {
            ScanAvailable = false,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);

        DesktopLegacyMigrationSnapshot snapshot = await coordinator.RefreshAsync();

        Assert.True(snapshot.Available);
        Assert.False(snapshot.ScanAvailable);
        Assert.Empty(snapshot.Candidates);
        Assert.Single(snapshot.DisabledMigrations);
    }

    [Fact]
    public async Task LostCompletionResponse_IsRecoveredFromPersistedPrepareOnNextRefresh()
    {
        FakeLegacyMigrationService service = new()
        {
            RejectCompletions = true,
        };
        FakeLegacyTaskAdapter adapter = new([CandidateA])
        {
            DisableStatus = LegacyTaskMutationStatus.Disabled,
            ReconcileStatus = LegacyTaskMutationStatus.Unchanged,
        };
        LegacyTaskMigrationCoordinator coordinator = new(adapter, service);
        _ = await coordinator.RefreshAsync();

        DesktopLegacyMigrationSnapshot interrupted = await coordinator
            .DisableSelectedAsync([CandidateA]);

        Assert.Equal(1, interrupted.PendingRecoveryCount);
        Assert.Empty(interrupted.DisabledMigrations);
        service.RejectCompletions = false;

        DesktopLegacyMigrationSnapshot recovered = await coordinator.RefreshAsync();

        Assert.Equal(0, recovered.PendingRecoveryCount);
        Assert.Single(recovered.DisabledMigrations);
        Assert.Equal(
            DesktopLegacyTaskMigrationStatus.Disabled,
            service.Migrations.Single().Status);
    }

    private static DesktopLegacyTaskMigration Migration(
        LegacyShutdownTaskCandidate candidate,
        DesktopLegacyTaskMigrationStatus status,
        bool? disabledStateVerified = null)
    {
        DateTimeOffset prepared = new(2026, 7, 15, 1, 0, 0, TimeSpan.Zero);
        return new(
            Guid.NewGuid().ToString("N"),
            candidate.TaskPath,
            candidate.ActionFingerprint,
            candidate.WasEnabled,
            status,
            prepared,
            status is DesktopLegacyTaskMigrationStatus.Prepared or
                DesktopLegacyTaskMigrationStatus.RestorePrepared
                    ? null
                    : prepared.AddSeconds(1),
            disabledStateVerified ?? status == DesktopLegacyTaskMigrationStatus.Disabled);
    }

    private sealed class FakeLegacyTaskAdapter : ILegacyShutdownTaskAdapter
    {
        private readonly List<LegacyShutdownTaskCandidate> _scan;
        private readonly List<string> _events;

        public FakeLegacyTaskAdapter(
            IReadOnlyList<LegacyShutdownTaskCandidate> scan,
            List<string>? events = null)
        {
            _scan = [.. scan];
            _events = events ?? [];
        }

        public LegacyTaskMutationStatus DisableStatus { get; init; } =
            LegacyTaskMutationStatus.Disabled;

        public LegacyTaskMutationStatus ReconcileStatus { get; init; } =
            LegacyTaskMutationStatus.Disabled;

        public LegacyTaskMutationStatus RestoreStatus { get; init; } =
            LegacyTaskMutationStatus.Restored;

        public LegacyTaskObservationStatus ObservationStatus { get; init; } =
            LegacyTaskObservationStatus.MatchingEnabled;

        public bool ScanAvailable { get; init; } = true;

        public IReadOnlyList<string> Events => _events;

        public IReadOnlyList<LegacyShutdownTaskCandidate> Scan(
            CancellationToken cancellationToken = default)
        {
            _events.Add("scan");
            return _scan;
        }

        public LegacyShutdownTaskScanResult ScanWithStatus(
            CancellationToken cancellationToken = default)
        {
            _events.Add("scan");
            return ScanAvailable
                ? new(true, null, _scan)
                : LegacyShutdownTaskScanResult.Unavailable("scheduler-unavailable");
        }

        public IReadOnlyList<LegacyTaskMutationResult> DisableSelected(
            IEnumerable<LegacyShutdownTaskCandidate>? selectedCandidates,
            CancellationToken cancellationToken = default) => Mutate(
            "disable",
            selectedCandidates,
            DisableStatus);

        public IReadOnlyList<LegacyTaskMutationResult> ReconcilePrepared(
            IEnumerable<LegacyShutdownTaskCandidate>? persistedCandidates,
            CancellationToken cancellationToken = default) => Mutate(
            "reconcile",
            persistedCandidates,
            ReconcileStatus);

        public IReadOnlyList<LegacyTaskMutationResult> Restore(
            IEnumerable<LegacyShutdownTaskCandidate>? persistedCandidates,
            CancellationToken cancellationToken = default) => Mutate(
            "restore",
            persistedCandidates,
            RestoreStatus);

        public IReadOnlyList<LegacyTaskObservationResult> Observe(
            IEnumerable<LegacyShutdownTaskCandidate>? persistedCandidates,
            CancellationToken cancellationToken = default) =>
            (persistedCandidates ?? [])
                .Select(candidate =>
                {
                    _events.Add($"observe:{candidate.TaskPath}");
                    return new LegacyTaskObservationResult(
                        candidate.TaskPath,
                        candidate.ActionFingerprint,
                        ObservationStatus);
                })
                .ToArray();

        private IReadOnlyList<LegacyTaskMutationResult> Mutate(
            string operation,
            IEnumerable<LegacyShutdownTaskCandidate>? candidates,
            LegacyTaskMutationStatus status) => (candidates ?? [])
            .Select(candidate =>
            {
                _events.Add($"{operation}:{candidate.TaskPath}");
                bool? enabled = operation switch
                {
                    "reconcile" when status is LegacyTaskMutationStatus.Disabled or
                        LegacyTaskMutationStatus.Unchanged => false,
                    "restore" when status is LegacyTaskMutationStatus.Restored or
                        LegacyTaskMutationStatus.Unchanged => true,
                    _ => null,
                };
                if (enabled is { } currentEnabled)
                {
                    int index = _scan.FindIndex(item => string.Equals(
                        item.TaskPath,
                        candidate.TaskPath,
                        StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        _scan[index] = _scan[index] with { WasEnabled = currentEnabled };
                    }
                }

                return new LegacyTaskMutationResult(
                    candidate.TaskPath,
                    candidate.ActionFingerprint,
                    status);
            })
            .ToArray();
    }

    private sealed class FakeLegacyMigrationService : IDesktopLegacyMigrationService
    {
        private readonly List<string> _events;

        public FakeLegacyMigrationService(params DesktopLegacyTaskMigration[] migrations)
            : this([], migrations)
        {
        }

        public FakeLegacyMigrationService(
            List<string> events,
            params DesktopLegacyTaskMigration[] migrations)
        {
            _events = events;
            Migrations = [.. migrations];
        }

        public bool Available { get; init; } = true;

        public bool RejectCompletions { get; set; }

        public HashSet<DesktopLegacyTaskMigrationStatus>
            RejectedCompletionStatuses
        { get; init; } = [];

        public List<DesktopLegacyTaskMigration> Migrations { get; }

        public IReadOnlyList<string> Events => _events;

        public ValueTask<DesktopLegacyMigrationListResult> ListAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
            Available
                ? new DesktopLegacyMigrationListResult(
                    true,
                    null,
                    Migrations.Where(item => item.Status is
                            DesktopLegacyTaskMigrationStatus.Prepared or
                            DesktopLegacyTaskMigrationStatus.Disabled or
                            DesktopLegacyTaskMigrationStatus.RestorePrepared)
                        .ToArray(),
                    Migrations.Count(item => item.Status ==
                        DesktopLegacyTaskMigrationStatus.Failed))
                : DesktopLegacyMigrationListResult.Unavailable("offline"));

        public ValueTask<DesktopLegacyMigrationMutationResult> PrepareAsync(
            LegacyShutdownTaskCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"prepare:{candidate.TaskPath}");
            DesktopLegacyTaskMigration? active = Migrations.LastOrDefault(item =>
                (item.Status is DesktopLegacyTaskMigrationStatus.Prepared or
                    DesktopLegacyTaskMigrationStatus.Disabled or
                    DesktopLegacyTaskMigrationStatus.RestorePrepared)
                && string.Equals(
                    item.TaskPath,
                    candidate.TaskPath,
                    StringComparison.OrdinalIgnoreCase));
            if (active is not null)
            {
                bool accepted = string.Equals(
                        active.ActionFingerprint,
                        candidate.ActionFingerprint,
                        StringComparison.Ordinal)
                    && active.OriginalEnabled == candidate.WasEnabled;
                return ValueTask.FromResult(new DesktopLegacyMigrationMutationResult(
                    accepted,
                    accepted ? null : "taskAlreadyTracked",
                    accepted ? active : null));
            }

            DesktopLegacyTaskMigration migration = Migration(
                candidate,
                DesktopLegacyTaskMigrationStatus.Prepared);
            Migrations.Add(migration);
            return ValueTask.FromResult(new DesktopLegacyMigrationMutationResult(
                true,
                null,
                migration));
        }

        public ValueTask<DesktopLegacyMigrationMutationResult> CompleteAsync(
            string migrationId,
            DesktopLegacyTaskMigrationStatus status,
            CancellationToken cancellationToken = default)
        {
            int index = Migrations.FindIndex(item => item.MigrationId == migrationId);
            DesktopLegacyTaskMigration current = Migrations[index];
            if (RejectCompletions || RejectedCompletionStatuses.Contains(status))
            {
                _events.Add($"complete-rejected:{status.ToString().ToLowerInvariant()}:{current.TaskPath}");
                return ValueTask.FromResult(new DesktopLegacyMigrationMutationResult(
                    false,
                    "offline",
                    null));
            }

            DesktopLegacyTaskMigration completed = current with
            {
                Status = status,
                CompletedAtUtc = status == DesktopLegacyTaskMigrationStatus.RestorePrepared
                    ? null
                    : current.PreparedAtUtc.AddSeconds(1),
                DisabledStateVerified = status == DesktopLegacyTaskMigrationStatus.Disabled
                    || current.DisabledStateVerified,
            };
            Migrations[index] = completed;
            _events.Add($"complete:{status.ToString().ToLowerInvariant()}:{current.TaskPath}");
            return ValueTask.FromResult(new DesktopLegacyMigrationMutationResult(
                true,
                null,
                completed));
        }

        public ValueTask<DesktopLegacyMigrationLookupResult> FindRecoveryCandidateAsync(
            string taskPath,
            CancellationToken cancellationToken = default)
        {
            _events.Add($"find-recovery:{taskPath}");
            DesktopLegacyTaskMigration? latest = Migrations
                .Where(item => string.Equals(
                    item.TaskPath,
                    taskPath,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.PreparedAtUtc)
                .ThenByDescending(item => item.MigrationId, StringComparer.Ordinal)
                .FirstOrDefault();
            DesktopLegacyTaskMigration? candidate = latest is
            {
                Status: DesktopLegacyTaskMigrationStatus.Failed,
                OriginalEnabled: true,
                DisabledStateVerified: false,
            }
                    ? latest
                    : null;
            return ValueTask.FromResult(new DesktopLegacyMigrationLookupResult(
                true,
                null,
                candidate,
                candidate is null ? null : new string('b', 64)));
        }

        public ValueTask<DesktopLegacyMigrationMutationResult> RecoverDisabledAsync(
            DesktopLegacyTaskMigration migration,
            string recoveryToken,
            CancellationToken cancellationToken = default)
        {
            int index = Migrations.FindIndex(item => item.MigrationId == migration.MigrationId);
            DesktopLegacyTaskMigration current = Migrations[index];
            _events.Add($"recover-disabled:{current.TaskPath}");
            DesktopLegacyTaskMigration recovered = current with
            {
                Status = DesktopLegacyTaskMigrationStatus.Disabled,
                DisabledStateVerified = true,
            };
            Migrations[index] = recovered;
            return ValueTask.FromResult(new DesktopLegacyMigrationMutationResult(
                true,
                null,
                recovered));
        }
    }

    private sealed class FakeLegacyTaskElevationService(List<string> events) :
        ILegacyTaskElevationService
    {
        public LegacyTaskMutationStatus DisableStatus { get; init; } =
            LegacyTaskMutationStatus.Disabled;

        public LegacyTaskMutationStatus RestoreStatus { get; init; } =
            LegacyTaskMutationStatus.Restored;

        public ValueTask<LegacyTaskMutationStatus> DisableAsync(
            LegacyShutdownTaskCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            events.Add($"elevate-disable:{candidate.TaskPath}");
            return ValueTask.FromResult(DisableStatus);
        }

        public ValueTask<LegacyTaskMutationStatus> RestoreAsync(
            LegacyShutdownTaskCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            events.Add($"elevate-restore:{candidate.TaskPath}");
            return ValueTask.FromResult(RestoreStatus);
        }
    }
}
