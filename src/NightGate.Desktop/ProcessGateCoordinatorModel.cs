using System.Collections.Immutable;

namespace NightGate.Desktop;

public enum ProcessGateSourceStatus
{
    Available,
    Unavailable,
    Corrupt,
}

/// <summary>
/// A policy witness produced by a validating source. Evaluation identities are globally
/// unique per revision; replaying an identity is permitted only with that same revision
/// and payload fingerprint.
/// </summary>
public sealed record ValidatedProcessPolicy(
    long Revision,
    string EvaluationIdentity,
    string PayloadFingerprint,
    DesktopPolicyResult PolicyResult,
    DesktopPolicySnapshotDto? ValidatedSnapshot = null)
{
    public DesktopPolicySnapshotDto? Snapshot =>
        ValidatedSnapshot ?? PolicyResult.Status?.Policy;
}

public sealed record ProcessGatePolicySourceResult(
    ProcessGateSourceStatus Status,
    ValidatedProcessPolicy? Policy,
    string? DegradationCode);

public interface IProcessGatePolicySource
{
    /// <summary>
    /// Returns a freshly validated policy witness. Implementations own payload hashing and
    /// the stable, non-reusable evaluation-identity contract described above.
    /// </summary>
    ValueTask<ProcessGatePolicySourceResult> ReadAsync(
        CancellationToken cancellationToken = default);
}

public sealed record ProcessObservationBatchEvidence(
    ProcessGateSourceStatus Status,
    ProcessObservationBatchKind BatchKind,
    IReadOnlyList<ProcessObservation> Observations,
    string? ObserverEpoch,
    bool IsComplete,
    bool IsAuthoritativeAllProcessCatalog,
    bool CreationTimelineTrusted,
    bool ContinuityLost,
    string? DegradationCode,
    ProcessObservationClockSample? ClockSample = null);

public sealed record ProcessObservationClockSample(
    DateTimeOffset StartedAtUtc,
    TimeSpan StartedMonotonic,
    DateTimeOffset CompletedAtUtc,
    TimeSpan CompletedMonotonic);

public enum ProcessObservationAcknowledgementKind
{
    TrustSeverPersisted,
    AuthoritativeRecoveryPersisted,
}

public sealed record ProcessObservationAcknowledgement(
    ProcessObservationAcknowledgementKind Kind,
    string ObserverEpoch,
    long EnvelopeRevision);

public sealed record ProcessObservationAcknowledgementCheckpoint(
    ProcessObservationAcknowledgementKind Kind,
    string ObserverEpoch,
    long TransitionRevision,
    bool Delivered);

public interface IProcessGateObservationSource
{
    ValueTask<ProcessObservationBatchEvidence> ReadBatchAsync(
        ProcessCatalogReadRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ProcessObservationBatchEvidence> ReadExactAsync(
        ProcessExactTarget target,
        ProcessCatalogPolicyBinding policyBinding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges a durable continuity transition. Implementations must treat the
    /// kind/epoch/envelope-revision tuple as an idempotency key and accept exact retries.
    /// They must also advance transition revisions monotonically and ignore a late ACK
    /// that has been superseded by a newer revision or conflicts with current source state.
    /// </summary>
    ValueTask AcknowledgeAsync(
        ProcessObservationAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default);
}

public enum ProcessCloseOutcome
{
    Requested,
    NoEligibleWindow,
    TargetExited,
    IdentityMismatch,
    Ambiguous,
    Unavailable,
}

public enum ProcessTerminationOutcome
{
    Terminated,
    TargetExited,
    IdentityMismatch,
    Ambiguous,
    Unavailable,
}

public interface IExactProcessActionAdapter
{
    ValueTask<ProcessCloseOutcome> RequestCloseAsync(
        ProcessExactTarget target,
        CancellationToken cancellationToken = default);

    ValueTask<ProcessTerminationOutcome> RequestTerminationAsync(
        ProcessExactTarget target,
        CancellationToken cancellationToken = default);
}

public interface IProcessGateMonotonicDelay
{
    long GetTimestamp();

    TimeSpan GetElapsedTime(long startingTimestamp);

    ValueTask DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken = default);
}

public sealed record ProcessExactTarget(
    ProcessInstanceKey InstanceKey,
    DateTimeOffset CreationInstantUtc,
    string ExecutablePath,
    string UserSid,
    int SessionId,
    string RuleId,
    DateTimeOffset CutoffUtc,
    DateOnly NightDate,
    string RuleFingerprint,
    long OriginalPolicyRevision,
    string OriginalPolicyIdentity,
    string OriginalPolicyPayloadFingerprint,
    DateTimeOffset OriginalPolicyEvaluatedAtUtc)
{
    public ProcessActionKey ActionKey => new(
        InstanceKey,
        NightDate,
        RuleId,
        CutoffUtc,
        RuleFingerprint);
}

public readonly record struct ProcessActionKey
{
    public ProcessActionKey(
        ProcessInstanceKey instanceKey,
        DateOnly nightDate,
        string ruleId,
        DateTimeOffset cutoffUtc,
        string ruleFingerprint)
    {
        InstanceKey = instanceKey;
        NightDate = nightDate;
        RuleId = ruleId.ToUpperInvariant();
        CutoffUtc = cutoffUtc;
        RuleFingerprint = ruleFingerprint;
    }

    public ProcessInstanceKey InstanceKey { get; }

    public DateOnly NightDate { get; }

    public string RuleId { get; }

    public DateTimeOffset CutoffUtc { get; }

    public string RuleFingerprint { get; }
}

public enum ProcessActionTerminalReason
{
    CloseTargetExited,
    CloseIdentityMismatch,
    CloseAmbiguous,
    CloseUnavailable,
    RecheckCancelled,
    TerminationCompleted,
    TerminationFailedOpen,
    Superseded,
}

public sealed record ProcessActionJournalEntry(
    ProcessExactTarget Target,
    long Sequence,
    ProcessCloseOutcome? CloseCompletion,
    string? RecheckClaimIdentity,
    bool TerminationClaimed,
    ProcessTerminationOutcome? TerminationCompletion,
    ProcessActionTerminalReason? TerminalReason)
{
    internal const string ModernRecheckClaimPrefix = "ng2:";

    public ProcessOverrideIdentity? DeferredByOverride { get; init; }

    public bool IsLegacyRecheckCancellation =>
        TerminalReason == ProcessActionTerminalReason.RecheckCancelled
        && DeferredByOverride is null
        && (RecheckClaimIdentity is null
            || !RecheckClaimIdentity.StartsWith(
                ModernRecheckClaimPrefix,
                StringComparison.Ordinal));

    public bool CloseClaimed => true;

    public bool CanResumeAfterClose =>
        TerminalReason is null
        && RecheckClaimIdentity is null
        && !TerminationClaimed
        && CloseCompletion is ProcessCloseOutcome.Requested
            or ProcessCloseOutcome.NoEligibleWindow;
}

public sealed record ProcessPolicyLedger(
    long HighestRevision,
    string? HighestEvaluationIdentity,
    ImmutableDictionary<string, ProcessPolicyPayloadBinding> PayloadByEvaluationIdentity)
{
    public static ProcessPolicyLedger Empty { get; } = new(
        -1,
        null,
        ImmutableDictionary<string, ProcessPolicyPayloadBinding>.Empty.WithComparers(
            StringComparer.Ordinal));
}

public sealed record ProcessPolicyPayloadBinding(
    long Revision,
    string PayloadFingerprint);

public sealed record ProcessObservationContinuityState(
    bool IsLost,
    bool TrustSeverPersisted,
    string? LastTrustedEpoch,
    string? LossEpoch,
    string? ClockEpoch,
    DateTimeOffset? SampleUtcHighWater,
    TimeSpan? SampleMonotonicHighWater,
    ProcessObservationAcknowledgementCheckpoint? AcknowledgementCheckpoint = null)
{
    public static ProcessObservationContinuityState Empty { get; } = new(
        false,
        false,
        null,
        null,
        null,
        null,
        null);
}

public sealed record ProcessGateEnvelope(
    long Revision,
    ProcessGateState ReducerState,
    ImmutableDictionary<ProcessActionKey, ProcessActionJournalEntry> ActionJournal,
    ProcessPolicyLedger PolicyLedger,
    ProcessObservationContinuityState ObservationContinuity,
    long NextJournalSequence)
{
    public static ProcessGateEnvelope Empty { get; } = new(
        0,
        ProcessGateState.Empty,
        ImmutableDictionary<ProcessActionKey, ProcessActionJournalEntry>.Empty,
        ProcessPolicyLedger.Empty,
        ProcessObservationContinuityState.Empty,
        1);
}

public enum ProcessGateStoreLoadStatus
{
    Found,
    NotFound,
    Unavailable,
    Corrupt,
}

public sealed record ProcessGateEnvelopeLoadResult(
    ProcessGateStoreLoadStatus Status,
    ProcessGateEnvelope? Envelope);

public enum ProcessGateStoreSaveStatus
{
    Saved,
    Conflict,
    Unavailable,
    Corrupt,
}

public sealed record ProcessGateEnvelopeSaveResult(
    ProcessGateStoreSaveStatus Status,
    ProcessGateEnvelope? Envelope);

public interface IProcessGateEnvelopeStore
{
    ValueTask<ProcessGateEnvelopeLoadResult> LoadAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ProcessGateEnvelopeSaveResult> CompareExchangeAsync(
        long expectedRevision,
        ProcessGateEnvelope replacement,
        CancellationToken cancellationToken = default);
}

public sealed record ProcessGateRunRequest(
    ProcessObservationBatchKind BatchKind,
    string InteractiveUserSid,
    int InteractiveSessionId);

public enum ProcessGateOutcomeKind
{
    Healthy,
    Degraded,
    CloseRequested,
    NoEligibleWindow,
    TargetExited,
    TerminateAttempted,
    Cancelled,
}

public sealed record ProcessGateOrchestrationOutcome(
    ProcessGateOutcomeKind Kind,
    ProcessExactTarget? Target,
    string? Code);

public sealed record ProcessGateRunResult(
    ImmutableArray<ProcessGateOrchestrationOutcome> Outcomes);

public interface IProcessGateOutcomeSink
{
    ValueTask PublishAsync(
        ProcessGateOrchestrationOutcome outcome,
        CancellationToken cancellationToken = default);
}
