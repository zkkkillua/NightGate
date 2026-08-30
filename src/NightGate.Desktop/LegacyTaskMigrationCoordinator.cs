namespace NightGate.Desktop;

public interface IDesktopLegacyMigrationService
{
    ValueTask<DesktopLegacyMigrationListResult> ListAsync(
        CancellationToken cancellationToken = default);

    ValueTask<DesktopLegacyMigrationMutationResult> PrepareAsync(
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken = default);

    ValueTask<DesktopLegacyMigrationMutationResult> CompleteAsync(
        string migrationId,
        DesktopLegacyTaskMigrationStatus status,
        CancellationToken cancellationToken = default);

    ValueTask<DesktopLegacyMigrationLookupResult> FindRecoveryCandidateAsync(
        string taskPath,
        CancellationToken cancellationToken = default);

    ValueTask<DesktopLegacyMigrationMutationResult> RecoverDisabledAsync(
        DesktopLegacyTaskMigration migration,
        string recoveryToken,
        CancellationToken cancellationToken = default);
}

public sealed class DesktopClientLegacyMigrationService(
    NightGateDesktopClient client) : IDesktopLegacyMigrationService
{
    private readonly NightGateDesktopClient _client =
        client ?? throw new ArgumentNullException(nameof(client));

    public ValueTask<DesktopLegacyMigrationListResult> ListAsync(
        CancellationToken cancellationToken = default) =>
        _client.ListLegacyTaskMigrationsAsync(cancellationToken);

    public ValueTask<DesktopLegacyMigrationMutationResult> PrepareAsync(
        LegacyShutdownTaskCandidate candidate,
        CancellationToken cancellationToken = default) =>
        _client.PrepareLegacyTaskMigrationAsync(candidate, cancellationToken);

    public ValueTask<DesktopLegacyMigrationMutationResult> CompleteAsync(
        string migrationId,
        DesktopLegacyTaskMigrationStatus status,
        CancellationToken cancellationToken = default) =>
        _client.CompleteLegacyTaskMigrationAsync(
            migrationId,
            status,
            cancellationToken);

    public ValueTask<DesktopLegacyMigrationLookupResult> FindRecoveryCandidateAsync(
        string taskPath,
        CancellationToken cancellationToken = default) =>
        _client.FindLegacyTaskMigrationRecoveryCandidateAsync(
            taskPath,
            cancellationToken);

    public ValueTask<DesktopLegacyMigrationMutationResult> RecoverDisabledAsync(
        DesktopLegacyTaskMigration migration,
        string recoveryToken,
        CancellationToken cancellationToken = default) =>
        _client.RecoverLegacyTaskMigrationDisabledAsync(
            migration,
            recoveryToken,
            cancellationToken);
}

public sealed record DesktopLegacyMigrationSnapshot(
    bool Available,
    string? Error,
    IReadOnlyList<LegacyShutdownTaskCandidate> Candidates,
    IReadOnlyList<DesktopLegacyTaskMigration> DisabledMigrations,
    int PendingRecoveryCount,
    int FailedCount,
    bool ScanAvailable = true,
    int UnverifiedDisabledCount = 0,
    int PendingRestoreCount = 0)
{
    public static DesktopLegacyMigrationSnapshot Unavailable(string error) => new(
        false,
        error,
        Array.Empty<LegacyShutdownTaskCandidate>(),
        Array.Empty<DesktopLegacyTaskMigration>(),
        0,
        0,
        false);
}

public interface ILegacyTaskMigrationCoordinator
{
    ValueTask<DesktopLegacyMigrationSnapshot> RefreshAsync(
        CancellationToken cancellationToken = default);

    ValueTask<DesktopLegacyMigrationSnapshot> DisableSelectedAsync(
        IReadOnlyList<LegacyShutdownTaskCandidate> selected,
        CancellationToken cancellationToken = default);

    ValueTask<DesktopLegacyMigrationSnapshot> RestoreDisabledAsync(
        CancellationToken cancellationToken = default);
}

public sealed class LegacyTaskMigrationCoordinator : ILegacyTaskMigrationCoordinator
{
    private readonly ILegacyShutdownTaskAdapter _adapter;
    private readonly IDesktopLegacyMigrationService _service;
    private readonly ILegacyTaskElevationService? _elevationService;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public LegacyTaskMigrationCoordinator(
        ILegacyShutdownTaskAdapter adapter,
        IDesktopLegacyMigrationService service,
        ILegacyTaskElevationService? elevationService = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(service);
        _adapter = adapter;
        _service = service;
        _elevationService = elevationService;
    }

    public async ValueTask<DesktopLegacyMigrationSnapshot> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<DesktopLegacyMigrationSnapshot> DisableSelectedAsync(
        IReadOnlyList<LegacyShutdownTaskCandidate> selected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
            foreach (LegacyShutdownTaskCandidate? candidate in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate is null
                    || !candidate.WasEnabled
                    || !paths.Add(candidate.TaskPath))
                {
                    continue;
                }

                DesktopLegacyMigrationMutationResult prepared = await _service
                    .PrepareAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
                if (!prepared.Accepted || prepared.Migration is null)
                {
                    continue;
                }

                LegacyTaskMutationResult? mutation = _adapter.ReconcilePrepared(
                        [CandidateFor(prepared.Migration)],
                        cancellationToken)
                    .SingleOrDefault();
                if (mutation is null)
                {
                    continue;
                }

                LegacyTaskMutationStatus mutationStatus = mutation.Status;
                if (mutationStatus == LegacyTaskMutationStatus.Unavailable
                    && _elevationService is not null)
                {
                    mutationStatus = await _elevationService.DisableAsync(
                        CandidateFor(prepared.Migration),
                        cancellationToken).ConfigureAwait(false);
                }

                DesktopLegacyTaskMigrationStatus? completion =
                    CompletionForDisable(mutationStatus);
                if (completion is { } status
                    && (status != prepared.Migration.Status
                        || status == DesktopLegacyTaskMigrationStatus.Disabled
                        && !prepared.Migration.DisabledStateVerified))
                {
                    _ = await _service.CompleteAsync(
                        prepared.Migration.MigrationId,
                        status,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask<DesktopLegacyMigrationSnapshot> RestoreDisabledAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DesktopLegacyMigrationListResult listed = await _service
                .ListAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.Available)
            {
                return DesktopLegacyMigrationSnapshot.Unavailable(
                    listed.Error ?? "service-degraded");
            }

            foreach (DesktopLegacyTaskMigration migration in listed.Migrations.Where(
                         item => item.Status is
                             DesktopLegacyTaskMigrationStatus.Prepared or
                             DesktopLegacyTaskMigrationStatus.Disabled or
                             DesktopLegacyTaskMigrationStatus.RestorePrepared))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DesktopLegacyTaskMigration restorePrepared = migration;
                if (migration.Status !=
                    DesktopLegacyTaskMigrationStatus.RestorePrepared)
                {
                    DesktopLegacyMigrationMutationResult preparedRestore =
                        await _service.CompleteAsync(
                            migration.MigrationId,
                            DesktopLegacyTaskMigrationStatus.RestorePrepared,
                            cancellationToken).ConfigureAwait(false);
                    if (!preparedRestore.Accepted
                        || preparedRestore.Migration is not
                        {
                            Status: DesktopLegacyTaskMigrationStatus.RestorePrepared,
                        } persistedRestore)
                    {
                        // The durable restore intent is the write-ahead record.
                        // Never enable a task until that record is safely stored.
                        continue;
                    }

                    restorePrepared = persistedRestore;
                }

                _ = await RestorePreparedMigrationAsync(
                    restorePrepared,
                    allowElevation: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return await RefreshCoreAsync(
                cancellationToken,
                reconcilePrepared: false,
                recoverRestorePrepared: false).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async ValueTask<DesktopLegacyMigrationSnapshot> RefreshCoreAsync(
        CancellationToken cancellationToken,
        bool reconcilePrepared = true,
        bool recoverRestorePrepared = true)
    {
        DesktopLegacyMigrationListResult listed = await _service
            .ListAsync(cancellationToken).ConfigureAwait(false);
        if (!listed.Available)
        {
            return DesktopLegacyMigrationSnapshot.Unavailable(
                listed.Error ?? "service-degraded");
        }

        bool stateChanged = false;
        if (recoverRestorePrepared)
        {
            foreach (DesktopLegacyTaskMigration migration in listed.Migrations.Where(
                         item => item.Status ==
                             DesktopLegacyTaskMigrationStatus.RestorePrepared))
            {
                cancellationToken.ThrowIfCancellationRequested();
                stateChanged |= await RestorePreparedMigrationAsync(
                    migration,
                    allowElevation: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (stateChanged)
            {
                listed = await _service.ListAsync(cancellationToken).ConfigureAwait(false);
                if (!listed.Available)
                {
                    return DesktopLegacyMigrationSnapshot.Unavailable(
                        listed.Error ?? "service-degraded");
                }

                stateChanged = false;
            }
        }

        HashSet<string> verifiedDisabledIds = listed.Migrations
            .Where(item => item.Status == DesktopLegacyTaskMigrationStatus.Disabled
                && item.DisabledStateVerified)
            .Select(item => item.MigrationId)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> unverifiedDisabledIds = listed.Migrations
            .Where(item => item.Status == DesktopLegacyTaskMigrationStatus.Disabled
                && !item.DisabledStateVerified)
            .Select(item => item.MigrationId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (DesktopLegacyTaskMigration migration in listed.Migrations.Where(
                     item => reconcilePrepared
                        && (item.Status == DesktopLegacyTaskMigrationStatus.Prepared
                            || item.Status == DesktopLegacyTaskMigrationStatus.Disabled
                            && !item.DisabledStateVerified)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DesktopLegacyTaskMigrationStatus? completion;
            if (!migration.OriginalEnabled)
            {
                completion = DesktopLegacyTaskMigrationStatus.Failed;
            }
            else
            {
                LegacyTaskMutationResult? mutation = _adapter
                    .ReconcilePrepared([CandidateFor(migration)], cancellationToken)
                    .SingleOrDefault();
                completion = CompletionForDisable(mutation?.Status);
            }

            if (completion == DesktopLegacyTaskMigrationStatus.Disabled)
            {
                verifiedDisabledIds.Add(migration.MigrationId);
                unverifiedDisabledIds.Remove(migration.MigrationId);
            }
            else if (completion is null
                && migration.Status == DesktopLegacyTaskMigrationStatus.Disabled)
            {
                unverifiedDisabledIds.Add(migration.MigrationId);
            }

            if (completion is { } status
                && (status != migration.Status
                    || status == DesktopLegacyTaskMigrationStatus.Disabled
                    && !migration.DisabledStateVerified))
            {
                DesktopLegacyMigrationMutationResult completed = await _service
                    .CompleteAsync(
                        migration.MigrationId,
                        status,
                        cancellationToken)
                    .ConfigureAwait(false);
                stateChanged |= completed.Accepted;
                if (!completed.Accepted)
                {
                    verifiedDisabledIds.Remove(migration.MigrationId);
                    if (migration.Status == DesktopLegacyTaskMigrationStatus.Disabled)
                    {
                        unverifiedDisabledIds.Add(migration.MigrationId);
                    }
                }
            }
        }

        if (stateChanged)
        {
            listed = await _service.ListAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.Available)
            {
                return DesktopLegacyMigrationSnapshot.Unavailable(
                    listed.Error ?? "service-degraded");
            }
        }

        LegacyShutdownTaskScanResult scan = _adapter.ScanWithStatus(cancellationToken);
        if (listed.FailedCount > 0
            && await RecoverFailedMigrationsAsync(scan, cancellationToken)
                .ConfigureAwait(false))
        {
            return await RefreshCoreAsync(
                cancellationToken,
                reconcilePrepared,
                recoverRestorePrepared: false).ConfigureAwait(false);
        }

        bool scanStateChanged = false;
        Dictionary<string, LegacyShutdownTaskCandidate> retryCandidates = new(
            StringComparer.OrdinalIgnoreCase);
        foreach (DesktopLegacyTaskMigration migration in listed.Migrations)
        {
            LegacyShutdownTaskCandidate? liveEnabled = scan.Candidates.FirstOrDefault(candidate =>
                candidate.WasEnabled
                && string.Equals(
                    candidate.TaskPath,
                    migration.TaskPath,
                    StringComparison.OrdinalIgnoreCase));
            if (liveEnabled is null)
            {
                continue;
            }

            if (!string.Equals(
                    liveEnabled.ActionFingerprint,
                    migration.ActionFingerprint,
                    StringComparison.Ordinal))
            {
                LegacyTaskObservationResult? observation = _adapter
                    .Observe([CandidateFor(migration)], cancellationToken)
                    .SingleOrDefault();
                if (observation?.Status == LegacyTaskObservationStatus.Changed)
                {
                    DesktopLegacyMigrationMutationResult failed = await _service
                        .CompleteAsync(
                            migration.MigrationId,
                            DesktopLegacyTaskMigrationStatus.Failed,
                            cancellationToken)
                        .ConfigureAwait(false);
                    scanStateChanged |= failed.Accepted;
                    if (failed.Accepted)
                    {
                        verifiedDisabledIds.Remove(migration.MigrationId);
                        unverifiedDisabledIds.Remove(migration.MigrationId);
                    }

                    continue;
                }

                // A 0.3.3 record intentionally has a different persisted hash
                // from the current full-action scan. Keep using its persisted
                // identity so Prepare/elevation can safely retry that record.
                retryCandidates[migration.TaskPath] = CandidateFor(migration);
            }

            if (verifiedDisabledIds.Remove(migration.MigrationId))
            {
                // A currently enabled task observed by the full scheduler scan is
                // stronger evidence than the last successful disable. Never hide
                // that live candidate or claim it is still disabled.
                unverifiedDisabledIds.Add(migration.MigrationId);
            }
        }

        if (scanStateChanged)
        {
            listed = await _service.ListAsync(cancellationToken).ConfigureAwait(false);
            if (!listed.Available)
            {
                return DesktopLegacyMigrationSnapshot.Unavailable(
                    listed.Error ?? "service-degraded");
            }
        }

        HashSet<string> activePaths = listed.Migrations
            .Where(item => verifiedDisabledIds.Contains(item.MigrationId))
            .Select(item => item.TaskPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<LegacyShutdownTaskCandidate> candidates = scan.Candidates
            .Where(candidate => candidate.WasEnabled
                && !activePaths.Contains(candidate.TaskPath))
            .Select(candidate => retryCandidates.TryGetValue(
                    candidate.TaskPath,
                    out LegacyShutdownTaskCandidate? persisted)
                ? persisted
                : candidate)
            .ToList();
        HashSet<string> candidatePaths = candidates
            .Select(candidate => candidate.TaskPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopLegacyTaskMigration migration in listed.Migrations.Where(item =>
                     item.OriginalEnabled
                     && !verifiedDisabledIds.Contains(item.MigrationId)
                     && (item.Status == DesktopLegacyTaskMigrationStatus.Prepared
                         || unverifiedDisabledIds.Contains(item.MigrationId))))
        {
            if (candidatePaths.Add(migration.TaskPath))
            {
                candidates.Add(CandidateFor(migration));
            }
        }

        candidates.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(
            left.TaskPath,
            right.TaskPath));
        DesktopLegacyTaskMigration[] disabled = listed.Migrations
            .Where(item => item.Status == DesktopLegacyTaskMigrationStatus.Disabled)
            .OrderBy(item => item.TaskPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(
            true,
            null,
            candidates.ToArray(),
            disabled,
            listed.Migrations.Count(item => item.Status ==
                DesktopLegacyTaskMigrationStatus.Prepared),
            listed.FailedCount,
            scan.Available,
            disabled.Count(item => unverifiedDisabledIds.Contains(item.MigrationId)),
            listed.Migrations.Count(item => item.Status ==
                DesktopLegacyTaskMigrationStatus.RestorePrepared));
    }

    private async ValueTask<bool> RecoverFailedMigrationsAsync(
        LegacyShutdownTaskScanResult scan,
        CancellationToken cancellationToken)
    {
        bool recoveredAny = false;
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (LegacyShutdownTaskCandidate liveTask in scan.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (liveTask.WasEnabled || !paths.Add(liveTask.TaskPath))
            {
                continue;
            }

            DesktopLegacyMigrationLookupResult lookup = await _service
                .FindRecoveryCandidateAsync(liveTask.TaskPath, cancellationToken)
                .ConfigureAwait(false);
            if (!lookup.Available
                || lookup.Migration is not { } migration
                || lookup.RecoveryToken is not { } recoveryToken)
            {
                continue;
            }

            LegacyTaskObservationResult? observation = _adapter
                .Observe([CandidateFor(migration)], cancellationToken)
                .SingleOrDefault();
            if (observation?.Status !=
                LegacyTaskObservationStatus.MatchingDisabled)
            {
                continue;
            }

            DesktopLegacyMigrationMutationResult recovered = await _service
                .RecoverDisabledAsync(migration, recoveryToken, cancellationToken)
                .ConfigureAwait(false);
            recoveredAny |= recovered.Accepted
                && recovered.Migration is
                {
                    Status: DesktopLegacyTaskMigrationStatus.Disabled,
                    DisabledStateVerified: true,
                };
        }

        return recoveredAny;
    }

    private async ValueTask<bool> RestorePreparedMigrationAsync(
        DesktopLegacyTaskMigration migration,
        bool allowElevation,
        CancellationToken cancellationToken)
    {
        LegacyShutdownTaskCandidate candidate = CandidateFor(migration);
        LegacyTaskMutationResult? mutation = _adapter
            .Restore([candidate], cancellationToken)
            .SingleOrDefault();
        LegacyTaskMutationStatus? mutationStatus = mutation?.Status;
        if (mutationStatus == LegacyTaskMutationStatus.Unavailable
            && allowElevation
            && _elevationService is not null)
        {
            mutationStatus = await _elevationService.RestoreAsync(
                candidate,
                cancellationToken).ConfigureAwait(false);
        }

        DesktopLegacyTaskMigrationStatus? completion =
            CompletionForRestore(mutationStatus);
        if (completion is not { } status)
        {
            return false;
        }

        DesktopLegacyMigrationMutationResult completed = await _service
            .CompleteAsync(
                migration.MigrationId,
                status,
                cancellationToken).ConfigureAwait(false);
        return completed.Accepted;
    }

    private static LegacyShutdownTaskCandidate CandidateFor(
        DesktopLegacyTaskMigration migration) => new(
        migration.TaskPath,
        migration.ActionFingerprint,
        migration.OriginalEnabled);

    private static DesktopLegacyTaskMigrationStatus? CompletionForDisable(
        LegacyTaskMutationStatus? status) => status switch
        {
            LegacyTaskMutationStatus.Disabled or
            LegacyTaskMutationStatus.Unchanged =>
                DesktopLegacyTaskMigrationStatus.Disabled,
            LegacyTaskMutationStatus.Changed or
            LegacyTaskMutationStatus.Missing or
            LegacyTaskMutationStatus.Invalid =>
                DesktopLegacyTaskMigrationStatus.Failed,
            _ => null,
        };

    private static DesktopLegacyTaskMigrationStatus? CompletionForRestore(
        LegacyTaskMutationStatus? status) => status switch
        {
            LegacyTaskMutationStatus.Restored or
            LegacyTaskMutationStatus.Unchanged =>
                DesktopLegacyTaskMigrationStatus.Restored,
            LegacyTaskMutationStatus.Changed or
            LegacyTaskMutationStatus.Missing or
            LegacyTaskMutationStatus.Invalid =>
                DesktopLegacyTaskMigrationStatus.Failed,
            _ => null,
        };
}
