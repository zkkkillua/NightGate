using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class ProcessCatalogPolicyBindingTests
{
    private const string UserSid = "S-1-5-21-1000";

    [Fact]
    public void Create_UsesTheValidatedWitnessAndCanonicalSortedDeduplicatedPaths()
    {
        ValidatedProcessPolicy policy = Policy(
            revision: 17,
            identity: "policy-17",
            evaluatedAt: At(6, 21, 0),
            phase: DesktopNightPhase.Free,
            rules:
            [
                Rule(
                    @"\\?\C:\Games\GAME.exe",
                    [@"C:\Games\helper.exe", @"c:\games\HELPER.exe"]),
                Rule(
                    @"\\?\UNC\server\share\voice.exe",
                    [],
                    "voice"),
            ]);

        bool created = ProcessCatalogPolicyBinding.TryCreate(
            policy,
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? binding);

        Assert.True(created);
        Assert.NotNull(binding);
        Assert.Equal(17, binding.PolicyRevision);
        Assert.Equal("policy-17", binding.EvaluationIdentity);
        Assert.Equal("payload-policy-17", binding.PayloadFingerprint);
        Assert.Equal(At(6, 21, 0), binding.EvaluatedAtUtc);
        Assert.Equal(new DateOnly(2026, 7, 6), binding.NightDate);
        Assert.True(binding.MonitoringActive);
        Assert.Equal(UserSid, binding.InteractiveUserSid);
        Assert.Equal(7, binding.InteractiveSessionId);
        Assert.Equal(
            [
                @"C:\Games\GAME.exe",
                @"C:\Games\helper.exe",
                @"\\server\share\voice.exe",
            ],
            binding.CanonicalExecutablePaths,
            StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DesktopNightPhase.Free, -1, false)]
    [InlineData(DesktopNightPhase.Free, 0, true)]
    [InlineData(DesktopNightPhase.OverrideActive, 60, true)]
    [InlineData(DesktopNightPhase.Morning, 60, false)]
    [InlineData(DesktopNightPhase.Grace, 720, false)]
    public void Create_DerivesTheWholeProtectedMonitoringWindow(
        DesktopNightPhase phase,
        int minutesAfterProtectedStart,
        bool expectedActive)
    {
        DateTimeOffset evaluatedAt = At(6, 21, 0).AddMinutes(minutesAfterProtectedStart);
        ValidatedProcessPolicy policy = Policy(1, "policy-1", evaluatedAt, phase, [Rule()]);

        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            policy,
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? binding));
        Assert.Equal(expectedActive, binding!.MonitoringActive);
    }

    [Fact]
    public void Create_StoresEvaluatedTimeAsUtc()
    {
        DateTimeOffset localEvaluation = new(
            2026,
            7,
            7,
            0,
            10,
            0,
            TimeSpan.FromHours(8));
        ValidatedProcessPolicy policy = Policy(
            4,
            "policy-4",
            localEvaluation,
            DesktopNightPhase.Grace,
            [Rule()]);

        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            policy,
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? binding));
        Assert.Equal(localEvaluation.ToUniversalTime(), binding!.EvaluatedAtUtc);
        Assert.Equal(TimeSpan.Zero, binding.EvaluatedAtUtc.Offset);
    }

    [Fact]
    public void Comparison_IsStructuralAcrossFreshImmutableArraysAndCaseVariants()
    {
        ValidatedProcessPolicy firstPolicy = Policy(
            5,
            "policy-5",
            At(6, 21, 0),
            DesktopNightPhase.Free,
            [Rule(@"C:\Games\GAME.exe", [@"C:\Games\helper.exe"])]);
        ValidatedProcessPolicy secondPolicy = Policy(
            5,
            "policy-5",
            At(6, 21, 0),
            DesktopNightPhase.Free,
            [Rule(@"c:\games\game.exe", [@"c:\games\HELPER.exe"])]);

        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            firstPolicy,
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? first));
        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            secondPolicy,
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? second));

        Assert.False(first!.CanonicalExecutablePaths.Equals(second!.CanonicalExecutablePaths));
        Assert.True(first.HasSamePolicyWitness(second));
        Assert.True(first.HasSameEffectiveScope(second));
        Assert.True(first.IsExactReplayOf(second));
    }

    [Fact]
    public void Comparison_SeparatesANewWitnessFromAnEffectiveScopeChange()
    {
        ValidatedProcessPolicy original = Policy(
            6,
            "policy-6",
            At(6, 21, 0),
            DesktopNightPhase.Free,
            [Rule()]);
        ValidatedProcessPolicy newerSameScope = Policy(
            7,
            "policy-7",
            At(6, 21, 1),
            DesktopNightPhase.Free,
            [Rule()]);
        ValidatedProcessPolicy sameWitnessDifferentScope = Policy(
            6,
            "policy-6",
            At(6, 21, 0),
            DesktopNightPhase.Free,
            [Rule(@"C:\Games\other.exe")]);

        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            original, UserSid, 7, out ProcessCatalogPolicyBinding? first));
        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            newerSameScope, UserSid, 7, out ProcessCatalogPolicyBinding? second));
        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            sameWitnessDifferentScope,
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? conflict));

        Assert.False(first!.HasSamePolicyWitness(second));
        Assert.True(first.HasSameEffectiveScope(second));
        Assert.False(first.IsExactReplayOf(second));
        Assert.True(first.HasSamePolicyWitness(conflict));
        Assert.False(first.HasSameEffectiveScope(conflict));
        Assert.False(first.IsExactReplayOf(conflict));
    }

    [Fact]
    public void Comparison_RejectsAReusedRevisionOrEvaluationIdentityWithChangedEvidence()
    {
        ValidatedProcessPolicy original = Policy(
            9,
            "policy-9",
            At(6, 21, 0),
            DesktopNightPhase.Free,
            [Rule()]);
        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            original,
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? baseline));

        ProcessCatalogPolicyBinding changedFingerprint = baseline! with
        {
            PayloadFingerprint = "changed",
        };
        ProcessCatalogPolicyBinding changedEvaluationTime = baseline with
        {
            EvaluatedAtUtc = baseline.EvaluatedAtUtc.AddTicks(1),
        };
        ProcessCatalogPolicyBinding reusedIdentityAtAnotherRevision = baseline with
        {
            PolicyRevision = baseline.PolicyRevision + 1,
        };
        ProcessCatalogPolicyBinding reusedRevisionWithAnotherIdentity = baseline with
        {
            EvaluationIdentity = "another-identity",
        };
        ProcessCatalogPolicyBinding changedScope = baseline with
        {
            CanonicalExecutablePaths = [@"C:\Games\other.exe"],
        };

        Assert.True(changedFingerprint.IsCorruptReplayOf(baseline));
        Assert.True(changedEvaluationTime.IsCorruptReplayOf(baseline));
        Assert.True(reusedIdentityAtAnotherRevision.IsCorruptReplayOf(baseline));
        Assert.True(reusedRevisionWithAnotherIdentity.IsCorruptReplayOf(baseline));
        Assert.True(changedScope.IsCorruptReplayOf(baseline));
        Assert.Equal(
            ProcessCatalogPolicyBindingRelation.ConflictingReplay,
            ProcessCatalogPolicyBinding.Classify(baseline, changedFingerprint));
    }

    [Fact]
    public void Comparison_TreatsANewRevisionAndIdentityAsANewWitness()
    {
        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            Policy(10, "policy-10", At(6, 21, 0), DesktopNightPhase.Free, [Rule()]),
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? baseline));
        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            Policy(11, "policy-11", At(6, 21, 1), DesktopNightPhase.Free, [Rule()]),
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? next));

        Assert.False(next!.IsCorruptReplayOf(baseline));
        Assert.False(next.HasSamePolicyWitness(baseline));
        Assert.True(next.HasSameEffectiveScope(baseline));
        Assert.Equal(
            ProcessCatalogPolicyBindingRelation.NewWitnessSameScope,
            ProcessCatalogPolicyBinding.Classify(baseline, next));
    }

    [Fact]
    public void Comparison_ClassifiesExactChangedStaleAndMalformedBindings()
    {
        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            Policy(12, "policy-12", At(6, 21, 0), DesktopNightPhase.Free, [Rule()]),
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? baseline));
        ProcessCatalogPolicyBinding changedScope = baseline! with
        {
            PolicyRevision = 13,
            EvaluationIdentity = "policy-13",
            EvaluatedAtUtc = baseline.EvaluatedAtUtc.AddMinutes(1),
            CanonicalExecutablePaths = [@"C:\Games\other.exe"],
        };
        ProcessCatalogPolicyBinding stale = baseline with
        {
            PolicyRevision = 11,
            EvaluationIdentity = "policy-11",
        };
        ProcessCatalogPolicyBinding malformed = baseline with
        {
            CanonicalExecutablePaths = default,
        };

        Assert.Equal(
            ProcessCatalogPolicyBindingRelation.ExactReplay,
            ProcessCatalogPolicyBinding.Classify(baseline, baseline));
        Assert.Equal(
            ProcessCatalogPolicyBindingRelation.NewWitnessChangedScope,
            ProcessCatalogPolicyBinding.Classify(baseline, changedScope));
        Assert.Equal(
            ProcessCatalogPolicyBindingRelation.StaleWitness,
            ProcessCatalogPolicyBinding.Classify(baseline, stale));
        Assert.Equal(
            ProcessCatalogPolicyBindingRelation.Malformed,
            ProcessCatalogPolicyBinding.Classify(baseline, malformed));
        Assert.Equal(
            ProcessCatalogPolicyBindingRelation.Malformed,
            ProcessCatalogPolicyBinding.Classify(null, baseline));
    }

    [Fact]
    public void Comparison_RejectsNullOrDefaultPathCollectionsWithoutThrowing()
    {
        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            Policy(8, "policy-8", At(6, 21, 0), DesktopNightPhase.Free, [Rule()]),
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? valid));
        ProcessCatalogPolicyBinding malformed = valid! with
        {
            CanonicalExecutablePaths = default,
        };

        Assert.False(valid.HasSameEffectiveScope(null));
        Assert.False(valid.HasSameEffectiveScope(malformed));
        Assert.False(valid.IsExactReplayOf(malformed));
    }

    [Fact]
    public void Create_KeepsStructurallyValidDegradedPolicyActive()
    {
        ValidatedProcessPolicy executable = Policy(
            2,
            "policy-2",
            At(7, 0, 10),
            DesktopNightPhase.Grace,
            [Rule()]);
        ValidatedProcessPolicy degraded = executable with
        {
            PolicyResult = executable.PolicyResult with
            {
                CanEnforce = false,
                IsDegraded = true,
                DegradationCode = "service-degraded",
            },
        };

        Assert.True(ProcessCatalogPolicyBinding.TryCreate(
            degraded,
            UserSid,
            7,
            out ProcessCatalogPolicyBinding? binding));
        Assert.True(binding!.MonitoringActive);
    }

    [Theory]
    [InlineData(@"game.exe")]
    [InlineData(@"\\.\C:\Games\game.exe")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\game.exe")]
    [InlineData("C:\\Games\\bad\0.exe")]
    [InlineData(@"C:\Games\game.com")]
    public void Create_RejectsAPathTheSharedCanonicalizerCannotProve(string path)
    {
        ValidatedProcessPolicy policy = Policy(
            3,
            "policy-3",
            At(7, 0, 10),
            DesktopNightPhase.Grace,
            [Rule(path)]);

        Assert.False(ProcessCatalogPolicyBinding.TryCreate(
            policy,
            UserSid,
            7,
            out _));
    }

    private static ValidatedProcessPolicy Policy(
        long revision,
        string identity,
        DateTimeOffset evaluatedAt,
        DesktopNightPhase phase,
        IReadOnlyList<DesktopAppRuleDto> rules)
    {
        DesktopPolicySnapshotDto snapshot = new(
            evaluatedAt,
            phase,
            new(
                new DateOnly(2026, 7, 6),
                At(6, 21, 0),
                At(7, 0, 5),
                At(7, 0, 40),
                At(7, 1, 0),
                At(7, 9, 0)),
            rules,
            [],
            true,
            false,
            null);
        return new(
            revision,
            identity,
            $"payload-{identity}",
            new(
                true,
                false,
                null,
                new(true, false, null, snapshot)),
            snapshot);
    }

    private static DesktopAppRuleDto Rule(
        string root = @"C:\Games\game.exe",
        IReadOnlyList<string>? helpers = null,
        string id = "game") =>
        new(
            id,
            root,
            helpers ?? [],
            DesktopAppRuleCategory.Game,
            35,
            true);

    private static DateTimeOffset At(int day, int hour, int minute) =>
        new(2026, 7, day, hour, minute, 0, TimeSpan.Zero);
}
