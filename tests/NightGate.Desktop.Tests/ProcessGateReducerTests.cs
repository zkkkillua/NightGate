using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class ProcessGateReducerTests
{
    private const string UserSid = "S-1-5-21-1000";
    private const int SessionId = 3;
    private static readonly DateOnly NightDate = new(2026, 7, 6);
    private static readonly DateTimeOffset Lock = At(7, 0, 40);

    [Theory]
    [InlineData("short", 15, 0, 25)]
    [InlineData("normal", 35, 0, 5)]
    [InlineData("long", 90, 23, 10)]
    public void RulesSealAtTheirOwnCrossMidnightCutoff(
        string ruleId,
        int sessionMinutes,
        int cutoffHour,
        int cutoffMinute)
    {
        DateTimeOffset cutoff = cutoffHour == 23
            ? At(6, cutoffHour, cutoffMinute)
            : At(7, cutoffHour, cutoffMinute);
        DesktopAppRuleDto rule = Rule(ruleId, $@"C:\Games\{ruleId}.exe", sessionMinutes);
        ProcessObservation before = Root(10, cutoff.AddTicks(-1), rule.RootExecutablePath!);
        ProcessObservation exact = Root(11, cutoff, rule.RootExecutablePath!);
        ProcessObservation after = Root(12, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddTicks(1), DesktopNightPhase.Free, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            before,
            exact,
            after);

        Assert.Equal(cutoff, Assert.Single(result.State.RuleStates).Value.CutoffUtc);
        Assert.True(Assert.Single(result.State.RuleStates).Value.IsSealed);
        AssertDecision(result, 10, ProcessGateDisposition.AllowEligible, cutoff);
        AssertDecision(result, 11, ProcessGateDisposition.AllowEligible, cutoff);
        AssertDecision(result, 12, ProcessGateDisposition.BlockNewRoot, cutoff);
    }

    [Fact]
    public void NinetyMinuteRuleCanGateWhileGlobalPhaseIsFree()
    {
        DesktopAppRuleDto rule = Rule("long", @"C:\Games\long.exe", 90);
        DateTimeOffset cutoff = At(6, 23, 10);

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(At(6, 23, 15), DesktopNightPhase.Free, [rule]),
            ProcessObservationBatchKind.StartDelta,
            Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!));

        ProcessGateDecision decision = Assert.Single(result.Decisions);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, decision.Disposition);
        Assert.Equal(cutoff, decision.CutoffUtc);
    }

    [Theory]
    [InlineData(@"\\?\C:\Games\game.exe", @"C:\Games\game.exe")]
    [InlineData(@"\\?\UNC\server\share\game.exe", @"\\server\share\game.exe")]
    public void SupportedExtendedRulePathMatchesTheCanonicalObservedPath(
        string configuredPath,
        string observedPath)
    {
        DesktopAppRuleDto rule = Rule("game", configuredPath, 35);
        DateTimeOffset cutoff = At(7, 0, 5);

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddTicks(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            Root(10, cutoff.AddTicks(1), observedPath));

        ProcessGateDecision decision = Assert.Single(result.Decisions);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, decision.Disposition);
        Assert.Equal(ProcessGateReason.NewRootAtOrAfterCutoff, decision.Reason);
    }

    [Fact]
    public void StartDeltaNeverSealsAndFirstAuthoritativeSnapshotCapturesExistingRoot()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation existing = Root(10, cutoff.AddMinutes(-1), rule.RootExecutablePath!);

        ProcessGateEvaluation delta = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            existing);
        Assert.False(delta.State.RuleStates["game"].IsSealed);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(delta.Decisions).Disposition);

        ProcessGateEvaluation snapshot = Evaluate(
            delta.State,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            existing);

        Assert.True(snapshot.State.RuleStates["game"].IsSealed);
        Assert.Equal(ProcessGateDisposition.AllowEligible, Assert.Single(snapshot.Decisions).Disposition);
        Assert.Contains(existing.Identity!.Key, snapshot.State.EligibleInstances.Keys);
    }

    [Fact]
    public void LateLoginAuthoritativeSnapshotReconstructsEligibleRoot()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation existing = Root(10, cutoff.AddHours(-1), rule.RootExecutablePath!);

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddMinutes(20), DesktopNightPhase.Grace, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            existing);

        Assert.Equal(ProcessGateDisposition.AllowEligible, Assert.Single(result.Decisions).Disposition);
        Assert.True(result.State.RuleStates["game"].IsSealed);
    }

    [Fact]
    public void ClaimedPreCutoffRootFirstSeenAfterSealFailsOpenWithoutGrandfathering()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        ProcessObservation lateClaim = Root(10, cutoff.AddMinutes(-1), rule.RootExecutablePath!);

        ProcessGateEvaluation result = Evaluate(
            sealedState.State,
            Policy(cutoff.AddMinutes(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            lateClaim);

        ProcessGateDecision decision = Assert.Single(result.Decisions);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, decision.Disposition);
        Assert.Equal(ProcessGateReason.PreCutoffRootNotInSealSnapshot, decision.Reason);
        Assert.DoesNotContain(lateClaim.Identity!.Key, result.State.EligibleInstances.Keys);
    }

    [Fact]
    public void SameFilenameInAnotherDirectoryIsUnrestricted()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        ProcessObservation other = Root(10, At(7, 0, 6), @"D:\Portable\game.exe");

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 6), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            other);

        Assert.Equal(ProcessGateDisposition.AllowUnrestricted, Assert.Single(result.Decisions).Disposition);
    }

    [Fact]
    public void CanonicalExactPathComparisonIsCaseInsensitive()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\Game.exe", 35);
        ProcessObservation samePathDifferentCase = Root(
            10,
            At(7, 0, 5).AddTicks(1),
            @"c:\games\GAME.exe");

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 6), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            samePathDifferentCase);

        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(result.Decisions).Disposition);
    }

    [Fact]
    public void RootHelperOrCrossRulePathAmbiguityDegradesWholeProcessReducerOpen()
    {
        DesktopAppRuleDto first = Rule(
            "first",
            @"C:\Games\first.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Shared\agent.exe");
        DesktopAppRuleDto rootHelper = Rule(
            "second",
            @"C:\Shared\agent.exe",
            35);
        DesktopAppRuleDto helperHelper = Rule(
            "third",
            @"C:\Games\third.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"c:\shared\AGENT.exe");

        foreach (DesktopAppRuleDto[] ambiguous in new[]
                 {
                     new[] { first, rootHelper },
                     new[] { first, helperHelper },
                 })
        {
            ProcessGateEvaluation result = Evaluate(
                ProcessGateState.Empty,
                Policy(At(7, 0, 6), DesktopNightPhase.LastStart, ambiguous),
                ProcessObservationBatchKind.StartDelta,
                Root(10, At(7, 0, 6), first.RootExecutablePath!));

            Assert.Equal(ProcessProtectionHealthCode.RulePathAmbiguity, result.HealthCode);
            Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(result.Decisions).Disposition);
        }
    }

    [Fact]
    public void PidReuseIsANewInstanceAndDoesNotInheritEligibility()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation original = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation first = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(2), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            original);
        ProcessObservation reusedPid = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation result = Evaluate(
            first.State,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            reusedPid);

        Assert.NotEqual(original.Identity!.Key, reusedPid.Identity!.Key);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(result.Decisions).Disposition);
        Assert.Contains(original.Identity.Key, result.State.EligibleInstances.Keys);
        Assert.DoesNotContain(reusedPid.Identity.Key, result.State.EligibleInstances.Keys);
    }

    [Fact]
    public void PidHintMismatchTaintsIdentityAndRemovesItsTrust()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation eligible = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation first = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            eligible);
        ProcessObservation mismatch = eligible with { PidHint = 99 };

        ProcessGateEvaluation result = Evaluate(
            first.State,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            mismatch);

        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(result.Decisions).Disposition);
        Assert.Contains(eligible.Identity!.Key, result.State.TaintedInstances);
        Assert.DoesNotContain(eligible.Identity.Key, result.State.EligibleInstances.Keys);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("creation")]
    [InlineData("sid")]
    [InlineData("session")]
    public void SameKeyMetadataConflictTaintsAndRemovesEligibility(string conflict)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation original = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation first = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            original);
        ObservedProcessIdentity identity = original.Identity!;
        ObservedProcessIdentity conflictingIdentity = conflict switch
        {
            "path" => identity with { ExecutablePath = @"C:\Games\other.exe" },
            "creation" => identity with { CreationInstantUtc = identity.CreationInstantUtc.AddTicks(1) },
            "sid" => identity with { UserSid = "S-1-5-21-2000" },
            "session" => identity with { SessionId = 9 },
            _ => throw new ArgumentOutOfRangeException(nameof(conflict)),
        };

        ProcessGateEvaluation result = Evaluate(
            first.State,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            new ProcessObservation(10, conflictingIdentity, ParentLink.None));

        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(result.Decisions).Disposition);
        Assert.Contains(identity.Key, result.State.TaintedInstances);
        Assert.DoesNotContain(identity.Key, result.State.EligibleInstances.Keys);
    }

    [Fact]
    public void TwoDifferentExactParentsTaintInstanceWhileUnknownReplacesPriorLineage()
    {
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation firstParent = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessObservation secondParent = Root(11, cutoff, rule.RootExecutablePath!);
        ProcessObservation helper = Complete(
            20,
            cutoff.AddSeconds(1),
            @"C:\Games\helper.exe",
            ParentLink.Exact(firstParent.Identity!.Key));
        ProcessGateEvaluation initial = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            firstParent,
            secondParent,
            helper);

        ProcessGateEvaluation unknown = Evaluate(
            initial.State,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            helper with { Parent = ParentLink.Unknown });
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(unknown.Decisions).Disposition);
        Assert.DoesNotContain(helper.Identity!.Key, unknown.State.TaintedInstances);
        Assert.Equal(ParentLink.Unknown, unknown.State.KnownInstances[helper.Identity.Key].Parent);

        ProcessGateEvaluation conflicting = Evaluate(
            initial.State,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            helper with { Parent = ParentLink.Exact(secondParent.Identity!.Key) });
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(conflicting.Decisions).Disposition);
        Assert.Contains(helper.Identity.Key, conflicting.State.TaintedInstances);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CurrentUnknownDominatesDuplicateExactLinkRegardlessOfBatchOrder(bool unknownFirst)
    {
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessObservation exact = Complete(
            11,
            cutoff.AddSeconds(1),
            @"C:\Games\helper.exe",
            ParentLink.Exact(root.Identity!.Key));
        ProcessGateEvaluation initial = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            root,
            exact);
        ProcessObservation unknown = exact with { Parent = ParentLink.Unknown };
        ProcessObservation[] batch = unknownFirst ? [unknown, exact] : [exact, unknown];

        ProcessGateEvaluation result = Evaluate(
            initial.State,
            Policy(cutoff.AddSeconds(2), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            batch);

        Assert.Equal(ParentLink.Unknown, result.State.KnownInstances[exact.Identity!.Key].Parent);
        Assert.DoesNotContain(exact.Identity.Key, result.State.EligibleInstances.Keys);
        Assert.All(result.Decisions, decision =>
            Assert.Equal(ProcessGateDisposition.AllowFailOpen, decision.Disposition));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoneAndExactParentConflictTaintsRegardlessOfBatchOrder(bool noneFirst)
    {
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessObservation exact = Complete(
            11,
            cutoff.AddSeconds(1),
            @"C:\Games\helper.exe",
            ParentLink.Exact(root.Identity!.Key));
        ProcessObservation none = exact with { Parent = ParentLink.None };
        ProcessObservation[] batch = noneFirst ? [root, none, exact] : [root, exact, none];

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            batch);

        Assert.Contains(exact.Identity!.Key, result.State.TaintedInstances);
        Assert.DoesNotContain(exact.Identity.Key, result.State.EligibleInstances.Keys);
        Assert.All(
            result.Decisions.Where(decision => decision.PidHint == exact.PidHint),
            decision => Assert.Equal(ProcessGateDisposition.AllowFailOpen, decision.Disposition));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoneAndExactParentConflictWithPriorObservationAlwaysTaints(bool priorWasNone)
    {
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessObservation exact = Complete(
            11,
            cutoff.AddSeconds(1),
            @"C:\Games\helper.exe",
            ParentLink.Exact(root.Identity!.Key));
        ProcessObservation none = exact with { Parent = ParentLink.None };
        ProcessGateEvaluation initial = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            root,
            priorWasNone ? none : exact);

        ProcessGateEvaluation result = Evaluate(
            initial.State,
            Policy(cutoff.AddSeconds(2), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            priorWasNone ? exact : none);

        Assert.Contains(exact.Identity!.Key, result.State.TaintedInstances);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(result.Decisions).Disposition);
    }

    [Fact]
    public void TaintedParentMakesDescendantFailOpenWithoutTaintingTheDescendant()
    {
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe",
            @"C:\Games\renderer.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessObservation helper = Complete(
            11,
            cutoff.AddSeconds(1),
            @"C:\Games\helper.exe",
            ParentLink.Exact(root.Identity!.Key));
        ProcessObservation renderer = Complete(
            12,
            cutoff.AddSeconds(2),
            @"C:\Games\renderer.exe",
            ParentLink.Exact(helper.Identity!.Key));
        ProcessGateEvaluation initial = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(2), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            root,
            helper,
            renderer);
        ProcessObservation conflictingHelper = helper with
        {
            Identity = helper.Identity! with { ExecutablePath = @"C:\Games\renderer.exe" },
        };

        ProcessGateEvaluation result = Evaluate(
            initial.State,
            Policy(cutoff.AddSeconds(3), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            conflictingHelper,
            renderer);

        Assert.All(result.Decisions, decision =>
            Assert.Equal(ProcessGateDisposition.AllowFailOpen, decision.Disposition));
        Assert.Contains(helper.Identity!.Key, result.State.TaintedInstances);
        Assert.DoesNotContain(renderer.Identity!.Key, result.State.TaintedInstances);
        Assert.DoesNotContain(renderer.Identity.Key, result.State.EligibleInstances.Keys);
    }

    [Fact]
    public void MissingIdentityWrongContextAndUnknownHelperParentFailOpenButRootUnknownParentStillBlocks()
    {
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe");
        DateTimeOffset created = At(7, 0, 6);
        ProcessObservation missing = new(1, null, ParentLink.Unknown);
        ProcessObservation wrongSid = Root(2, created, rule.RootExecutablePath!, "S-1-5-21-2000");
        ProcessObservation wrongSession = Root(3, created, rule.RootExecutablePath!, UserSid, 99);
        ProcessObservation helperUnknown = Complete(
            4,
            created,
            @"C:\Games\helper.exe",
            ParentLink.Unknown);
        ProcessObservation rootUnknown = Complete(
            5,
            created,
            rule.RootExecutablePath!,
            ParentLink.Unknown);

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(created, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            missing,
            wrongSid,
            wrongSession,
            helperUnknown,
            rootUnknown);

        Assert.All(result.Decisions.Take(4), decision =>
            Assert.Equal(ProcessGateDisposition.AllowFailOpen, decision.Disposition));
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, result.Decisions[4].Disposition);
    }

    [Fact]
    public void EligibleHelpersResolveDirectAndMultiLevelInAnyObservationOrder()
    {
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe",
            @"C:\Games\renderer.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessObservation helper = Complete(
            11,
            cutoff.AddSeconds(1),
            @"C:\Games\helper.exe",
            ParentLink.Exact(root.Identity!.Key));
        ProcessObservation renderer = Complete(
            12,
            cutoff.AddSeconds(2),
            @"C:\Games\renderer.exe",
            ParentLink.Exact(helper.Identity!.Key));

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(2), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            renderer,
            helper,
            root);

        Assert.Equal(ProcessGateDisposition.AllowEligible, result.Decisions[0].Disposition);
        Assert.Equal(ProcessGateDisposition.AllowEligible, result.Decisions[1].Disposition);
        Assert.Equal(ProcessGateDisposition.AllowEligible, result.Decisions[2].Disposition);
        Assert.Equal(3, result.State.EligibleInstances.Count);
    }

    [Fact]
    public void HelperCannotInheritFromAnExactParentCreatedAfterTheChild()
    {
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe",
            @"C:\Games\renderer.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessObservation laterParent = Complete(
            11,
            cutoff.AddSeconds(2),
            @"C:\Games\helper.exe",
            ParentLink.Exact(root.Identity!.Key));
        ProcessObservation earlierChild = Complete(
            12,
            cutoff.AddSeconds(1),
            @"C:\Games\renderer.exe",
            ParentLink.Exact(laterParent.Identity!.Key));

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(3), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            root,
            laterParent,
            earlierChild);

        Assert.Equal(ProcessGateDisposition.AllowEligible, result.Decisions[0].Disposition);
        Assert.Equal(ProcessGateDisposition.AllowEligible, result.Decisions[1].Disposition);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, result.Decisions[2].Disposition);
        Assert.Equal(ProcessGateReason.ParentCreatedAfterChild, result.Decisions[2].Reason);
        Assert.DoesNotContain(earlierChild.Identity!.Key, result.State.EligibleInstances.Keys);
    }

    [Fact]
    public void FiveThousandLevelHelperChainResolvesWithoutCallStackRecursion()
    {
        const int depth = 5_000;
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        List<ProcessObservation> observations = new(depth + 1) { root };
        ProcessInstanceKey parent = root.Identity!.Key;
        for (int index = 1; index <= depth; index++)
        {
            ProcessObservation helper = Complete(
                10 + index,
                cutoff.AddTicks(index),
                @"C:\Games\helper.exe",
                ParentLink.Exact(parent));
            observations.Add(helper);
            parent = helper.Identity!.Key;
        }

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddTicks(depth), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            observations.ToArray());

        Assert.Equal(depth + 1, result.State.EligibleInstances.Count);
        Assert.All(result.Decisions, decision =>
            Assert.Equal(ProcessGateDisposition.AllowEligible, decision.Disposition));
    }

    [Fact]
    public void InvalidHelperChainsFailOpenAndNeverPropagateTrust()
    {
        DesktopAppRuleDto first = Rule(
            "first",
            @"C:\Games\first.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\first-helper.exe");
        DesktopAppRuleDto second = Rule(
            "second",
            @"C:\Games\second.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\second-helper.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation firstRoot = Root(10, cutoff, first.RootExecutablePath!);
        ProcessObservation secondRoot = Root(20, cutoff, second.RootExecutablePath!);
        ProcessObservation unknownParent = Complete(
            11,
            cutoff.AddSeconds(1),
            @"C:\Games\first-helper.exe",
            ParentLink.Unknown);
        ProcessObservation missingParent = Complete(
            12,
            cutoff.AddSeconds(1),
            @"C:\Games\first-helper.exe",
            ParentLink.Exact(new ProcessInstanceKey(99, cutoff.UtcTicks)));
        ProcessObservation crossRule = Complete(
            13,
            cutoff.AddSeconds(1),
            @"C:\Games\first-helper.exe",
            ParentLink.Exact(secondRoot.Identity!.Key));
        ProcessObservation unknownIntermediate = Complete(
            30,
            cutoff.AddSeconds(1),
            @"C:\Games\unlisted.exe",
            ParentLink.Exact(firstRoot.Identity!.Key));
        ProcessObservation throughUnknownIntermediate = Complete(
            14,
            cutoff.AddSeconds(2),
            @"C:\Games\first-helper.exe",
            ParentLink.Exact(unknownIntermediate.Identity!.Key));
        ProcessObservation cycleA = Complete(
            15,
            cutoff.AddSeconds(1),
            @"C:\Games\first-helper.exe",
            ParentLink.None);
        ProcessObservation cycleB = Complete(
            16,
            cutoff.AddSeconds(1),
            @"C:\Games\first-helper.exe",
            ParentLink.Exact(cycleA.Identity!.Key));
        cycleA = cycleA with { Parent = ParentLink.Exact(cycleB.Identity!.Key) };

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(2), DesktopNightPhase.LastStart, [first, second]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            firstRoot,
            secondRoot,
            unknownParent,
            missingParent,
            crossRule,
            unknownIntermediate,
            throughUnknownIntermediate,
            cycleA,
            cycleB);

        Assert.All(
            result.Decisions.Where(decision => decision.PidHint is 11 or 12 or 13 or 14 or 15 or 16),
            decision => Assert.Equal(ProcessGateDisposition.AllowFailOpen, decision.Disposition));
        Assert.Equal(
            ProcessGateDisposition.AllowUnrestricted,
            Assert.Single(result.Decisions, decision => decision.PidHint == 30).Disposition);
    }

    [Fact]
    public void ReusedParentPidWithoutExactInstanceDoesNotPropagateEligibility()
    {
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation oldRoot = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation first = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddTicks(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            oldRoot);
        ProcessObservation newRoot = Root(10, cutoff.AddSeconds(1), rule.RootExecutablePath!);
        ProcessObservation helperLinkedToOld = Complete(
            11,
            cutoff.AddSeconds(2),
            @"C:\Games\helper.exe",
            ParentLink.Exact(oldRoot.Identity!.Key));

        ProcessGateEvaluation result = Evaluate(
            first.State,
            Policy(cutoff.AddSeconds(2), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot,
            helperLinkedToOld);

        Assert.Equal(ProcessGateDisposition.BlockNewRoot, result.Decisions[0].Disposition);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, result.Decisions[1].Disposition);
    }

    [Fact]
    public void OnlyStrictlyPostCutoffCompleteSameContextExactRootCanBeBlockCandidate()
    {
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation exactCandidate = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);
        ProcessObservation exactBoundary = Root(11, cutoff, rule.RootExecutablePath!);
        ProcessObservation otherPath = Root(12, cutoff.AddTicks(1), @"C:\Other\game.exe");
        ProcessObservation helper = Complete(
            13,
            cutoff.AddTicks(1),
            @"C:\Games\helper.exe",
            ParentLink.Unknown);
        ProcessObservation wrongSid = Root(14, cutoff.AddTicks(1), rule.RootExecutablePath!, "S-1-5-21-2");
        ProcessObservation missing = new(15, null, ParentLink.Unknown);

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddTicks(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            exactCandidate,
            exactBoundary,
            otherPath,
            helper,
            wrongSid,
            missing);

        ProcessGateDecision blocked = Assert.Single(
            result.Decisions,
            decision => decision.Disposition == ProcessGateDisposition.BlockNewRoot);
        Assert.Equal(exactCandidate.Identity!.Key, blocked.InstanceKey);
    }

    [Fact]
    public void DuplicateDeltaAndReconciliationDeduplicateExactStateAndStayStable()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation existing = Root(10, cutoff, rule.RootExecutablePath!);

        ProcessGateEvaluation first = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            existing,
            existing);
        ProcessGateEvaluation second = Evaluate(
            first.State,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            existing);

        Assert.Equal(2, first.Decisions.Length);
        Assert.All(first.Decisions, decision =>
            Assert.Equal(ProcessGateDisposition.AllowEligible, decision.Disposition));
        Assert.Single(first.State.KnownInstances);
        Assert.Single(first.State.EligibleInstances);
        Assert.Single(second.State.KnownInstances);
        Assert.Single(second.State.EligibleInstances);
        Assert.Equal(ProcessGateDisposition.AllowEligible, Assert.Single(second.Decisions).Disposition);
    }

    [Fact]
    public void AuthoritativeBatchWithSamePidAndDifferentCreationKeysFailsBothOpen()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation first = Root(10, cutoff.AddSeconds(1), rule.RootExecutablePath!);
        ProcessObservation second = Root(10, cutoff.AddSeconds(2), rule.RootExecutablePath!);

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(2), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            first,
            second);

        Assert.All(result.Decisions, decision =>
            Assert.Equal(ProcessGateDisposition.AllowFailOpen, decision.Disposition));
        Assert.Contains(first.Identity!.Key, result.State.TaintedInstances);
        Assert.Contains(second.Identity!.Key, result.State.TaintedInstances);
        Assert.Empty(result.State.EligibleInstances);
        Assert.Empty(result.State.TemporaryInstances);
    }

    [Fact]
    public void EmergencyTemporarilyAllowsOnlyPostCutoffRootAndHelperThenExpiryRestrictsAgain()
    {
        DesktopAppRuleDto rule = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\helper.exe");
        DateTimeOffset cutoff = At(7, 0, 5);
        DesktopActiveOverrideDto emergency = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        ProcessObservation permanent = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessObservation temporary = Root(11, cutoff.AddTicks(1), rule.RootExecutablePath!);
        ProcessObservation helper = Complete(
            12,
            cutoff.AddSeconds(2),
            @"C:\Games\helper.exe",
            ParentLink.Exact(temporary.Identity!.Key));

        ProcessGateEvaluation active = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            helper,
            temporary,
            permanent);

        Assert.Equal(ProcessGateDisposition.AllowTemporaryOverride, active.Decisions[0].Disposition);
        Assert.Equal(ProcessGateDisposition.AllowTemporaryOverride, active.Decisions[1].Disposition);
        Assert.Equal(ProcessGateDisposition.AllowEligible, active.Decisions[2].Disposition);
        Assert.Contains(permanent.Identity!.Key, active.State.EligibleInstances.Keys);
        Assert.DoesNotContain(temporary.Identity!.Key, active.State.EligibleInstances.Keys);
        Assert.Contains(temporary.Identity.Key, active.State.TemporaryInstances.Keys);
        Assert.Contains(helper.Identity!.Key, active.State.TemporaryInstances.Keys);

        ProcessGateEvaluation expired = Evaluate(
            active.State,
            Policy(At(7, 0, 41), DesktopNightPhase.Grace, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            temporary,
            helper);

        Assert.Equal(ProcessGateDisposition.BlockNewRoot, expired.Decisions[0].Disposition);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, expired.Decisions[1].Disposition);
        Assert.Empty(expired.State.TemporaryInstances);
    }

    [Fact]
    public void FutureCreationEvidenceFailsOpenAndCannotBecomeEligibleTemporaryOrBlock()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DesktopActiveOverrideDto emergency = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        ProcessObservation futureRoot = Root(10, At(7, 0, 21), rule.RootExecutablePath!);

        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            futureRoot);

        ProcessGateDecision decision = Assert.Single(result.Decisions);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, decision.Disposition);
        Assert.Equal(ProcessGateReason.CreationInstantAfterEffectiveTime, decision.Reason);
        Assert.Empty(result.State.EligibleInstances);
        Assert.Empty(result.State.TemporaryInstances);
    }

    [Fact]
    public void EntertainmentCoolingIsRestrictedAndActiveWindowIsTemporary()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        DesktopActiveOverrideDto entertainment = Override(
            DesktopOverrideKind.Entertainment,
            At(7, 0, 0),
            At(7, 0, 10),
            At(7, 0, 30));
        ProcessObservation root = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation cooling = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddTicks(1), DesktopNightPhase.CoolingOff, [rule], entertainment),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessGateEvaluation active = Evaluate(
            cooling.State,
            Policy(At(7, 0, 10), DesktopNightPhase.OverrideActive, [rule], entertainment),
            ProcessObservationBatchKind.StartDelta,
            root);

        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(cooling.Decisions).Disposition);
        Assert.Equal(ProcessGateDisposition.AllowTemporaryOverride, Assert.Single(active.Decisions).Disposition);
        Assert.DoesNotContain(root.Identity!.Key, active.State.EligibleInstances.Keys);
    }

    [Fact]
    public void ExpiredOverrideCannotBeResurrectedByClockRollbackOrOldIdentityReplay()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        DesktopActiveOverrideDto firstOverride = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        ProcessObservation root = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);
        ProcessGateEvaluation active = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], firstOverride),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessGateEvaluation expired = Evaluate(
            active.State,
            Policy(At(7, 0, 41), DesktopNightPhase.Grace, [rule]),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessGateEvaluation replayed = Evaluate(
            expired.State,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], firstOverride),
            ProcessObservationBatchKind.StartDelta,
            root);

        Assert.Equal(ProcessGateDisposition.AllowTemporaryOverride, Assert.Single(active.Decisions).Disposition);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(expired.Decisions).Disposition);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(replayed.Decisions).Disposition);
        Assert.Equal(At(7, 0, 41), replayed.State.LastEffectiveLogicalTime);
        Assert.Empty(replayed.State.TemporaryInstances);
    }

    [Fact]
    public void OverrideDisappearanceRetiresScopeAndReplayCannotRestoreIt()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        ProcessObservation root = Root(10, At(7, 0, 6), rule.RootExecutablePath!);
        DesktopActiveOverrideDto emergency = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        ProcessGateEvaluation active = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessGateEvaluation disappeared = Evaluate(
            active.State,
            Policy(At(7, 0, 21), DesktopNightPhase.Grace, [rule]),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessGateEvaluation replay = Evaluate(
            disappeared.State,
            Policy(At(7, 0, 22), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessOverrideIdentity identity = OverrideIdentity(emergency);

        Assert.Contains(identity, disappeared.State.RetiredOverrideIdentities);
        Assert.Equal(identity, disappeared.State.OverrideHighWater);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(disappeared.Decisions).Disposition);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(replay.Decisions).Disposition);
        Assert.Empty(replay.State.TemporaryInstances);
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("trust")]
    public void OlderUnseenOverrideCannotReplaceHighWaterAcrossContinuityReset(string resetKind)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        ProcessObservation root = Root(10, At(7, 0, 6), rule.RootExecutablePath!);
        DesktopActiveOverrideDto newerTeam = Override(
            DesktopOverrideKind.TeamRescue,
            At(7, 0, 20),
            At(7, 0, 20),
            At(7, 0, 40));
        DesktopActiveOverrideDto olderEmergency = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        ProcessGateEvaluation newer = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 25), DesktopNightPhase.OverrideActive, [rule], newerTeam),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessGateState beforeOlder = newer.State;
        string epoch = "epoch-b";
        if (resetKind == "trust")
        {
            ProcessGateEvaluation broken = EvaluateWithObserver(
                newer.State,
                Policy(At(7, 0, 26), DesktopNightPhase.OverrideActive, [rule], newerTeam),
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                false,
                root);
            beforeOlder = broken.State;
            epoch = "epoch-a";
        }

        ProcessGateEvaluation result = EvaluateWithObserver(
            beforeOlder,
            Policy(At(7, 0, 27), DesktopNightPhase.OverrideActive, [rule], olderEmergency),
            ProcessObservationBatchKind.StartDelta,
            epoch,
            true,
            root);

        Assert.Equal(OverrideIdentity(newerTeam), result.State.OverrideHighWater);
        Assert.Equal(
            resetKind == "trust"
                ? ProcessGateDisposition.AllowFailOpen
                : ProcessGateDisposition.BlockNewRoot,
            Assert.Single(result.Decisions).Disposition);
        if (resetKind == "trust")
        {
            Assert.Contains(root.Identity!.Key, result.State.TaintedInstances);
        }

        Assert.Empty(result.State.TemporaryInstances);
    }

    [Fact]
    public void GenuinelyLaterOverrideGetsANewTemporaryScope()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);
        DesktopActiveOverrideDto first = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        DesktopActiveOverrideDto later = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 50),
            At(7, 0, 50),
            At(7, 1, 20));
        ProcessGateEvaluation firstActive = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], first),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessGateEvaluation ended = Evaluate(
            firstActive.State,
            Policy(At(7, 0, 45), DesktopNightPhase.Grace, [rule]),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessGateEvaluation replacement = Evaluate(
            ended.State,
            Policy(At(7, 0, 50), DesktopNightPhase.OverrideActive, [rule], later),
            ProcessObservationBatchKind.StartDelta,
            root);

        Assert.Equal(ProcessGateDisposition.AllowTemporaryOverride, Assert.Single(replacement.Decisions).Disposition);
        Assert.Equal(later.StartsAtUtc, replacement.State.TemporaryOverrideIdentity!.StartsAtUtc);
    }

    [Theory]
    [InlineData(DesktopOverrideKind.TeamRescue)]
    [InlineData(DesktopOverrideKind.Emergency)]
    [InlineData(DesktopOverrideKind.Entertainment)]
    public void ExactEmergencyImmediatelyPreemptsAnyActiveOrCoolingOverride(
        DesktopOverrideKind priorKind)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset priorRequest = At(7, 0, 10);
        DateTimeOffset priorStart = priorKind == DesktopOverrideKind.Entertainment
            ? At(7, 0, 20)
            : priorRequest;
        DateTimeOffset priorEnd = priorKind == DesktopOverrideKind.TeamRescue
            ? At(7, 0, 30)
            : At(7, 0, 40);
        string[] priorAllowed = priorKind == DesktopOverrideKind.TeamRescue
            ? ["game"]
            : [];
        DesktopActiveOverrideDto prior = Override(
            priorKind,
            priorRequest,
            priorStart,
            priorEnd,
            priorAllowed);
        DesktopNightPhase priorPhase = priorKind == DesktopOverrideKind.Entertainment
            ? DesktopNightPhase.CoolingOff
            : DesktopNightPhase.OverrideActive;
        ProcessGateEvaluation observedPrior = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 15), priorPhase, [rule], prior),
            ProcessObservationBatchKind.StartDelta);
        DateTimeOffset emergencyRequest = At(7, 0, 16);
        DesktopActiveOverrideDto emergency = Override(
            DesktopOverrideKind.Emergency,
            emergencyRequest,
            emergencyRequest,
            At(7, 0, 46));
        ProcessObservation newRoot = Root(
            10,
            emergencyRequest,
            rule.RootExecutablePath!);

        ProcessGateEvaluation preempted = Evaluate(
            observedPrior.State,
            Policy(
                emergencyRequest,
                DesktopNightPhase.OverrideActive,
                [rule],
                emergency),
            ProcessObservationBatchKind.StartDelta,
            newRoot);

        Assert.Equal(ProcessProtectionHealthCode.Healthy, preempted.HealthCode);
        Assert.Equal(
            ProcessGateDisposition.AllowTemporaryOverride,
            Assert.Single(preempted.Decisions).Disposition);
        Assert.Equal(OverrideIdentity(emergency), preempted.State.OverrideHighWater);
        Assert.Equal(
            OverrideIdentity(emergency),
            preempted.State.TemporaryOverrideIdentity);
        Assert.Contains(OverrideIdentity(prior), preempted.State.RetiredOverrideIdentities);
    }

    [Theory]
    [InlineData("delayedStart")]
    [InlineData("shortDuration")]
    [InlineData("longDuration")]
    [InlineData("nonemptyAllowlist")]
    [InlineData("nonadvancingRequest")]
    public void MalformedOrNonadvancingEmergencyCannotPreemptOverrideHighWater(
        string scenario)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DesktopActiveOverrideDto prior = Override(
            DesktopOverrideKind.TeamRescue,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 30),
            "game");
        ProcessGateEvaluation observedPrior = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 15), DesktopNightPhase.OverrideActive, [rule], prior),
            ProcessObservationBatchKind.StartDelta);
        DesktopActiveOverrideDto candidate = scenario switch
        {
            "delayedStart" => Override(
                DesktopOverrideKind.Emergency,
                At(7, 0, 16),
                At(7, 0, 17),
                At(7, 0, 47)),
            "shortDuration" => Override(
                DesktopOverrideKind.Emergency,
                At(7, 0, 16),
                At(7, 0, 16),
                At(7, 0, 45)),
            "longDuration" => Override(
                DesktopOverrideKind.Emergency,
                At(7, 0, 16),
                At(7, 0, 16),
                At(7, 0, 47)),
            "nonemptyAllowlist" => Override(
                DesktopOverrideKind.Emergency,
                At(7, 0, 16),
                At(7, 0, 16),
                At(7, 0, 46),
                "game"),
            "nonadvancingRequest" => Override(
                DesktopOverrideKind.Emergency,
                At(7, 0, 10),
                At(7, 0, 10),
                At(7, 0, 40)),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        ProcessObservation newRoot = Root(
            10,
            At(7, 0, 18),
            rule.RootExecutablePath!);

        ProcessGateEvaluation rejected = Evaluate(
            observedPrior.State,
            Policy(
                At(7, 0, 18),
                DesktopNightPhase.OverrideActive,
                [rule],
                candidate),
            ProcessObservationBatchKind.StartDelta,
            newRoot);

        Assert.Equal(
            ProcessGateDisposition.BlockNewRoot,
            Assert.Single(rejected.Decisions).Disposition);
        Assert.Equal(OverrideIdentity(prior), rejected.State.OverrideHighWater);
        Assert.Null(rejected.State.TemporaryOverrideIdentity);
        Assert.Contains(
            OverrideIdentity(candidate),
            rejected.State.RetiredOverrideIdentities);
    }

    [Fact]
    public void LaterRequestedButOverlappingOverrideCannotReplaceHighWater()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);
        DesktopActiveOverrideDto first = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        DesktopActiveOverrideDto overlapping = Override(
            DesktopOverrideKind.Entertainment,
            At(7, 0, 20),
            At(7, 0, 20),
            At(7, 0, 50));
        ProcessGateEvaluation firstActive = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 15), DesktopNightPhase.OverrideActive, [rule], first),
            ProcessObservationBatchKind.StartDelta,
            root);

        ProcessGateEvaluation rejected = Evaluate(
            firstActive.State,
            Policy(At(7, 0, 25), DesktopNightPhase.OverrideActive, [rule], overlapping),
            ProcessObservationBatchKind.StartDelta,
            root);

        Assert.Equal(OverrideIdentity(first), rejected.State.OverrideHighWater);
        Assert.Contains(OverrideIdentity(overlapping), rejected.State.RetiredOverrideIdentities);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(rejected.Decisions).Disposition);
        Assert.Empty(rejected.State.TemporaryInstances);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveOverrideBecomingNonactiveRetiresEvenTheSameDeclaredIdentity(
        bool observerEpochChanges)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        ProcessObservation root = Root(10, At(7, 0, 6), rule.RootExecutablePath!);
        DesktopActiveOverrideDto emergency = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        ProcessGateEvaluation active = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 15), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            root);
        string nextEpoch = observerEpochChanges ? "epoch-b" : "epoch-a";
        ProcessGateEvaluation staleCooling = EvaluateWithObserver(
            active.State,
            Policy(At(7, 0, 16), DesktopNightPhase.CoolingOff, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            nextEpoch,
            true,
            root);

        ProcessGateEvaluation replay = EvaluateWithObserver(
            staleCooling.State,
            Policy(At(7, 0, 17), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            nextEpoch,
            true,
            root);

        Assert.Contains(OverrideIdentity(emergency), staleCooling.State.RetiredOverrideIdentities);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(staleCooling.Decisions).Disposition);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(replay.Decisions).Disposition);
        Assert.Empty(replay.State.TemporaryInstances);
    }

    [Fact]
    public void CoolingOverrideDisappearanceRetiresHighWaterBeforeActiveReplay()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DesktopActiveOverrideDto entertainment = Override(
            DesktopOverrideKind.Entertainment,
            At(7, 0, 0),
            At(7, 0, 10),
            At(7, 0, 30));
        ProcessGateEvaluation cooling = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 6), DesktopNightPhase.CoolingOff, [rule], entertainment),
            ProcessObservationBatchKind.StartDelta);
        ProcessGateEvaluation disappeared = Evaluate(
            cooling.State,
            Policy(At(7, 0, 7), DesktopNightPhase.Grace, [rule]),
            ProcessObservationBatchKind.StartDelta);
        ProcessObservation root = Root(10, At(7, 0, 6), rule.RootExecutablePath!);

        ProcessGateEvaluation replay = Evaluate(
            disappeared.State,
            Policy(At(7, 0, 15), DesktopNightPhase.OverrideActive, [rule], entertainment),
            ProcessObservationBatchKind.StartDelta,
            root);

        Assert.Contains(OverrideIdentity(entertainment), disappeared.State.RetiredOverrideIdentities);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(replay.Decisions).Disposition);
        Assert.Empty(replay.State.TemporaryInstances);
    }

    [Fact]
    public void RetiredOverrideIdentityCannotReturnAfterObserverEpochChanges()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);
        DesktopActiveOverrideDto first = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        DesktopActiveOverrideDto replacement = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 40),
            At(7, 0, 40),
            At(7, 1, 0));
        ProcessGateEvaluation firstActive = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 15), DesktopNightPhase.OverrideActive, [rule], first),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessGateEvaluation replaced = Evaluate(
            firstActive.State,
            Policy(At(7, 0, 45), DesktopNightPhase.OverrideActive, [rule], replacement),
            ProcessObservationBatchKind.StartDelta,
            root);

        ProcessGateEvaluation replayAfterEpochChange = EvaluateWithObserver(
            replaced.State,
            Policy(At(7, 0, 50), DesktopNightPhase.OverrideActive, [rule], first),
            ProcessObservationBatchKind.StartDelta,
            "epoch-b",
            true,
            root);

        Assert.Contains(
            new ProcessOverrideIdentity(
                DesktopOverrideKind.Emergency,
                first.RequestedAtUtc,
                first.StartsAtUtc,
                first.EndsAtUtc),
            replayAfterEpochChange.State.RetiredOverrideIdentities);
        Assert.Equal(
            ProcessGateDisposition.BlockNewRoot,
            Assert.Single(replayAfterEpochChange.Decisions).Disposition);
        Assert.Empty(replayAfterEpochChange.State.TemporaryInstances);
    }

    [Fact]
    public void TeamRescueCapturesOnlyBaselineGameAndAllowsSelectedVoiceAndHelpers()
    {
        DesktopAppRuleDto game = Rule(
            "game",
            @"C:\Games\game.exe",
            35,
            DesktopAppRuleCategory.Game,
            @"C:\Games\game-helper.exe");
        DesktopAppRuleDto voice = Rule(
            "voice",
            @"C:\Voice\voice.exe",
            35,
            DesktopAppRuleCategory.Voice,
            @"C:\Voice\voice-helper.exe");
        DesktopAppRuleDto other = Rule("other", @"C:\Games\other.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation currentGame = Root(10, At(7, 0, 9), game.RootExecutablePath!);
        ProcessGateEvaluation baseline = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 9), DesktopNightPhase.LastStart, [game, voice, other]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            currentGame);
        DesktopActiveOverrideDto rescue = Override(
            DesktopOverrideKind.TeamRescue,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 30),
            "GAME",
            "voice");
        ProcessObservation newGame = Root(11, At(7, 0, 11), game.RootExecutablePath!);
        ProcessObservation voiceRoot = Root(20, At(7, 0, 12), voice.RootExecutablePath!);
        ProcessObservation voiceHelper = Complete(
            21,
            At(7, 0, 13),
            @"C:\Voice\voice-helper.exe",
            ParentLink.Exact(voiceRoot.Identity!.Key));
        ProcessObservation otherRoot = Root(30, At(7, 0, 12), other.RootExecutablePath!);

        ProcessGateEvaluation active = Evaluate(
            baseline.State,
            Policy(At(7, 0, 15), DesktopNightPhase.OverrideActive, [game, voice, other], rescue),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            voiceHelper,
            newGame,
            currentGame,
            voiceRoot,
            otherRoot);

        Assert.Equal(ProcessGateDisposition.AllowTemporaryOverride, active.Decisions[0].Disposition);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, active.Decisions[1].Disposition);
        Assert.Equal(ProcessGateDisposition.AllowTemporaryOverride, active.Decisions[2].Disposition);
        Assert.Equal(ProcessGateDisposition.AllowTemporaryOverride, active.Decisions[3].Disposition);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, active.Decisions[4].Disposition);
        Assert.DoesNotContain(currentGame.Identity!.Key, active.State.EligibleInstances.Keys);
        Assert.Contains(currentGame.Identity.Key, active.State.TemporaryInstances.Keys);
        Assert.Contains(voiceRoot.Identity!.Key, active.State.TemporaryInstances.Keys);
        Assert.Contains(voiceHelper.Identity!.Key, active.State.TemporaryInstances.Keys);

        ProcessGateEvaluation expired = Evaluate(
            active.State,
            Policy(At(7, 0, 31), DesktopNightPhase.Grace, [game, voice, other]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            currentGame,
            voiceRoot);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, expired.Decisions[0].Disposition);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, expired.Decisions[1].Disposition);
        Assert.Empty(expired.State.TemporaryInstances);
    }

    [Fact]
    public void TeamRescueTemporarilyAllowsSelectedVoiceAlreadyRunningBeforeStart()
    {
        DesktopAppRuleDto voice = Rule(
            "voice",
            @"C:\Voice\voice.exe",
            35,
            DesktopAppRuleCategory.Voice);
        ProcessObservation existingVoice = Root(20, At(7, 0, 9), voice.RootExecutablePath!);
        ProcessGateEvaluation baseline = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 9), DesktopNightPhase.LastStart, [voice]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            existingVoice);
        DesktopActiveOverrideDto rescue = Override(
            DesktopOverrideKind.TeamRescue,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 30),
            "voice");

        ProcessGateEvaluation active = Evaluate(
            baseline.State,
            Policy(At(7, 0, 15), DesktopNightPhase.OverrideActive, [voice], rescue),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            existingVoice);

        Assert.Equal(
            ProcessGateDisposition.AllowTemporaryOverride,
            Assert.Single(active.Decisions).Disposition);
        Assert.Contains(existingVoice.Identity!.Key, active.State.TemporaryInstances.Keys);
    }

    [Fact]
    public void TeamRescueEmptyOrPartialIdsGrantOnlyExplicitRules()
    {
        DesktopAppRuleDto first = Rule("first", @"C:\Games\first.exe", 35);
        DesktopAppRuleDto second = Rule("second", @"C:\Games\second.exe", 35);
        ProcessObservation firstRoot = Root(10, At(7, 0, 9), first.RootExecutablePath!);
        ProcessObservation secondRoot = Root(20, At(7, 0, 9), second.RootExecutablePath!);
        ProcessGateEvaluation baseline = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 9), DesktopNightPhase.LastStart, [first, second]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            firstRoot,
            secondRoot);

        foreach ((string[] allowed, ProcessGateDisposition firstExpected) in new[]
                 {
                     (Array.Empty<string>(), ProcessGateDisposition.BlockNewRoot),
                     (new[] { "first" }, ProcessGateDisposition.AllowTemporaryOverride),
                 })
        {
            DesktopActiveOverrideDto rescue = Override(
                DesktopOverrideKind.TeamRescue,
                At(7, 0, 10),
                At(7, 0, 10),
                At(7, 0, 30),
                allowed);
            ProcessGateEvaluation active = Evaluate(
                baseline.State,
                Policy(At(7, 0, 15), DesktopNightPhase.OverrideActive, [first, second], rescue),
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                firstRoot,
                secondRoot);
            Assert.Equal(firstExpected, active.Decisions[0].Disposition);
            Assert.Equal(ProcessGateDisposition.BlockNewRoot, active.Decisions[1].Disposition);
        }
    }

    [Theory]
    [InlineData("alreadyActive")]
    [InlineData("epochChanged")]
    [InlineData("timelineUntrusted")]
    public void TeamRescueUncertainCaptureFailsOpenAndNeverCreatesTemporaryGrant(string scenario)
    {
        DesktopAppRuleDto game = Rule("game", @"C:\Games\game.exe", 35);
        ProcessObservation currentGame = Root(10, At(7, 0, 9), game.RootExecutablePath!);
        ProcessGateState state = ProcessGateState.Empty;
        string epoch = "epoch-a";
        bool trusted = true;
        if (scenario != "alreadyActive")
        {
            ProcessGateEvaluation baseline = Evaluate(
                state,
                Policy(At(7, 0, 9), DesktopNightPhase.LastStart, [game]),
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                currentGame);
            state = baseline.State;
        }

        if (scenario == "epochChanged")
        {
            epoch = "epoch-b";
        }
        else if (scenario == "timelineUntrusted")
        {
            trusted = false;
        }

        DesktopActiveOverrideDto rescue = Override(
            DesktopOverrideKind.TeamRescue,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 30),
            "game");
        ProcessGateEvaluation active = EvaluateWithObserver(
            state,
            Policy(At(7, 0, 15), DesktopNightPhase.OverrideActive, [game], rescue),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            epoch,
            trusted,
            currentGame);

        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(active.Decisions).Disposition);
        Assert.Empty(active.State.TemporaryInstances);
    }

    [Fact]
    public void UntrustedCreationTimelineCannotEligibleTemporaryOrBlockConfiguredProcesses()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        DesktopActiveOverrideDto emergency = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        ProcessObservation before = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessObservation after = Root(11, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation result = EvaluateWithObserver(
            ProcessGateState.Empty,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            "epoch-a",
            false,
            before,
            after);

        Assert.Equal(ProcessProtectionHealthCode.CreationTimelineUntrusted, result.HealthCode);
        Assert.All(result.Decisions, decision =>
            Assert.Equal(ProcessGateDisposition.AllowFailOpen, decision.Disposition));
        Assert.Empty(result.State.EligibleInstances);
        Assert.Empty(result.State.TemporaryInstances);
    }

    [Theory]
    [InlineData("sameMetadata")]
    [InlineData("changedPath")]
    public void EveryUntrustedIdentityRemainsTaintedAcrossBatchesAndRecovery(string scenario)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        ProcessObservation configured = Root(10, At(7, 0, 6), rule.RootExecutablePath!);
        ProcessObservation first = scenario == "changedPath"
            ? configured with
            {
                Identity = configured.Identity! with { ExecutablePath = @"C:\Games\other.exe" },
            }
            : configured;
        ProcessGateEvaluation firstUntrusted = EvaluateWithObserver(
            ProcessGateState.Empty,
            Policy(At(7, 0, 7), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            false,
            first);
        ProcessGateEvaluation secondUntrusted = EvaluateWithObserver(
            firstUntrusted.State,
            Policy(At(7, 0, 8), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            false,
            configured);

        ProcessGateEvaluation recovered = EvaluateWithObserver(
            secondUntrusted.State,
            Policy(At(7, 0, 9), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            "epoch-a",
            true,
            configured);

        Assert.Contains(configured.Identity!.Key, firstUntrusted.State.TaintedInstances);
        Assert.Contains(configured.Identity.Key, secondUntrusted.State.TaintedInstances);
        Assert.Contains(configured.Identity.Key, recovered.State.TaintedInstances);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(recovered.Decisions).Disposition);
        Assert.DoesNotContain(configured.Identity.Key, recovered.State.EligibleInstances.Keys);
    }

    [Fact]
    public void TrustBreakWithoutObservationsTaintsEveryPreviouslyKnownIdentity()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);
        ProcessGateEvaluation known = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            root);
        ProcessGateEvaluation broken = EvaluateWithObserver(
            known.State,
            Policy(cutoff.AddSeconds(2), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            false);

        ProcessGateEvaluation recovered = EvaluateWithObserver(
            broken.State,
            Policy(cutoff.AddSeconds(3), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            "epoch-a",
            true,
            root);

        Assert.Contains(root.Identity!.Key, broken.State.TaintedInstances);
        Assert.Contains(root.Identity.Key, recovered.State.TaintedInstances);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(recovered.Decisions).Disposition);
    }

    [Fact]
    public void PidHintMismatchStillTaintsDuringUntrustedTimeline()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        ProcessObservation mismatch = Root(10, At(7, 0, 6), rule.RootExecutablePath!) with
        {
            PidHint = 99,
        };

        ProcessGateEvaluation result = EvaluateWithObserver(
            ProcessGateState.Empty,
            Policy(At(7, 0, 6), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            false,
            mismatch);

        Assert.Contains(mismatch.Identity!.Key, result.State.TaintedInstances);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(result.Decisions).Disposition);
    }

    [Fact]
    public void TaintSurvivesTimelineTrustLossAndRecoveryWithinTheNight()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation eligible = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            root);
        ProcessObservation conflict = root with
        {
            Identity = root.Identity! with { UserSid = "S-1-5-21-2000" },
        };
        ProcessGateEvaluation tainted = Evaluate(
            eligible.State,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            conflict);
        ProcessGateEvaluation untrusted = EvaluateWithObserver(
            tainted.State,
            Policy(cutoff.AddSeconds(2), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            false);

        ProcessGateEvaluation recovered = EvaluateWithObserver(
            untrusted.State,
            Policy(cutoff.AddSeconds(3), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            "epoch-a",
            true,
            root);

        Assert.Contains(root.Identity!.Key, recovered.State.TaintedInstances);
        Assert.DoesNotContain(root.Identity.Key, recovered.State.EligibleInstances.Keys);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(recovered.Decisions).Disposition);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("exactParent")]
    public void UntrustedEmptyStateTaintsCurrentBatchConflictsAndRecoveryCannotBlock(string conflictKind)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        ProcessObservation original = Root(10, At(7, 0, 6), rule.RootExecutablePath!);
        ProcessObservation conflicting = conflictKind switch
        {
            "path" => original with
            {
                Identity = original.Identity! with { ExecutablePath = @"C:\Games\other.exe" },
            },
            "exactParent" => original with
            {
                Parent = ParentLink.Exact(new ProcessInstanceKey(99, At(7, 0, 1).UtcTicks)),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(conflictKind)),
        };
        if (conflictKind == "exactParent")
        {
            original = original with
            {
                Parent = ParentLink.Exact(new ProcessInstanceKey(98, At(7, 0, 1).UtcTicks)),
            };
        }

        ProcessGateEvaluation untrusted = EvaluateWithObserver(
            ProcessGateState.Empty,
            Policy(At(7, 0, 7), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            false,
            original,
            conflicting);
        ProcessGateEvaluation recovered = EvaluateWithObserver(
            untrusted.State,
            Policy(At(7, 0, 8), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            "epoch-a",
            true,
            original);

        Assert.Contains(original.Identity!.Key, untrusted.State.TaintedInstances);
        Assert.Contains(original.Identity.Key, recovered.State.TaintedInstances);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(recovered.Decisions).Disposition);
        Assert.DoesNotContain(original.Identity.Key, recovered.State.EligibleInstances.Keys);
    }

    [Fact]
    public void MorningAllowsEverythingAndNewNightStartsClean()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            root);
        ProcessObservation morningNewRoot = Root(11, At(7, 9, 0), rule.RootExecutablePath!);

        ProcessGateEvaluation morning = Evaluate(
            sealedState.State,
            Policy(At(7, 9, 0), DesktopNightPhase.Morning, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            morningNewRoot);
        Assert.Equal(ProcessGateDisposition.AllowUnrestricted, Assert.Single(morning.Decisions).Disposition);
        Assert.Empty(morning.State.EligibleInstances);
        Assert.All(morning.State.RuleStates.Values, state => Assert.False(state.IsSealed));

        DateOnly nextNight = NightDate.AddDays(1);
        ProcessGateEvaluation next = Evaluate(
            morning.State,
            Policy(
                new DateTimeOffset(nextNight.ToDateTime(new TimeOnly(21, 0)), TimeSpan.Zero),
                DesktopNightPhase.Free,
                [rule],
                nightDate: nextNight),
            ProcessObservationBatchKind.StartDelta);
        Assert.Equal(nextNight, next.State.NightDate);
        Assert.Empty(next.State.KnownInstances);
        Assert.Empty(next.State.EligibleInstances);
    }

    [Fact]
    public void MorningReleaseCannotBeReversedByStaleSameNightGraceOrChangedWake()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        ProcessGateEvaluation morning = Evaluate(
            sealedState.State,
            Policy(At(7, 9, 0), DesktopNightPhase.Morning, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        DesktopPolicySnapshotDto staleGrace = Policy(
            cutoff.AddSeconds(1),
            DesktopNightPhase.Grace,
            [rule]) with
        {
            Window = Window(NightDate) with { Wake = At(7, 11, 0) },
        };
        ProcessObservation newRoot = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation afterMorning = Evaluate(
            morning.State,
            staleGrace,
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);

        Assert.Equal(ProcessProtectionHealthCode.Healthy, afterMorning.HealthCode);
        Assert.Equal(
            ProcessGateDisposition.AllowUnrestricted,
            Assert.Single(afterMorning.Decisions).Disposition);
        Assert.Equal(ProcessGateReason.Morning, Assert.Single(afterMorning.Decisions).Reason);
    }

    [Fact]
    public void ReachingWakeReleasesMorningEvenIfNoMorningPhaseWasObserved()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        ProcessObservation newRoot = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation afterWake = Evaluate(
            sealedState.State,
            Policy(At(7, 9, 0), DesktopNightPhase.Grace, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);

        Assert.True(afterWake.State.MorningReleased);
        Assert.Equal(ProcessProtectionHealthCode.Healthy, afterWake.HealthCode);
        Assert.Equal(
            ProcessGateDisposition.AllowUnrestricted,
            Assert.Single(afterWake.Decisions).Disposition);
        Assert.Equal(ProcessGateReason.Morning, Assert.Single(afterWake.Decisions).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DegradedWakeSnapshotPersistsMorningReleaseForSameOrFutureNight(
        bool advancesToFutureNight)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation eligibleRoot = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            eligibleRoot);
        DateOnly targetNight = advancesToFutureNight ? NightDate.AddDays(1) : NightDate;
        DesktopNightWindowDto targetWindow = Window(targetNight);
        DesktopAppRuleDto invalidRule = rule with { SessionMinutes = 14 };
        ProcessObservation newRoot = Root(
            11,
            targetWindow.Lock.AddMinutes(-35).AddTicks(1),
            rule.RootExecutablePath!);

        ProcessGateEvaluation degradedAtWake = Evaluate(
            sealedState.State,
            Policy(
                targetWindow.Wake,
                DesktopNightPhase.Grace,
                [invalidRule],
                nightDate: targetNight),
            ProcessObservationBatchKind.StartDelta,
            newRoot);
        DesktopPolicySnapshotDto rolledBackWithLaterWake = Policy(
            targetWindow.LastStart,
            DesktopNightPhase.Grace,
            [rule],
            nightDate: targetNight) with
        {
            Window = targetWindow with { Wake = targetWindow.Wake.AddHours(2) },
        };

        ProcessGateEvaluation afterRollback = Evaluate(
            degradedAtWake.State,
            rolledBackWithLaterWake,
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);

        Assert.Equal(ProcessProtectionHealthCode.InvalidRule, degradedAtWake.HealthCode);
        Assert.True(degradedAtWake.State.MorningReleased);
        Assert.Equal(targetNight, degradedAtWake.State.NightDate);
        Assert.Equal(targetWindow.Wake, degradedAtWake.State.LastEffectiveLogicalTime);
        Assert.Empty(degradedAtWake.State.KnownInstances);
        Assert.Empty(degradedAtWake.State.EligibleInstances);
        Assert.Empty(degradedAtWake.State.TemporaryInstances);
        Assert.Equal(ProcessProtectionHealthCode.Healthy, afterRollback.HealthCode);
        Assert.Equal(
            ProcessGateDisposition.AllowUnrestricted,
            Assert.Single(afterRollback.Decisions).Disposition);
        Assert.Equal(ProcessGateReason.Morning, Assert.Single(afterRollback.Decisions).Reason);
    }

    [Fact]
    public void InvalidBatchAtWakeStillPersistsTerminalMorningRelease()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation eligibleRoot = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            eligibleRoot);
        ProcessObservation newRoot = Root(11, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation invalidBatchAtWake = EvaluateWithObserver(
            sealedState.State,
            Policy(At(7, 9, 0), DesktopNightPhase.Grace, [rule]),
            (ProcessObservationBatchKind)int.MaxValue,
            "epoch-a",
            true,
            newRoot);
        DesktopPolicySnapshotDto rolledBackWithLaterWake = Policy(
            cutoff.AddSeconds(1),
            DesktopNightPhase.Grace,
            [rule]) with
        {
            Window = Window(NightDate) with { Wake = At(7, 11, 0) },
        };

        ProcessGateEvaluation afterRollback = Evaluate(
            invalidBatchAtWake.State,
            rolledBackWithLaterWake,
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);

        Assert.Equal(ProcessProtectionHealthCode.InvalidContext, invalidBatchAtWake.HealthCode);
        Assert.True(invalidBatchAtWake.State.MorningReleased);
        Assert.Empty(invalidBatchAtWake.State.KnownInstances);
        Assert.Empty(invalidBatchAtWake.State.EligibleInstances);
        Assert.Equal(
            ProcessGateDisposition.AllowUnrestricted,
            Assert.Single(afterRollback.Decisions).Disposition);
        Assert.Equal(ProcessGateReason.Morning, Assert.Single(afterRollback.Decisions).Reason);
    }

    [Fact]
    public void SealedNightCannotMoveCommittedWakeEarlier()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        DesktopPolicySnapshotDto earlierWake = Policy(
            At(7, 2, 0),
            DesktopNightPhase.Grace,
            [rule]) with
        {
            Window = Window(NightDate) with { Wake = At(7, 2, 0) },
        };
        ProcessObservation newRoot = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation beforeCommittedWake = Evaluate(
            sealedState.State,
            earlierWake,
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);
        ProcessGateEvaluation atCommittedWake = Evaluate(
            beforeCommittedWake.State,
            earlierWake with { EvaluatedAt = At(7, 9, 0) },
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);

        Assert.Equal(At(7, 9, 0), sealedState.State.CommittedWake);
        Assert.True(sealedState.State.IsCommittedWakeLocked);
        Assert.False(beforeCommittedWake.State.MorningReleased);
        Assert.Equal(
            ProcessGateDisposition.BlockNewRoot,
            Assert.Single(beforeCommittedWake.Decisions).Disposition);
        Assert.True(atCommittedWake.State.MorningReleased);
        Assert.Equal(
            ProcessGateDisposition.AllowUnrestricted,
            Assert.Single(atCommittedWake.Decisions).Disposition);
    }

    [Fact]
    public void SealedNightCannotMoveCommittedWakeLater()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        DesktopPolicySnapshotDto laterWake = Policy(
            At(7, 9, 0),
            DesktopNightPhase.Grace,
            [rule]) with
        {
            Window = Window(NightDate) with { Wake = At(7, 11, 0) },
        };
        ProcessObservation newRoot = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation atCommittedWake = Evaluate(
            sealedState.State,
            laterWake,
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);

        Assert.True(atCommittedWake.State.MorningReleased);
        Assert.Equal(
            ProcessGateDisposition.AllowUnrestricted,
            Assert.Single(atCommittedWake.Decisions).Disposition);
    }

    [Fact]
    public void InvalidSameNightWindowCannotHideReachedCommittedWake()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        DesktopPolicySnapshotDto invalidAtWake = Policy(
            At(7, 9, 0),
            DesktopNightPhase.Grace,
            [rule]) with
        {
            Window = Window(NightDate) with { Wake = At(7, 1, 0) },
        };
        ProcessObservation newRoot = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation degradedAtWake = Evaluate(
            sealedState.State,
            invalidAtWake,
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);
        ProcessGateEvaluation rollbackWithLaterWake = Evaluate(
            degradedAtWake.State,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.Grace, [rule]) with
            {
                Window = Window(NightDate) with { Wake = At(7, 11, 0) },
            },
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);

        Assert.Equal(ProcessProtectionHealthCode.InvalidContext, degradedAtWake.HealthCode);
        Assert.True(degradedAtWake.State.MorningReleased);
        Assert.Empty(degradedAtWake.State.KnownInstances);
        Assert.Equal(
            ProcessGateDisposition.AllowUnrestricted,
            Assert.Single(rollbackWithLaterWake.Decisions).Disposition);
    }

    [Fact]
    public void UnsealedNightCanLegitimatelyUpdateCommittedWakeBeforeSealing()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        DesktopPolicySnapshotDto laterWake = Policy(
            At(6, 23, 1),
            DesktopNightPhase.Free,
            [rule]) with
        {
            Window = Window(NightDate) with { Wake = At(7, 11, 0) },
        };
        ProcessGateEvaluation initial = Evaluate(
            ProcessGateState.Empty,
            Policy(At(6, 23, 0), DesktopNightPhase.Free, [rule]),
            ProcessObservationBatchKind.StartDelta);
        ProcessGateEvaluation adjusted = Evaluate(
            initial.State,
            laterWake,
            ProcessObservationBatchKind.StartDelta);
        ProcessGateEvaluation sealedState = Evaluate(
            adjusted.State,
            laterWake with
            {
                EvaluatedAt = cutoff,
                Phase = DesktopNightPhase.LastStart,
            },
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        ProcessObservation newRoot = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation atOriginalWake = Evaluate(
            sealedState.State,
            Policy(At(7, 9, 0), DesktopNightPhase.Grace, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);
        ProcessGateEvaluation atAdjustedWake = Evaluate(
            atOriginalWake.State,
            Policy(At(7, 11, 0), DesktopNightPhase.Grace, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);

        Assert.Equal(At(7, 11, 0), adjusted.State.CommittedWake);
        Assert.False(adjusted.State.IsCommittedWakeLocked);
        Assert.Equal(At(7, 11, 0), sealedState.State.CommittedWake);
        Assert.True(sealedState.State.IsCommittedWakeLocked);
        Assert.False(atOriginalWake.State.MorningReleased);
        Assert.Equal(
            ProcessGateDisposition.BlockNewRoot,
            Assert.Single(atOriginalWake.Decisions).Disposition);
        Assert.True(atAdjustedWake.State.MorningReleased);
        Assert.Equal(
            ProcessGateDisposition.AllowUnrestricted,
            Assert.Single(atAdjustedWake.Decisions).Disposition);
    }

    [Fact]
    public void ValidFutureNightEstablishesItsOwnCommittedWake()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation current = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        DateOnly nextNight = NightDate.AddDays(1);
        DesktopNightWindowDto nextWindow = Window(nextNight) with
        {
            Wake = new DateTimeOffset(
                nextNight.AddDays(1).ToDateTime(new TimeOnly(8, 0)),
                TimeSpan.Zero),
        };
        DesktopPolicySnapshotDto nextPolicy = Policy(
            nextWindow.ProtectedStart,
            DesktopNightPhase.Free,
            [rule],
            nightDate: nextNight) with
        {
            Window = nextWindow,
        };
        ProcessGateEvaluation next = Evaluate(
            current.State,
            nextPolicy,
            ProcessObservationBatchKind.StartDelta);
        ProcessGateEvaluation sealedNext = Evaluate(
            next.State,
            nextPolicy with
            {
                EvaluatedAt = nextWindow.Lock.AddMinutes(-35),
                Phase = DesktopNightPhase.LastStart,
            },
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        ProcessObservation newRoot = Root(
            10,
            nextWindow.Lock.AddMinutes(-35).AddTicks(1),
            rule.RootExecutablePath!);

        ProcessGateEvaluation atNewCommittedWake = Evaluate(
            sealedNext.State,
            nextPolicy with
            {
                EvaluatedAt = nextWindow.Wake,
                Phase = DesktopNightPhase.Grace,
                Window = nextWindow with { Wake = nextWindow.Wake.AddHours(3) },
            },
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            newRoot);

        Assert.Equal(nextWindow.Wake, next.State.CommittedWake);
        Assert.False(next.State.IsCommittedWakeLocked);
        Assert.Equal(nextWindow.Wake, sealedNext.State.CommittedWake);
        Assert.True(sealedNext.State.IsCommittedWakeLocked);
        Assert.Equal(nextNight, atNewCommittedWake.State.NightDate);
        Assert.True(atNewCommittedWake.State.MorningReleased);
        Assert.Equal(
            ProcessGateDisposition.AllowUnrestricted,
            Assert.Single(atNewCommittedWake.Decisions).Disposition);
    }

    [Fact]
    public void OlderNightPolicyFailsOpenWithoutResettingCurrentSealedState()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation eligibleRoot = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation current = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            eligibleRoot);
        DateOnly olderNight = NightDate.AddDays(-1);

        ProcessGateEvaluation stale = Evaluate(
            current.State,
            Policy(
                At(6, 9, 0),
                DesktopNightPhase.Morning,
                [rule],
                nightDate: olderNight),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            Root(11, At(6, 8, 0), rule.RootExecutablePath!));

        Assert.Equal(ProcessProtectionHealthCode.StaleNightPolicy, stale.HealthCode);
        Assert.Equal(current.State.NightDate, stale.State.NightDate);
        Assert.True(stale.State.RuleStates["game"].IsSealed);
        Assert.Contains(eligibleRoot.Identity!.Key, stale.State.EligibleInstances.Keys);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(stale.Decisions).Disposition);
    }

    [Fact]
    public void MorningBeforeWakeFailsOpenWithoutResettingCurrentSealedState()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation eligibleRoot = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation current = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            eligibleRoot);
        DesktopActiveOverrideDto emergency = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        ProcessObservation temporaryRoot = Root(11, cutoff.AddTicks(1), rule.RootExecutablePath!);
        ProcessGateEvaluation active = Evaluate(
            current.State,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            temporaryRoot);

        ProcessGateEvaluation fakeMorning = Evaluate(
            active.State,
            Policy(At(7, 0, 30), DesktopNightPhase.Morning, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            temporaryRoot);
        ProcessGateEvaluation replay = Evaluate(
            fakeMorning.State,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            temporaryRoot);

        Assert.Equal(ProcessProtectionHealthCode.InvalidMorningPolicy, fakeMorning.HealthCode);
        Assert.True(fakeMorning.State.RuleStates["game"].IsSealed);
        Assert.Contains(eligibleRoot.Identity!.Key, fakeMorning.State.EligibleInstances.Keys);
        Assert.Equal(At(7, 0, 30), fakeMorning.State.LastEffectiveLogicalTime);
        Assert.Empty(fakeMorning.State.TemporaryInstances);
        Assert.Null(fakeMorning.State.TemporaryOverrideIdentity);
        Assert.Contains(OverrideIdentity(emergency), fakeMorning.State.RetiredOverrideIdentities);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(fakeMorning.Decisions).Disposition);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(replay.Decisions).Disposition);
    }

    [Theory]
    [InlineData("clockRollback")]
    [InlineData("beforeProtectedStart")]
    public void FutureNightCannotResetCurrentStateWithoutForwardTimeEvidence(string scenario)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation eligibleRoot = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation current = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            eligibleRoot);
        DateOnly nextNight = NightDate.AddDays(1);
        DateTimeOffset evaluatedAt = scenario == "clockRollback"
            ? At(6, 23, 0)
            : new DateTimeOffset(
                nextNight.ToDateTime(new TimeOnly(20, 59)),
                TimeSpan.Zero);

        ProcessGateEvaluation premature = Evaluate(
            current.State,
            Policy(
                evaluatedAt,
                DesktopNightPhase.Free,
                [rule],
                nightDate: nextNight),
            ProcessObservationBatchKind.StartDelta,
            Root(11, evaluatedAt, rule.RootExecutablePath!));

        Assert.True(premature.IsDegraded);
        Assert.Equal(current.State.NightDate, premature.State.NightDate);
        Assert.True(premature.State.RuleStates["game"].IsSealed);
        Assert.Contains(eligibleRoot.Identity!.Key, premature.State.EligibleInstances.Keys);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(premature.Decisions).Disposition);
    }

    [Fact]
    public void MorningResetsEvenWhenRulesChangedAfterASealedNight()
    {
        DesktopAppRuleDto original = Rule("game", @"C:\Games\game.exe", 35);
        DesktopAppRuleDto changed = Rule("game", @"C:\Games\game.exe", 36);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [original]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);

        ProcessGateEvaluation morning = Evaluate(
            sealedState.State,
            Policy(At(7, 9, 0), DesktopNightPhase.Morning, [changed]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            Root(10, At(7, 9, 0), changed.RootExecutablePath!));

        Assert.Equal(ProcessProtectionHealthCode.Healthy, morning.HealthCode);
        Assert.Equal(ProcessGateDisposition.AllowUnrestricted, Assert.Single(morning.Decisions).Disposition);
        Assert.Equal(Lock.AddMinutes(-36), morning.State.RuleStates["game"].CutoffUtc);
        Assert.False(morning.State.RuleStates["game"].IsSealed);
    }

    [Fact]
    public void ClockRollbackCannotUnsealRuleOrReopenGate()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        ProcessObservation newRoot = Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!);

        ProcessGateEvaluation rolledBack = Evaluate(
            sealedState.State,
            Policy(cutoff.AddMinutes(-30), DesktopNightPhase.Free, [rule]),
            ProcessObservationBatchKind.StartDelta,
            newRoot);

        Assert.True(rolledBack.State.RuleStates["game"].IsSealed);
        Assert.Equal(cutoff.AddSeconds(1), rolledBack.State.LastEffectiveLogicalTime);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(rolledBack.Decisions).Disposition);
    }

    [Fact]
    public void MidNightRuleOrCutoffMutationAfterSealDegradesOnlyProcessProtectionOpen()
    {
        DesktopAppRuleDto original = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [original]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        DesktopAppRuleDto mutated = Rule("game", @"C:\Games\game.exe", 36);
        DesktopPolicySnapshotDto shiftedLock = Policy(
            cutoff.AddSeconds(1),
            DesktopNightPhase.LastStart,
            [original]) with
        {
            Window = Window(NightDate) with { Lock = Lock.AddMinutes(1) },
        };

        foreach (DesktopPolicySnapshotDto mutation in new[]
                 {
                     Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [mutated]),
                     shiftedLock,
                 })
        {
            ProcessGateEvaluation result = Evaluate(
                sealedState.State,
                mutation,
                ProcessObservationBatchKind.StartDelta,
                Root(10, cutoff.AddMinutes(1), original.RootExecutablePath!));
            Assert.Equal(ProcessProtectionHealthCode.SealedRuleMutation, result.HealthCode);
            Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(result.Decisions).Disposition);
        }
    }

    [Fact]
    public void InvalidRuleDegradationAdvancesTimeAndClearsTemporaryOverrideState()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DesktopActiveOverrideDto emergency = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        ProcessObservation root = Root(10, At(7, 0, 6), rule.RootExecutablePath!);
        ProcessGateEvaluation active = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            root);
        DesktopAppRuleDto invalid = rule with { SessionMinutes = 14 };

        ProcessGateEvaluation degraded = Evaluate(
            active.State,
            Policy(At(7, 0, 41), DesktopNightPhase.Grace, [invalid]),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessGateEvaluation replay = Evaluate(
            degraded.State,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            root);

        Assert.Equal(ProcessProtectionHealthCode.InvalidRule, degraded.HealthCode);
        Assert.Equal(At(7, 0, 41), degraded.State.LastEffectiveLogicalTime);
        Assert.Empty(degraded.State.TemporaryInstances);
        Assert.Null(degraded.State.TemporaryOverrideIdentity);
        Assert.Null(degraded.State.CapturedTeamRescueOverride);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(replay.Decisions).Disposition);
    }

    [Theory]
    [InlineData("invalidRule")]
    [InlineData("sealedMutation")]
    public void TrustBreakSeversLineageEvenWhenPolicyDegradesBeforeNormalEvaluation(string failure)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation current = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            root);
        DesktopAppRuleDto changed = failure == "invalidRule"
            ? rule with { SessionMinutes = 14 }
            : rule with { SessionMinutes = 36 };

        ProcessGateEvaluation degraded = EvaluateWithObserver(
            current.State,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [changed]),
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            false,
            root);

        Assert.Equal(
            failure == "invalidRule"
                ? ProcessProtectionHealthCode.InvalidRule
                : ProcessProtectionHealthCode.SealedRuleMutation,
            degraded.HealthCode);
        Assert.False(degraded.State.CreationTimelineTrusted);
        Assert.Empty(degraded.State.KnownInstances);
        Assert.Empty(degraded.State.EligibleInstances);
        Assert.Empty(degraded.State.TemporaryInstances);
        Assert.Null(degraded.State.PreOverrideBaselineObservedAtUtc);
    }

    [Fact]
    public void TrustBreakSeversLineageBeforeInvalidBatchKindDegrades()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation current = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            root);

        ProcessGateEvaluation degraded = EvaluateWithObserver(
            current.State,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            (ProcessObservationBatchKind)int.MaxValue,
            "epoch-a",
            false,
            root);

        Assert.Equal(ProcessProtectionHealthCode.InvalidContext, degraded.HealthCode);
        Assert.False(degraded.State.CreationTimelineTrusted);
        Assert.Empty(degraded.State.KnownInstances);
        Assert.Empty(degraded.State.EligibleInstances);
        Assert.Empty(degraded.State.TemporaryInstances);
        Assert.Null(degraded.State.PreOverrideBaselineObservedAtUtc);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(degraded.Decisions).Disposition);
    }

    [Fact]
    public void TrustBreakRetiresActiveOverrideEvenWhenPolicyStillDeclaresIt()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DesktopActiveOverrideDto emergency = Override(
            DesktopOverrideKind.Emergency,
            At(7, 0, 10),
            At(7, 0, 10),
            At(7, 0, 40));
        ProcessObservation root = Root(10, At(7, 0, 6), rule.RootExecutablePath!);
        ProcessGateEvaluation active = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            root);
        ProcessGateEvaluation broken = EvaluateWithObserver(
            active.State,
            Policy(At(7, 0, 21), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            false,
            root);

        ProcessGateEvaluation recovered = EvaluateWithObserver(
            broken.State,
            Policy(At(7, 0, 22), DesktopNightPhase.OverrideActive, [rule], emergency),
            ProcessObservationBatchKind.StartDelta,
            "epoch-a",
            true,
            root);

        Assert.Contains(OverrideIdentity(emergency), broken.State.RetiredOverrideIdentities);
        Assert.Contains(root.Identity!.Key, recovered.State.TaintedInstances);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(recovered.Decisions).Disposition);
        Assert.Empty(recovered.State.TemporaryInstances);
    }

    [Theory]
    [InlineData("epochChanged")]
    [InlineData("trustRecovered")]
    public void SealedRuleMutationDegradesBeforeAnyContinuityReset(string scenario)
    {
        DesktopAppRuleDto original = Rule("game", @"C:\Games\game.exe", 35);
        DesktopAppRuleDto mutated = Rule("game", @"C:\Games\game.exe", 36);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [original]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        ProcessGateState beforeMutation = sealedState.State;
        string epoch = "epoch-b";
        if (scenario == "trustRecovered")
        {
            ProcessGateEvaluation broken = EvaluateWithObserver(
                sealedState.State,
                Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [original]),
                ProcessObservationBatchKind.StartDelta,
                "epoch-a",
                false);
            beforeMutation = broken.State;
            epoch = "epoch-a";
        }

        ProcessObservation newRoot = Root(10, cutoff.AddTicks(1), original.RootExecutablePath!);
        ProcessGateEvaluation result = EvaluateWithObserver(
            beforeMutation,
            Policy(cutoff.AddSeconds(2), DesktopNightPhase.LastStart, [mutated]),
            ProcessObservationBatchKind.StartDelta,
            epoch,
            true,
            newRoot);

        Assert.Equal(ProcessProtectionHealthCode.SealedRuleMutation, result.HealthCode);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(result.Decisions).Disposition);
        Assert.Equal(sealedState.State.RuleFingerprint, result.State.RuleFingerprint);
        Assert.True(result.State.RuleStates["game"].IsSealed);
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("sealed")]
    public void MissingCommittedWakeInLockedOrSealedStateDegradesWithoutAdoptingNewWake(
        string scenario)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        ProcessGateState corrupted = sealedState.State with
        {
            CommittedWake = null,
            IsCommittedWakeLocked = scenario == "locked",
        };
        DesktopPolicySnapshotDto laterWake = Policy(
            At(7, 9, 0),
            DesktopNightPhase.Grace,
            [rule]) with
        {
            Window = Window(NightDate) with { Wake = At(7, 11, 0) },
        };

        ProcessGateEvaluation result = Evaluate(
            corrupted,
            laterWake,
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            Root(10, cutoff.AddTicks(1), rule.RootExecutablePath!));

        Assert.Equal(ProcessProtectionHealthCode.InvalidPersistedState, result.HealthCode);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(result.Decisions).Disposition);
        Assert.Null(result.State.CommittedWake);
    }

    [Theory]
    [InlineData("RuleStates")]
    [InlineData("KnownInstances")]
    [InlineData("EligibleInstances")]
    [InlineData("TemporaryInstances")]
    [InlineData("TaintedInstances")]
    [InlineData("RetiredOverrideIdentities")]
    public void NullPersistedCollectionsDegradeBeforeAnyStateEnumeration(string field)
    {
        ProcessGateState corrupted = field switch
        {
            "RuleStates" => ProcessGateState.Empty with { RuleStates = null! },
            "KnownInstances" => ProcessGateState.Empty with { KnownInstances = null! },
            "EligibleInstances" => ProcessGateState.Empty with { EligibleInstances = null! },
            "TemporaryInstances" => ProcessGateState.Empty with { TemporaryInstances = null! },
            "TaintedInstances" => ProcessGateState.Empty with { TaintedInstances = null! },
            "RetiredOverrideIdentities" => ProcessGateState.Empty with
            {
                RetiredOverrideIdentities = null!,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        ProcessGateEvaluation result = Evaluate(
            corrupted,
            Policy(At(6, 23, 0), DesktopNightPhase.Free, []),
            ProcessObservationBatchKind.StartDelta,
            Root(10, At(6, 23, 0), @"C:\Games\game.exe"));

        Assert.Equal(ProcessProtectionHealthCode.InvalidPersistedState, result.HealthCode);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(result.Decisions).Disposition);
    }

    [Theory]
    [InlineData("nullEntry")]
    [InlineData("keyMismatch")]
    [InlineData("missingCutoff")]
    [InlineData("missingFingerprint")]
    public void InvalidPersistedRuleStateMetadataDegradesBeforeEvaluation(string scenario)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        ProcessGateEvaluation initialized = Evaluate(
            ProcessGateState.Empty,
            Policy(At(6, 23, 0), DesktopNightPhase.Free, [rule]),
            ProcessObservationBatchKind.StartDelta);
        ProcessRuleGateState current = initialized.State.RuleStates["game"];
        ProcessGateState corrupted = scenario switch
        {
            "nullEntry" => initialized.State with
            {
                RuleStates = initialized.State.RuleStates.SetItem("game", null!),
            },
            "keyMismatch" => initialized.State with
            {
                RuleStates = initialized.State.RuleStates.SetItem(
                    "game",
                    current with { RuleId = "other" }),
            },
            "missingCutoff" => initialized.State with
            {
                RuleStates = initialized.State.RuleStates.SetItem(
                    "game",
                    current with { CutoffUtc = default }),
            },
            "missingFingerprint" => initialized.State with { RuleFingerprint = null },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        ProcessGateEvaluation result = Evaluate(
            corrupted,
            Policy(At(6, 23, 1), DesktopNightPhase.Free, [rule]),
            ProcessObservationBatchKind.StartDelta);

        Assert.Equal(ProcessProtectionHealthCode.InvalidPersistedState, result.HealthCode);
    }

    [Theory]
    [InlineData("missing", ProcessObservationBatchKind.StartDelta)]
    [InlineData("missing", ProcessObservationBatchKind.AuthoritativeSnapshot)]
    [InlineData("extra", ProcessObservationBatchKind.StartDelta)]
    [InlineData("extra", ProcessObservationBatchKind.AuthoritativeSnapshot)]
    [InlineData("cutoffMismatch", ProcessObservationBatchKind.StartDelta)]
    [InlineData("cutoffMismatch", ProcessObservationBatchKind.AuthoritativeSnapshot)]
    public void SameNightRuleStateMustExactlyCorrespondToCompiledRules(
        string scenario,
        ProcessObservationBatchKind batchKind)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        ProcessGateEvaluation initialized = Evaluate(
            ProcessGateState.Empty,
            Policy(At(6, 23, 0), DesktopNightPhase.Free, [rule]),
            ProcessObservationBatchKind.StartDelta);
        ProcessRuleGateState current = initialized.State.RuleStates["game"];
        ProcessGateState corrupted = scenario switch
        {
            "missing" => initialized.State with
            {
                RuleStates = initialized.State.RuleStates.Remove("game"),
            },
            "extra" => initialized.State with
            {
                RuleStates = initialized.State.RuleStates.Add(
                    "extra",
                    new ProcessRuleGateState("EXTRA", current.CutoffUtc, false)),
            },
            "cutoffMismatch" => initialized.State with
            {
                RuleStates = initialized.State.RuleStates.SetItem(
                    "GAME",
                    current with { CutoffUtc = current.CutoffUtc.AddMinutes(1) }),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        ProcessObservation root = Root(10, At(6, 23, 0), rule.RootExecutablePath!);

        ProcessGateEvaluation result = Evaluate(
            corrupted,
            Policy(At(6, 23, 1), DesktopNightPhase.Free, [rule]),
            batchKind,
            root);

        Assert.Equal(ProcessProtectionHealthCode.InvalidPersistedState, result.HealthCode);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(result.Decisions).Disposition);
        Assert.Equal(corrupted.RuleFingerprint, result.State.RuleFingerprint);
    }

    [Fact]
    public void FutureNightRebuildSkipsOldNightRuleStateCorrespondence()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        ProcessGateEvaluation current = Evaluate(
            ProcessGateState.Empty,
            Policy(At(6, 23, 0), DesktopNightPhase.Free, [rule]),
            ProcessObservationBatchKind.StartDelta);
        ProcessGateState corruptedOldNight = current.State with
        {
            RuleStates = current.State.RuleStates.Remove("game"),
        };
        DateOnly nextNight = NightDate.AddDays(1);
        DateTimeOffset nextProtectedStart = new(
            nextNight.ToDateTime(new TimeOnly(21, 0)),
            TimeSpan.Zero);

        ProcessGateEvaluation next = Evaluate(
            corruptedOldNight,
            Policy(
                nextProtectedStart,
                DesktopNightPhase.Free,
                [rule],
                nightDate: nextNight),
            ProcessObservationBatchKind.StartDelta);

        Assert.Equal(ProcessProtectionHealthCode.Healthy, next.HealthCode);
        Assert.Equal(nextNight, next.State.NightDate);
        Assert.Contains("game", next.State.RuleStates.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerminalMorningDoesNotReopenForRuleStateCorrespondenceDamage()
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        ProcessGateEvaluation morning = Evaluate(
            ProcessGateState.Empty,
            Policy(At(7, 9, 0), DesktopNightPhase.Morning, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);
        ProcessGateState damagedMorning = morning.State with
        {
            RuleStates = morning.State.RuleStates.Remove("game"),
        };
        ProcessObservation root = Root(10, At(7, 9, 1), rule.RootExecutablePath!);

        ProcessGateEvaluation result = Evaluate(
            damagedMorning,
            Policy(At(7, 9, 1), DesktopNightPhase.Grace, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            root);

        Assert.Equal(ProcessProtectionHealthCode.Healthy, result.HealthCode);
        Assert.True(result.State.MorningReleased);
        Assert.Equal(ProcessGateDisposition.AllowUnrestricted, Assert.Single(result.Decisions).Disposition);
    }

    [Theory]
    [InlineData("knownKey")]
    [InlineData("eligibleRule")]
    [InlineData("temporaryRule")]
    public void InvalidPersistedKnownOrGrantReferencesDegradeBeforeEvaluation(string scenario)
    {
        DesktopAppRuleDto rule = Rule("game", @"C:\Games\game.exe", 35);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessObservation root = Root(10, cutoff, rule.RootExecutablePath!);
        ProcessGateEvaluation eligible = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.AuthoritativeSnapshot,
            root);
        ProcessGateState corrupted;
        if (scenario == "knownKey")
        {
            ProcessKnownInstance known = eligible.State.KnownInstances[root.Identity!.Key];
            ProcessInstanceKey wrongKey = new(root.Identity.Key.Pid + 1, root.Identity.Key.CreationUtcTicks);
            corrupted = eligible.State with
            {
                KnownInstances = eligible.State.KnownInstances.SetItem(
                    root.Identity.Key,
                    known with { Identity = known.Identity with { Key = wrongKey } }),
            };
        }
        else if (scenario == "eligibleRule")
        {
            corrupted = eligible.State with
            {
                EligibleInstances = eligible.State.EligibleInstances.SetItem(
                    root.Identity!.Key,
                    "missing"),
            };
        }
        else
        {
            DesktopActiveOverrideDto emergency = Override(
                DesktopOverrideKind.Emergency,
                At(7, 0, 10),
                At(7, 0, 10),
                At(7, 0, 40));
            ProcessObservation temporaryRoot = Root(11, cutoff.AddTicks(1), rule.RootExecutablePath!);
            ProcessGateEvaluation active = Evaluate(
                eligible.State,
                Policy(At(7, 0, 20), DesktopNightPhase.OverrideActive, [rule], emergency),
                ProcessObservationBatchKind.StartDelta,
                temporaryRoot);
            TemporaryProcessGrant grant = active.State.TemporaryInstances[temporaryRoot.Identity!.Key];
            corrupted = active.State with
            {
                TemporaryInstances = active.State.TemporaryInstances.SetItem(
                    temporaryRoot.Identity.Key,
                    grant with { RuleId = "missing" }),
            };
            root = temporaryRoot;
        }

        ProcessGateEvaluation result = Evaluate(
            corrupted,
            Policy(cutoff.AddSeconds(1), DesktopNightPhase.LastStart, [rule]),
            ProcessObservationBatchKind.StartDelta,
            root);

        Assert.Equal(ProcessProtectionHealthCode.InvalidPersistedState, result.HealthCode);
        Assert.Equal(ProcessGateDisposition.AllowFailOpen, Assert.Single(result.Decisions).Disposition);
    }

    [Fact]
    public void NullObservationEntryDegradesWithoutDereferencingTheEntry()
    {
        ProcessGateEvaluation result = Evaluate(
            ProcessGateState.Empty,
            Policy(At(6, 23, 0), DesktopNightPhase.Free, []),
            ProcessObservationBatchKind.StartDelta,
            null!,
            Root(10, At(6, 23, 0), @"C:\Games\game.exe"));

        Assert.Equal(ProcessProtectionHealthCode.InvalidContext, result.HealthCode);
        Assert.Empty(result.Decisions);
        Assert.Equal(ProcessGateState.Empty, result.State);
    }

    [Fact]
    public void DegradedRuleMutationStillAdvancesMonotonicLogicalTime()
    {
        DesktopAppRuleDto original = Rule("game", @"C:\Games\game.exe", 35);
        DesktopAppRuleDto changed = Rule("game", @"C:\Games\game.exe", 36);
        DateTimeOffset cutoff = At(7, 0, 5);
        ProcessGateEvaluation sealedState = Evaluate(
            ProcessGateState.Empty,
            Policy(cutoff, DesktopNightPhase.LastStart, [original]),
            ProcessObservationBatchKind.AuthoritativeSnapshot);

        ProcessGateEvaluation degraded = Evaluate(
            sealedState.State,
            Policy(At(7, 0, 50), DesktopNightPhase.Grace, [changed]),
            ProcessObservationBatchKind.StartDelta);
        ProcessGateEvaluation restoredAfterRollback = Evaluate(
            degraded.State,
            Policy(At(7, 0, 10), DesktopNightPhase.LastStart, [original]),
            ProcessObservationBatchKind.StartDelta,
            Root(10, cutoff.AddTicks(1), original.RootExecutablePath!));

        Assert.Equal(At(7, 0, 50), degraded.State.LastEffectiveLogicalTime);
        Assert.Equal(At(7, 0, 50), restoredAfterRollback.State.LastEffectiveLogicalTime);
        Assert.Equal(ProcessGateDisposition.BlockNewRoot, Assert.Single(restoredAfterRollback.Decisions).Disposition);
    }

    [Fact]
    public void CleanPreCutoffRuleMutationResetsSafely()
    {
        DesktopAppRuleDto original = Rule("game", @"C:\Games\game.exe", 35);
        DesktopAppRuleDto mutated = Rule("game", @"C:\Games\game.exe", 36);
        ProcessGateEvaluation initial = Evaluate(
            ProcessGateState.Empty,
            Policy(At(6, 23, 0), DesktopNightPhase.Free, [original]),
            ProcessObservationBatchKind.StartDelta);

        ProcessGateEvaluation result = Evaluate(
            initial.State,
            Policy(At(6, 23, 1), DesktopNightPhase.Free, [mutated]),
            ProcessObservationBatchKind.StartDelta);

        Assert.Equal(ProcessProtectionHealthCode.Healthy, result.HealthCode);
        Assert.Equal(Lock.AddMinutes(-36), result.State.RuleStates["game"].CutoffUtc);
        Assert.False(result.State.RuleStates["game"].IsSealed);
    }

    private static ProcessGateEvaluation Evaluate(
        ProcessGateState state,
        DesktopPolicySnapshotDto policy,
        ProcessObservationBatchKind batchKind,
        params ProcessObservation[] observations) =>
        EvaluateWithObserver(
            state,
            policy,
            batchKind,
            "epoch-a",
            true,
            observations);

    private static ProcessGateEvaluation EvaluateWithObserver(
        ProcessGateState state,
        DesktopPolicySnapshotDto policy,
        ProcessObservationBatchKind batchKind,
        string observerEpoch,
        bool creationTimelineTrusted,
        params ProcessObservation[] observations) =>
        ProcessGateReducer.Evaluate(
            state,
            new ProcessGateContext(
                policy,
                UserSid,
                SessionId,
                observerEpoch,
                creationTimelineTrusted),
            batchKind,
            observations);

    private static DesktopPolicySnapshotDto Policy(
        DateTimeOffset evaluatedAt,
        DesktopNightPhase phase,
        IReadOnlyList<DesktopAppRuleDto> rules,
        DesktopActiveOverrideDto? activeOverride = null,
        DateOnly? nightDate = null) =>
        new(
            evaluatedAt,
            phase,
            Window(nightDate ?? NightDate),
            rules,
            [],
            true,
            false,
            activeOverride);

    private static DesktopNightWindowDto Window(DateOnly nightDate)
    {
        DateTimeOffset protectedStart = new(nightDate.ToDateTime(new TimeOnly(21, 0)), TimeSpan.Zero);
        DateTimeOffset lastStart = new(nightDate.AddDays(1).ToDateTime(new TimeOnly(0, 5)), TimeSpan.Zero);
        DateTimeOffset @lock = new(nightDate.AddDays(1).ToDateTime(new TimeOnly(0, 40)), TimeSpan.Zero);
        DateTimeOffset lightsOut = new(nightDate.AddDays(1).ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero);
        DateTimeOffset wake = new(nightDate.AddDays(1).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        return new(nightDate, protectedStart, lastStart, @lock, lightsOut, wake);
    }

    private static DesktopAppRuleDto Rule(
        string id,
        string rootPath,
        int sessionMinutes,
        DesktopAppRuleCategory category = DesktopAppRuleCategory.Game,
        params string[] helpers) =>
        new(id, rootPath, helpers, category, sessionMinutes, true);

    private static DesktopActiveOverrideDto Override(
        DesktopOverrideKind kind,
        DateTimeOffset requestedAt,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        params string[] allowedRuleIds) =>
        new(kind, requestedAt, startsAt, endsAt, allowedRuleIds);

    private static ProcessOverrideIdentity OverrideIdentity(DesktopActiveOverrideDto value) =>
        new(value.Kind, value.RequestedAtUtc, value.StartsAtUtc, value.EndsAtUtc);

    private static ProcessObservation Root(
        int pid,
        DateTimeOffset createdAt,
        string path,
        string sid = UserSid,
        int sessionId = SessionId) =>
        Complete(pid, createdAt, path, ParentLink.None, sid, sessionId);

    private static ProcessObservation Complete(
        int pid,
        DateTimeOffset createdAt,
        string path,
        ParentLink parent,
        string sid = UserSid,
        int sessionId = SessionId)
    {
        DateTimeOffset utc = createdAt.ToUniversalTime();
        ProcessInstanceKey key = new(pid, utc.UtcTicks);
        return new(
            pid,
            new ObservedProcessIdentity(key, utc, path, sid, sessionId),
            parent);
    }

    private static void AssertDecision(
        ProcessGateEvaluation result,
        int pid,
        ProcessGateDisposition disposition,
        DateTimeOffset cutoff)
    {
        ProcessGateDecision decision = Assert.Single(
            result.Decisions,
            candidate => candidate.InstanceKey?.Pid == pid);
        Assert.Equal(disposition, decision.Disposition);
        Assert.Equal(cutoff, decision.CutoffUtc);
    }

    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);
}
