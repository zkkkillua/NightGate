namespace NightGate.Desktop;

internal enum ProcessSourceContinuityPhase
{
    Dormant,
    FreshLost,
    Lost,
    Recovering,
    RecoveryCandidate,
    Trusted,
}

internal sealed record ProcessSourceAcknowledgementTuple(
    ProcessObservationAcknowledgementKind Kind,
    string ObserverEpoch,
    long TransitionRevision);

internal sealed record ProcessSourceContinuityCheckpoint(
    long Version,
    ProcessSourceContinuityPhase Phase,
    string ObserverEpoch,
    long HighestAcceptedTransitionRevision,
    ProcessSourceAcknowledgementTuple? LastAcceptedAcknowledgement);

internal enum ProcessSourceContinuityReductionKind
{
    Applied,
    Idempotent,
    Ignored,
}

internal sealed record ProcessSourceContinuityReduction(
    ProcessSourceContinuityReductionKind Kind,
    ProcessSourceContinuityCheckpoint Checkpoint);

internal static class ProcessSourceContinuityReducer
{
    private const int MaximumEpochLength = 256;

    internal static bool IsValidCheckpoint(
        ProcessSourceContinuityCheckpoint? candidate)
    {
        if (candidate is null
            || candidate.Version < 1
            || candidate.Version == long.MaxValue
            || !Enum.IsDefined(candidate.Phase)
            || !IsValidObserverEpoch(candidate.ObserverEpoch)
            || candidate.HighestAcceptedTransitionRevision < 0)
        {
            return false;
        }

        ProcessSourceAcknowledgementTuple? last =
            candidate.LastAcceptedAcknowledgement;
        if (last is null)
        {
            if (candidate.HighestAcceptedTransitionRevision != 0)
            {
                return false;
            }
        }
        else if (!Enum.IsDefined(last.Kind)
            || !IsValidObserverEpoch(last.ObserverEpoch)
            || last.TransitionRevision < 1
            || last.TransitionRevision
                != candidate.HighestAcceptedTransitionRevision)
        {
            return false;
        }

        return candidate.Phase switch
        {
            ProcessSourceContinuityPhase.FreshLost => last is null,
            ProcessSourceContinuityPhase.Recovering
                or ProcessSourceContinuityPhase.RecoveryCandidate =>
                last is
                {
                    Kind: ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                }
                && !string.Equals(
                    last.ObserverEpoch,
                    candidate.ObserverEpoch,
                    StringComparison.Ordinal),
            ProcessSourceContinuityPhase.Trusted =>
                last is
                {
                    Kind: ProcessObservationAcknowledgementKind
                        .AuthoritativeRecoveryPersisted,
                }
                && string.Equals(
                    last.ObserverEpoch,
                    candidate.ObserverEpoch,
                    StringComparison.Ordinal),
            _ => true,
        };
    }

    internal static ProcessSourceContinuityReduction ReduceAcknowledgement(
        ProcessSourceContinuityCheckpoint current,
        ProcessObservationAcknowledgement acknowledgement,
        string? rotatedEpoch)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (!IsValidCheckpoint(current)
            || !Enum.IsDefined(acknowledgement.Kind)
            || !IsValidObserverEpoch(acknowledgement.ObserverEpoch)
            || acknowledgement.EnvelopeRevision < 1)
        {
            return Ignored(current);
        }

        ProcessSourceAcknowledgementTuple tuple = new(
            acknowledgement.Kind,
            acknowledgement.ObserverEpoch,
            acknowledgement.EnvelopeRevision);
        if (current.HighestAcceptedTransitionRevision
                == acknowledgement.EnvelopeRevision
            && current.LastAcceptedAcknowledgement == tuple)
        {
            return new(ProcessSourceContinuityReductionKind.Idempotent, current);
        }

        if (acknowledgement.EnvelopeRevision
            <= current.HighestAcceptedTransitionRevision)
        {
            return Ignored(current);
        }

        bool acceptsSever = acknowledgement.Kind
                == ProcessObservationAcknowledgementKind.TrustSeverPersisted
            && (current.Phase == ProcessSourceContinuityPhase.FreshLost
                || current.Phase == ProcessSourceContinuityPhase.Lost
                && string.Equals(
                    acknowledgement.ObserverEpoch,
                    current.ObserverEpoch,
                    StringComparison.Ordinal));
        if (acceptsSever)
        {
            if (!IsValidObserverEpoch(rotatedEpoch)
                || string.Equals(
                    rotatedEpoch,
                    current.ObserverEpoch,
                    StringComparison.Ordinal)
                || string.Equals(
                    rotatedEpoch,
                    acknowledgement.ObserverEpoch,
                    StringComparison.Ordinal)
                || string.Equals(
                    rotatedEpoch,
                    current.LastAcceptedAcknowledgement?.ObserverEpoch,
                    StringComparison.Ordinal))
            {
                return Ignored(current);
            }

            return Applied(
                current,
                ProcessSourceContinuityPhase.Recovering,
                rotatedEpoch!,
                tuple);
        }

        bool acceptsRecovery = current.Phase
                == ProcessSourceContinuityPhase.RecoveryCandidate
            && acknowledgement.Kind
                == ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted
            && string.Equals(
                acknowledgement.ObserverEpoch,
                current.ObserverEpoch,
                StringComparison.Ordinal);
        return acceptsRecovery
            ? Applied(
                current,
                ProcessSourceContinuityPhase.Trusted,
                current.ObserverEpoch,
                tuple)
            : Ignored(current);
    }

    internal static ProcessSourceContinuityReduction ReduceLoss(
        ProcessSourceContinuityCheckpoint current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!IsValidCheckpoint(current))
        {
            return Ignored(current);
        }

        return current.Phase is ProcessSourceContinuityPhase.FreshLost
            or ProcessSourceContinuityPhase.Lost
                ? new(ProcessSourceContinuityReductionKind.Idempotent, current)
                : TransitionPhase(current, ProcessSourceContinuityPhase.Lost);
    }

    internal static ProcessSourceContinuityReduction ReduceRecoveryCandidate(
        ProcessSourceContinuityCheckpoint current,
        string observerEpoch)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!IsValidCheckpoint(current)
            || !IsValidObserverEpoch(observerEpoch)
            || !string.Equals(
                observerEpoch,
                current.ObserverEpoch,
                StringComparison.Ordinal))
        {
            return Ignored(current);
        }

        return current.Phase switch
        {
            ProcessSourceContinuityPhase.Recovering => TransitionPhase(
                current,
                ProcessSourceContinuityPhase.RecoveryCandidate),
            ProcessSourceContinuityPhase.RecoveryCandidate =>
                new(ProcessSourceContinuityReductionKind.Idempotent, current),
            _ => Ignored(current),
        };
    }

    internal static ProcessSourceContinuityReduction ReduceDormant(
        ProcessSourceContinuityCheckpoint current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!IsValidCheckpoint(current))
        {
            return Ignored(current);
        }

        return current.Phase is ProcessSourceContinuityPhase.FreshLost
            or ProcessSourceContinuityPhase.Lost
            or ProcessSourceContinuityPhase.Dormant
                ? new(ProcessSourceContinuityReductionKind.Idempotent, current)
                : TransitionPhase(current, ProcessSourceContinuityPhase.Dormant);
    }

    private static ProcessSourceContinuityReduction Applied(
        ProcessSourceContinuityCheckpoint current,
        ProcessSourceContinuityPhase phase,
        string epoch,
        ProcessSourceAcknowledgementTuple tuple) =>
        new(
            ProcessSourceContinuityReductionKind.Applied,
            current with
            {
                Version = checked(current.Version + 1),
                Phase = phase,
                ObserverEpoch = epoch,
                HighestAcceptedTransitionRevision = tuple.TransitionRevision,
                LastAcceptedAcknowledgement = tuple,
            });

    private static ProcessSourceContinuityReduction Ignored(
        ProcessSourceContinuityCheckpoint current) =>
        new(ProcessSourceContinuityReductionKind.Ignored, current);

    private static ProcessSourceContinuityReduction TransitionPhase(
        ProcessSourceContinuityCheckpoint current,
        ProcessSourceContinuityPhase phase) =>
        new(
            ProcessSourceContinuityReductionKind.Applied,
            current with
            {
                Version = checked(current.Version + 1),
                Phase = phase,
            });

    internal static bool IsValidObserverEpoch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumEpochLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }
}

internal enum ProcessSourceContinuityStoreLoadStatus
{
    Found,
    Missing,
    Unavailable,
    Corrupt,
}

internal sealed record ProcessSourceContinuityStoreLoadResult(
    ProcessSourceContinuityStoreLoadStatus Status,
    ProcessSourceContinuityCheckpoint? Checkpoint);

internal enum ProcessSourceContinuityStoreSaveStatus
{
    Saved,
    Conflict,
    Unavailable,
    Corrupt,
}

internal sealed record ProcessSourceContinuityStoreSaveResult(
    ProcessSourceContinuityStoreSaveStatus Status,
    ProcessSourceContinuityCheckpoint? Checkpoint);

internal interface IProcessSourceContinuityStore
{
    ValueTask<ProcessSourceContinuityStoreLoadResult> LoadAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ProcessSourceContinuityStoreSaveResult> CompareExchangeAsync(
        long? expectedVersion,
        ProcessSourceContinuityCheckpoint replacement,
        CancellationToken cancellationToken = default);
}

internal interface IProcessObserverEpochFactory
{
    /// <summary>
    /// Returns a globally unique observer epoch that this factory will never reuse.
    /// </summary>
    string CreateEpoch();
}

internal enum ProcessSourceContinuityAccessStatus
{
    Available,
    Unavailable,
    Corrupt,
}

internal sealed record ProcessSourceContinuityAccessResult(
    ProcessSourceContinuityAccessStatus Status,
    ProcessSourceContinuityCheckpoint? Checkpoint);

internal sealed record ProcessSourceAcknowledgementResult(
    ProcessSourceContinuityAccessStatus Status,
    ProcessSourceContinuityReductionKind Disposition,
    ProcessSourceContinuityCheckpoint? Checkpoint);

internal sealed class ProcessSourceContinuityPersistenceException(
    ProcessSourceContinuityAccessStatus status) : Exception(
        $"Process source continuity persistence is {status}.")
{
    internal ProcessSourceContinuityAccessStatus Status { get; } = status;
}

internal sealed class DurableProcessSourceContinuity
{
    private const int MaximumCasAttempts = 8;
    private const int MaximumEpochAttempts = 8;
    private readonly IProcessSourceContinuityStore _store;
    private readonly IProcessObserverEpochFactory _epochFactory;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private bool _restartBoundaryApplied;

    internal DurableProcessSourceContinuity(
        IProcessSourceContinuityStore store,
        IProcessObserverEpochFactory epochFactory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(epochFactory);
        _store = store;
        _epochFactory = epochFactory;
    }

    internal async ValueTask<ProcessSourceContinuityAccessResult> GetCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureRestartBoundaryLockedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    internal async ValueTask<ProcessSourceAcknowledgementResult>
        TryAcknowledgeAsync(
            ProcessObservationAcknowledgement acknowledgement,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProcessSourceContinuityAccessResult boundary =
                await EnsureRestartBoundaryLockedAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (boundary.Status != ProcessSourceContinuityAccessStatus.Available
                || boundary.Checkpoint is null)
            {
                return new(
                    boundary.Status,
                    ProcessSourceContinuityReductionKind.Ignored,
                    null);
            }

            ProcessSourceContinuityCheckpoint current = boundary.Checkpoint;
            for (int attempt = 0; attempt < MaximumCasAttempts; attempt++)
            {
                string? rotatedEpoch = null;
                if (NeedsEpochRotation(current, acknowledgement))
                {
                    if (!TryCreateDistinctEpoch(
                            current,
                            acknowledgement.ObserverEpoch,
                            out string candidateEpoch))
                    {
                        return new(
                            ProcessSourceContinuityAccessStatus.Unavailable,
                            ProcessSourceContinuityReductionKind.Ignored,
                            null);
                    }

                    rotatedEpoch = candidateEpoch;
                }

                ProcessSourceContinuityReduction reduction =
                    ProcessSourceContinuityReducer.ReduceAcknowledgement(
                        current,
                        acknowledgement,
                        rotatedEpoch);
                if (reduction.Kind != ProcessSourceContinuityReductionKind.Applied)
                {
                    return new(
                        ProcessSourceContinuityAccessStatus.Available,
                        reduction.Kind,
                        current);
                }

                ProcessSourceContinuityStoreSaveResult saved = await SaveAsync(
                        current.Version,
                        reduction.Checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false);
                SaveInterpretation interpretation = InterpretSave(
                    saved,
                    current.Version,
                    reduction.Checkpoint);
                if (interpretation.Status
                    != ProcessSourceContinuityAccessStatus.Available)
                {
                    return new(
                        interpretation.Status,
                        ProcessSourceContinuityReductionKind.Ignored,
                        null);
                }

                if (interpretation.Saved)
                {
                    return new(
                        ProcessSourceContinuityAccessStatus.Available,
                        ProcessSourceContinuityReductionKind.Applied,
                        reduction.Checkpoint);
                }

                current = interpretation.Checkpoint!;
            }

            return new(
                ProcessSourceContinuityAccessStatus.Unavailable,
                ProcessSourceContinuityReductionKind.Ignored,
                null);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    internal async ValueTask AcknowledgeAsync(
        ProcessObservationAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ProcessSourceAcknowledgementResult result = await TryAcknowledgeAsync(
                acknowledgement,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Status != ProcessSourceContinuityAccessStatus.Available)
        {
            throw new ProcessSourceContinuityPersistenceException(result.Status);
        }
    }

    internal ValueTask<ProcessSourceContinuityAccessResult> MarkLossAsync(
        CancellationToken cancellationToken = default) =>
        ApplyReductionAsync(
            ProcessSourceContinuityReducer.ReduceLoss,
            cancellationToken);

    internal ValueTask<ProcessSourceContinuityAccessResult>
        PublishRecoveryCandidateAsync(
            string observerEpoch,
            CancellationToken cancellationToken = default) =>
        ApplyReductionAsync(
            current => ProcessSourceContinuityReducer.ReduceRecoveryCandidate(
                current,
                observerEpoch),
            cancellationToken);

    internal ValueTask<ProcessSourceContinuityAccessResult> EnterDormantAsync(
        CancellationToken cancellationToken = default) =>
        ApplyReductionAsync(
            ProcessSourceContinuityReducer.ReduceDormant,
            cancellationToken);

    private async ValueTask<ProcessSourceContinuityAccessResult> ApplyReductionAsync(
        Func<ProcessSourceContinuityCheckpoint, ProcessSourceContinuityReduction> reduce,
        CancellationToken cancellationToken)
    {
        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProcessSourceContinuityAccessResult boundary =
                await EnsureRestartBoundaryLockedAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (boundary.Status != ProcessSourceContinuityAccessStatus.Available
                || boundary.Checkpoint is null)
            {
                return boundary;
            }

            ProcessSourceContinuityCheckpoint current = boundary.Checkpoint;
            for (int attempt = 0; attempt < MaximumCasAttempts; attempt++)
            {
                ProcessSourceContinuityReduction reduction = reduce(current);
                if (reduction.Kind != ProcessSourceContinuityReductionKind.Applied)
                {
                    return new(
                        ProcessSourceContinuityAccessStatus.Available,
                        current);
                }

                ProcessSourceContinuityStoreSaveResult saved = await SaveAsync(
                        current.Version,
                        reduction.Checkpoint,
                        cancellationToken)
                    .ConfigureAwait(false);
                SaveInterpretation interpretation = InterpretSave(
                    saved,
                    current.Version,
                    reduction.Checkpoint);
                if (interpretation.Status
                    != ProcessSourceContinuityAccessStatus.Available)
                {
                    return new(interpretation.Status, null);
                }

                if (interpretation.Saved)
                {
                    return new(
                        ProcessSourceContinuityAccessStatus.Available,
                        reduction.Checkpoint);
                }

                current = interpretation.Checkpoint!;
            }

            return new(ProcessSourceContinuityAccessStatus.Unavailable, null);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private async ValueTask<ProcessSourceContinuityAccessResult>
        EnsureRestartBoundaryLockedAsync(CancellationToken cancellationToken)
    {
        ProcessSourceContinuityCheckpoint? current = null;
        bool hasConflictWinner = false;
        for (int attempt = 0; attempt < MaximumCasAttempts; attempt++)
        {
            if (!hasConflictWinner)
            {
                ProcessSourceContinuityStoreLoadResult loaded = await LoadAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
                switch (loaded.Status)
                {
                    case ProcessSourceContinuityStoreLoadStatus.Unavailable:
                        return new(ProcessSourceContinuityAccessStatus.Unavailable, null);
                    case ProcessSourceContinuityStoreLoadStatus.Corrupt:
                        return new(ProcessSourceContinuityAccessStatus.Corrupt, null);
                    case ProcessSourceContinuityStoreLoadStatus.Missing:
                        if (loaded.Checkpoint is not null)
                        {
                            return new(ProcessSourceContinuityAccessStatus.Corrupt, null);
                        }

                        if (!TryCreateInitialCheckpoint(out ProcessSourceContinuityCheckpoint? fresh))
                        {
                            return new(ProcessSourceContinuityAccessStatus.Unavailable, null);
                        }

                        ProcessSourceContinuityStoreSaveResult initialized = await SaveAsync(
                                null,
                                fresh,
                                cancellationToken)
                            .ConfigureAwait(false);
                        SaveInterpretation initialization = InterpretSave(
                            initialized,
                            null,
                            fresh);
                        if (initialization.Status
                            != ProcessSourceContinuityAccessStatus.Available)
                        {
                            return new(initialization.Status, null);
                        }

                        if (initialization.Saved)
                        {
                            _restartBoundaryApplied = true;
                            return new(
                                ProcessSourceContinuityAccessStatus.Available,
                                fresh);
                        }

                        current = initialization.Checkpoint;
                        hasConflictWinner = true;
                        break;
                    case ProcessSourceContinuityStoreLoadStatus.Found:
                        if (!ProcessSourceContinuityReducer.IsValidCheckpoint(
                                loaded.Checkpoint))
                        {
                            return new(ProcessSourceContinuityAccessStatus.Corrupt, null);
                        }

                        current = loaded.Checkpoint;
                        hasConflictWinner = true;
                        break;
                    default:
                        return new(ProcessSourceContinuityAccessStatus.Corrupt, null);
                }
            }

            if (current is null
                || !ProcessSourceContinuityReducer.IsValidCheckpoint(current))
            {
                return new(ProcessSourceContinuityAccessStatus.Corrupt, null);
            }

            if (_restartBoundaryApplied)
            {
                return new(ProcessSourceContinuityAccessStatus.Available, current);
            }

            ProcessSourceContinuityReduction restart =
                ProcessSourceContinuityReducer.ReduceLoss(current);
            if (restart.Kind != ProcessSourceContinuityReductionKind.Applied)
            {
                _restartBoundaryApplied = true;
                return new(ProcessSourceContinuityAccessStatus.Available, current);
            }

            ProcessSourceContinuityStoreSaveResult saved = await SaveAsync(
                    current.Version,
                    restart.Checkpoint,
                    cancellationToken)
                .ConfigureAwait(false);
            SaveInterpretation interpretation = InterpretSave(
                saved,
                current.Version,
                restart.Checkpoint);
            if (interpretation.Status
                != ProcessSourceContinuityAccessStatus.Available)
            {
                return new(interpretation.Status, null);
            }

            if (interpretation.Saved)
            {
                _restartBoundaryApplied = true;
                return new(
                    ProcessSourceContinuityAccessStatus.Available,
                    restart.Checkpoint);
            }

            current = interpretation.Checkpoint;
            hasConflictWinner = true;
        }

        return new(ProcessSourceContinuityAccessStatus.Unavailable, null);
    }

    private async ValueTask<ProcessSourceContinuityStoreLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessSourceContinuityStoreLoadResult? loaded = await _store.LoadAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            return loaded ?? new(
                ProcessSourceContinuityStoreLoadStatus.Corrupt,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new(ProcessSourceContinuityStoreLoadStatus.Unavailable, null);
        }
    }

    private async ValueTask<ProcessSourceContinuityStoreSaveResult> SaveAsync(
        long? expectedVersion,
        ProcessSourceContinuityCheckpoint replacement,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessSourceContinuityStoreSaveResult? saved =
                await _store.CompareExchangeAsync(
                        expectedVersion,
                        replacement,
                        cancellationToken)
                    .ConfigureAwait(false);
            return saved ?? new(ProcessSourceContinuityStoreSaveStatus.Corrupt, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new(ProcessSourceContinuityStoreSaveStatus.Unavailable, null);
        }
    }

    private static SaveInterpretation InterpretSave(
        ProcessSourceContinuityStoreSaveResult saved,
        long? expectedVersion,
        ProcessSourceContinuityCheckpoint replacement)
    {
        switch (saved.Status)
        {
            case ProcessSourceContinuityStoreSaveStatus.Saved:
                return saved.Checkpoint == replacement
                    && ProcessSourceContinuityReducer.IsValidCheckpoint(saved.Checkpoint)
                    ? new(
                        ProcessSourceContinuityAccessStatus.Available,
                        true,
                        saved.Checkpoint)
                    : new(ProcessSourceContinuityAccessStatus.Corrupt, false, null);
            case ProcessSourceContinuityStoreSaveStatus.Conflict:
                bool versionAdvanced = expectedVersion is null
                    ? saved.Checkpoint is { Version: >= 1 }
                    : saved.Checkpoint?.Version > expectedVersion.Value;
                return versionAdvanced
                    && ProcessSourceContinuityReducer.IsValidCheckpoint(saved.Checkpoint)
                    ? new(
                        ProcessSourceContinuityAccessStatus.Available,
                        false,
                        saved.Checkpoint)
                    : new(ProcessSourceContinuityAccessStatus.Corrupt, false, null);
            case ProcessSourceContinuityStoreSaveStatus.Unavailable:
                return new(ProcessSourceContinuityAccessStatus.Unavailable, false, null);
            case ProcessSourceContinuityStoreSaveStatus.Corrupt:
            default:
                return new(ProcessSourceContinuityAccessStatus.Corrupt, false, null);
        }
    }

    private bool TryCreateInitialCheckpoint(
        out ProcessSourceContinuityCheckpoint checkpoint)
    {
        checkpoint = null!;
        if (!TryCreateDistinctEpoch(null, null, out string epoch))
        {
            return false;
        }

        checkpoint = new(
            1,
            ProcessSourceContinuityPhase.FreshLost,
            epoch,
            0,
            null);
        return true;
    }

    private bool TryCreateDistinctEpoch(
        ProcessSourceContinuityCheckpoint? current,
        string? acknowledgementEpoch,
        out string epoch)
    {
        epoch = null!;
        for (int attempt = 0; attempt < MaximumEpochAttempts; attempt++)
        {
            string candidate;
            try
            {
                candidate = _epochFactory.CreateEpoch();
            }
            catch (Exception)
            {
                return false;
            }

            if (ProcessSourceContinuityReducer.IsValidObserverEpoch(candidate)
                && !string.Equals(
                    candidate,
                    current?.ObserverEpoch,
                    StringComparison.Ordinal)
                && !string.Equals(
                    candidate,
                    acknowledgementEpoch,
                    StringComparison.Ordinal)
                && !string.Equals(
                    candidate,
                    current?.LastAcceptedAcknowledgement?.ObserverEpoch,
                    StringComparison.Ordinal))
            {
                epoch = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool NeedsEpochRotation(
        ProcessSourceContinuityCheckpoint current,
        ProcessObservationAcknowledgement acknowledgement) =>
        acknowledgement.Kind
            == ProcessObservationAcknowledgementKind.TrustSeverPersisted
        && ProcessSourceContinuityReducer.IsValidObserverEpoch(
            acknowledgement.ObserverEpoch)
        && acknowledgement.EnvelopeRevision
            > current.HighestAcceptedTransitionRevision
        && (current.Phase == ProcessSourceContinuityPhase.FreshLost
            || current.Phase == ProcessSourceContinuityPhase.Lost
            && string.Equals(
                current.ObserverEpoch,
                acknowledgement.ObserverEpoch,
                StringComparison.Ordinal));

    private sealed record SaveInterpretation(
        ProcessSourceContinuityAccessStatus Status,
        bool Saved,
        ProcessSourceContinuityCheckpoint? Checkpoint);
}
