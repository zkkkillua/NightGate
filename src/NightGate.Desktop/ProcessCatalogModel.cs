using System.Collections.Immutable;

namespace NightGate.Desktop;

public enum ProcessCatalogPolicyBindingRelation
{
    ExactReplay,
    NewWitnessSameScope,
    NewWitnessChangedScope,
    ConflictingReplay,
    StaleWitness,
    Malformed,
}

public sealed record ProcessCatalogPolicyBinding(
    long PolicyRevision,
    string EvaluationIdentity,
    string PayloadFingerprint,
    DateTimeOffset EvaluatedAtUtc,
    DateOnly NightDate,
    bool MonitoringActive,
    string InteractiveUserSid,
    int InteractiveSessionId,
    ImmutableArray<string> CanonicalExecutablePaths)
{
    public bool HasSamePolicyWitness(ProcessCatalogPolicyBinding? other) =>
        other is not null
        && PolicyRevision == other.PolicyRevision
        && string.Equals(
            EvaluationIdentity,
            other.EvaluationIdentity,
            StringComparison.Ordinal)
        && string.Equals(
            PayloadFingerprint,
            other.PayloadFingerprint,
            StringComparison.Ordinal)
        && EvaluatedAtUtc.EqualsExact(other.EvaluatedAtUtc);

    public bool HasSameEffectiveScope(ProcessCatalogPolicyBinding? other) =>
        other is not null
        && !CanonicalExecutablePaths.IsDefault
        && !other.CanonicalExecutablePaths.IsDefault
        && NightDate == other.NightDate
        && MonitoringActive == other.MonitoringActive
        && string.Equals(
            InteractiveUserSid,
            other.InteractiveUserSid,
            StringComparison.Ordinal)
        && InteractiveSessionId == other.InteractiveSessionId
        && CanonicalExecutablePaths.SequenceEqual(
            other.CanonicalExecutablePaths,
            StringComparer.OrdinalIgnoreCase);

    public bool IsExactReplayOf(ProcessCatalogPolicyBinding? other) =>
        HasSamePolicyWitness(other) && HasSameEffectiveScope(other);

    /// <summary>
    /// Returns true when classification identifies a conflicting replay. A lower revision is
    /// reported separately as stale even if it reuses an old evaluation identity.
    /// </summary>
    public bool IsCorruptReplayOf(ProcessCatalogPolicyBinding? other)
        => Classify(other, this)
            == ProcessCatalogPolicyBindingRelation.ConflictingReplay;

    public static ProcessCatalogPolicyBindingRelation Classify(
        ProcessCatalogPolicyBinding? previous,
        ProcessCatalogPolicyBinding? candidate)
    {
        if (!IsStructurallyValid(previous) || !IsStructurallyValid(candidate))
        {
            return ProcessCatalogPolicyBindingRelation.Malformed;
        }

        ProcessCatalogPolicyBinding prior = previous!;
        ProcessCatalogPolicyBinding next = candidate!;

        if (next.PolicyRevision < prior.PolicyRevision)
        {
            return ProcessCatalogPolicyBindingRelation.StaleWitness;
        }

        bool reusesRevision = next.PolicyRevision == prior.PolicyRevision;
        bool reusesEvaluationIdentity = string.Equals(
            next.EvaluationIdentity,
            prior.EvaluationIdentity,
            StringComparison.Ordinal);
        if (reusesRevision || reusesEvaluationIdentity)
        {
            return next.IsExactReplayOf(prior)
                ? ProcessCatalogPolicyBindingRelation.ExactReplay
                : ProcessCatalogPolicyBindingRelation.ConflictingReplay;
        }

        return next.HasSameEffectiveScope(prior)
            ? ProcessCatalogPolicyBindingRelation.NewWitnessSameScope
            : ProcessCatalogPolicyBindingRelation.NewWitnessChangedScope;
    }

    public static bool TryCreate(
        ValidatedProcessPolicy? policy,
        string interactiveUserSid,
        int interactiveSessionId,
        out ProcessCatalogPolicyBinding? binding)
    {
        binding = null;
        DesktopPolicySnapshotDto? snapshot = policy?.Snapshot;
        if (policy is null
            || policy.Revision < 0
            || string.IsNullOrWhiteSpace(policy.EvaluationIdentity)
            || string.IsNullOrWhiteSpace(policy.PayloadFingerprint)
            || snapshot is null
            || snapshot.Window is null
            || snapshot.AppRules is null
            || !Enum.IsDefined(snapshot.Phase)
            || string.IsNullOrWhiteSpace(interactiveUserSid)
            || interactiveSessionId < 0)
        {
            return false;
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopAppRuleDto? rule in snapshot.AppRules)
        {
            if (rule is null
                || !rule.IsConfigured
                || rule.RootExecutablePath is null
                || rule.HelperExecutablePaths is null
                || rule.Category is null
                || !Enum.IsDefined(rule.Category.Value)
                || rule.SessionMinutes is < 15 or > 90
                || !Win32ExecutablePathCanonicalizer.TryCanonicalize(
                    rule.RootExecutablePath,
                    out string rootPath))
            {
                return false;
            }

            paths.Add(rootPath);
            foreach (string? helper in rule.HelperExecutablePaths)
            {
                if (!Win32ExecutablePathCanonicalizer.TryCanonicalize(
                        helper,
                        out string helperPath))
                {
                    return false;
                }

                paths.Add(helperPath);
            }
        }

        DesktopNightWindowDto window = snapshot.Window;
        bool monitoringActive = snapshot.Phase != DesktopNightPhase.Morning
            && snapshot.EvaluatedAt >= window.ProtectedStart
            && snapshot.EvaluatedAt < window.Wake;
        binding = new(
            policy.Revision,
            policy.EvaluationIdentity,
            policy.PayloadFingerprint,
            snapshot.EvaluatedAt.ToUniversalTime(),
            window.NightDate,
            monitoringActive,
            interactiveUserSid,
            interactiveSessionId,
            paths.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray());
        return true;
    }

    private static bool IsStructurallyValid(ProcessCatalogPolicyBinding? value)
    {
        if (value is null
            || value.PolicyRevision < 0
            || string.IsNullOrWhiteSpace(value.EvaluationIdentity)
            || string.IsNullOrWhiteSpace(value.PayloadFingerprint)
            || value.EvaluatedAtUtc.Offset != TimeSpan.Zero
            || string.IsNullOrWhiteSpace(value.InteractiveUserSid)
            || value.InteractiveSessionId < 0
            || value.CanonicalExecutablePaths.IsDefault)
        {
            return false;
        }

        string? prior = null;
        foreach (string? path in value.CanonicalExecutablePaths)
        {
            if (!Win32ExecutablePathCanonicalizer.TryCanonicalize(
                    path,
                    out string canonical)
                || !string.Equals(path, canonical, StringComparison.OrdinalIgnoreCase)
                || prior is not null
                && StringComparer.OrdinalIgnoreCase.Compare(prior, canonical) >= 0)
            {
                return false;
            }

            prior = canonical;
        }

        return true;
    }
}

public sealed record ProcessCatalogReadRequest(
    ProcessObservationBatchKind RequestedKind,
    ProcessCatalogPolicyBinding PolicyBinding);
