using System.Collections.Immutable;

namespace NightGate.Desktop;

public readonly record struct ProcessInstanceKey
{
    public ProcessInstanceKey(int pid, long creationUtcTicks)
    {
        if (pid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pid));
        }

        if (creationUtcTicks < DateTime.MinValue.Ticks
            || creationUtcTicks > DateTime.MaxValue.Ticks)
        {
            throw new ArgumentOutOfRangeException(nameof(creationUtcTicks));
        }

        Pid = pid;
        CreationUtcTicks = creationUtcTicks;
    }

    public int Pid { get; }

    public long CreationUtcTicks { get; }
}

public sealed record ObservedProcessIdentity(
    ProcessInstanceKey Key,
    DateTimeOffset CreationInstantUtc,
    string ExecutablePath,
    string UserSid,
    int SessionId);

public enum ParentLinkKind
{
    None,
    Exact,
    Unknown,
}

public readonly record struct ParentLink
{
    private ParentLink(ParentLinkKind kind, ProcessInstanceKey? exactParent)
    {
        Kind = kind;
        ExactParent = exactParent;
    }

    public ParentLinkKind Kind { get; }

    public ProcessInstanceKey? ExactParent { get; }

    public static ParentLink None { get; } = new(ParentLinkKind.None, null);

    public static ParentLink Unknown { get; } = new(ParentLinkKind.Unknown, null);

    public static ParentLink Exact(ProcessInstanceKey parent) =>
        new(ParentLinkKind.Exact, parent);
}

public sealed record ProcessObservation(
    int PidHint,
    ObservedProcessIdentity? Identity,
    ParentLink Parent);

public enum ProcessObservationBatchKind
{
    StartDelta,
    AuthoritativeSnapshot,
}

public sealed record ProcessGateContext(
    DesktopPolicySnapshotDto Policy,
    string InteractiveUserSid,
    int InteractiveSessionId,
    string ObserverContinuityEpoch,
    bool CreationTimelineTrusted);

public enum ProcessGateDisposition
{
    AllowUnrestricted,
    AllowEligible,
    AllowTemporaryOverride,
    AllowFailOpen,
    BlockNewRoot,
}

public enum ProcessGateReason
{
    UnconfiguredPath,
    BeforeRuleCutoff,
    EligibleRoot,
    EligibleHelper,
    TemporaryOverrideRoot,
    TemporaryOverrideHelper,
    NewRootAtOrAfterCutoff,
    PreCutoffRootAwaitingSealSnapshot,
    PreCutoffRootNotInSealSnapshot,
    MissingIdentity,
    InvalidIdentity,
    WrongUserOrSession,
    UnknownParent,
    MissingExactParent,
    CrossRuleParent,
    NonAllowlistedAncestor,
    ParentCycle,
    ParentCreatedAfterChild,
    TaintedIdentity,
    CreationInstantAfterEffectiveTime,
    ProcessProtectionDegraded,
    Morning,
    TeamRescueAwaitingSnapshot,
    TeamRescueRootNotCaptured,
}

public enum ProcessProtectionHealthCode
{
    Healthy,
    InvalidContext,
    InvalidRule,
    RulePathAmbiguity,
    SealedRuleMutation,
    CreationTimelineUntrusted,
    StaleNightPolicy,
    InvalidNightTransition,
    InvalidMorningPolicy,
    InvalidPersistedState,
}

public sealed record ProcessGateDecision(
    int PidHint,
    ProcessInstanceKey? InstanceKey,
    string? RuleId,
    DateTimeOffset? CutoffUtc,
    ProcessGateDisposition Disposition,
    ProcessGateReason Reason);

public sealed record ProcessRuleGateState(
    string RuleId,
    DateTimeOffset CutoffUtc,
    bool IsSealed);

public sealed record ProcessKnownInstance(
    ObservedProcessIdentity Identity,
    ParentLink Parent);

public sealed record ProcessOverrideIdentity(
    DesktopOverrideKind Kind,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc);

public sealed record TemporaryProcessGrant(
    string RuleId,
    ProcessOverrideIdentity OverrideIdentity);

public sealed record ProcessGateState(
    DateOnly? NightDate,
    DateTimeOffset? LastEffectiveLogicalTime,
    DateTimeOffset? CommittedWake,
    bool IsCommittedWakeLocked,
    string? RuleFingerprint,
    ImmutableDictionary<string, ProcessRuleGateState> RuleStates,
    ImmutableDictionary<ProcessInstanceKey, ProcessKnownInstance> KnownInstances,
    ImmutableDictionary<ProcessInstanceKey, string> EligibleInstances,
    ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant> TemporaryInstances,
    ImmutableHashSet<ProcessInstanceKey> TaintedInstances,
    ProcessOverrideIdentity? TemporaryOverrideIdentity,
    ProcessOverrideIdentity? CapturedTeamRescueOverride,
    ProcessOverrideIdentity? OverrideHighWater,
    ImmutableHashSet<ProcessOverrideIdentity> RetiredOverrideIdentities,
    string? ObserverContinuityEpoch,
    DateTimeOffset? PreOverrideBaselineObservedAtUtc,
    bool CreationTimelineTrusted,
    bool MorningReleased)
{
    public static ProcessGateState Empty { get; } = new(
        null,
        null,
        null,
        false,
        null,
        ImmutableDictionary<string, ProcessRuleGateState>.Empty.WithComparers(
            StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<ProcessInstanceKey, ProcessKnownInstance>.Empty,
        ImmutableDictionary<ProcessInstanceKey, string>.Empty,
        ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Empty,
        ImmutableHashSet<ProcessInstanceKey>.Empty,
        null,
        null,
        null,
        ImmutableHashSet<ProcessOverrideIdentity>.Empty,
        null,
        null,
        false,
        false);
}

public sealed record ProcessGateEvaluation(
    ProcessGateState State,
    ImmutableArray<ProcessGateDecision> Decisions,
    ProcessProtectionHealthCode HealthCode)
{
    public bool IsDegraded => HealthCode != ProcessProtectionHealthCode.Healthy;
}
