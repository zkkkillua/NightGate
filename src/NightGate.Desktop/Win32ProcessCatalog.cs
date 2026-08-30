using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;

namespace NightGate.Desktop;

internal readonly record struct ProcessCatalogClockInstant(
    DateTimeOffset Utc,
    TimeSpan Monotonic);

internal interface IProcessCatalogClock
{
    ProcessCatalogClockInstant Capture();

    ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default);
}

internal sealed class Win32ProcessCatalogClock : IProcessCatalogClock
{
    internal static Win32ProcessCatalogClock Instance { get; } = new();

    private Win32ProcessCatalogClock()
    {
    }

    public ProcessCatalogClockInstant Capture() => new(
        DateTimeOffset.UtcNow,
        Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()));

    public ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default) =>
        new(Task.Delay(delay, cancellationToken));
}

internal sealed record ProcessCatalogIdentityReadResult(
    Win32ProcessIdentityReadStatus Status,
    ObservedProcessIdentity? Identity)
{
    internal static ProcessCatalogIdentityReadResult Success(
        ObservedProcessIdentity identity) => new(
        Win32ProcessIdentityReadStatus.Success,
        identity);

    internal static ProcessCatalogIdentityReadResult Failure(
        Win32ProcessIdentityReadStatus status) => new(status, null);
}

internal interface IProcessCatalogIdentityReader
{
    ProcessCatalogIdentityReadResult Read(int pid);
}

internal sealed class Win32ProcessCatalogIdentityReader(
    Win32ProcessIdentityReader reader) : IProcessCatalogIdentityReader
{
    private readonly Win32ProcessIdentityReader _reader =
        reader ?? throw new ArgumentNullException(nameof(reader));

    public ProcessCatalogIdentityReadResult Read(int pid)
    {
        using Win32ProcessIdentityReadResult result = _reader.OpenAndRead(
            pid,
            Win32ProcessAccess.QueryLimitedInformation
                | Win32ProcessAccess.Synchronize);
        return result.Status == Win32ProcessIdentityReadStatus.Success
            && result.Identity is not null
                ? ProcessCatalogIdentityReadResult.Success(result.Identity)
                : ProcessCatalogIdentityReadResult.Failure(result.Status);
    }
}

internal sealed class Win32ProcessCatalog : IProcessGateObservationSource
{
    private const int MaximumSnapshotRows = 131_072;
    private static readonly TimeSpan MinimumCatalogCadence =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumContinuityGap =
        TimeSpan.FromSeconds(2);

    private readonly IWin32ProcessCatalogNative _native;
    private readonly IProcessCatalogIdentityReader _identities;
    private readonly DurableProcessSourceContinuity _continuity;
    private readonly IProcessCatalogClock _clock;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly object _flightSync = new();
    private ProcessCatalogPolicyBinding? _binding;
    private ImmutableHashSet<ProcessInstanceKey> _knownKeys =
        ImmutableHashSet<ProcessInstanceKey>.Empty;
    private TimeSpan? _lastCatalogStart;
    private TimeSpan? _lastAuthoritativeStart;
    private TimeSpan? _lastPublishedEnd;
    private bool _forceAuthoritative = true;
    private bool _readInFlight;
    private long _breakGeneration;
    private long _durablyAppliedBreakGeneration;
    private long _activeHandledBreakGeneration;

    internal Win32ProcessCatalog(
        IWin32ProcessCatalogNative native,
        IProcessCatalogIdentityReader identities,
        DurableProcessSourceContinuity continuity,
        IProcessCatalogClock clock)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _continuity = continuity ?? throw new ArgumentNullException(nameof(continuity));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    internal Win32ProcessCatalog(DurableProcessSourceContinuity continuity)
        : this(
            Win32ProcessCatalogNative.Instance,
            new Win32ProcessCatalogIdentityReader(new Win32ProcessIdentityReader()),
            continuity,
            Win32ProcessCatalogClock.Instance)
    {
    }

    public ValueTask<ProcessObservationBatchEvidence> ReadBatchAsync(
        ProcessCatalogReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.PolicyBinding);
        return ExecuteReadAsync(
            request.PolicyBinding.MonitoringActive,
            request.RequestedKind,
            token => ReadBatchLockedAsync(request, token),
            cancellationToken);
    }

    public ValueTask<ProcessObservationBatchEvidence> ReadExactAsync(
        ProcessExactTarget target,
        ProcessCatalogPolicyBinding policyBinding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(policyBinding);
        return ExecuteReadAsync(
            policyBinding.MonitoringActive,
            ProcessObservationBatchKind.StartDelta,
            token => ReadExactLockedAsync(target, policyBinding, token),
            cancellationToken);
    }

    public async ValueTask AcknowledgeAsync(
        ProcessObservationAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long pending = ReadBreakGeneration();
            if (pending > ReadAppliedBreakGeneration())
            {
                ProcessSourceContinuityAccessResult loss =
                    await EnsureBreakDurableLockedAsync(pending).ConfigureAwait(false);
                if (!IsDurableLoss(loss))
                {
                    throw new ProcessSourceContinuityPersistenceException(
                        loss.Status);
                }
            }

            await _continuity.AcknowledgeAsync(acknowledgement, cancellationToken)
                .ConfigureAwait(false);
            if (acknowledgement.Kind
                == ProcessObservationAcknowledgementKind.TrustSeverPersisted)
            {
                _forceAuthoritative = true;
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    internal void NotifyDiscontinuity() => RegisterBreak();

    private async ValueTask<ProcessObservationBatchEvidence> ExecuteReadAsync(
        bool activeHint,
        ProcessObservationBatchKind requestedKind,
        Func<CancellationToken, ValueTask<ProcessObservationBatchEvidence>> read,
        CancellationToken cancellationToken)
    {
        if (!TryBeginRead(out _))
        {
            return await OverlapAsync(requestedKind).ConfigureAwait(false);
        }

        bool gateHeld = false;
        ProcessObservationBatchEvidence? evidence = null;
        OperationCanceledException? cancellation = null;
        try
        {
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;
            evidence = await read(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
            if (activeHint)
            {
                RegisterBreak();
            }
        }
        catch (Exception)
        {
            if (activeHint)
            {
                RegisterBreak();
            }

            evidence = null;
        }

        try
        {
            if (gateHeld
                && activeHint
                && ReadBreakGeneration() > ReadActiveHandledBreakGeneration())
            {
                ProcessSourceContinuityAccessResult loss =
                    await EnsurePendingBreakLockedAsync()
                        .ConfigureAwait(false);
                _forceAuthoritative = true;
                evidence = Unavailable(
                    requestedKind,
                    loss.Checkpoint?.ObserverEpoch ?? evidence?.ObserverEpoch,
                    evidence?.ClockSample,
                    "catalog-continuity-break");
            }

            if (evidence is null)
            {
                ProcessSourceContinuityAccessResult loss = activeHint && gateHeld
                    ? await EnsurePendingBreakLockedAsync()
                        .ConfigureAwait(false)
                    : new(ProcessSourceContinuityAccessStatus.Unavailable, null);
                evidence = Unavailable(
                    requestedKind,
                    loss.Checkpoint?.ObserverEpoch,
                    null,
                    cancellation is null
                        ? "catalog-unavailable"
                        : "catalog-cancelled");
            }
        }
        finally
        {
            if (gateHeld)
            {
                _stateGate.Release();
            }
        }

        ReadEndState ending = EndRead();
        if (activeHint && ending.Generation > ending.HandledGeneration)
        {
            ProcessSourceContinuityAccessResult loss =
                await EnsureBreakDurableLockedAsync(ending.Generation)
                    .ConfigureAwait(false);
            evidence = Unavailable(
                requestedKind,
                loss.Checkpoint?.ObserverEpoch ?? evidence?.ObserverEpoch,
                evidence?.ClockSample,
                "catalog-continuity-break");
        }

        if (cancellation is not null)
        {
            throw cancellation;
        }

        return evidence;
    }

    private async ValueTask<ProcessObservationBatchEvidence> ReadBatchLockedAsync(
        ProcessCatalogReadRequest request,
        CancellationToken cancellationToken)
    {
        ProcessCatalogPolicyBinding binding = request.PolicyBinding;
        if (!Enum.IsDefined(request.RequestedKind) || !IsValidBinding(binding))
        {
            RegisterBreak();
            return await FailedBreakEvidenceLockedAsync(
                    request.RequestedKind,
                    null,
                    "catalog-binding-invalid")
                .ConfigureAwait(false);
        }

        BindingAcceptance bindingAcceptance = AcceptBindingLocked(binding);
        if (!bindingAcceptance.Accepted)
        {
            return await FailedBreakEvidenceLockedAsync(
                    request.RequestedKind,
                    null,
                    "catalog-binding-conflict")
                .ConfigureAwait(false);
        }

        if (!binding.MonitoringActive)
        {
            ProcessSourceContinuityAccessResult dormant =
                await _continuity.EnterDormantAsync(cancellationToken)
                    .ConfigureAwait(false);
            ResetCadenceLocked();
            return EvidenceForInactive(dormant);
        }

        ProcessSourceContinuityAccessResult pendingLoss =
            await EnsurePendingBreakLockedAsync().ConfigureAwait(false);
        if (!IsContinuityAvailable(pendingLoss))
        {
            return Unavailable(
                request.RequestedKind,
                pendingLoss.Checkpoint?.ObserverEpoch,
                null,
                "continuity-unavailable");
        }

        ProcessCatalogClockInstant scheduled =
            await WaitForCadenceLockedAsync(cancellationToken).ConfigureAwait(false);
        bool cadenceFault = _lastCatalogStart is { } previousStart
            && (scheduled.Monotonic < previousStart
                || scheduled.Monotonic - previousStart > MaximumContinuityGap);
        bool orderingFault = _lastPublishedEnd is { } previousEnd
            && scheduled.Monotonic < previousEnd;
        if (cadenceFault || orderingFault)
        {
            RegisterBreak();
            pendingLoss = await EnsurePendingBreakLockedAsync().ConfigureAwait(false);
            _forceAuthoritative = true;
            if (!IsDurableLoss(pendingLoss))
            {
                return Unavailable(
                    request.RequestedKind,
                    pendingLoss.Checkpoint?.ObserverEpoch,
                    null,
                    "continuity-persistence-unavailable");
            }
        }

        ProcessSourceContinuityAccessResult continuity =
            await _continuity.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!IsContinuityAvailable(continuity))
        {
            return Unavailable(
                request.RequestedKind,
                continuity.Checkpoint?.ObserverEpoch,
                null,
                "continuity-unavailable");
        }

        ProcessSourceContinuityCheckpoint checkpoint = continuity.Checkpoint!;
        bool authoritativeAttempt = IsAuthoritativeDueLocked(
            request.RequestedKind,
            scheduled.Monotonic);
        bool proveParents = authoritativeAttempt
            && checkpoint.Phase is ProcessSourceContinuityPhase.Recovering
                or ProcessSourceContinuityPhase.Trusted;

        ProcessCatalogClockInstant started = CaptureClock();
        CatalogSample sample = Enumerate(
            binding,
            started.Utc,
            proveParents,
            cancellationToken);
        ProcessCatalogClockInstant completed = CaptureClock();
        TimeSpan? priorCatalogStart = _lastCatalogStart;
        _lastCatalogStart = started.Monotonic;
        ProcessObservationClockSample clockSample = ClockSample(started, completed);
        bool sampleClockFault = completed.Utc < started.Utc
            || completed.Monotonic < started.Monotonic
            || completed.Monotonic - started.Monotonic > MaximumContinuityGap
            || scheduled.Monotonic > started.Monotonic
            || started.Monotonic - scheduled.Monotonic > MaximumContinuityGap
            || priorCatalogStart is { } priorActualStart
                && (started.Monotonic < priorActualStart
                    || started.Monotonic - priorActualStart
                        > MaximumContinuityGap)
            || _lastPublishedEnd is { } lastEnd
                && started.Monotonic < lastEnd;
        if (completed.Monotonic >= started.Monotonic)
        {
            _lastPublishedEnd = completed.Monotonic;
        }

        if (!sample.Completed || sampleClockFault)
        {
            RegisterBreak();
            ProcessSourceContinuityAccessResult loss =
                await EnsurePendingBreakLockedAsync().ConfigureAwait(false);
            _forceAuthoritative = true;
            return new(
                ProcessGateSourceStatus.Unavailable,
                ProcessObservationBatchKind.StartDelta,
                sample.Observations,
                loss.Checkpoint?.ObserverEpoch ?? checkpoint.ObserverEpoch,
                false,
                false,
                false,
                true,
                sample.DegradationCode ?? (sampleClockFault
                    ? "catalog-clock-discontinuity"
                    : "catalog-incomplete"),
                clockSample);
        }

        bool establishesRecovery = authoritativeAttempt
            && checkpoint.Phase == ProcessSourceContinuityPhase.Recovering;
        bool trustedBeforeSample = checkpoint.Phase
            == ProcessSourceContinuityPhase.Trusted;
        bool recoveryCandidateBeforeSample = checkpoint.Phase
            == ProcessSourceContinuityPhase.RecoveryCandidate;
        bool authoritative = authoritativeAttempt
            && (establishesRecovery || trustedBeforeSample);
        if (establishesRecovery)
        {
            ProcessSourceContinuityAccessResult candidate =
                await _continuity.PublishRecoveryCandidateAsync(
                        checkpoint.ObserverEpoch,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            if (!IsContinuityAvailable(candidate)
                || candidate.Checkpoint!.Phase
                    != ProcessSourceContinuityPhase.RecoveryCandidate)
            {
                RegisterBreak();
                ProcessSourceContinuityAccessResult loss =
                    await EnsurePendingBreakLockedAsync().ConfigureAwait(false);
                _forceAuthoritative = true;
                return Unavailable(
                    ProcessObservationBatchKind.StartDelta,
                    loss.Checkpoint?.ObserverEpoch ?? checkpoint.ObserverEpoch,
                    clockSample,
                    "recovery-persistence-unavailable");
            }

            checkpoint = candidate.Checkpoint;
        }

        IReadOnlyList<ProcessObservation> published = authoritative
            ? sample.Observations
            : sample.Observations.Where(observation =>
                    observation.Identity is not null
                    && !_knownKeys.Contains(observation.Identity.Key))
                .ToArray();
        _knownKeys = sample.Observations
            .Where(static observation => observation.Identity is not null)
            .Select(static observation => observation.Identity!.Key)
            .ToImmutableHashSet();
        if (authoritativeAttempt)
        {
            _lastAuthoritativeStart = started.Monotonic;
            _forceAuthoritative = false;
        }

        bool creationTrusted = trustedBeforeSample
            || recoveryCandidateBeforeSample
            || establishesRecovery;
        return new(
            ProcessGateSourceStatus.Available,
            authoritative
                ? ProcessObservationBatchKind.AuthoritativeSnapshot
                : ProcessObservationBatchKind.StartDelta,
            published,
            checkpoint.ObserverEpoch,
            true,
            authoritative,
            creationTrusted,
            !creationTrusted,
            null,
            clockSample);
    }

    private async ValueTask<ProcessObservationBatchEvidence> ReadExactLockedAsync(
        ProcessExactTarget target,
        ProcessCatalogPolicyBinding binding,
        CancellationToken cancellationToken)
    {
        if (!IsValidBinding(binding))
        {
            RegisterBreak();
            return await FailedBreakEvidenceLockedAsync(
                    ProcessObservationBatchKind.StartDelta,
                    null,
                    "catalog-binding-invalid")
                .ConfigureAwait(false);
        }

        BindingAcceptance accepted = AcceptBindingLocked(binding);
        if (!accepted.Accepted)
        {
            return await FailedBreakEvidenceLockedAsync(
                    ProcessObservationBatchKind.StartDelta,
                    null,
                    "catalog-binding-conflict")
                .ConfigureAwait(false);
        }

        if (!binding.MonitoringActive)
        {
            ProcessSourceContinuityAccessResult dormant =
                await _continuity.EnterDormantAsync(cancellationToken)
                    .ConfigureAwait(false);
            ResetCadenceLocked();
            return EvidenceForInactive(dormant);
        }

        ProcessSourceContinuityAccessResult pendingLoss =
            await EnsurePendingBreakLockedAsync().ConfigureAwait(false);
        if (!IsContinuityAvailable(pendingLoss))
        {
            return Unavailable(
                ProcessObservationBatchKind.StartDelta,
                pendingLoss.Checkpoint?.ObserverEpoch,
                null,
                "continuity-unavailable");
        }

        ProcessCatalogClockInstant scheduled = CaptureClock();
        bool cadenceGap = _lastCatalogStart is { } priorCatalog
            && (scheduled.Monotonic < priorCatalog
                || scheduled.Monotonic - priorCatalog > MaximumContinuityGap);
        bool orderingFault = _lastPublishedEnd is { } priorEnd
            && scheduled.Monotonic < priorEnd;
        if (cadenceGap || orderingFault)
        {
            RegisterBreak();
            pendingLoss = await EnsurePendingBreakLockedAsync().ConfigureAwait(false);
            _forceAuthoritative = true;
            if (!IsDurableLoss(pendingLoss))
            {
                return Unavailable(
                    ProcessObservationBatchKind.StartDelta,
                    pendingLoss.Checkpoint?.ObserverEpoch,
                    null,
                    "continuity-persistence-unavailable");
            }
        }

        ProcessSourceContinuityAccessResult continuity =
            await _continuity.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!IsContinuityAvailable(continuity))
        {
            return Unavailable(
                ProcessObservationBatchKind.StartDelta,
                continuity.Checkpoint?.ObserverEpoch,
                null,
                "continuity-unavailable");
        }

        ProcessCatalogClockInstant started = CaptureClock();
        cancellationToken.ThrowIfCancellationRequested();
        ProcessCatalogIdentityReadResult read;
        try
        {
            read = _identities.Read(target.InstanceKey.Pid);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            read = ProcessCatalogIdentityReadResult.Failure(
                Win32ProcessIdentityReadStatus.Ambiguous);
        }

        ProcessCatalogClockInstant completed = CaptureClock();
        ProcessObservationClockSample clockSample = ClockSample(started, completed);
        bool clockFault = completed.Utc < started.Utc
            || completed.Monotonic < started.Monotonic
            || completed.Monotonic - started.Monotonic > MaximumContinuityGap
            || scheduled.Monotonic > started.Monotonic
            || started.Monotonic - scheduled.Monotonic > MaximumContinuityGap
            || _lastCatalogStart is { } lastCatalogStart
                && (started.Monotonic < lastCatalogStart
                    || started.Monotonic - lastCatalogStart
                        > MaximumContinuityGap)
            || _lastPublishedEnd is { } lastEnd
                && started.Monotonic < lastEnd;
        if (completed.Monotonic >= started.Monotonic)
        {
            _lastPublishedEnd = completed.Monotonic;
        }

        bool normalExit = read.Status is Win32ProcessIdentityReadStatus.Exited
            or Win32ProcessIdentityReadStatus.NotFound;
        bool success = read.Status == Win32ProcessIdentityReadStatus.Success
            && read.Identity is not null;
        if ((!normalExit && !success) || clockFault)
        {
            RegisterBreak();
            ProcessSourceContinuityAccessResult loss =
                await EnsurePendingBreakLockedAsync().ConfigureAwait(false);
            _forceAuthoritative = true;
            return Unavailable(
                ProcessObservationBatchKind.StartDelta,
                loss.Checkpoint?.ObserverEpoch
                    ?? continuity.Checkpoint!.ObserverEpoch,
                clockSample,
                clockFault
                    ? "exact-clock-discontinuity"
                    : "exact-identity-unavailable");
        }

        bool trusted = continuity.Checkpoint!.Phase is
            ProcessSourceContinuityPhase.Trusted
            or ProcessSourceContinuityPhase.RecoveryCandidate;
        return new(
            ProcessGateSourceStatus.Available,
            ProcessObservationBatchKind.StartDelta,
            success
                ? [new(read.Identity!.Key.Pid, read.Identity, ParentLink.Unknown)]
                : [],
            continuity.Checkpoint.ObserverEpoch,
            true,
            false,
            trusted,
            !trusted,
            null,
            clockSample);
    }

    private CatalogSample Enumerate(
        ProcessCatalogPolicyBinding binding,
        DateTimeOffset snapshotStartedAtUtc,
        bool proveParents,
        CancellationToken cancellationToken)
    {
        HashSet<string> candidateBasenames = binding.CanonicalExecutablePaths
            .Select(static path => Path.GetFileName(path)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<int, Win32ProcessCatalogEntry> rows = [];
        using (SafeWin32ProcessSnapshotHandle? snapshot =
               _native.CreateProcessSnapshot(out int createError))
        {
            if (snapshot is null || snapshot.IsInvalid || snapshot.IsClosed)
            {
                return CatalogSample.Failed([], $"snapshot-create-{createError}");
            }

            Win32ProcessCatalogMoveResult move = _native.ReadFirst(snapshot);
            int rowCount = 0;
            while (move.Status == Win32ProcessCatalogMoveStatus.Entry)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++rowCount > MaximumSnapshotRows
                    || move.Value is not { } row
                    || row.ProcessId < 0
                    || row.ParentProcessId < 0
                    || string.IsNullOrWhiteSpace(row.ExecutableName)
                    || !string.Equals(
                        Path.GetFileName(row.ExecutableName),
                        row.ExecutableName,
                        StringComparison.Ordinal)
                    || row.ProcessId != 0 && !rows.TryAdd(row.ProcessId, row))
                {
                    return CatalogSample.Failed([], "snapshot-row-invalid");
                }

                move = _native.ReadNext(snapshot);
            }

            if (move.Status != Win32ProcessCatalogMoveStatus.Completed
                || move.Error != Win32Error.NoMoreFiles)
            {
                return CatalogSample.Failed(
                    [],
                    $"snapshot-enumeration-{move.Error}");
            }

            Dictionary<int, ObservedProcessIdentity> candidateIdentities = [];
            List<ObservedProcessIdentity> scopedIdentities = [];
            foreach (Win32ProcessCatalogEntry row in rows.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidateBasenames.Contains(row.ExecutableName))
                {
                    continue;
                }

                ProcessCatalogIdentityReadResult result;
                try
                {
                    result = _identities.Read(row.ProcessId);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return CatalogSample.Failed(
                        Observations(scopedIdentities),
                        "candidate-identity-unavailable");
                }

                if (result.Status != Win32ProcessIdentityReadStatus.Success
                    || result.Identity is null
                    || result.Identity.Key.Pid != row.ProcessId
                    || !candidateIdentities.TryAdd(row.ProcessId, result.Identity))
                {
                    return CatalogSample.Failed(
                        Observations(scopedIdentities),
                        "candidate-identity-unavailable");
                }

                if (binding.CanonicalExecutablePaths.Contains(
                        result.Identity.ExecutablePath,
                        StringComparer.OrdinalIgnoreCase))
                {
                    scopedIdentities.Add(result.Identity);
                }
            }

            if (!proveParents)
            {
                return CatalogSample.Success(Observations(scopedIdentities));
            }

            List<ProcessObservation> projected = [];
            foreach (ObservedProcessIdentity child in scopedIdentities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Win32ProcessCatalogEntry childRow = rows[child.Key.Pid];
                ParentResolution parent = ResolveParent(
                    child,
                    childRow.ParentProcessId,
                    snapshotStartedAtUtc,
                    rows,
                    candidateBasenames,
                    candidateIdentities);
                if (parent.RelevantFailure)
                {
                    return CatalogSample.Failed(
                        projected,
                        "candidate-parent-identity-unavailable");
                }

                projected.Add(new(child.Key.Pid, child, parent.Link));
            }

            return CatalogSample.Success(projected);
        }
    }

    private ParentResolution ResolveParent(
        ObservedProcessIdentity child,
        int parentPid,
        DateTimeOffset snapshotStartedAtUtc,
        IReadOnlyDictionary<int, Win32ProcessCatalogEntry> rows,
        IReadOnlySet<string> candidateBasenames,
        IReadOnlyDictionary<int, ObservedProcessIdentity> candidateIdentities)
    {
        if (child.CreationInstantUtc > snapshotStartedAtUtc)
        {
            return ParentResolution.Unknown;
        }

        if (parentPid == 0)
        {
            return new(ParentLink.None, false);
        }

        if (!rows.TryGetValue(parentPid, out Win32ProcessCatalogEntry parentRow))
        {
            return ParentResolution.Unknown;
        }

        bool parentIsCandidate = candidateBasenames.Contains(
            parentRow.ExecutableName);
        ProcessCatalogIdentityReadResult read;
        try
        {
            read = _identities.Read(parentPid);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(ParentLink.Unknown, parentIsCandidate);
        }

        if (read.Status != Win32ProcessIdentityReadStatus.Success
            || read.Identity is null
            || read.Identity.Key.Pid != parentPid)
        {
            return new(ParentLink.Unknown, parentIsCandidate);
        }

        ObservedProcessIdentity parent = read.Identity;
        if (parentIsCandidate
            && (!candidateIdentities.TryGetValue(parentPid, out ObservedProcessIdentity? first)
                || first != parent))
        {
            return new(ParentLink.Unknown, true);
        }

        return parent.CreationInstantUtc <= child.CreationInstantUtc
            ? new(ParentLink.Exact(parent.Key), false)
            : ParentResolution.Unknown;
    }

    private BindingAcceptance AcceptBindingLocked(
        ProcessCatalogPolicyBinding binding)
    {
        if (_binding is null)
        {
            _binding = binding;
            _knownKeys = ImmutableHashSet<ProcessInstanceKey>.Empty;
            if (binding.MonitoringActive)
            {
                RegisterBreak();
                _forceAuthoritative = true;
            }

            return new(true);
        }

        ProcessCatalogPolicyBindingRelation relation =
            ProcessCatalogPolicyBinding.Classify(_binding, binding);
        switch (relation)
        {
            case ProcessCatalogPolicyBindingRelation.ExactReplay:
            case ProcessCatalogPolicyBindingRelation.NewWitnessSameScope:
                _binding = binding;
                return new(true);
            case ProcessCatalogPolicyBindingRelation.NewWitnessChangedScope:
                _binding = binding;
                _knownKeys = ImmutableHashSet<ProcessInstanceKey>.Empty;
                ResetCadenceLocked();
                if (binding.MonitoringActive)
                {
                    RegisterBreak();
                    _forceAuthoritative = true;
                }

                return new(true);
            default:
                RegisterBreak();
                _forceAuthoritative = true;
                return new(false);
        }
    }

    private async ValueTask<ProcessCatalogClockInstant> WaitForCadenceLockedAsync(
        CancellationToken cancellationToken)
    {
        ProcessCatalogClockInstant current = CaptureClock();
        if (_lastCatalogStart is not { } prior)
        {
            return current;
        }

        for (int attempt = 0; attempt < 8; attempt++)
        {
            TimeSpan elapsed = current.Monotonic - prior;
            if (elapsed < TimeSpan.Zero || elapsed >= MinimumCatalogCadence)
            {
                return current;
            }

            await _clock.DelayAsync(
                    MinimumCatalogCadence - elapsed,
                    cancellationToken)
                .ConfigureAwait(false);
            current = CaptureClock();
        }

        throw new InvalidOperationException("Catalog cadence delay returned too early.");
    }

    private bool IsAuthoritativeDueLocked(
        ProcessObservationBatchKind requested,
        TimeSpan scheduledAt) => _forceAuthoritative
        || requested == ProcessObservationBatchKind.AuthoritativeSnapshot
        || _lastAuthoritativeStart is null
        || scheduledAt - _lastAuthoritativeStart >= MaximumContinuityGap;

    private async ValueTask<ProcessSourceContinuityAccessResult>
        EnsurePendingBreakLockedAsync()
    {
        long generation = ReadBreakGeneration();
        if (generation <= ReadAppliedBreakGeneration())
        {
            ProcessSourceContinuityAccessResult current =
                await _continuity.GetCurrentAsync(CancellationToken.None)
                .ConfigureAwait(false);
            MarkBreakHandledByActive(generation);
            return current;
        }

        ProcessSourceContinuityAccessResult result =
            await EnsureBreakDurableLockedAsync(generation).ConfigureAwait(false);
        if (IsDurableLoss(result))
        {
            MarkBreakHandledByActive(generation);
        }

        return result;
    }

    private async ValueTask<ProcessSourceContinuityAccessResult>
        EnsureBreakDurableLockedAsync(long generation)
    {
        ProcessSourceContinuityAccessResult result;
        try
        {
            result = await _continuity.MarkLossAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return new(ProcessSourceContinuityAccessStatus.Unavailable, null);
        }

        if (IsDurableLoss(result))
        {
            lock (_flightSync)
            {
                if (generation > _durablyAppliedBreakGeneration)
                {
                    _durablyAppliedBreakGeneration = generation;
                }
            }
        }

        return result;
    }

    private async ValueTask<ProcessObservationBatchEvidence>
        FailedBreakEvidenceLockedAsync(
            ProcessObservationBatchKind requestedKind,
            ProcessObservationClockSample? clock,
            string code)
    {
        ProcessSourceContinuityAccessResult loss =
            await EnsurePendingBreakLockedAsync().ConfigureAwait(false);
        return Unavailable(
            requestedKind,
            loss.Checkpoint?.ObserverEpoch,
            clock,
            code);
    }

    private async ValueTask<ProcessObservationBatchEvidence> OverlapAsync(
        ProcessObservationBatchKind requestedKind)
    {
        long generation = ReadBreakGeneration();
        ProcessSourceContinuityAccessResult loss =
            await EnsureBreakDurableLockedAsync(generation).ConfigureAwait(false);
        return Unavailable(
            requestedKind,
            loss.Checkpoint?.ObserverEpoch,
            null,
            "catalog-overlap");
    }

    private bool TryBeginRead(out long generation)
    {
        lock (_flightSync)
        {
            if (_readInFlight)
            {
                generation = ++_breakGeneration;
                return false;
            }

            _readInFlight = true;
            generation = _breakGeneration;
            _activeHandledBreakGeneration = generation;
            return true;
        }
    }

    private ReadEndState EndRead()
    {
        lock (_flightSync)
        {
            _readInFlight = false;
            return new(
                _breakGeneration,
                _activeHandledBreakGeneration);
        }
    }

    private long RegisterBreak()
    {
        lock (_flightSync)
        {
            return ++_breakGeneration;
        }
    }

    private long ReadBreakGeneration()
    {
        lock (_flightSync)
        {
            return _breakGeneration;
        }
    }

    private long ReadAppliedBreakGeneration()
    {
        lock (_flightSync)
        {
            return _durablyAppliedBreakGeneration;
        }
    }

    private long ReadActiveHandledBreakGeneration()
    {
        lock (_flightSync)
        {
            return _activeHandledBreakGeneration;
        }
    }

    private void MarkBreakHandledByActive(long generation)
    {
        lock (_flightSync)
        {
            if (_readInFlight && generation > _activeHandledBreakGeneration)
            {
                _activeHandledBreakGeneration = generation;
            }
        }
    }

    private ProcessCatalogClockInstant CaptureClock()
    {
        ProcessCatalogClockInstant instant = _clock.Capture();
        if (instant.Utc.Offset != TimeSpan.Zero || instant.Monotonic < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Catalog clock returned invalid evidence.");
        }

        return instant;
    }

    private static ProcessObservationClockSample ClockSample(
        ProcessCatalogClockInstant started,
        ProcessCatalogClockInstant completed) => new(
        started.Utc,
        started.Monotonic,
        completed.Utc,
        completed.Monotonic);

    private static IReadOnlyList<ProcessObservation> Observations(
        IEnumerable<ObservedProcessIdentity> identities) => identities
        .Select(static identity => new ProcessObservation(
            identity.Key.Pid,
            identity,
            ParentLink.Unknown))
        .ToArray();

    private static bool IsValidBinding(ProcessCatalogPolicyBinding binding) =>
        ProcessCatalogPolicyBinding.Classify(binding, binding)
            == ProcessCatalogPolicyBindingRelation.ExactReplay;

    private static bool IsContinuityAvailable(
        ProcessSourceContinuityAccessResult result) =>
        result.Status == ProcessSourceContinuityAccessStatus.Available
        && result.Checkpoint is not null;

    private static bool IsDurableLoss(
        ProcessSourceContinuityAccessResult result) =>
        IsContinuityAvailable(result)
        && result.Checkpoint!.Phase is ProcessSourceContinuityPhase.FreshLost
            or ProcessSourceContinuityPhase.Lost;

    private static ProcessObservationBatchEvidence EvidenceForInactive(
        ProcessSourceContinuityAccessResult continuity) => new(
        continuity.Status == ProcessSourceContinuityAccessStatus.Available
            ? ProcessGateSourceStatus.Available
            : ProcessGateSourceStatus.Unavailable,
        ProcessObservationBatchKind.StartDelta,
        [],
        continuity.Checkpoint?.ObserverEpoch,
        true,
        false,
        false,
        true,
        "monitoring-inactive",
        null);

    private static ProcessObservationBatchEvidence Unavailable(
        ProcessObservationBatchKind requestedKind,
        string? epoch,
        ProcessObservationClockSample? clock,
        string code) => new(
        ProcessGateSourceStatus.Unavailable,
        ProcessObservationBatchKind.StartDelta,
        [],
        epoch,
        false,
        false,
        false,
        true,
        code,
        clock);

    private void ResetCadenceLocked()
    {
        _lastCatalogStart = null;
        _lastAuthoritativeStart = null;
        _lastPublishedEnd = null;
        _forceAuthoritative = true;
    }

    private readonly record struct BindingAcceptance(bool Accepted);

    private readonly record struct ReadEndState(
        long Generation,
        long HandledGeneration);

    private readonly record struct ParentResolution(
        ParentLink Link,
        bool RelevantFailure)
    {
        internal static ParentResolution Unknown { get; } = new(
            ParentLink.Unknown,
            false);
    }

    private sealed record CatalogSample(
        bool Completed,
        IReadOnlyList<ProcessObservation> Observations,
        string? DegradationCode)
    {
        internal static CatalogSample Success(
            IReadOnlyList<ProcessObservation> observations) => new(
            true,
            observations,
            null);

        internal static CatalogSample Failed(
            IReadOnlyList<ProcessObservation> observations,
            string code) => new(false, observations, code);
    }
}
