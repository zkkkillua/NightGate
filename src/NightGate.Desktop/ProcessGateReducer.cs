using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using NightGate.Core;

namespace NightGate.Desktop;

public static class ProcessGateReducer
{
    private static readonly TimeSpan EmergencyDuration = TimeSpan.FromMinutes(30);

    public static ProcessGateEvaluation Evaluate(
        ProcessGateState state,
        ProcessGateContext context,
        ProcessObservationBatchKind batchKind,
        IReadOnlyList<ProcessObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observations);

        if (observations.Any(static observation => observation is null))
        {
            return new(
                state,
                ImmutableArray<ProcessGateDecision>.Empty,
                ProcessProtectionHealthCode.InvalidContext);
        }

        if (!IsValidPersistedState(state))
        {
            return Degraded(
                state,
                observations,
                ProcessProtectionHealthCode.InvalidPersistedState);
        }

        if (!context.CreationTimelineTrusted)
        {
            state = SeverCreationTrust(state, context, observations, batchKind);
        }

        DesktopPolicySnapshotDto? rawPolicy = context.Policy;
        if (rawPolicy?.Window is { } rawWindow
            && state.NightDate is { } currentNight
            && rawWindow.NightDate < currentNight)
        {
            return Degraded(
                state,
                observations,
                ProcessProtectionHealthCode.StaleNightPolicy);
        }

        if (rawPolicy?.Window is { } futureWindow
            && state.NightDate is { } priorNight
            && futureWindow.NightDate > priorNight
            && (rawPolicy.EvaluatedAt < futureWindow.ProtectedStart
                || state.LastEffectiveLogicalTime is { } priorTime
                && rawPolicy.EvaluatedAt < priorTime))
        {
            return Degraded(
                state,
                observations,
                ProcessProtectionHealthCode.InvalidNightTransition);
        }

        ProcessGateState? terminalMorningState = null;
        if (rawPolicy?.Window is { } releasedWindow
            && state.MorningReleased
            && state.NightDate == releasedWindow.NightDate)
        {
            terminalMorningState = state with
            {
                LastEffectiveLogicalTime = Max(
                    state.LastEffectiveLogicalTime,
                    rawPolicy.EvaluatedAt),
            };
        }
        else if (TryCreateTerminalMorningState(
                     state,
                     context,
                     rawPolicy,
                     out ProcessGateState? releasedState))
        {
            terminalMorningState = releasedState;
        }

        if (!Enum.IsDefined(batchKind))
        {
            return Degraded(
                terminalMorningState ?? state,
                observations,
                ProcessProtectionHealthCode.InvalidContext);
        }

        if (state.MorningReleased && terminalMorningState is not null)
        {
            return MorningEvaluation(
                terminalMorningState,
                observations);
        }

        if (rawPolicy?.Window is { } morningWindow
            && rawPolicy.Phase == DesktopNightPhase.Morning
            && rawPolicy.EvaluatedAt < morningWindow.Wake)
        {
            return Degraded(
                terminalMorningState
                ?? AdvanceDegradedPolicyState(state, rawPolicy),
                observations,
                ProcessProtectionHealthCode.InvalidMorningPolicy);
        }

        if (!TryCompile(
                context,
                out CompiledPolicy? maybeCompiled,
                out ProcessProtectionHealthCode health))
        {
            return Degraded(
                terminalMorningState
                ?? AdvanceDegradedPolicyState(state, context.Policy),
                observations,
                health);
        }

        CompiledPolicy compiled = maybeCompiled!;
        DesktopPolicySnapshotDto policy = context.Policy;
        DateTimeOffset effectiveTime = state.NightDate == policy.Window.NightDate
            ? Max(state.LastEffectiveLogicalTime, policy.EvaluatedAt)
            : policy.EvaluatedAt;
        bool sameNightForWake = state.NightDate == policy.Window.NightDate;
        bool committedWakeLocked = sameNightForWake
            && (state.IsCommittedWakeLocked
                || state.RuleStates.Values.Any(rule => rule.IsSealed));
        DateTimeOffset committedWake = committedWakeLocked
            ? state.CommittedWake ?? policy.Window.Wake
            : policy.Window.Wake;
        if (effectiveTime >= committedWake)
        {
            return MorningEvaluation(
                NewState(compiled, effectiveTime, context) with
                {
                    CommittedWake = committedWake,
                    IsCommittedWakeLocked = true,
                    MorningReleased = true,
                },
                observations);
        }

        if (state.NightDate == policy.Window.NightDate
            && string.Equals(
                state.RuleFingerprint,
                compiled.Fingerprint,
                StringComparison.Ordinal)
            && !RuleStatesCorrespondToCompiledRules(state.RuleStates, compiled))
        {
            return Degraded(
                state,
                observations,
                ProcessProtectionHealthCode.InvalidPersistedState);
        }

        bool epochChanged = state.ObserverContinuityEpoch is not null
            && !string.Equals(
                state.ObserverContinuityEpoch,
                context.ObserverContinuityEpoch,
                StringComparison.Ordinal);
        bool trustRecovered = state.ObserverContinuityEpoch is not null
            && !state.CreationTimelineTrusted
            && context.CreationTimelineTrusted;
        bool isNewNight = state.NightDate is null
            || policy.Window.NightDate > state.NightDate;
        if (isNewNight)
        {
            state = NewState(compiled, effectiveTime, context);
            if (!context.CreationTimelineTrusted)
            {
                state = SeverCreationTrust(state, context, observations, batchKind);
            }
        }
        else if (!string.Equals(
                     state.RuleFingerprint,
                     compiled.Fingerprint,
                     StringComparison.Ordinal)
                 && state.RuleStates.Values.Any(rule => rule.IsSealed))
        {
            return Degraded(
                AdvanceDegradedPolicyState(
                    state with { LastEffectiveLogicalTime = effectiveTime },
                    policy),
                observations,
                ProcessProtectionHealthCode.SealedRuleMutation);
        }
        else if (epochChanged || trustRecovered)
        {
            state = ResetWithinNight(state, compiled, effectiveTime, context);
        }
        else if (!string.Equals(
                     state.RuleFingerprint,
                     compiled.Fingerprint,
                     StringComparison.Ordinal))
        {
            state = ResetWithinNight(state, compiled, effectiveTime, context);
        }

        state = state with
        {
            CommittedWake = committedWake,
            IsCommittedWakeLocked = committedWakeLocked,
        };

        if (!context.CreationTimelineTrusted)
        {
            ProcessGateState untrustedState = AdvanceOverrideEvidence(
                state with
            {
                LastEffectiveLogicalTime = effectiveTime,
            },
                policy);
            ImmutableArray<ProcessGateDecision> decisions = observations
                .Select(observation => ClassifyUntrusted(
                    observation,
                    context,
                    compiled,
                    untrustedState.TaintedInstances))
                .ToImmutableArray();
            return new(
                untrustedState,
                decisions,
                ProcessProtectionHealthCode.CreationTimelineUntrusted);
        }

        ImmutableDictionary<ProcessInstanceKey, ProcessKnownInstance> knownBefore =
            state.KnownInstances;
        ImmutableDictionary<ProcessInstanceKey, ProcessKnownInstance>.Builder known =
            batchKind == ProcessObservationBatchKind.AuthoritativeSnapshot
                ? ImmutableDictionary.CreateBuilder<ProcessInstanceKey, ProcessKnownInstance>()
                : state.KnownInstances.ToBuilder();
        ImmutableDictionary<ProcessInstanceKey, string>.Builder eligible =
            state.EligibleInstances.ToBuilder();
        ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Builder temporary =
            state.TemporaryInstances.ToBuilder();
        ImmutableHashSet<ProcessInstanceKey>.Builder tainted = CollectConservativeTaints(
                observations,
                state.KnownInstances,
                batchKind,
                state.TaintedInstances)
            .ToBuilder();

        PreprocessObservations(
            observations,
            state.KnownInstances,
            known,
            eligible,
            temporary,
            tainted);
        if (batchKind == ProcessObservationBatchKind.AuthoritativeSnapshot)
        {
            HashSet<ProcessInstanceKey> present = known.Keys.ToHashSet();
            RemoveAbsent(eligible, present);
            RemoveAbsent(temporary, present);
        }

        RemoveTaintedGrants(eligible, temporary, tainted);

        ImmutableDictionary<string, ProcessRuleGateState>.Builder ruleStates =
            state.RuleStates.ToBuilder();
        if (batchKind == ProcessObservationBatchKind.AuthoritativeSnapshot)
        {
            SealRules(
                observations,
                context,
                compiled,
                effectiveTime,
                ruleStates,
                eligible,
                tainted);
        }

        ProcessOverrideIdentity? declaredOverride = Identity(policy.ActiveOverride);
        ImmutableHashSet<ProcessOverrideIdentity>.Builder retired =
            state.RetiredOverrideIdentities.ToBuilder();
        ProcessOverrideIdentity? highWater = state.OverrideHighWater;
        ProcessOverrideIdentity? priorHighWater = highWater;
        ProcessOverrideIdentity? priorScope = state.TemporaryOverrideIdentity;
        bool declaredAccepted = TryObserveOverride(
            declaredOverride,
            IsExactEmergencyOverride(policy.ActiveOverride),
            ref highWater,
            retired);
        if (priorHighWater is { } displaced
            && (declaredOverride is null
                || declaredOverride != displaced && !declaredAccepted))
        {
            retired.Add(displaced);
        }

        if (declaredOverride is { } declared && effectiveTime >= declared.EndsAtUtc)
        {
            retired.Add(declared);
        }

        ProcessOverrideIdentity? activeOverride = declaredAccepted && IsActiveOverride(
            policy,
            declaredOverride,
            effectiveTime,
            retired)
            ? declaredOverride
            : null;
        if (priorScope is { } prior && activeOverride != prior)
        {
            temporary.Clear();
            retired.Add(prior);
        }

        if (activeOverride is null)
        {
            temporary.Clear();
        }
        else
        {
            RemoveWrongOverrideScope(temporary, activeOverride);
        }

        DateTimeOffset? baseline = state.PreOverrideBaselineObservedAtUtc;
        if (baseline is null && policy.ActiveOverride is null)
        {
            baseline = effectiveTime;
        }

        ProcessOverrideIdentity? capturedRescue = state.CapturedTeamRescueOverride;
        if (activeOverride is { Kind: DesktopOverrideKind.TeamRescue } rescue)
        {
            if (batchKind == ProcessObservationBatchKind.AuthoritativeSnapshot
                && capturedRescue != rescue)
            {
                bool hasContinuousBaseline = baseline is { } observedAt
                    && observedAt <= rescue.RequestedAtUtc
                    && string.Equals(
                        state.ObserverContinuityEpoch,
                        context.ObserverContinuityEpoch,
                        StringComparison.Ordinal);
                if (hasContinuousBaseline)
                {
                    CaptureTeamRescueGames(
                        observations,
                        knownBefore,
                        context,
                        compiled,
                        effectiveTime,
                        policy.ActiveOverride!,
                        rescue,
                        eligible,
                        temporary,
                        tainted);
                }

                capturedRescue = rescue;
            }

            GrantTeamRescueVoiceRoots(
                observations,
                context,
                compiled,
                effectiveTime,
                policy.ActiveOverride!,
                rescue,
                eligible,
                temporary,
                tainted);
        }
        else if (activeOverride is { } broadOverride)
        {
            GrantBroadOverrideRoots(
                observations,
                context,
                compiled,
                effectiveTime,
                broadOverride,
                eligible,
                temporary,
                tainted);
        }

        RetainOnlyRootGrants(eligible, temporary, known, compiled, activeOverride);
        ImmutableDictionary<ProcessInstanceKey, LineageResult> lineage = ResolveAllHelpers(
            context,
            compiled,
            effectiveTime,
            known,
            eligible,
            temporary,
            tainted,
            activeOverride);
        foreach ((ProcessInstanceKey key, LineageResult result) in lineage)
        {
            if (result.Grant == LineageGrant.Eligible && result.RuleId is { } eligibleRule)
            {
                eligible[key] = eligibleRule;
            }
            else if (result.Grant == LineageGrant.Temporary
                     && result.RuleId is { } temporaryRule
                     && activeOverride is { } scope)
            {
                temporary[key] = new(temporaryRule, scope);
            }
        }

        ProcessGateState nextState = state with
        {
            NightDate = policy.Window.NightDate,
            LastEffectiveLogicalTime = effectiveTime,
            CommittedWake = committedWake,
            IsCommittedWakeLocked = committedWakeLocked
                || ruleStates.Values.Any(rule => rule.IsSealed),
            RuleFingerprint = compiled.Fingerprint,
            RuleStates = ruleStates.ToImmutable(),
            KnownInstances = known.ToImmutable(),
            EligibleInstances = eligible.ToImmutable(),
            TemporaryInstances = temporary.ToImmutable(),
            TaintedInstances = tainted.ToImmutable(),
            TemporaryOverrideIdentity = activeOverride,
            CapturedTeamRescueOverride = capturedRescue,
            OverrideHighWater = highWater,
            RetiredOverrideIdentities = retired.ToImmutable(),
            ObserverContinuityEpoch = context.ObserverContinuityEpoch,
            PreOverrideBaselineObservedAtUtc = baseline,
            CreationTimelineTrusted = true,
        };

        ImmutableArray<ProcessGateDecision>.Builder output =
            ImmutableArray.CreateBuilder<ProcessGateDecision>(observations.Count);
        foreach (ProcessObservation observation in observations)
        {
            output.Add(Classify(
                nextState,
                context,
                compiled,
                observation,
                lineage,
                policy.ActiveOverride));
        }

        return new(
            nextState,
            output.MoveToImmutable(),
            ProcessProtectionHealthCode.Healthy);
    }

    private static void SealRules(
        IReadOnlyList<ProcessObservation> observations,
        ProcessGateContext context,
        CompiledPolicy compiled,
        DateTimeOffset effectiveTime,
        ImmutableDictionary<string, ProcessRuleGateState>.Builder ruleStates,
        ImmutableDictionary<ProcessInstanceKey, string>.Builder eligible,
        IEnumerable<ProcessInstanceKey> tainted)
    {
        foreach (CompiledRule rule in compiled.Rules.Values)
        {
            ProcessRuleGateState state = ruleStates[rule.Id];
            if (state.IsSealed || effectiveTime < rule.CutoffUtc)
            {
                continue;
            }

            ruleStates[rule.Id] = state with { IsSealed = true };
            foreach (ProcessObservation observation in observations)
            {
                if (!TryGetUsableIdentity(
                        observation,
                        context,
                        tainted,
                        out ObservedProcessIdentity identity,
                        out _)
                    || !string.Equals(
                        identity.ExecutablePath,
                        rule.RootPath,
                        StringComparison.OrdinalIgnoreCase)
                    || identity.CreationInstantUtc > effectiveTime
                    || identity.CreationInstantUtc > rule.CutoffUtc)
                {
                    continue;
                }

                eligible[identity.Key] = rule.Id;
            }
        }
    }

    private static void CaptureTeamRescueGames(
        IReadOnlyList<ProcessObservation> observations,
        IReadOnlyDictionary<ProcessInstanceKey, ProcessKnownInstance> knownBefore,
        ProcessGateContext context,
        CompiledPolicy compiled,
        DateTimeOffset effectiveTime,
        DesktopActiveOverrideDto activeDto,
        ProcessOverrideIdentity rescue,
        IReadOnlyDictionary<ProcessInstanceKey, string> eligible,
        ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Builder temporary,
        IEnumerable<ProcessInstanceKey> tainted)
    {
        HashSet<string> allowed = activeDto.AllowedProcessIdentifiers.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        foreach (ProcessObservation observation in observations)
        {
            if (!TryGetRoot(
                    observation,
                    context,
                    compiled,
                    effectiveTime,
                    tainted,
                    out ObservedProcessIdentity identity,
                    out CompiledRule rule)
                || rule.Category != DesktopAppRuleCategory.Game
                || !allowed.Contains(rule.Id)
                || !knownBefore.ContainsKey(identity.Key)
                || eligible.ContainsKey(identity.Key)
                || identity.CreationInstantUtc <= rule.CutoffUtc
                || identity.CreationInstantUtc > rescue.StartsAtUtc)
            {
                continue;
            }

            temporary[identity.Key] = new(rule.Id, rescue);
        }
    }

    private static void GrantTeamRescueVoiceRoots(
        IReadOnlyList<ProcessObservation> observations,
        ProcessGateContext context,
        CompiledPolicy compiled,
        DateTimeOffset effectiveTime,
        DesktopActiveOverrideDto activeDto,
        ProcessOverrideIdentity rescue,
        IReadOnlyDictionary<ProcessInstanceKey, string> eligible,
        ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Builder temporary,
        IEnumerable<ProcessInstanceKey> tainted)
    {
        HashSet<string> allowed = activeDto.AllowedProcessIdentifiers.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        foreach (ProcessObservation observation in observations)
        {
            if (!TryGetRoot(
                    observation,
                    context,
                    compiled,
                    effectiveTime,
                    tainted,
                    out ObservedProcessIdentity identity,
                    out CompiledRule rule)
                || rule.Category != DesktopAppRuleCategory.Voice
                || !allowed.Contains(rule.Id)
                || eligible.ContainsKey(identity.Key)
                || identity.CreationInstantUtc <= rule.CutoffUtc)
            {
                continue;
            }

            temporary[identity.Key] = new(rule.Id, rescue);
        }
    }

    private static void GrantBroadOverrideRoots(
        IReadOnlyList<ProcessObservation> observations,
        ProcessGateContext context,
        CompiledPolicy compiled,
        DateTimeOffset effectiveTime,
        ProcessOverrideIdentity activeOverride,
        IReadOnlyDictionary<ProcessInstanceKey, string> eligible,
        ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Builder temporary,
        IEnumerable<ProcessInstanceKey> tainted)
    {
        foreach (ProcessObservation observation in observations)
        {
            if (!TryGetRoot(
                    observation,
                    context,
                    compiled,
                    effectiveTime,
                    tainted,
                    out ObservedProcessIdentity identity,
                    out CompiledRule rule)
                || eligible.ContainsKey(identity.Key)
                || identity.CreationInstantUtc <= rule.CutoffUtc)
            {
                continue;
            }

            temporary[identity.Key] = new(rule.Id, activeOverride);
        }
    }

    private static ImmutableDictionary<ProcessInstanceKey, LineageResult> ResolveAllHelpers(
        ProcessGateContext context,
        CompiledPolicy compiled,
        DateTimeOffset effectiveTime,
        IReadOnlyDictionary<ProcessInstanceKey, ProcessKnownInstance> known,
        IReadOnlyDictionary<ProcessInstanceKey, string> eligible,
        IReadOnlyDictionary<ProcessInstanceKey, TemporaryProcessGrant> temporary,
        IEnumerable<ProcessInstanceKey> tainted,
        ProcessOverrideIdentity? activeOverride)
    {
        HashSet<ProcessInstanceKey> taintedSet = tainted.ToHashSet();
        Dictionary<ProcessInstanceKey, LineageResult> resolved = [];
        Dictionary<ProcessInstanceKey, HelperDependency> pending = [];
        Dictionary<ProcessInstanceKey, List<ProcessInstanceKey>> dependents = [];
        Queue<ProcessInstanceKey> completed = [];
        foreach ((ProcessInstanceKey key, ProcessKnownInstance instance) in known)
        {
            if (!compiled.PathsByPath.TryGetValue(
                    instance.Identity.ExecutablePath,
                    out PathBinding? binding)
                || binding.IsRoot)
            {
                continue;
            }

            CompiledRule expectedRule = binding.Rule;
            LineageResult? terminal = null;
            ProcessInstanceKey? helperParent = null;
            if (!IsUsableKnownIdentity(instance.Identity, context)
                || taintedSet.Contains(key))
            {
                terminal = LineageResult.Fail(ProcessGateReason.TaintedIdentity);
            }
            else if (instance.Identity.CreationInstantUtc > effectiveTime)
            {
                terminal = LineageResult.Fail(
                    ProcessGateReason.CreationInstantAfterEffectiveTime);
            }
            else if (instance.Parent.Kind == ParentLinkKind.Unknown)
            {
                terminal = LineageResult.Fail(ProcessGateReason.UnknownParent);
            }
            else if (instance.Parent.Kind != ParentLinkKind.Exact
                     || instance.Parent.ExactParent is not { } parentKey)
            {
                terminal = LineageResult.Fail(ProcessGateReason.MissingExactParent);
            }
            else if (taintedSet.Contains(parentKey))
            {
                terminal = LineageResult.Fail(ProcessGateReason.TaintedIdentity);
            }
            else if (!known.TryGetValue(parentKey, out ProcessKnownInstance? parent))
            {
                terminal = LineageResult.Fail(ProcessGateReason.MissingExactParent);
            }
            else if (!IsUsableKnownIdentity(parent.Identity, context))
            {
                terminal = LineageResult.Fail(ProcessGateReason.WrongUserOrSession);
            }
            else if (parent.Identity.CreationInstantUtc > effectiveTime)
            {
                terminal = LineageResult.Fail(
                    ProcessGateReason.CreationInstantAfterEffectiveTime);
            }
            else if (parent.Identity.CreationInstantUtc > instance.Identity.CreationInstantUtc)
            {
                terminal = LineageResult.Fail(ProcessGateReason.ParentCreatedAfterChild);
            }
            else if (!compiled.PathsByPath.TryGetValue(
                         parent.Identity.ExecutablePath,
                         out PathBinding? parentBinding))
            {
                terminal = LineageResult.Fail(ProcessGateReason.NonAllowlistedAncestor);
            }
            else if (!string.Equals(
                         parentBinding.Rule.Id,
                         expectedRule.Id,
                         StringComparison.OrdinalIgnoreCase))
            {
                terminal = LineageResult.Fail(ProcessGateReason.CrossRuleParent);
            }
            else if (!parentBinding.IsRoot)
            {
                helperParent = parentKey;
            }
            else if (eligible.TryGetValue(parentKey, out string? eligibleRule)
                     && string.Equals(
                         eligibleRule,
                         expectedRule.Id,
                         StringComparison.OrdinalIgnoreCase))
            {
                terminal = LineageResult.Eligible(expectedRule.Id);
            }
            else if (activeOverride is { } scope
                     && temporary.TryGetValue(parentKey, out TemporaryProcessGrant? grant)
                     && grant.OverrideIdentity == scope
                     && string.Equals(
                         grant.RuleId,
                         expectedRule.Id,
                         StringComparison.OrdinalIgnoreCase))
            {
                terminal = LineageResult.Temporary(expectedRule.Id);
            }
            else
            {
                terminal = LineageResult.Fail(ProcessGateReason.NonAllowlistedAncestor);
            }

            if (terminal is not null)
            {
                resolved[key] = terminal;
                completed.Enqueue(key);
            }
            else
            {
                pending[key] = new(expectedRule.Id, helperParent!.Value);
                if (!dependents.TryGetValue(helperParent.Value, out List<ProcessInstanceKey>? children))
                {
                    children = [];
                    dependents.Add(helperParent.Value, children);
                }

                children.Add(key);
            }
        }

        while (completed.TryDequeue(out ProcessInstanceKey completedParent))
        {
            if (!dependents.TryGetValue(completedParent, out List<ProcessInstanceKey>? children))
            {
                continue;
            }

            LineageResult parentResult = resolved[completedParent];
            foreach (ProcessInstanceKey child in children)
            {
                if (!pending.TryGetValue(child, out HelperDependency? dependency)
                    || resolved.ContainsKey(child))
                {
                    continue;
                }

                LineageResult childResult = parentResult.Grant switch
                {
                    LineageGrant.Eligible => LineageResult.Eligible(dependency.RuleId),
                    LineageGrant.Temporary => LineageResult.Temporary(dependency.RuleId),
                    _ => LineageResult.Fail(parentResult.Reason),
                };
                resolved[child] = childResult;
                completed.Enqueue(child);
            }
        }

        foreach (ProcessInstanceKey unresolved in pending.Keys)
        {
            resolved.TryAdd(
                unresolved,
                LineageResult.Fail(ProcessGateReason.ParentCycle));
        }

        return resolved.ToImmutableDictionary();
    }

    private static ProcessGateDecision Classify(
        ProcessGateState state,
        ProcessGateContext context,
        CompiledPolicy compiled,
        ProcessObservation observation,
        IReadOnlyDictionary<ProcessInstanceKey, LineageResult> lineage,
        DesktopActiveOverrideDto? activeOverrideDto)
    {
        if (!TryGetUsableIdentity(
                observation,
                context,
                state.TaintedInstances,
                out ObservedProcessIdentity identity,
                out ProcessGateReason invalidReason))
        {
            return Decision(
                observation,
                null,
                null,
                ProcessGateDisposition.AllowFailOpen,
                invalidReason);
        }

        if (!compiled.PathsByPath.TryGetValue(identity.ExecutablePath, out PathBinding? binding))
        {
            return Decision(
                observation,
                null,
                null,
                ProcessGateDisposition.AllowUnrestricted,
                ProcessGateReason.UnconfiguredPath);
        }

        CompiledRule rule = binding.Rule;
        if (identity.CreationInstantUtc > state.LastEffectiveLogicalTime)
        {
            return Decision(
                observation,
                rule.Id,
                rule.CutoffUtc,
                ProcessGateDisposition.AllowFailOpen,
                ProcessGateReason.CreationInstantAfterEffectiveTime);
        }

        if (!binding.IsRoot)
        {
            if (state.EligibleInstances.TryGetValue(identity.Key, out string? eligibleRule)
                && string.Equals(eligibleRule, rule.Id, StringComparison.OrdinalIgnoreCase))
            {
                return Decision(
                    observation,
                    rule.Id,
                    rule.CutoffUtc,
                    ProcessGateDisposition.AllowEligible,
                    ProcessGateReason.EligibleHelper);
            }

            if (state.TemporaryInstances.TryGetValue(identity.Key, out TemporaryProcessGrant? temporary)
                && state.TemporaryOverrideIdentity == temporary.OverrideIdentity)
            {
                return Decision(
                    observation,
                    rule.Id,
                    rule.CutoffUtc,
                    ProcessGateDisposition.AllowTemporaryOverride,
                    ProcessGateReason.TemporaryOverrideHelper);
            }

            ProcessGateReason reason = lineage.TryGetValue(identity.Key, out LineageResult? result)
                ? result.Reason
                : ProcessGateReason.MissingExactParent;
            return Decision(
                observation,
                rule.Id,
                rule.CutoffUtc,
                ProcessGateDisposition.AllowFailOpen,
                reason);
        }

        if (state.EligibleInstances.TryGetValue(identity.Key, out string? rootEligibleRule)
            && string.Equals(rootEligibleRule, rule.Id, StringComparison.OrdinalIgnoreCase))
        {
            return Decision(
                observation,
                rule.Id,
                rule.CutoffUtc,
                ProcessGateDisposition.AllowEligible,
                ProcessGateReason.EligibleRoot);
        }

        if (state.TemporaryInstances.TryGetValue(identity.Key, out TemporaryProcessGrant? rootTemporary)
            && state.TemporaryOverrideIdentity == rootTemporary.OverrideIdentity)
        {
            return Decision(
                observation,
                rule.Id,
                rule.CutoffUtc,
                ProcessGateDisposition.AllowTemporaryOverride,
                ProcessGateReason.TemporaryOverrideRoot);
        }

        if (state.TemporaryOverrideIdentity is { Kind: DesktopOverrideKind.TeamRescue } rescue
            && activeOverrideDto is not null
            && rule.Category == DesktopAppRuleCategory.Game
            && activeOverrideDto.AllowedProcessIdentifiers.Contains(
                rule.Id,
                StringComparer.OrdinalIgnoreCase)
            && identity.CreationInstantUtc > rule.CutoffUtc
            && identity.CreationInstantUtc <= rescue.StartsAtUtc)
        {
            ProcessGateReason rescueReason = state.CapturedTeamRescueOverride == rescue
                ? ProcessGateReason.TeamRescueRootNotCaptured
                : ProcessGateReason.TeamRescueAwaitingSnapshot;
            return Decision(
                observation,
                rule.Id,
                rule.CutoffUtc,
                ProcessGateDisposition.AllowFailOpen,
                rescueReason);
        }

        if (identity.CreationInstantUtc > rule.CutoffUtc)
        {
            return Decision(
                observation,
                rule.Id,
                rule.CutoffUtc,
                ProcessGateDisposition.BlockNewRoot,
                ProcessGateReason.NewRootAtOrAfterCutoff);
        }

        if (state.RuleStates[rule.Id].IsSealed)
        {
            return Decision(
                observation,
                rule.Id,
                rule.CutoffUtc,
                ProcessGateDisposition.AllowFailOpen,
                ProcessGateReason.PreCutoffRootNotInSealSnapshot);
        }

        if (state.LastEffectiveLogicalTime < rule.CutoffUtc)
        {
            return Decision(
                observation,
                rule.Id,
                rule.CutoffUtc,
                ProcessGateDisposition.AllowUnrestricted,
                ProcessGateReason.BeforeRuleCutoff);
        }

        return Decision(
            observation,
            rule.Id,
            rule.CutoffUtc,
            ProcessGateDisposition.AllowFailOpen,
            ProcessGateReason.PreCutoffRootAwaitingSealSnapshot);
    }

    private static ProcessGateDecision ClassifyUntrusted(
        ProcessObservation observation,
        ProcessGateContext context,
        CompiledPolicy compiled,
        IEnumerable<ProcessInstanceKey> tainted)
    {
        if (!TryGetUsableIdentity(
                observation,
                context,
                tainted,
                out ObservedProcessIdentity identity,
                out ProcessGateReason invalidReason))
        {
            return Decision(
                observation,
                null,
                null,
                ProcessGateDisposition.AllowFailOpen,
                invalidReason);
        }

        if (!compiled.PathsByPath.TryGetValue(identity.ExecutablePath, out PathBinding? binding))
        {
            return Decision(
                observation,
                null,
                null,
                ProcessGateDisposition.AllowUnrestricted,
                ProcessGateReason.UnconfiguredPath);
        }

        return Decision(
            observation,
            binding.Rule.Id,
            binding.Rule.CutoffUtc,
            ProcessGateDisposition.AllowFailOpen,
            ProcessGateReason.ProcessProtectionDegraded);
    }

    private static void PreprocessObservations(
        IReadOnlyList<ProcessObservation> observations,
        IReadOnlyDictionary<ProcessInstanceKey, ProcessKnownInstance> priorKnown,
        ImmutableDictionary<ProcessInstanceKey, ProcessKnownInstance>.Builder known,
        ImmutableDictionary<ProcessInstanceKey, string>.Builder eligible,
        ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Builder temporary,
        ImmutableHashSet<ProcessInstanceKey>.Builder tainted)
    {
        HashSet<ProcessInstanceKey> unknownSeenInBatch = [];
        foreach (ProcessObservation observation in observations)
        {
            if (observation.Identity is not { } identity)
            {
                continue;
            }

            if (observation.PidHint <= 0 || observation.PidHint != identity.Key.Pid)
            {
                Taint(identity.Key, eligible, temporary, tainted);
                continue;
            }

            known.TryGetValue(identity.Key, out ProcessKnownInstance? current);
            priorKnown.TryGetValue(identity.Key, out ProcessKnownInstance? persisted);
            if (current is not null && !SameIdentity(current.Identity, identity)
                || persisted is not null && !SameIdentity(persisted.Identity, identity))
            {
                Taint(identity.Key, eligible, temporary, tainted);
                continue;
            }

            if (current is not null
                && ParentLinksConflict(current.Parent, observation.Parent)
                || persisted is not null
                && ParentLinksConflict(persisted.Parent, observation.Parent))
            {
                Taint(identity.Key, eligible, temporary, tainted);
                continue;
            }

            if (observation.Parent.Kind == ParentLinkKind.Unknown)
            {
                unknownSeenInBatch.Add(identity.Key);
            }

            ParentLink effectiveParent = unknownSeenInBatch.Contains(identity.Key)
                ? ParentLink.Unknown
                : observation.Parent;
            known[identity.Key] = new(identity, effectiveParent);
        }
    }

    private static void Taint(
        ProcessInstanceKey key,
        ImmutableDictionary<ProcessInstanceKey, string>.Builder eligible,
        ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Builder temporary,
        ImmutableHashSet<ProcessInstanceKey>.Builder tainted)
    {
        tainted.Add(key);
        eligible.Remove(key);
        temporary.Remove(key);
    }

    private static ImmutableHashSet<ProcessInstanceKey> CollectConservativeTaints(
        IReadOnlyList<ProcessObservation> observations,
        IReadOnlyDictionary<ProcessInstanceKey, ProcessKnownInstance> priorKnown,
        ProcessObservationBatchKind batchKind,
        IEnumerable<ProcessInstanceKey> existingTaints)
    {
        ImmutableHashSet<ProcessInstanceKey>.Builder tainted =
            existingTaints.ToImmutableHashSet().ToBuilder();
        Dictionary<ProcessInstanceKey, ObservedProcessIdentity> identities = [];
        Dictionary<ProcessInstanceKey, BatchParentEvidence> parentEvidence = [];
        foreach (ProcessObservation observation in observations)
        {
            if (observation.Identity is not { } identity)
            {
                continue;
            }

            if (observation.PidHint <= 0 || observation.PidHint != identity.Key.Pid)
            {
                tainted.Add(identity.Key);
            }

            if (priorKnown.TryGetValue(identity.Key, out ProcessKnownInstance? prior)
                && (!SameIdentity(prior.Identity, identity)
                    || ParentLinksConflict(prior.Parent, observation.Parent)))
            {
                tainted.Add(identity.Key);
            }

            if (identities.TryGetValue(identity.Key, out ObservedProcessIdentity? seenIdentity)
                && !SameIdentity(seenIdentity, identity))
            {
                tainted.Add(identity.Key);
            }
            else
            {
                identities.TryAdd(identity.Key, identity);
            }

            if (!parentEvidence.TryGetValue(identity.Key, out BatchParentEvidence? evidence))
            {
                evidence = new();
                parentEvidence.Add(identity.Key, evidence);
            }

            evidence.Observe(observation.Parent);
            if (evidence.HasConflict)
            {
                tainted.Add(identity.Key);
            }
        }

        if (batchKind == ProcessObservationBatchKind.AuthoritativeSnapshot)
        {
            foreach (IGrouping<int, ProcessInstanceKey> pidGroup in observations
                         .Where(observation => observation.Identity is not null)
                         .Select(observation => observation.Identity!.Key)
                         .Distinct()
                         .GroupBy(key => key.Pid)
                         .Where(group => group.Skip(1).Any()))
            {
                tainted.UnionWith(pidGroup);
            }
        }

        return tainted.ToImmutable();
    }

    private static bool ParentLinksConflict(ParentLink left, ParentLink right) =>
        left.Kind == ParentLinkKind.Exact && right.Kind == ParentLinkKind.Exact
            ? left.ExactParent != right.ExactParent
            : left.Kind == ParentLinkKind.Exact && right.Kind == ParentLinkKind.None
              || left.Kind == ParentLinkKind.None && right.Kind == ParentLinkKind.Exact;

    private static bool SameIdentity(
        ObservedProcessIdentity left,
        ObservedProcessIdentity right) =>
        left.Key == right.Key
        && left.CreationInstantUtc.EqualsExact(right.CreationInstantUtc)
        && string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.UserSid, right.UserSid, StringComparison.Ordinal)
        && left.SessionId == right.SessionId;

    private static void RetainOnlyRootGrants(
        ImmutableDictionary<ProcessInstanceKey, string>.Builder eligible,
        ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Builder temporary,
        IReadOnlyDictionary<ProcessInstanceKey, ProcessKnownInstance> known,
        CompiledPolicy compiled,
        ProcessOverrideIdentity? activeOverride)
    {
        foreach (ProcessInstanceKey key in eligible.Keys.ToArray())
        {
            if (!known.TryGetValue(key, out ProcessKnownInstance? instance)
                || !compiled.PathsByPath.TryGetValue(instance.Identity.ExecutablePath, out PathBinding? binding)
                || !binding.IsRoot
                || !string.Equals(binding.Rule.Id, eligible[key], StringComparison.OrdinalIgnoreCase))
            {
                eligible.Remove(key);
            }
        }

        foreach (ProcessInstanceKey key in temporary.Keys.ToArray())
        {
            TemporaryProcessGrant grant = temporary[key];
            if (activeOverride is null
                || grant.OverrideIdentity != activeOverride
                || !known.TryGetValue(key, out ProcessKnownInstance? instance)
                || !compiled.PathsByPath.TryGetValue(instance.Identity.ExecutablePath, out PathBinding? binding)
                || !binding.IsRoot
                || !string.Equals(binding.Rule.Id, grant.RuleId, StringComparison.OrdinalIgnoreCase))
            {
                temporary.Remove(key);
            }
        }
    }

    private static void RemoveAbsent<T>(
        ImmutableDictionary<ProcessInstanceKey, T>.Builder values,
        IReadOnlySet<ProcessInstanceKey> present)
    {
        foreach (ProcessInstanceKey key in values.Keys.ToArray())
        {
            if (!present.Contains(key))
            {
                values.Remove(key);
            }
        }
    }

    private static void RemoveTaintedGrants(
        ImmutableDictionary<ProcessInstanceKey, string>.Builder eligible,
        ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Builder temporary,
        IEnumerable<ProcessInstanceKey> tainted)
    {
        foreach (ProcessInstanceKey key in tainted)
        {
            eligible.Remove(key);
            temporary.Remove(key);
        }
    }

    private static void RemoveWrongOverrideScope(
        ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Builder temporary,
        ProcessOverrideIdentity activeOverride)
    {
        foreach (ProcessInstanceKey key in temporary.Keys.ToArray())
        {
            if (temporary[key].OverrideIdentity != activeOverride)
            {
                temporary.Remove(key);
            }
        }
    }

    private static bool TryGetRoot(
        ProcessObservation observation,
        ProcessGateContext context,
        CompiledPolicy compiled,
        DateTimeOffset effectiveTime,
        IEnumerable<ProcessInstanceKey> tainted,
        out ObservedProcessIdentity identity,
        out CompiledRule rule)
    {
        if (TryGetUsableIdentity(
                observation,
                context,
                tainted,
                out identity,
                out _)
            && identity.CreationInstantUtc <= effectiveTime
            && compiled.PathsByPath.TryGetValue(identity.ExecutablePath, out PathBinding? binding)
            && binding.IsRoot)
        {
            rule = binding.Rule;
            return true;
        }

        identity = null!;
        rule = null!;
        return false;
    }

    private static bool TryGetUsableIdentity(
        ProcessObservation observation,
        ProcessGateContext context,
        IEnumerable<ProcessInstanceKey> tainted,
        out ObservedProcessIdentity identity,
        out ProcessGateReason reason)
    {
        if (observation.Identity is not { } completeIdentity)
        {
            identity = null!;
            reason = ProcessGateReason.MissingIdentity;
            return false;
        }

        identity = completeIdentity;
        if (tainted.Contains(identity.Key))
        {
            reason = ProcessGateReason.TaintedIdentity;
            return false;
        }

        if (observation.PidHint <= 0
            || observation.PidHint != identity.Key.Pid
            || identity.CreationInstantUtc.Offset != TimeSpan.Zero
            || identity.CreationInstantUtc.UtcTicks != identity.Key.CreationUtcTicks
            || !IsCanonicalExecutablePath(identity.ExecutablePath)
            || string.IsNullOrWhiteSpace(identity.UserSid)
            || identity.SessionId < 0)
        {
            reason = ProcessGateReason.InvalidIdentity;
            return false;
        }

        if (!string.Equals(identity.UserSid, context.InteractiveUserSid, StringComparison.Ordinal)
            || identity.SessionId != context.InteractiveSessionId)
        {
            reason = ProcessGateReason.WrongUserOrSession;
            return false;
        }

        reason = default;
        return true;
    }

    private static bool IsUsableKnownIdentity(
        ObservedProcessIdentity identity,
        ProcessGateContext context) =>
        identity.CreationInstantUtc.Offset == TimeSpan.Zero
        && identity.CreationInstantUtc.UtcTicks == identity.Key.CreationUtcTicks
        && IsCanonicalExecutablePath(identity.ExecutablePath)
        && string.Equals(identity.UserSid, context.InteractiveUserSid, StringComparison.Ordinal)
        && identity.SessionId == context.InteractiveSessionId;

    private static bool IsCanonicalExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsValidPersistedState(ProcessGateState state)
    {
        if (state.RuleStates is null
            || state.KnownInstances is null
            || state.EligibleInstances is null
            || state.TemporaryInstances is null
            || state.TaintedInstances is null
            || state.RetiredOverrideIdentities is null)
        {
            return false;
        }

        if (state.NightDate is null)
        {
            if (state.LastEffectiveLogicalTime is not null
                || state.CommittedWake is not null
                || state.IsCommittedWakeLocked
                || state.MorningReleased
                || state.RuleStates.Count != 0
                || state.KnownInstances.Count != 0
                || state.EligibleInstances.Count != 0
                || state.TemporaryInstances.Count != 0)
            {
                return false;
            }
        }
        else if (state.LastEffectiveLogicalTime is null
                 || state.CommittedWake is null)
        {
            return false;
        }

        bool anySealed = false;
        foreach ((string key, ProcessRuleGateState? rule) in state.RuleStates)
        {
            if (string.IsNullOrWhiteSpace(key)
                || rule is null
                || string.IsNullOrWhiteSpace(rule.RuleId)
                || !string.Equals(key, rule.RuleId, StringComparison.OrdinalIgnoreCase)
                || rule.CutoffUtc == default)
            {
                return false;
            }

            anySealed |= rule.IsSealed;
        }

        if (state.RuleStates.Count != 0
            && string.IsNullOrWhiteSpace(state.RuleFingerprint))
        {
            return false;
        }

        if ((state.IsCommittedWakeLocked || anySealed)
            && state.CommittedWake is null)
        {
            return false;
        }

        if (anySealed && !state.IsCommittedWakeLocked)
        {
            return false;
        }

        foreach ((ProcessInstanceKey key, ProcessKnownInstance? known) in state.KnownInstances)
        {
            if (known?.Identity is null
                || known.Identity.Key != key)
            {
                return false;
            }
        }

        foreach ((ProcessInstanceKey key, string? ruleId) in state.EligibleInstances)
        {
            if (string.IsNullOrWhiteSpace(ruleId)
                || !state.KnownInstances.ContainsKey(key)
                || !state.RuleStates.ContainsKey(ruleId))
            {
                return false;
            }
        }

        foreach ((ProcessInstanceKey key, TemporaryProcessGrant? grant) in state.TemporaryInstances)
        {
            if (grant is null
                || string.IsNullOrWhiteSpace(grant.RuleId)
                || grant.OverrideIdentity is null
                || !state.KnownInstances.ContainsKey(key)
                || !state.RuleStates.ContainsKey(grant.RuleId)
                || state.TemporaryOverrideIdentity is null
                || grant.OverrideIdentity != state.TemporaryOverrideIdentity)
            {
                return false;
            }
        }

        foreach (ProcessOverrideIdentity? identity in state.RetiredOverrideIdentities)
        {
            if (identity is null)
            {
                return false;
            }
        }

        return true;
    }

    private static bool RuleStatesCorrespondToCompiledRules(
        IReadOnlyDictionary<string, ProcessRuleGateState> persisted,
        CompiledPolicy compiled)
    {
        if (persisted.Count != compiled.Rules.Count)
        {
            return false;
        }

        foreach (CompiledRule compiledRule in compiled.Rules.Values)
        {
            ProcessRuleGateState? matched = null;
            int matchCount = 0;
            foreach ((string key, ProcessRuleGateState ruleState) in persisted)
            {
                if (!string.Equals(
                        key,
                        compiledRule.Id,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matchCount++;
                matched = ruleState;
            }

            if (matchCount != 1
                || matched is null
                || !string.Equals(
                    matched.RuleId,
                    compiledRule.Id,
                    StringComparison.OrdinalIgnoreCase)
                || matched.CutoffUtc != compiledRule.CutoffUtc)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCompile(
        ProcessGateContext context,
        out CompiledPolicy? compiled,
        out ProcessProtectionHealthCode health)
    {
        compiled = null;
        DesktopPolicySnapshotDto? policy = context.Policy;
        if (policy is null
            || policy.Window is null
            || !IsUsableWindow(policy.Window)
            || policy.AppRules is null
            || !policy.EnforcementEnabled
            || policy.IsDegraded
            || string.IsNullOrWhiteSpace(context.InteractiveUserSid)
            || context.InteractiveSessionId < 0
            || string.IsNullOrWhiteSpace(context.ObserverContinuityEpoch))
        {
            health = ProcessProtectionHealthCode.InvalidContext;
            return false;
        }

        Dictionary<string, CompiledRule> rules = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PathBinding> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopAppRuleDto? dto in policy.AppRules)
        {
            if (!TryCompileRule(dto, policy.Window.Lock, out CompiledRule? maybeRule)
                || maybeRule is null)
            {
                health = ProcessProtectionHealthCode.InvalidRule;
                return false;
            }

            CompiledRule rule = maybeRule;
            if (!rules.TryAdd(rule.Id, rule))
            {
                health = ProcessProtectionHealthCode.InvalidRule;
                return false;
            }

            if (!paths.TryAdd(rule.RootPath, new(rule, true)))
            {
                health = ProcessProtectionHealthCode.RulePathAmbiguity;
                return false;
            }

            foreach (string helper in rule.HelperPaths)
            {
                if (!paths.TryAdd(helper, new(rule, false)))
                {
                    health = ProcessProtectionHealthCode.RulePathAmbiguity;
                    return false;
                }
            }
        }

        string fingerprint = Fingerprint(rules.Values);
        compiled = new(
            policy.Window.NightDate,
            policy.Window.Wake,
            rules.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            paths.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            fingerprint);
        health = ProcessProtectionHealthCode.Healthy;
        return true;
    }

    private static bool TryCreateTerminalMorningState(
        ProcessGateState state,
        ProcessGateContext context,
        DesktopPolicySnapshotDto? policy,
        out ProcessGateState? released)
    {
        released = null;
        if (policy?.Window is not { } window
            || state.NightDate is { } currentNight
            && window.NightDate < currentNight)
        {
            return false;
        }

        bool sameNight = state.NightDate == window.NightDate;
        bool usableWindow = IsUsableWindow(window);
        bool wakeLocked = state.IsCommittedWakeLocked
            || state.RuleStates.Values.Any(rule => rule.IsSealed);
        DateTimeOffset committedWake;
        if (sameNight && wakeLocked)
        {
            if (state.CommittedWake is not { } lockedWake)
            {
                return false;
            }

            committedWake = lockedWake;
        }
        else if (usableWindow)
        {
            committedWake = window.Wake;
        }
        else if (sameNight && state.CommittedWake is { } priorWake)
        {
            committedWake = priorWake;
        }
        else
        {
            return false;
        }

        DateTimeOffset effectiveTime = sameNight
            ? Max(state.LastEffectiveLogicalTime, policy.EvaluatedAt)
            : policy.EvaluatedAt;
        if (effectiveTime < committedWake)
        {
            return false;
        }

        released = ProcessGateState.Empty with
        {
            NightDate = window.NightDate,
            LastEffectiveLogicalTime = effectiveTime,
            CommittedWake = committedWake,
            IsCommittedWakeLocked = true,
            ObserverContinuityEpoch = context.ObserverContinuityEpoch,
            CreationTimelineTrusted = context.CreationTimelineTrusted,
            MorningReleased = true,
        };
        return true;
    }

    private static bool IsUsableWindow(DesktopNightWindowDto window)
    {
        if (!(window.ProtectedStart < window.LastStart
            && window.LastStart < window.Lock
            && window.Lock < window.LightsOut
            && window.LightsOut < window.Wake))
        {
            return false;
        }

        DateOnly nextDate;
        try
        {
            nextDate = window.NightDate.AddDays(1);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        DateOnly protectedDate = DateOnly.FromDateTime(window.ProtectedStart.DateTime);
        DateOnly lastStartDate = DateOnly.FromDateTime(window.LastStart.DateTime);
        DateOnly lockDate = DateOnly.FromDateTime(window.Lock.DateTime);
        DateOnly lightsOutDate = DateOnly.FromDateTime(window.LightsOut.DateTime);
        DateOnly wakeDate = DateOnly.FromDateTime(window.Wake.DateTime);
        return protectedDate == window.NightDate
            && (lastStartDate == window.NightDate || lastStartDate == nextDate)
            && (lockDate == window.NightDate || lockDate == nextDate)
            && (lightsOutDate == window.NightDate || lightsOutDate == nextDate)
            && wakeDate == nextDate;
    }

    private static bool TryCompileRule(
        DesktopAppRuleDto? dto,
        DateTimeOffset lockAt,
        out CompiledRule? compiled)
    {
        compiled = null;
        if (dto is null
            || !dto.IsConfigured
            || dto.RootExecutablePath is null
            || dto.HelperExecutablePaths is null
            || dto.Category is not { } category)
        {
            return false;
        }

        AppRuleCategory coreCategory = category switch
        {
            DesktopAppRuleCategory.Game => AppRuleCategory.Game,
            DesktopAppRuleCategory.Voice => AppRuleCategory.Voice,
            _ => (AppRuleCategory)(-1),
        };

        try
        {
            if (!Win32ExecutablePathCanonicalizer.TryCanonicalize(
                    dto.RootExecutablePath,
                    out string canonicalRoot))
            {
                return false;
            }

            List<string> canonicalHelperList = [];
            HashSet<string> canonicalHelperSet = new(StringComparer.OrdinalIgnoreCase);
            foreach (string? helper in dto.HelperExecutablePaths)
            {
                if (!Win32ExecutablePathCanonicalizer.TryCanonicalize(
                        helper,
                        out string canonicalHelper)
                    || string.Equals(
                        canonicalRoot,
                        canonicalHelper,
                        StringComparison.OrdinalIgnoreCase)
                    || !canonicalHelperSet.Add(canonicalHelper))
                {
                    return false;
                }

                canonicalHelperList.Add(canonicalHelper);
            }

            string[] canonicalHelpers = canonicalHelperList.ToArray();
            AppRule reconstructed = new(
                dto.Id,
                canonicalRoot,
                canonicalHelpers,
                coreCategory,
                dto.SessionMinutes);
            if (!string.Equals(reconstructed.Id, dto.Id, StringComparison.Ordinal)
                || !string.Equals(
                    reconstructed.RootExecutablePath,
                    canonicalRoot,
                    StringComparison.OrdinalIgnoreCase)
                || !reconstructed.HelperExecutablePaths.SequenceEqual(
                    canonicalHelpers,
                    StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            compiled = new(
                reconstructed.Id,
                canonicalRoot,
                canonicalHelpers.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
                category,
                reconstructed.SessionMinutes,
                lockAt - TimeSpan.FromMinutes(reconstructed.SessionMinutes));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }

    private static string Fingerprint(IEnumerable<CompiledRule> rules)
    {
        StringBuilder builder = new();
        foreach (CompiledRule rule in rules.OrderBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(rule.Id.ToUpperInvariant()).Append('|')
                .Append(rule.RootPath.ToUpperInvariant()).Append('|')
                .Append((int)rule.Category).Append('|')
                .Append(rule.SessionMinutes.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(rule.CutoffUtc.UtcTicks.ToString(CultureInfo.InvariantCulture));
            foreach (string helper in rule.HelperPaths.Order(StringComparer.OrdinalIgnoreCase))
            {
                builder.Append('|').Append(helper.ToUpperInvariant());
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static ProcessGateState NewState(
        CompiledPolicy policy,
        DateTimeOffset effectiveTime,
        ProcessGateContext context)
    {
        ImmutableDictionary<string, ProcessRuleGateState> ruleStates = policy.Rules.Values
            .ToImmutableDictionary(
                rule => rule.Id,
                rule => new ProcessRuleGateState(rule.Id, rule.CutoffUtc, false),
                StringComparer.OrdinalIgnoreCase);
        return ProcessGateState.Empty with
        {
            NightDate = policy.NightDate,
            LastEffectiveLogicalTime = effectiveTime,
            CommittedWake = policy.Wake,
            RuleFingerprint = policy.Fingerprint,
            RuleStates = ruleStates,
            ObserverContinuityEpoch = context.ObserverContinuityEpoch,
            CreationTimelineTrusted = context.CreationTimelineTrusted,
        };
    }

    private static ProcessGateState ResetWithinNight(
        ProcessGateState prior,
        CompiledPolicy policy,
        DateTimeOffset effectiveTime,
        ProcessGateContext context) =>
        NewState(policy, effectiveTime, context) with
        {
            TaintedInstances = prior.TaintedInstances,
            TemporaryOverrideIdentity = prior.TemporaryOverrideIdentity,
            OverrideHighWater = prior.OverrideHighWater,
            RetiredOverrideIdentities = prior.RetiredOverrideIdentities,
        };

    private static ProcessGateState AdvanceDegradedPolicyState(
        ProcessGateState state,
        DesktopPolicySnapshotDto? policy)
    {
        if (policy?.Window is not { } window
            || state.NightDate != window.NightDate)
        {
            return state;
        }

        return AdvanceOverrideEvidence(
            state with
        {
            LastEffectiveLogicalTime = Max(state.LastEffectiveLogicalTime, policy.EvaluatedAt),
        },
            policy);
    }

    private static ProcessGateState SeverCreationTrust(
        ProcessGateState state,
        ProcessGateContext context,
        IReadOnlyList<ProcessObservation> observations,
        ProcessObservationBatchKind batchKind)
    {
        ImmutableHashSet<ProcessInstanceKey>.Builder tainted = CollectConservativeTaints(
                observations,
                state.KnownInstances,
                batchKind,
                state.TaintedInstances)
            .ToBuilder();
        tainted.UnionWith(state.KnownInstances.Keys);
        foreach (ProcessObservation observation in observations)
        {
            if (observation.Identity is { } identity)
            {
                tainted.Add(identity.Key);
            }
        }

        ImmutableHashSet<ProcessOverrideIdentity>.Builder retired =
            state.RetiredOverrideIdentities.ToBuilder();
        if (state.TemporaryOverrideIdentity is { } activeScope)
        {
            retired.Add(activeScope);
        }

        if (context.Policy?.Window is { } window
            && window.NightDate == state.NightDate
            && context.Policy.ActiveOverride is null
            && state.OverrideHighWater is { } disappeared)
        {
            retired.Add(disappeared);
        }

        return state with
        {
            KnownInstances = ImmutableDictionary<ProcessInstanceKey, ProcessKnownInstance>.Empty,
            EligibleInstances = ImmutableDictionary<ProcessInstanceKey, string>.Empty,
            TemporaryInstances = ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Empty,
            TemporaryOverrideIdentity = null,
            CapturedTeamRescueOverride = null,
            TaintedInstances = tainted.ToImmutable(),
            RetiredOverrideIdentities = retired.ToImmutable(),
            ObserverContinuityEpoch = context.ObserverContinuityEpoch,
            PreOverrideBaselineObservedAtUtc = null,
            CreationTimelineTrusted = false,
        };
    }

    private static ProcessGateState AdvanceOverrideEvidence(
        ProcessGateState state,
        DesktopPolicySnapshotDto? policy)
    {
        if (policy?.Window is not { } window
            || state.NightDate != window.NightDate)
        {
            return state;
        }

        ImmutableHashSet<ProcessOverrideIdentity>.Builder retired =
            state.RetiredOverrideIdentities.ToBuilder();
        ProcessOverrideIdentity? highWater = state.OverrideHighWater;
        ProcessOverrideIdentity? priorHighWater = highWater;
        ProcessOverrideIdentity? declared = Identity(policy.ActiveOverride);
        bool accepted = TryObserveOverride(
            declared,
            IsExactEmergencyOverride(policy.ActiveOverride),
            ref highWater,
            retired);
        DateTimeOffset effectiveTime = Max(
            state.LastEffectiveLogicalTime,
            policy.EvaluatedAt);
        if (priorHighWater is { } displaced
            && (declared is null || declared != displaced && !accepted))
        {
            retired.Add(displaced);
        }

        if (declared is { } candidate && effectiveTime >= candidate.EndsAtUtc)
        {
            retired.Add(candidate);
        }

        if (state.TemporaryOverrideIdentity is { } prior)
        {
            retired.Add(prior);
        }

        return state with
        {
            OverrideHighWater = highWater,
            RetiredOverrideIdentities = retired.ToImmutable(),
            TemporaryInstances = ImmutableDictionary<ProcessInstanceKey, TemporaryProcessGrant>.Empty,
            TemporaryOverrideIdentity = null,
            CapturedTeamRescueOverride = null,
        };
    }

    private static bool TryObserveOverride(
        ProcessOverrideIdentity? candidate,
        bool exactEmergency,
        ref ProcessOverrideIdentity? highWater,
        ImmutableHashSet<ProcessOverrideIdentity>.Builder retired)
    {
        if (candidate is null)
        {
            return false;
        }

        if (candidate.RequestedAtUtc > candidate.StartsAtUtc
            || candidate.StartsAtUtc >= candidate.EndsAtUtc)
        {
            retired.Add(candidate);
            return false;
        }

        if (retired.Contains(candidate))
        {
            return false;
        }

        if (highWater is null)
        {
            highWater = candidate;
            return true;
        }

        if (candidate == highWater)
        {
            return true;
        }

        if (exactEmergency && CanEmergencyPreempt(candidate, highWater, retired))
        {
            retired.Add(highWater);
            highWater = candidate;
            return true;
        }

        if (candidate.RequestedAtUtc >= highWater.EndsAtUtc
            && candidate.StartsAtUtc > highWater.StartsAtUtc
            && candidate.EndsAtUtc > highWater.EndsAtUtc)
        {
            retired.Add(highWater);
            highWater = candidate;
            return true;
        }

        retired.Add(candidate);
        return false;
    }

    private static bool CanEmergencyPreempt(
        ProcessOverrideIdentity candidate,
        ProcessOverrideIdentity highWater,
        ISet<ProcessOverrideIdentity> retired) =>
        candidate.Kind == DesktopOverrideKind.Emergency
        && candidate.RequestedAtUtc == candidate.StartsAtUtc
        && candidate.EndsAtUtc - candidate.StartsAtUtc == EmergencyDuration
        && candidate.RequestedAtUtc > highWater.RequestedAtUtc
        && candidate.RequestedAtUtc < highWater.EndsAtUtc
        && candidate.EndsAtUtc > highWater.EndsAtUtc
        && !retired.Contains(highWater);

    private static bool IsExactEmergencyOverride(DesktopActiveOverrideDto? value) =>
        value is
        {
            Kind: DesktopOverrideKind.Emergency,
            AllowedProcessIdentifiers.Count: 0,
        }
        && value.RequestedAtUtc == value.StartsAtUtc
        && value.EndsAtUtc - value.StartsAtUtc == EmergencyDuration;

    private static ProcessOverrideIdentity? Identity(DesktopActiveOverrideDto? value) =>
        value is null
            ? null
            : new(
                value.Kind,
                value.RequestedAtUtc,
                value.StartsAtUtc,
                value.EndsAtUtc);

    private static bool IsActiveOverride(
        DesktopPolicySnapshotDto policy,
        ProcessOverrideIdentity? identity,
        DateTimeOffset effectiveTime,
        IEnumerable<ProcessOverrideIdentity> retired) =>
        policy.Phase == DesktopNightPhase.OverrideActive
        && identity is { } active
        && active.RequestedAtUtc <= active.StartsAtUtc
        && active.StartsAtUtc < active.EndsAtUtc
        && effectiveTime >= active.StartsAtUtc
        && effectiveTime < active.EndsAtUtc
        && !retired.Contains(active);

    private static ProcessGateEvaluation MorningEvaluation(
        ProcessGateState state,
        IReadOnlyList<ProcessObservation> observations)
    {
        ImmutableArray<ProcessGateDecision> decisions = observations
            .Select(observation => Decision(
                observation,
                null,
                null,
                ProcessGateDisposition.AllowUnrestricted,
                ProcessGateReason.Morning))
            .ToImmutableArray();
        return new(
            state,
            decisions,
            ProcessProtectionHealthCode.Healthy);
    }

    private static ProcessGateEvaluation Degraded(
        ProcessGateState state,
        IReadOnlyList<ProcessObservation> observations,
        ProcessProtectionHealthCode health)
    {
        ImmutableArray<ProcessGateDecision> decisions = observations
            .Select(observation => Decision(
                observation,
                null,
                null,
                ProcessGateDisposition.AllowFailOpen,
                ProcessGateReason.ProcessProtectionDegraded))
            .ToImmutableArray();
        return new(state, decisions, health);
    }

    private static ProcessGateDecision Decision(
        ProcessObservation observation,
        string? ruleId,
        DateTimeOffset? cutoff,
        ProcessGateDisposition disposition,
        ProcessGateReason reason) =>
        new(
            observation.PidHint,
            observation.Identity?.Key,
            ruleId,
            cutoff,
            disposition,
            reason);

    private static DateTimeOffset Max(DateTimeOffset? previous, DateTimeOffset current) =>
        previous is { } value && value > current ? value : current;

    private sealed record CompiledPolicy(
        DateOnly NightDate,
        DateTimeOffset Wake,
        ImmutableDictionary<string, CompiledRule> Rules,
        ImmutableDictionary<string, PathBinding> PathsByPath,
        string Fingerprint);

    private sealed record CompiledRule(
        string Id,
        string RootPath,
        ImmutableHashSet<string> HelperPaths,
        DesktopAppRuleCategory Category,
        int SessionMinutes,
        DateTimeOffset CutoffUtc);

    private sealed record PathBinding(CompiledRule Rule, bool IsRoot);

    private sealed record HelperDependency(string RuleId, ProcessInstanceKey ParentKey);

    private sealed class BatchParentEvidence
    {
        private readonly HashSet<ProcessInstanceKey> _exactParents = [];

        public bool HasNone { get; private set; }

        public bool HasConflict => _exactParents.Count > 1
            || HasNone && _exactParents.Count > 0;

        public void Observe(ParentLink parent)
        {
            if (parent.Kind == ParentLinkKind.Exact && parent.ExactParent is { } exact)
            {
                _exactParents.Add(exact);
            }
            else if (parent.Kind == ParentLinkKind.None)
            {
                HasNone = true;
            }
        }
    }

    private enum LineageGrant
    {
        None,
        Eligible,
        Temporary,
    }

    private sealed record LineageResult(
        LineageGrant Grant,
        string? RuleId,
        ProcessGateReason Reason)
    {
        public static LineageResult Eligible(string ruleId) =>
            new(LineageGrant.Eligible, ruleId, ProcessGateReason.EligibleHelper);

        public static LineageResult Temporary(string ruleId) =>
            new(LineageGrant.Temporary, ruleId, ProcessGateReason.TemporaryOverrideHelper);

        public static LineageResult Fail(ProcessGateReason reason) =>
            new(LineageGrant.None, null, reason);
    }
}
