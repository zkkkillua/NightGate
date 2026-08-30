using System.Collections.Immutable;

namespace NightGate.Desktop;

public sealed class ProcessGateCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumClockDrift = TimeSpan.FromSeconds(2);
    private const int MaximumCasAttempts = 3;
    private const int MaximumJournalEntries = 256;
    private const int MaximumPolicyBindings = 64;
    private readonly IProcessGateEnvelopeStore _store;
    private readonly IProcessGatePolicySource _policySource;
    private readonly IProcessGateObservationSource _observationSource;
    private readonly IExactProcessActionAdapter _actions;
    private readonly IProcessGateMonotonicDelay _delay;
    private readonly IProcessGateOutcomeSink? _outcomeSink;
    private readonly ICutoffPipelineBarrier _cutoffBarrier;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly HashSet<Task> _operations = [];
    private readonly HashSet<ProcessActionKey> _scheduledContinuations = [];
    private bool _isStopping;

    public ProcessGateCoordinator(
        IProcessGateEnvelopeStore store,
        IProcessGatePolicySource policySource,
        IProcessGateObservationSource observationSource,
        IExactProcessActionAdapter actions,
        IProcessGateMonotonicDelay delay,
        IProcessGateOutcomeSink? outcomeSink = null,
        ICutoffPipelineBarrier? cutoffBarrier = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policySource);
        ArgumentNullException.ThrowIfNull(observationSource);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(delay);
        _store = store;
        _policySource = policySource;
        _observationSource = observationSource;
        _actions = actions;
        _delay = delay;
        _outcomeSink = outcomeSink;
        _cutoffBarrier = cutoffBarrier ?? new CutoffPipelineBarrier();
    }

    public Task<ProcessGateRunResult> EvaluateAsync(
        ProcessGateRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_lifecycle)
        {
            if (_isStopping)
            {
                return Task.FromResult(Cancelled("coordinator-stopping"));
            }

            Task<ProcessGateRunResult> operation = RunLifetimeAsync(
                request,
                cancellationToken);
            _operations.Add(operation);
            _ = RemoveWhenCompletedAsync(operation);
            return operation;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] operations;
        bool cancelStopping;
        lock (_lifecycle)
        {
            cancelStopping = !_isStopping;
            _isStopping = true;
            operations = _operations.ToArray();
        }

        if (cancelStopping)
        {
            try
            {
                _stopping.Cancel();
            }
            catch (Exception)
            {
                // Cancellation is already visible. A dependency callback cannot prevent
                // disposal from awaiting operations that started before the stop snapshot.
            }
        }

        try
        {
            await Task.WhenAll(operations).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Every operation translates failures to fail-open outcomes. This catch also
            // protects disposal if a dependency violates its asynchronous contract.
        }

        _singleFlight.Dispose();
        _stopping.Dispose();
    }

    private async Task<ProcessGateRunResult> RunLifetimeAsync(
        ProcessGateRunRequest request,
        CancellationToken callerCancellation)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation,
            _stopping.Token);
        try
        {
            return await RunAsync(
                    request,
                    callerCancellation,
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled("cancelled");
        }
        catch (Exception)
        {
            return Cancelled("coordinator-unavailable");
        }
    }

    private async Task RemoveWhenCompletedAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The public operation is responsible for converting dependency failures.
        }
        finally
        {
            lock (_lifecycle)
            {
                _operations.Remove(operation);
            }
        }
    }

    private async Task<ProcessGateRunResult> RunAsync(
        ProcessGateRunRequest request,
        CancellationToken continuationCancellation,
        CancellationToken cancellationToken)
    {
        List<ProcessGateOrchestrationOutcome> outcomes = [];
        InitialCommit? initial = await EvaluateAndCommitInitialAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (initial is null)
        {
            outcomes.Add(new(ProcessGateOutcomeKind.Cancelled, null, "initial-fail-open"));
            return await FinishAsync(outcomes, cancellationToken).ConfigureAwait(false);
        }

        outcomes.Add(new(
            initial.IsDegraded ? ProcessGateOutcomeKind.Degraded : ProcessGateOutcomeKind.Healthy,
            null,
            initial.HealthCode.ToString()));
        List<ProcessActionJournalEntry> continuations = [];
        foreach (ProcessActionJournalEntry claimed in initial.NewCloseClaims)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessCloseOutcome closeOutcome;
            try
            {
                (bool invoked, ProcessCloseOutcome value) = await InvokeEffectAsync(
                        token => _actions.RequestCloseAsync(claimed.Target, token),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!invoked)
                {
                    outcomes.Add(new(
                        ProcessGateOutcomeKind.Cancelled,
                        claimed.Target,
                        "close-not-started"));
                    continue;
                }

                closeOutcome = value;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                closeOutcome = ProcessCloseOutcome.Ambiguous;
            }

            ProcessActionJournalEntry? completed = await PersistCloseCompletionAsync(
                    claimed,
                    closeOutcome,
                    cancellationToken)
                .ConfigureAwait(false);
            if (completed is null)
            {
                outcomes.Add(new(
                    ProcessGateOutcomeKind.Cancelled,
                    claimed.Target,
                    "close-completion-not-durable"));
                continue;
            }

            switch (closeOutcome)
            {
                case ProcessCloseOutcome.Requested:
                    outcomes.Add(new(
                        ProcessGateOutcomeKind.CloseRequested,
                        claimed.Target,
                        null));
                    continuations.Add(completed);
                    break;
                case ProcessCloseOutcome.NoEligibleWindow:
                    outcomes.Add(new(
                        ProcessGateOutcomeKind.NoEligibleWindow,
                        claimed.Target,
                        null));
                    continuations.Add(completed);
                    break;
                case ProcessCloseOutcome.TargetExited:
                case ProcessCloseOutcome.IdentityMismatch:
                    outcomes.Add(new(
                        ProcessGateOutcomeKind.TargetExited,
                        claimed.Target,
                        closeOutcome.ToString()));
                    break;
                default:
                    outcomes.Add(new(
                        ProcessGateOutcomeKind.Cancelled,
                        claimed.Target,
                        closeOutcome.ToString()));
                    break;
            }
        }

        continuations.AddRange(initial.ResumableCloseCompletions);
        foreach (ProcessActionJournalEntry entry in continuations
                     .DistinctBy(entry => entry.Target.ActionKey))
        {
            _ = TryScheduleContinuation(
                request,
                entry,
                continuationCancellation);
        }

        return await FinishAsync(outcomes, cancellationToken).ConfigureAwait(false);
    }

    private bool TryScheduleContinuation(
        ProcessGateRunRequest request,
        ProcessActionJournalEntry entry,
        CancellationToken callerCancellation)
    {
        CancellationTokenSource? lifetime = null;
        lock (_lifecycle)
        {
            ProcessActionKey key = entry.Target.ActionKey;
            if (_isStopping
                || callerCancellation.IsCancellationRequested
                || !_scheduledContinuations.Add(key))
            {
                return false;
            }

            try
            {
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                    callerCancellation,
                    _stopping.Token);
                Task operation = RunContinuationLifetimeAsync(
                    request,
                    entry,
                    lifetime);
                _operations.Add(operation);
                _ = RemoveWhenCompletedAsync(operation);
                return true;
            }
            catch
            {
                _scheduledContinuations.Remove(key);
                lifetime?.Dispose();
                return false;
            }
        }
    }

    private async Task RunContinuationLifetimeAsync(
        ProcessGateRunRequest request,
        ProcessActionJournalEntry entry,
        CancellationTokenSource lifetime)
    {
        using (lifetime)
        {
            // Never execute the grace wait inline under the lifecycle lock. Production
            // scanning must be able to start its next cadence while this task is pending.
            await Task.Yield();
            ProcessGateOrchestrationOutcome? outcome;
            try
            {
                outcome = await ContinueReservedAsync(
                        request,
                        entry,
                        lifetime.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                return;
            }

            if (outcome is not null)
            {
                _ = await FinishAsync([outcome], lifetime.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<ProcessGateOrchestrationOutcome?> ContinueReservedAsync(
        ProcessGateRunRequest request,
        ProcessActionJournalEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ContinueAfterGraceAsync(request, entry, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_lifecycle)
            {
                _scheduledContinuations.Remove(entry.Target.ActionKey);
            }
        }
    }

    private async ValueTask<InitialCommit?> EvaluateAndCommitInitialAsync(
        ProcessGateRunRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsValidRequest(request))
        {
            return null;
        }

        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int attempt = 0; attempt < MaximumCasAttempts; attempt++)
            {
                ProcessGateEnvelope? envelope = await LoadEnvelopeAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (envelope is null)
                {
                    return null;
                }

                ValidatedProcessPolicy? policy = await ReadPolicyAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (policy is null
                    || !TryAdvancePolicyLedger(
                        envelope.PolicyLedger,
                        policy,
                        out ProcessPolicyLedger nextLedger)
                    || policy.Snapshot is not { } executablePolicy)
                {
                    return null;
                }

                ProcessObservationBatchEvidence observation = await ReadInitialObservationAsync(
                        request.BatchKind,
                        policy,
                        request,
                        envelope,
                        cancellationToken)
                    .ConfigureAwait(false);
                PreparedObservation prepared = PrepareObservation(
                    envelope,
                    observation,
                    executablePolicy.EvaluatedAt);
                prepared = ApplyPolicyAuthority(
                    prepared,
                    policy.PolicyResult.CanEnforce,
                    envelope.ObservationContinuity,
                    checked(envelope.Revision + 1));
                ProcessGateEvaluation evaluation = ProcessGateReducer.Evaluate(
                    envelope.ReducerState,
                    new ProcessGateContext(
                        executablePolicy,
                        request.InteractiveUserSid,
                        request.InteractiveSessionId,
                        prepared.ObserverEpoch,
                        prepared.CreationTimelineTrusted),
                    prepared.ReducerBatchKind,
                    prepared.Observations);

                ImmutableDictionary<ProcessActionKey, ProcessActionJournalEntry>.Builder journal =
                    PruneJournal(envelope, prepared, evaluation);
                List<ProcessActionJournalEntry> claims = [];
                bool enforcementHealthy = policy.PolicyResult.CanEnforce
                    && prepared.CanEnforce
                    && !evaluation.IsDegraded;
                if (enforcementHealthy)
                {
                    foreach (ProcessGateDecision decision in evaluation.Decisions)
                    {
                        if (decision.Disposition != ProcessGateDisposition.BlockNewRoot
                            || !TryCreateTarget(
                                policy,
                                evaluation,
                                prepared.Observations,
                                decision,
                                out ProcessExactTarget target)
                            || journal.ContainsKey(target.ActionKey)
                            || journal.Count >= MaximumJournalEntries)
                        {
                            continue;
                        }

                        ProcessActionJournalEntry claim = new(
                            target,
                            envelope.NextJournalSequence + claims.Count,
                            null,
                            null,
                            false,
                            null,
                            null);
                        journal.Add(target.ActionKey, claim);
                        claims.Add(claim);
                    }
                }

                ProcessObservationContinuityState continuity = prepared.PersistedContinuity;
                ProcessGateEnvelope replacement = envelope with
                {
                    Revision = checked(envelope.Revision + 1),
                    ReducerState = evaluation.State,
                    ActionJournal = journal.ToImmutable(),
                    PolicyLedger = nextLedger,
                    ObservationContinuity = continuity,
                    NextJournalSequence = checked(envelope.NextJournalSequence + claims.Count),
                };
                ProcessGateEnvelopeSaveResult saved = await SaveAsync(
                        envelope.Revision,
                        replacement,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (saved.Status == ProcessGateStoreSaveStatus.Conflict)
                {
                    continue;
                }

                if (saved.Status != ProcessGateStoreSaveStatus.Saved
                    || !IsAcceptedReplacement(saved.Envelope, replacement))
                {
                    return null;
                }

                AcknowledgementCommitResult acknowledgement =
                    await AcknowledgeContinuityAsync(replacement, cancellationToken)
                    .ConfigureAwait(false);
                if (acknowledgement is AcknowledgementCommitResult.PendingRetry
                    or AcknowledgementCommitResult.Superseded)
                {
                    return new(
                        [],
                        [],
                        evaluation.HealthCode,
                        true);
                }

                if (acknowledgement == AcknowledgementCommitResult.Invalid)
                {
                    return null;
                }

                ImmutableArray<ProcessActionJournalEntry> resumable = replacement.ActionJournal.Values
                    .Where(entry => entry.CanResumeAfterClose)
                    .Where(entry => claims.All(claim => claim.Target.ActionKey != entry.Target.ActionKey))
                    .ToImmutableArray();
                return new(
                    claims.ToImmutableArray(),
                    resumable,
                    evaluation.HealthCode,
                    evaluation.IsDegraded || !prepared.CanEnforce);
            }

            return null;
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private async Task<ProcessActionJournalEntry?> PersistCloseCompletionAsync(
        ProcessActionJournalEntry claimed,
        ProcessCloseOutcome outcome,
        CancellationToken cancellationToken)
    {
        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProcessGateEnvelope? current = await LoadEnvelopeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current is null
                || !current.ActionJournal.TryGetValue(
                    claimed.Target.ActionKey,
                    out ProcessActionJournalEntry? persisted)
                || persisted.Target != claimed.Target
                || persisted.CloseCompletion is not null
                || persisted.TerminalReason is not null)
            {
                return null;
            }

            ProcessActionJournalEntry completed = persisted with
            {
                CloseCompletion = outcome,
                TerminalReason = CloseTerminalReason(outcome),
            };
            ProcessGateEnvelope replacement = current with
            {
                Revision = checked(current.Revision + 1),
                ActionJournal = current.ActionJournal.SetItem(
                    claimed.Target.ActionKey,
                    completed),
            };
            ProcessGateEnvelopeSaveResult saved = await SaveAsync(
                    current.Revision,
                    replacement,
                    cancellationToken)
                .ConfigureAwait(false);
            return saved.Status == ProcessGateStoreSaveStatus.Saved
                && IsAcceptedReplacement(saved.Envelope, replacement)
                ? completed
                : null;
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private async Task<ProcessGateOrchestrationOutcome?> ContinueAfterGraceAsync(
        ProcessGateRunRequest request,
        ProcessActionJournalEntry completed,
        CancellationToken cancellationToken)
    {
        bool elapsed = await DelayAtLeastAsync(CloseGracePeriod, cancellationToken)
            .ConfigureAwait(false);
        if (!elapsed)
        {
            return new(
                ProcessGateOutcomeKind.Cancelled,
                completed.Target,
                "grace-delay-unavailable");
        }

        // The barrier is acquired before the final policy read and held through the
        // actual effect. A supported override is therefore either accepted first and
        // observed below, or accepted only after this termination attempt completes.
        // _singleFlight is only acquired inside this barrier order; no path acquires
        // _singleFlight and then waits for the cutoff barrier.
        using IDisposable lease = await _cutoffBarrier
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);
        TerminationCommit? commit = await RecheckAndClaimTerminationAsync(
                request,
                completed.Target,
                cancellationToken)
            .ConfigureAwait(false);
        if (commit is null)
        {
            return new(
                ProcessGateOutcomeKind.Cancelled,
                completed.Target,
                "fresh-recheck-cancelled");
        }

        ProcessTerminationOutcome termination;
        try
        {
            (bool invoked, ProcessTerminationOutcome value) = await InvokeEffectAsync(
                    token => _actions.RequestTerminationAsync(completed.Target, token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!invoked)
            {
                return new(
                    ProcessGateOutcomeKind.Cancelled,
                    completed.Target,
                    "termination-not-started");
            }

            termination = value;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            termination = ProcessTerminationOutcome.Ambiguous;
        }

        await PersistTerminationCompletionAsync(
                commit.Entry,
                termination,
                cancellationToken)
            .ConfigureAwait(false);
        return new(
            ProcessGateOutcomeKind.TerminateAttempted,
            completed.Target,
            termination.ToString());
    }

    private async ValueTask<TerminationCommit?> RecheckAndClaimTerminationAsync(
        ProcessGateRunRequest request,
        ProcessExactTarget target,
        CancellationToken cancellationToken)
    {
        string recheckClaimIdentity =
            ProcessActionJournalEntry.ModernRecheckClaimPrefix
            + Guid.NewGuid().ToString("N");
        await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProcessGateEnvelope? claimedEnvelope = await ClaimFreshRecheckAsync(
                    target,
                    recheckClaimIdentity,
                    cancellationToken)
                .ConfigureAwait(false);
            if (claimedEnvelope is null)
            {
                return null;
            }

            for (int attempt = 0; attempt < MaximumCasAttempts; attempt++)
            {
                if (!claimedEnvelope.ActionJournal.TryGetValue(
                        target.ActionKey,
                        out ProcessActionJournalEntry? currentEntry)
                    || currentEntry.RecheckClaimIdentity != recheckClaimIdentity
                    || currentEntry.TerminalReason is not null
                    || currentEntry.TerminationClaimed)
                {
                    return null;
                }

                ValidatedProcessPolicy? policy = await ReadPolicyAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (policy is null
                    || policy.Revision < target.OriginalPolicyRevision
                    || policy.Snapshot is not { } executablePolicy
                    || executablePolicy.EvaluatedAt < target.OriginalPolicyEvaluatedAtUtc
                    || !TryAdvancePolicyLedger(
                        claimedEnvelope.PolicyLedger,
                        policy,
                        out ProcessPolicyLedger nextLedger))
                {
                    return null;
                }

                ProcessObservationBatchEvidence exact = await ReadExactObservationAsync(
                        target,
                        policy,
                        request,
                        claimedEnvelope,
                        cancellationToken)
                    .ConfigureAwait(false);
                PreparedObservation prepared = PrepareObservation(
                    claimedEnvelope,
                    exact,
                    executablePolicy.EvaluatedAt);
                prepared = ApplyPolicyAuthority(
                    prepared,
                    policy.PolicyResult.CanEnforce,
                    claimedEnvelope.ObservationContinuity,
                    checked(claimedEnvelope.Revision + 1));
                ProcessGateEvaluation evaluation = ProcessGateReducer.Evaluate(
                    claimedEnvelope.ReducerState,
                    new ProcessGateContext(
                        executablePolicy,
                        request.InteractiveUserSid,
                        request.InteractiveSessionId,
                        prepared.ObserverEpoch,
                        prepared.CreationTimelineTrusted),
                    ProcessObservationBatchKind.StartDelta,
                    prepared.Observations);
                bool stillExact = policy.PolicyResult.CanEnforce
                    && prepared.CanEnforce
                    && exact.BatchKind == ProcessObservationBatchKind.StartDelta
                    && !exact.IsAuthoritativeAllProcessCatalog
                    && IsExactSingleton(prepared.Observations, target);
                ProcessGateDecision? decision = evaluation.Decisions.SingleOrDefault(candidate =>
                    candidate.InstanceKey == target.InstanceKey);
                bool stillBlocked = stillExact
                    && !evaluation.IsDegraded
                    && evaluation.State.NightDate == target.NightDate
                    && string.Equals(
                        evaluation.State.RuleFingerprint,
                        target.RuleFingerprint,
                        StringComparison.Ordinal)
                    && decision is
                    {
                        Disposition: ProcessGateDisposition.BlockNewRoot,
                        RuleId: not null,
                        CutoffUtc: not null,
                    }
                    && string.Equals(decision.RuleId, target.RuleId, StringComparison.OrdinalIgnoreCase)
                    && decision.CutoffUtc == target.CutoffUtc;
                ProcessActionJournalEntry nextEntry = currentEntry with
                {
                    TerminationClaimed = stillBlocked,
                    TerminalReason = stillBlocked
                        ? null
                        : ProcessActionTerminalReason.RecheckCancelled,
                    DeferredByOverride = stillBlocked
                        ? null
                        : DeferringOverride(evaluation.State, decision),
                };
                ProcessGateEnvelope replacement = claimedEnvelope with
                {
                    Revision = checked(claimedEnvelope.Revision + 1),
                    ReducerState = evaluation.State,
                    PolicyLedger = nextLedger,
                    ObservationContinuity = prepared.PersistedContinuity,
                    ActionJournal = claimedEnvelope.ActionJournal.SetItem(
                        target.ActionKey,
                        nextEntry),
                };
                ProcessGateEnvelopeSaveResult saved = await SaveAsync(
                        claimedEnvelope.Revision,
                        replacement,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (saved.Status == ProcessGateStoreSaveStatus.Conflict)
                {
                    ProcessGateEnvelope? winner = await LoadEnvelopeAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (winner is null)
                    {
                        return null;
                    }

                    claimedEnvelope = winner;
                    continue;
                }

                if (saved.Status != ProcessGateStoreSaveStatus.Saved
                    || !IsAcceptedReplacement(saved.Envelope, replacement))
                {
                    return null;
                }

                AcknowledgementCommitResult acknowledgement =
                    await AcknowledgeContinuityAsync(replacement, cancellationToken)
                    .ConfigureAwait(false);
                if (acknowledgement is AcknowledgementCommitResult.PendingRetry
                    or AcknowledgementCommitResult.Superseded
                    or AcknowledgementCommitResult.Invalid)
                {
                    return null;
                }

                return stillBlocked ? new(nextEntry) : null;
            }

            return null;
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private async ValueTask<ProcessGateEnvelope?> ClaimFreshRecheckAsync(
        ProcessExactTarget target,
        string claimIdentity,
        CancellationToken cancellationToken)
    {
        ProcessGateEnvelope? current = await LoadEnvelopeAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null
            || !current.ActionJournal.TryGetValue(
                target.ActionKey,
                out ProcessActionJournalEntry? entry)
            || entry.Target != target
            || !entry.CanResumeAfterClose)
        {
            return null;
        }

        ProcessActionJournalEntry claimed = entry with
        {
            RecheckClaimIdentity = claimIdentity,
        };
        ProcessGateEnvelope replacement = current with
        {
            Revision = checked(current.Revision + 1),
            ActionJournal = current.ActionJournal.SetItem(target.ActionKey, claimed),
        };
        ProcessGateEnvelopeSaveResult saved = await SaveAsync(
                current.Revision,
                replacement,
                cancellationToken)
            .ConfigureAwait(false);
        return saved.Status == ProcessGateStoreSaveStatus.Saved
            && IsAcceptedReplacement(saved.Envelope, replacement)
            ? replacement
            : null;
    }

    private async Task PersistTerminationCompletionAsync(
        ProcessActionJournalEntry claimed,
        ProcessTerminationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await _singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            ProcessGateEnvelope? current = await LoadEnvelopeAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current is null
                || !current.ActionJournal.TryGetValue(
                    claimed.Target.ActionKey,
                    out ProcessActionJournalEntry? persisted)
                || persisted.Target != claimed.Target
                || !persisted.TerminationClaimed
                || persisted.TerminationCompletion is not null)
            {
                return;
            }

            ProcessActionJournalEntry completed = persisted with
            {
                TerminationCompletion = outcome,
                TerminalReason = outcome == ProcessTerminationOutcome.Terminated
                    ? ProcessActionTerminalReason.TerminationCompleted
                    : ProcessActionTerminalReason.TerminationFailedOpen,
            };
            ProcessGateEnvelope replacement = current with
            {
                Revision = checked(current.Revision + 1),
                ActionJournal = current.ActionJournal.SetItem(
                    claimed.Target.ActionKey,
                    completed),
            };
            await SaveAsync(current.Revision, replacement, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The durable termination claim already prevents a second invocation.
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    private async ValueTask<(bool Invoked, T Value)> InvokeEffectAsync<T>(
        Func<CancellationToken, ValueTask<T>> effect,
        CancellationToken cancellationToken)
        where T : struct
    {
        Task<T> started;
        lock (_lifecycle)
        {
            if (_isStopping || cancellationToken.IsCancellationRequested)
            {
                return (false, default);
            }

            started = effect(cancellationToken).AsTask();
        }

        return (true, await started.ConfigureAwait(false));
    }

    private async ValueTask<bool> DelayAtLeastAsync(
        TimeSpan minimum,
        CancellationToken cancellationToken)
    {
        long started;
        try
        {
            started = _delay.GetTimestamp();
        }
        catch (Exception)
        {
            return false;
        }

        TimeSpan remaining = minimum;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                await _delay.DelayAsync(remaining, cancellationToken).ConfigureAwait(false);
                TimeSpan elapsed = _delay.GetElapsedTime(started);
                if (elapsed >= minimum)
                {
                    return true;
                }

                if (elapsed < TimeSpan.Zero)
                {
                    return false;
                }

                remaining = minimum - elapsed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    private async ValueTask<ValidatedProcessPolicy?> ReadPolicyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessGatePolicySourceResult result = await _policySource
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            return result.Status == ProcessGateSourceStatus.Available
                && IsValidPolicyEvidence(result.Policy)
                ? result.Policy
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async ValueTask<ProcessObservationBatchEvidence> ReadInitialObservationAsync(
        ProcessObservationBatchKind kind,
        ValidatedProcessPolicy policy,
        ProcessGateRunRequest runRequest,
        ProcessGateEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!ProcessCatalogPolicyBinding.TryCreate(
                policy,
                runRequest.InteractiveUserSid,
                runRequest.InteractiveSessionId,
                out ProcessCatalogPolicyBinding? binding))
        {
            return UnavailableObservation(envelope, kind);
        }

        try
        {
            ProcessObservationBatchEvidence? result = await _observationSource
                .ReadBatchAsync(new(kind, binding!), cancellationToken)
                .ConfigureAwait(false);
            return result ?? UnavailableObservation(envelope, kind);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return UnavailableObservation(envelope, kind);
        }
    }

    private async ValueTask<ProcessObservationBatchEvidence> ReadExactObservationAsync(
        ProcessExactTarget target,
        ValidatedProcessPolicy policy,
        ProcessGateRunRequest runRequest,
        ProcessGateEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!ProcessCatalogPolicyBinding.TryCreate(
                policy,
                runRequest.InteractiveUserSid,
                runRequest.InteractiveSessionId,
                out ProcessCatalogPolicyBinding? binding))
        {
            return UnavailableObservation(
                envelope,
                ProcessObservationBatchKind.StartDelta);
        }

        try
        {
            ProcessObservationBatchEvidence? result = await _observationSource
                .ReadExactAsync(target, binding!, cancellationToken)
                .ConfigureAwait(false);
            return result ?? UnavailableObservation(
                envelope,
                ProcessObservationBatchKind.StartDelta);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return UnavailableObservation(envelope, ProcessObservationBatchKind.StartDelta);
        }
    }

    private async ValueTask<ProcessGateEnvelope?> LoadEnvelopeAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessGateEnvelopeLoadResult result = await _store
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            return result.Status switch
            {
                ProcessGateStoreLoadStatus.NotFound => ProcessGateEnvelope.Empty,
                ProcessGateStoreLoadStatus.Found when IsValidEnvelope(result.Envelope) =>
                    result.Envelope,
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async ValueTask<ProcessGateEnvelopeSaveResult> SaveAsync(
        long expectedRevision,
        ProcessGateEnvelope replacement,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.CompareExchangeAsync(
                    expectedRevision,
                    replacement,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new(ProcessGateStoreSaveStatus.Unavailable, null);
        }
    }

    private async ValueTask<AcknowledgementCommitResult> AcknowledgeContinuityAsync(
        ProcessGateEnvelope persisted,
        CancellationToken cancellationToken)
    {
        ProcessObservationAcknowledgementCheckpoint? checkpoint =
            persisted.ObservationContinuity.AcknowledgementCheckpoint;
        if (checkpoint is null || checkpoint.Delivered)
        {
            return AcknowledgementCommitResult.NotRequired;
        }

        try
        {
            await _observationSource.AcknowledgeAsync(
                    new(
                        checkpoint.Kind,
                        checkpoint.ObserverEpoch,
                        checkpoint.TransitionRevision),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The durable pending checkpoint makes a later evaluation retry the same
            // idempotency key. Local persistence alone never counts as source delivery.
            return AcknowledgementCommitResult.PendingRetry;
        }

        ProcessGateEnvelope delivered = persisted with
        {
            Revision = checked(persisted.Revision + 1),
            ObservationContinuity = persisted.ObservationContinuity with
            {
                AcknowledgementCheckpoint = checkpoint with { Delivered = true },
            },
        };
        ProcessGateEnvelopeSaveResult saved = await SaveAsync(
                persisted.Revision,
                delivered,
                cancellationToken)
            .ConfigureAwait(false);
        if (saved.Status == ProcessGateStoreSaveStatus.Saved)
        {
            return IsAcceptedReplacement(saved.Envelope, delivered)
                ? AcknowledgementCommitResult.Delivered
                : AcknowledgementCommitResult.Invalid;
        }

        if (saved.Status != ProcessGateStoreSaveStatus.Conflict)
        {
            return AcknowledgementCommitResult.PendingRetry;
        }

        if (!IsValidEnvelope(saved.Envelope)
            || saved.Envelope!.Revision < persisted.Revision)
        {
            return AcknowledgementCommitResult.Invalid;
        }

        ProcessObservationAcknowledgementCheckpoint? winner = saved.Envelope
            .ObservationContinuity.AcknowledgementCheckpoint;
        if (saved.Envelope.Revision == persisted.Revision)
        {
            return SameAcknowledgementIdentity(winner, checkpoint)
                && winner is { Delivered: false }
                    ? AcknowledgementCommitResult.PendingRetry
                    : AcknowledgementCommitResult.Invalid;
        }

        if (SameAcknowledgementIdentity(winner, checkpoint))
        {
            return winner!.Delivered
                ? AcknowledgementCommitResult.Delivered
                : AcknowledgementCommitResult.PendingRetry;
        }

        return IsLegalAcknowledgementSupersede(checkpoint, saved.Envelope)
                ? AcknowledgementCommitResult.Superseded
                : AcknowledgementCommitResult.Invalid;
    }

    private static bool IsLegalAcknowledgementSupersede(
        ProcessObservationAcknowledgementCheckpoint pending,
        ProcessGateEnvelope winner)
    {
        ProcessObservationAcknowledgementCheckpoint? next = winner
            .ObservationContinuity.AcknowledgementCheckpoint;
        if (next is null || next.TransitionRevision <= pending.TransitionRevision)
        {
            return false;
        }

        return pending.Kind switch
        {
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted =>
                winner.ObservationContinuity.IsLost
                && next.Kind
                    == ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            ProcessObservationAcknowledgementKind.TrustSeverPersisted =>
                !winner.ObservationContinuity.IsLost
                && next.Kind
                    == ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            _ => false,
        };
    }

    private static bool SameAcknowledgementIdentity(
        ProcessObservationAcknowledgementCheckpoint? left,
        ProcessObservationAcknowledgementCheckpoint right) =>
        left is not null
        && left.Kind == right.Kind
        && string.Equals(left.ObserverEpoch, right.ObserverEpoch, StringComparison.Ordinal)
        && left.TransitionRevision == right.TransitionRevision;

    private static PreparedObservation PrepareObservation(
        ProcessGateEnvelope envelope,
        ProcessObservationBatchEvidence evidence,
        DateTimeOffset policyEvaluatedAtUtc)
    {
        IReadOnlyList<ProcessObservation> observations = evidence.Observations ?? [];
        bool epochValid = !string.IsNullOrWhiteSpace(evidence.ObserverEpoch);
        bool batchKindValid = Enum.IsDefined(evidence.BatchKind);
        bool batchShapeValid = batchKindValid && evidence.BatchKind switch
        {
            ProcessObservationBatchKind.StartDelta =>
                !evidence.IsAuthoritativeAllProcessCatalog,
            ProcessObservationBatchKind.AuthoritativeSnapshot =>
                evidence.IsAuthoritativeAllProcessCatalog,
            _ => false,
        };
        string epoch = epochValid
            ? evidence.ObserverEpoch!
            : envelope.ReducerState.ObserverContinuityEpoch
                ?? envelope.ObservationContinuity.LastTrustedEpoch
                ?? "continuity-unavailable";
        bool catalogIncomplete = evidence.BatchKind
                == ProcessObservationBatchKind.AuthoritativeSnapshot
            && !evidence.IsAuthoritativeAllProcessCatalog;
        ProcessObservationContinuityState prior = envelope.ObservationContinuity;
        bool sampleValid = TryValidateClockSample(
            evidence.ClockSample,
            out ProcessObservationClockSample sample);
        bool sameClockEpoch = sampleValid
            && string.Equals(prior.ClockEpoch, epoch, StringComparison.Ordinal);
        bool clockRollbackOrDrift = false;
        if (sampleValid
            && prior.SampleUtcHighWater is { } priorUtc
            && prior.SampleMonotonicHighWater is { } priorMonotonic
            && sameClockEpoch)
        {
            TimeSpan utcDelta = sample.StartedAtUtc - priorUtc;
            TimeSpan monotonicDelta = sample.StartedMonotonic - priorMonotonic;
            clockRollbackOrDrift = utcDelta < TimeSpan.Zero
                || monotonicDelta < TimeSpan.Zero
                || DriftExceedsTolerance(utcDelta, monotonicDelta);
        }

        bool unexpectedEpochChange = sampleValid
            && prior.ClockEpoch is not null
            && !sameClockEpoch
            && !prior.IsLost;
        bool evidenceLost = evidence.Status != ProcessGateSourceStatus.Available
            || !evidence.IsComplete
            || !evidence.CreationTimelineTrusted
            || evidence.ContinuityLost
            || catalogIncomplete
            || !epochValid
            || !batchKindValid
            || !batchShapeValid
            || !sampleValid
            || clockRollbackOrDrift
            || unexpectedEpochChange
            || observations.Any(static observation => observation is null);
        DateTimeOffset? logicalHighWater = envelope.ReducerState.LastEffectiveLogicalTime;
        bool utcRecoveryForward = sampleValid
            && (prior.SampleUtcHighWater is not { } persistedUtc
                || sample.StartedAtUtc >= persistedUtc
                && policyEvaluatedAtUtc >= persistedUtc)
            && (logicalHighWater is not { } logicalUtc
                || sample.StartedAtUtc >= logicalUtc
                && policyEvaluatedAtUtc >= logicalUtc);
        bool recovery = prior.IsLost
            && prior.TrustSeverPersisted
            && IsDeliveredAcknowledgement(
                prior,
                ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                prior.LossEpoch)
            && !evidenceLost
            && utcRecoveryForward
            && evidence.BatchKind == ProcessObservationBatchKind.AuthoritativeSnapshot
            && evidence.IsAuthoritativeAllProcessCatalog
            && (prior.LastTrustedEpoch is null
                || !string.Equals(prior.LastTrustedEpoch, epoch, StringComparison.Ordinal))
            && (prior.LossEpoch is null
                || !string.Equals(prior.LossEpoch, epoch, StringComparison.Ordinal));
        bool trusted = !evidenceLost && (!prior.IsLost || recovery);
        string? clockEpoch = prior.ClockEpoch;
        DateTimeOffset? sampleUtcHighWater = prior.SampleUtcHighWater;
        TimeSpan? sampleMonotonicHighWater = prior.SampleMonotonicHighWater;
        bool mayAdvanceClock = sampleValid
            && (prior.ClockEpoch is null || sameClockEpoch || recovery)
            && (sampleUtcHighWater is null
                || sample.CompletedAtUtc >= sampleUtcHighWater)
            && (sampleMonotonicHighWater is null
                || !sameClockEpoch
                || sample.CompletedMonotonic >= sampleMonotonicHighWater);
        if (mayAdvanceClock)
        {
            clockEpoch = epoch;
            sampleUtcHighWater = sample.CompletedAtUtc;
            sampleMonotonicHighWater = sample.CompletedMonotonic;
        }

        ProcessObservationContinuityState continuity;
        if (recovery)
        {
            continuity = new(
                false,
                false,
                epoch,
                null,
                clockEpoch,
                sampleUtcHighWater,
                sampleMonotonicHighWater,
                PendingAcknowledgement(
                    ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
                    epoch,
                    checked(envelope.Revision + 1)));
        }
        else if (evidenceLost || prior.IsLost)
        {
            bool hasPendingSever = HasPendingTrustSever(prior);
            string? lossEpoch = hasPendingSever
                ? prior.LossEpoch
                : evidenceLost
                    ? epoch
                    : prior.LossEpoch;
            bool startsNewLoss = !hasPendingSever
                && (!prior.IsLost
                    || !string.Equals(
                        prior.LossEpoch,
                        lossEpoch,
                        StringComparison.Ordinal));
            continuity = new(
                true,
                true,
                prior.LastTrustedEpoch,
                lossEpoch,
                clockEpoch,
                sampleUtcHighWater,
                sampleMonotonicHighWater,
                hasPendingSever
                    ? prior.AcknowledgementCheckpoint
                    : startsNewLoss
                    ? PendingAcknowledgement(
                        ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                        lossEpoch ?? epoch,
                        checked(envelope.Revision + 1))
                    : prior.AcknowledgementCheckpoint);
        }
        else
        {
            continuity = new(
                false,
                false,
                epoch,
                null,
                clockEpoch,
                sampleUtcHighWater,
                sampleMonotonicHighWater,
                prior.AcknowledgementCheckpoint);
        }

        ProcessObservationBatchKind reducerKind = evidence.BatchKind
            == ProcessObservationBatchKind.AuthoritativeSnapshot
            && evidence.IsComplete
            && evidence.IsAuthoritativeAllProcessCatalog
                ? ProcessObservationBatchKind.AuthoritativeSnapshot
                : ProcessObservationBatchKind.StartDelta;
        return new(
            observations,
            epoch,
            trusted,
            trusted
                && evidence.Status == ProcessGateSourceStatus.Available
                && continuity.AcknowledgementCheckpoint is not { Delivered: false },
            reducerKind,
            continuity);
    }

    private static bool TryValidateClockSample(
        ProcessObservationClockSample? candidate,
        out ProcessObservationClockSample sample)
    {
        sample = candidate!;
        if (candidate is null
            || candidate.StartedAtUtc.Offset != TimeSpan.Zero
            || candidate.CompletedAtUtc.Offset != TimeSpan.Zero
            || candidate.StartedAtUtc > candidate.CompletedAtUtc
            || candidate.StartedMonotonic < TimeSpan.Zero
            || candidate.CompletedMonotonic < candidate.StartedMonotonic)
        {
            return false;
        }

        TimeSpan utcDuration = candidate.CompletedAtUtc - candidate.StartedAtUtc;
        TimeSpan monotonicDuration = candidate.CompletedMonotonic
            - candidate.StartedMonotonic;
        return !DriftExceedsTolerance(utcDuration, monotonicDuration);
    }

    private static PreparedObservation ApplyPolicyAuthority(
        PreparedObservation prepared,
        bool policyCanEnforce,
        ProcessObservationContinuityState prior,
        long transitionRevision)
    {
        if (policyCanEnforce)
        {
            return prepared;
        }

        bool hasPendingSever = HasPendingTrustSever(prior);
        string lossEpoch = hasPendingSever
            ? prior.LossEpoch!
            : prepared.ObserverEpoch;
        return prepared with
        {
            CreationTimelineTrusted = false,
            CanEnforce = false,
            PersistedContinuity = prepared.PersistedContinuity with
            {
                IsLost = true,
                TrustSeverPersisted = true,
                LastTrustedEpoch = prior.LastTrustedEpoch,
                LossEpoch = lossEpoch,
                AcknowledgementCheckpoint = hasPendingSever
                    ? prior.AcknowledgementCheckpoint
                    : !prior.IsLost
                        || !string.Equals(
                            prior.LossEpoch,
                            lossEpoch,
                            StringComparison.Ordinal)
                        ? PendingAcknowledgement(
                            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
                            lossEpoch,
                            transitionRevision)
                        : prior.AcknowledgementCheckpoint,
            },
        };
    }

    private static bool HasPendingTrustSever(
        ProcessObservationContinuityState continuity) =>
        continuity.AcknowledgementCheckpoint is
        {
            Kind: ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            Delivered: false,
        } checkpoint
        && string.Equals(
            checkpoint.ObserverEpoch,
            continuity.LossEpoch,
            StringComparison.Ordinal);

    private static ProcessObservationAcknowledgementCheckpoint PendingAcknowledgement(
        ProcessObservationAcknowledgementKind kind,
        string epoch,
        long transitionRevision) =>
        new(kind, epoch, transitionRevision, false);

    private static bool IsDeliveredAcknowledgement(
        ProcessObservationContinuityState continuity,
        ProcessObservationAcknowledgementKind kind,
        string? epoch) =>
        epoch is not null
        && continuity.AcknowledgementCheckpoint is
        {
            Delivered: true,
        } checkpoint
        && checkpoint.Kind == kind
        && string.Equals(checkpoint.ObserverEpoch, epoch, StringComparison.Ordinal);

    private static bool DriftExceedsTolerance(TimeSpan utc, TimeSpan monotonic) =>
        Math.Abs((decimal)utc.Ticks - monotonic.Ticks) > MaximumClockDrift.Ticks;

    private static bool TryAdvancePolicyLedger(
        ProcessPolicyLedger current,
        ValidatedProcessPolicy candidate,
        out ProcessPolicyLedger next)
    {
        next = current;
        if (!IsValidPolicyEvidence(candidate)
            || candidate.Revision < current.HighestRevision)
        {
            return false;
        }

        if (current.PayloadByEvaluationIdentity.TryGetValue(
                candidate.EvaluationIdentity,
                out ProcessPolicyPayloadBinding? boundPayload)
            && (boundPayload.Revision != candidate.Revision
                || !string.Equals(
                    boundPayload.PayloadFingerprint,
                    candidate.PayloadFingerprint,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        if (candidate.Revision == current.HighestRevision
            && (!string.Equals(
                    candidate.EvaluationIdentity,
                    current.HighestEvaluationIdentity,
                    StringComparison.Ordinal)
                || !current.PayloadByEvaluationIdentity.ContainsKey(
                    candidate.EvaluationIdentity)))
        {
            return false;
        }

        ImmutableDictionary<string, ProcessPolicyPayloadBinding> bindings =
            current.PayloadByEvaluationIdentity.SetItem(
                candidate.EvaluationIdentity,
                new(candidate.Revision, candidate.PayloadFingerprint));
        if (bindings.Count > MaximumPolicyBindings)
        {
            string oldest = bindings
                .Where(pair => !string.Equals(
                    pair.Key,
                    candidate.EvaluationIdentity,
                    StringComparison.Ordinal))
                .OrderBy(pair => pair.Value.Revision)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .First()
                .Key;
            bindings = bindings.Remove(oldest);
        }

        next = new(
            Math.Max(current.HighestRevision, candidate.Revision),
            candidate.Revision > current.HighestRevision
                ? candidate.EvaluationIdentity
                : current.HighestEvaluationIdentity,
            bindings);
        return true;
    }

    private static bool TryCreateTarget(
        ValidatedProcessPolicy policy,
        ProcessGateEvaluation evaluation,
        IReadOnlyList<ProcessObservation> observations,
        ProcessGateDecision decision,
        out ProcessExactTarget target)
    {
        target = null!;
        if (decision.InstanceKey is not { } key
            || decision.RuleId is not { } ruleId
            || decision.CutoffUtc is not { } cutoff
            || evaluation.State.NightDate is not { } nightDate
            || string.IsNullOrWhiteSpace(evaluation.State.RuleFingerprint)
            || policy.Snapshot is not { } executablePolicy)
        {
            return false;
        }

        ProcessObservation[] matching = observations
            .Where(observation => observation.Identity?.Key == key)
            .ToArray();
        if (matching.Length != 1 || matching[0].Identity is not { } identity)
        {
            return false;
        }

        target = new(
            identity.Key,
            identity.CreationInstantUtc,
            identity.ExecutablePath,
            identity.UserSid,
            identity.SessionId,
            ruleId,
            cutoff,
            nightDate,
            evaluation.State.RuleFingerprint,
            policy.Revision,
            policy.EvaluationIdentity,
            policy.PayloadFingerprint,
            executablePolicy.EvaluatedAt);
        return true;
    }

    private static ImmutableDictionary<ProcessActionKey, ProcessActionJournalEntry>.Builder
        PruneJournal(
            ProcessGateEnvelope envelope,
            PreparedObservation observation,
            ProcessGateEvaluation evaluation)
    {
        ImmutableDictionary<ProcessActionKey, ProcessActionJournalEntry>.Builder journal =
            envelope.ActionJournal.ToBuilder();
        ProcessGateState evaluatedState = evaluation.State;
        DateOnly? currentNight = evaluatedState.NightDate;
        if (currentNight is { } evaluatedNight)
        {
            foreach (ProcessActionKey key in journal.Keys
                         .Where(key => key.NightDate < evaluatedNight
                             || evaluatedState.MorningReleased
                             && key.NightDate <= evaluatedNight)
                         .ToArray())
            {
                journal.Remove(key);
            }
        }

        if (observation.CanEnforce
            && observation.ReducerBatchKind == ProcessObservationBatchKind.AuthoritativeSnapshot)
        {
            HashSet<ProcessInstanceKey> present = observation.Observations
                .Where(value => value.Identity is not null)
                .Select(value => value.Identity!.Key)
                .ToHashSet();
            foreach (ProcessActionKey key in journal.Keys.ToArray())
            {
                ProcessActionJournalEntry entry = journal[key];
                if (!present.Contains(key.InstanceKey))
                {
                    journal.Remove(key);
                    continue;
                }

                bool exactTargetPresent = observation.Observations.Any(value =>
                    value.Identity is { } identity
                    && IsSameExactIdentity(identity, entry.Target));
                if (entry.TerminalReason == ProcessActionTerminalReason.RecheckCancelled
                    && exactTargetPresent
                    && HasSingleExactBlockDecision(evaluation, entry.Target)
                    && (entry.DeferredByOverride is { } deferredBy
                        && !SameNullableOverride(
                            deferredBy,
                            evaluatedState.TemporaryOverrideIdentity)
                        || entry.IsLegacyRecheckCancellation))
                {
                    journal.Remove(key);
                }
            }
        }

        return journal;
    }

    private static bool HasSingleExactBlockDecision(
        ProcessGateEvaluation evaluation,
        ProcessExactTarget target)
    {
        if (evaluation.IsDegraded)
        {
            return false;
        }

        ProcessGateDecision[] matching = evaluation.Decisions
            .Where(candidate => candidate.InstanceKey == target.InstanceKey)
            .ToArray();
        return matching is
        [
            {
                Disposition: ProcessGateDisposition.BlockNewRoot,
                RuleId: not null,
                CutoffUtc: not null,
            },
        ];
    }

    private static bool IsExactSingleton(
        IReadOnlyList<ProcessObservation> observations,
        ProcessExactTarget target)
    {
        if (observations.Count != 1 || observations[0].Identity is not { } identity)
        {
            return false;
        }

        return IsSameExactIdentity(identity, target);
    }

    private static bool IsSameExactIdentity(
        ObservedProcessIdentity identity,
        ProcessExactTarget target) =>
        identity.Key == target.InstanceKey
            && identity.CreationInstantUtc.EqualsExact(target.CreationInstantUtc)
            && string.Equals(
                identity.ExecutablePath,
                target.ExecutablePath,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(identity.UserSid, target.UserSid, StringComparison.Ordinal)
            && identity.SessionId == target.SessionId;

    private static ProcessOverrideIdentity? DeferringOverride(
        ProcessGateState state,
        ProcessGateDecision? decision) =>
        state.TemporaryOverrideIdentity is { } active
        && decision is
        {
            Disposition: ProcessGateDisposition.AllowTemporaryOverride,
        } or
        {
            Reason: ProcessGateReason.TeamRescueAwaitingSnapshot
                or ProcessGateReason.TeamRescueRootNotCaptured,
        }
            ? active
            : null;

    private static bool IsValidPolicyEvidence(ValidatedProcessPolicy? value) =>
        value is
        {
            Revision: >= 0,
            PolicyResult: not null,
        }
        && !string.IsNullOrWhiteSpace(value.EvaluationIdentity)
        && !string.IsNullOrWhiteSpace(value.PayloadFingerprint)
        && value.Snapshot is not null;

    internal static bool IsValidEnvelope(ProcessGateEnvelope? value) =>
        value is
        {
            Revision: >= 1,
            ReducerState: not null,
            ActionJournal: not null,
            PolicyLedger: not null,
            ObservationContinuity: not null,
            NextJournalSequence: >= 1,
        }
        && value.ActionJournal.Count <= MaximumJournalEntries
        && value.PolicyLedger.PayloadByEvaluationIdentity is not null
        && value.PolicyLedger.PayloadByEvaluationIdentity.Count <= MaximumPolicyBindings
        && IsValidContinuity(value.ObservationContinuity, value.Revision)
        && value.ActionJournal.All(pair =>
            pair.Key == pair.Value.Target.ActionKey
            && pair.Value.Sequence > 0
            && (pair.Value.DeferredByOverride is null
                || pair.Value.TerminalReason
                    == ProcessActionTerminalReason.RecheckCancelled
                && !pair.Value.TerminationClaimed
                && pair.Value.TerminationCompletion is null
                && pair.Value.RecheckClaimIdentity is not null
                && pair.Value.RecheckClaimIdentity.StartsWith(
                    ProcessActionJournalEntry.ModernRecheckClaimPrefix,
                    StringComparison.Ordinal)
                && IsValidOverrideIdentity(pair.Value.DeferredByOverride)));

    private static bool IsValidContinuity(
        ProcessObservationContinuityState continuity,
        long envelopeRevision)
    {
        ProcessObservationAcknowledgementCheckpoint? checkpoint =
            continuity.AcknowledgementCheckpoint;
        if (checkpoint is null)
        {
            return !continuity.IsLost
                && !continuity.TrustSeverPersisted
                && continuity.LossEpoch is null;
        }

        if (!Enum.IsDefined(checkpoint.Kind)
            || string.IsNullOrWhiteSpace(checkpoint.ObserverEpoch)
            || checkpoint.TransitionRevision < 1
            || checkpoint.TransitionRevision > envelopeRevision
            || checkpoint.Delivered
            && checkpoint.TransitionRevision >= envelopeRevision)
        {
            return false;
        }

        return continuity.IsLost
            ? continuity.TrustSeverPersisted
                && !string.IsNullOrWhiteSpace(continuity.LossEpoch)
                && checkpoint.Kind
                    == ProcessObservationAcknowledgementKind.TrustSeverPersisted
                && string.Equals(
                    checkpoint.ObserverEpoch,
                    continuity.LossEpoch,
                    StringComparison.Ordinal)
            : !continuity.TrustSeverPersisted
                && continuity.LossEpoch is null
                && checkpoint.Kind
                    == ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted
                && string.Equals(
                    checkpoint.ObserverEpoch,
                    continuity.LastTrustedEpoch,
                    StringComparison.Ordinal);
    }

    private static bool IsAcceptedReplacement(
        ProcessGateEnvelope? accepted,
        ProcessGateEnvelope proposed) =>
        accepted is not null
        && accepted.Revision == proposed.Revision
        && SameReducerState(accepted.ReducerState, proposed.ReducerState)
        && accepted.PolicyLedger.HighestRevision == proposed.PolicyLedger.HighestRevision
        && string.Equals(
            accepted.PolicyLedger.HighestEvaluationIdentity,
            proposed.PolicyLedger.HighestEvaluationIdentity,
            StringComparison.Ordinal)
        && SameDictionary(
            accepted.PolicyLedger.PayloadByEvaluationIdentity,
            proposed.PolicyLedger.PayloadByEvaluationIdentity,
            static (left, right) => left.Revision == right.Revision
                && string.Equals(
                    left.PayloadFingerprint,
                    right.PayloadFingerprint,
                    StringComparison.Ordinal))
        && SameContinuity(
            accepted.ObservationContinuity,
            proposed.ObservationContinuity)
        && accepted.NextJournalSequence == proposed.NextJournalSequence
        && SameDictionary(
            accepted.ActionJournal,
            proposed.ActionJournal,
            SameJournalEntry);

    private static bool SameReducerState(ProcessGateState left, ProcessGateState right) =>
        left.NightDate == right.NightDate
        && SameDateTime(left.LastEffectiveLogicalTime, right.LastEffectiveLogicalTime)
        && SameDateTime(left.CommittedWake, right.CommittedWake)
        && left.IsCommittedWakeLocked == right.IsCommittedWakeLocked
        && string.Equals(left.RuleFingerprint, right.RuleFingerprint, StringComparison.Ordinal)
        && SameDictionary(
            left.RuleStates,
            right.RuleStates,
            static (first, second) =>
                string.Equals(first.RuleId, second.RuleId, StringComparison.Ordinal)
                && first.CutoffUtc.EqualsExact(second.CutoffUtc)
                && first.IsSealed == second.IsSealed)
        && SameDictionary(
            left.KnownInstances,
            right.KnownInstances,
            static (first, second) =>
                SameIdentity(first.Identity, second.Identity)
                && first.Parent == second.Parent)
        && SameDictionary(
            left.EligibleInstances,
            right.EligibleInstances,
            static (first, second) => string.Equals(first, second, StringComparison.Ordinal))
        && SameDictionary(
            left.TemporaryInstances,
            right.TemporaryInstances,
            static (first, second) =>
                string.Equals(first.RuleId, second.RuleId, StringComparison.Ordinal)
                && SameOverride(first.OverrideIdentity, second.OverrideIdentity))
        && left.TaintedInstances.SetEquals(right.TaintedInstances)
        && SameNullableOverride(left.TemporaryOverrideIdentity, right.TemporaryOverrideIdentity)
        && SameNullableOverride(
            left.CapturedTeamRescueOverride,
            right.CapturedTeamRescueOverride)
        && SameNullableOverride(left.OverrideHighWater, right.OverrideHighWater)
        && left.RetiredOverrideIdentities.SetEquals(right.RetiredOverrideIdentities)
        && string.Equals(
            left.ObserverContinuityEpoch,
            right.ObserverContinuityEpoch,
            StringComparison.Ordinal)
        && SameDateTime(
            left.PreOverrideBaselineObservedAtUtc,
            right.PreOverrideBaselineObservedAtUtc)
        && left.CreationTimelineTrusted == right.CreationTimelineTrusted
        && left.MorningReleased == right.MorningReleased;

    private static bool SameContinuity(
        ProcessObservationContinuityState left,
        ProcessObservationContinuityState right) =>
        left.IsLost == right.IsLost
        && left.TrustSeverPersisted == right.TrustSeverPersisted
        && string.Equals(left.LastTrustedEpoch, right.LastTrustedEpoch, StringComparison.Ordinal)
        && string.Equals(left.LossEpoch, right.LossEpoch, StringComparison.Ordinal)
        && string.Equals(left.ClockEpoch, right.ClockEpoch, StringComparison.Ordinal)
        && SameDateTime(left.SampleUtcHighWater, right.SampleUtcHighWater)
        && left.SampleMonotonicHighWater == right.SampleMonotonicHighWater
        && SameAcknowledgementCheckpoint(
            left.AcknowledgementCheckpoint,
            right.AcknowledgementCheckpoint);

    private static bool SameAcknowledgementCheckpoint(
        ProcessObservationAcknowledgementCheckpoint? left,
        ProcessObservationAcknowledgementCheckpoint? right) =>
        left is null
            ? right is null
            : right is not null
                && left.Kind == right.Kind
                && string.Equals(
                    left.ObserverEpoch,
                    right.ObserverEpoch,
                    StringComparison.Ordinal)
                && left.TransitionRevision == right.TransitionRevision
                && left.Delivered == right.Delivered;

    private static bool SameJournalEntry(
        ProcessActionJournalEntry left,
        ProcessActionJournalEntry right) =>
        SameTarget(left.Target, right.Target)
        && left.Sequence == right.Sequence
        && left.CloseCompletion == right.CloseCompletion
        && string.Equals(
            left.RecheckClaimIdentity,
            right.RecheckClaimIdentity,
            StringComparison.Ordinal)
        && left.TerminationClaimed == right.TerminationClaimed
        && left.TerminationCompletion == right.TerminationCompletion
        && left.TerminalReason == right.TerminalReason
        && SameNullableOverride(left.DeferredByOverride, right.DeferredByOverride);

    private static bool SameTarget(ProcessExactTarget left, ProcessExactTarget right) =>
        left.InstanceKey == right.InstanceKey
        && left.CreationInstantUtc.EqualsExact(right.CreationInstantUtc)
        && string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.Ordinal)
        && string.Equals(left.UserSid, right.UserSid, StringComparison.Ordinal)
        && left.SessionId == right.SessionId
        && string.Equals(left.RuleId, right.RuleId, StringComparison.Ordinal)
        && left.CutoffUtc.EqualsExact(right.CutoffUtc)
        && left.NightDate == right.NightDate
        && string.Equals(left.RuleFingerprint, right.RuleFingerprint, StringComparison.Ordinal)
        && left.OriginalPolicyRevision == right.OriginalPolicyRevision
        && string.Equals(
            left.OriginalPolicyIdentity,
            right.OriginalPolicyIdentity,
            StringComparison.Ordinal)
        && string.Equals(
            left.OriginalPolicyPayloadFingerprint,
            right.OriginalPolicyPayloadFingerprint,
            StringComparison.Ordinal)
        && left.OriginalPolicyEvaluatedAtUtc.EqualsExact(
            right.OriginalPolicyEvaluatedAtUtc);

    private static bool SameIdentity(
        ObservedProcessIdentity left,
        ObservedProcessIdentity right) =>
        left.Key == right.Key
        && left.CreationInstantUtc.EqualsExact(right.CreationInstantUtc)
        && string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.Ordinal)
        && string.Equals(left.UserSid, right.UserSid, StringComparison.Ordinal)
        && left.SessionId == right.SessionId;

    private static bool SameNullableOverride(
        ProcessOverrideIdentity? left,
        ProcessOverrideIdentity? right) =>
        left is null
            ? right is null
            : right is not null && SameOverride(left, right);

    private static bool SameOverride(
        ProcessOverrideIdentity left,
        ProcessOverrideIdentity right) =>
        left.Kind == right.Kind
        && left.RequestedAtUtc.EqualsExact(right.RequestedAtUtc)
        && left.StartsAtUtc.EqualsExact(right.StartsAtUtc)
        && left.EndsAtUtc.EqualsExact(right.EndsAtUtc);

    private static bool IsValidOverrideIdentity(ProcessOverrideIdentity value) =>
        Enum.IsDefined(value.Kind)
        && value.RequestedAtUtc != default
        && value.RequestedAtUtc.Offset == TimeSpan.Zero
        && value.StartsAtUtc != default
        && value.StartsAtUtc.Offset == TimeSpan.Zero
        && value.EndsAtUtc != default
        && value.EndsAtUtc.Offset == TimeSpan.Zero
        && value.RequestedAtUtc <= value.StartsAtUtc
        && value.StartsAtUtc < value.EndsAtUtc;

    private static bool SameDateTime(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null
            ? right is null
            : right is not null && left.Value.EqualsExact(right.Value);

    private static bool SameDictionary<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> left,
        IReadOnlyDictionary<TKey, TValue> right,
        Func<TValue, TValue, bool> sameValue)
        where TKey : notnull =>
        left.Count == right.Count
        && left.All(pair =>
            right.TryGetValue(pair.Key, out TValue? value)
            && sameValue(pair.Value, value));

    private static bool IsValidRequest(ProcessGateRunRequest request) =>
        Enum.IsDefined(request.BatchKind)
        && !string.IsNullOrWhiteSpace(request.InteractiveUserSid)
        && request.InteractiveSessionId >= 0;

    private static ProcessActionTerminalReason? CloseTerminalReason(ProcessCloseOutcome outcome) =>
        outcome switch
        {
            ProcessCloseOutcome.Requested or ProcessCloseOutcome.NoEligibleWindow => null,
            ProcessCloseOutcome.TargetExited => ProcessActionTerminalReason.CloseTargetExited,
            ProcessCloseOutcome.IdentityMismatch => ProcessActionTerminalReason.CloseIdentityMismatch,
            ProcessCloseOutcome.Ambiguous => ProcessActionTerminalReason.CloseAmbiguous,
            ProcessCloseOutcome.Unavailable => ProcessActionTerminalReason.CloseUnavailable,
            _ => ProcessActionTerminalReason.CloseAmbiguous,
        };

    private static ProcessObservationBatchEvidence UnavailableObservation(
        ProcessGateEnvelope envelope,
        ProcessObservationBatchKind kind) =>
        new(
            ProcessGateSourceStatus.Unavailable,
            kind,
            [],
            envelope.ReducerState.ObserverContinuityEpoch
                ?? envelope.ObservationContinuity.LastTrustedEpoch,
            false,
            false,
            false,
            true,
            "observation-unavailable",
            null);

    private async ValueTask<ProcessGateRunResult> FinishAsync(
        List<ProcessGateOrchestrationOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        if (_outcomeSink is not null)
        {
            foreach (ProcessGateOrchestrationOutcome outcome in outcomes)
            {
                try
                {
                    await _outcomeSink.PublishAsync(outcome, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Outcome reporting cannot change enforcement state.
                }
            }
        }

        return new(outcomes.ToImmutableArray());
    }

    private static ProcessGateRunResult Cancelled(string code) =>
        new([new(ProcessGateOutcomeKind.Cancelled, null, code)]);

    private sealed record PreparedObservation(
        IReadOnlyList<ProcessObservation> Observations,
        string ObserverEpoch,
        bool CreationTimelineTrusted,
        bool CanEnforce,
        ProcessObservationBatchKind ReducerBatchKind,
        ProcessObservationContinuityState PersistedContinuity);

    private sealed record InitialCommit(
        ImmutableArray<ProcessActionJournalEntry> NewCloseClaims,
        ImmutableArray<ProcessActionJournalEntry> ResumableCloseCompletions,
        ProcessProtectionHealthCode HealthCode,
        bool IsDegraded);

    private sealed record TerminationCommit(ProcessActionJournalEntry Entry);

    private enum AcknowledgementCommitResult
    {
        NotRequired,
        Delivered,
        PendingRetry,
        Superseded,
        Invalid,
    }
}
