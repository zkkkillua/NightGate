using NightGate.Core;

namespace NightGate.Service.Tests;

public sealed class OverridePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 6, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TeamRescue_StartsImmediately_EndsAtExactlyTwentyMinutes()
    {
        NightState state = CreateState();
        ProgressState progress = ProgressState.Initial;

        OverrideDecision decision = CreatePolicy("game.exe", "voice.exe").Request(
            state,
            progress,
            new(OverrideKind.TeamRescue, null),
            Now);

        Assert.True(decision.Accepted);
        Assert.Equal(Now, decision.State.ActiveOverride!.StartsAtUtc);
        Assert.Equal(Now.AddMinutes(20), decision.State.ActiveOverride.EndsAtUtc);
        Assert.Equal(NightPhase.OverrideActive, OverridePolicy.ResolvePhase(decision.State, NightPhase.LandingLocked, Now));
        Assert.Equal(NightPhase.OverrideActive, OverridePolicy.ResolvePhase(decision.State, NightPhase.LandingLocked, Now.AddMinutes(20).AddTicks(-1)));
        Assert.Equal(NightPhase.LandingLocked, OverridePolicy.ResolvePhase(decision.State, NightPhase.LandingLocked, Now.AddMinutes(20)));
    }

    [Fact]
    public void TeamRescue_SnapshotsTrustedProcessIdentifiers()
    {
        MutableSnapshotProvider provider = new(["game.exe"]);
        OverrideRequest request = new(OverrideKind.TeamRescue, null);

        OverrideDecision decision = new OverridePolicy(provider).Request(
            CreateState(), ProgressState.Initial, request, Now);
        provider.Replace(["browser.exe"]);

        Assert.Equal(["game.exe"], decision.State.ActiveOverride!.AllowedProcessIdentifiers.ToArray());
    }

    [Fact]
    public void TeamRescue_ThrowingSnapshotProviderIsRejectedWithoutMutation()
    {
        NightState state = CreateState();
        ProgressState progress = ProgressState.Initial;

        OverrideDecision decision = new OverridePolicy(new ThrowingSnapshotProvider()).Request(
            state,
            progress,
            new(OverrideKind.TeamRescue, null),
            Now);

        Assert.False(decision.Accepted);
        Assert.Equal(OverrideError.TeamRescueUnavailable, decision.Error);
        Assert.Equal(state, decision.State);
        Assert.Equal(progress, decision.Progress);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public void TeamRescue_UsesRollingOneHundredSixtyEightHourBoundary(int offsetTicks, bool accepted)
    {
        ProgressState progress = ProgressState.Initial with { LastTeamRescueAtUtc = Now };
        DateTimeOffset requestedAt = Now.AddHours(168).AddTicks(offsetTicks);

        OverrideDecision decision = CreatePolicy("game.exe").Request(
            CreateState(),
            progress,
            new(OverrideKind.TeamRescue, null),
            requestedAt);

        Assert.Equal(accepted, decision.Accepted);
    }

    [Fact]
    public void Emergency_RequiresReason()
    {
        OverrideDecision decision = CreatePolicy().Request(
            CreateState(),
            ProgressState.Initial,
            new(OverrideKind.Emergency, null),
            Now);

        Assert.False(decision.Accepted);
        Assert.Equal(OverrideError.EmergencyReasonRequired, decision.Error);
    }

    [Fact]
    public void Emergency_RejectsUndefinedReason()
    {
        NightState state = CreateState();
        OverrideDecision decision = CreatePolicy().Request(
            state,
            ProgressState.Initial,
            new(OverrideKind.Emergency, (EmergencyReason)999),
            Now);

        Assert.False(decision.Accepted);
        Assert.Equal(OverrideError.EmergencyReasonRequired, decision.Error);
        Assert.False(decision.State.EmergencyUsed);
        Assert.Null(decision.State.ActiveOverride);
    }

    [Fact]
    public void Emergency_RejectsLegacyOtherReasonWithoutMutatingNight()
    {
        NightState state = CreateState();
        ProgressState progress = ProgressState.Initial;

        OverrideDecision decision = CreatePolicy().Request(
            state,
            progress,
            new(OverrideKind.Emergency, (EmergencyReason)3),
            Now);

        Assert.False(decision.Accepted);
        Assert.Equal(OverrideError.EmergencyReasonRequired, decision.Error);
        Assert.Equal(state, decision.State);
        Assert.Equal(progress, decision.Progress);
    }

    [Theory]
    [InlineData(EmergencyReason.Health)]
    [InlineData(EmergencyReason.Safety)]
    [InlineData(EmergencyReason.UrgentWork)]
    public void Emergency_StartsImmediately_EndsAtExactlyThirtyMinutes(EmergencyReason reason)
    {
        OverrideDecision decision = CreatePolicy().Request(
            CreateState(),
            ProgressState.Initial,
            new(OverrideKind.Emergency, reason),
            Now);

        Assert.True(decision.Accepted);
        Assert.Equal(Now, decision.State.ActiveOverride!.StartsAtUtc);
        Assert.Equal(Now.AddMinutes(30), decision.State.ActiveOverride.EndsAtUtc);
        Assert.True(decision.State.EmergencyUsed);
        Assert.Equal(NightPhase.OverrideActive, OverridePolicy.ResolvePhase(decision.State, NightPhase.LandingLocked, Now.AddMinutes(30).AddTicks(-1)));
        Assert.Equal(NightPhase.LandingLocked, OverridePolicy.ResolvePhase(decision.State, NightPhase.LandingLocked, Now.AddMinutes(30)));
    }

    [Fact]
    public void Emergency_CanBeRequestedAgain()
    {
        OverridePolicy policy = CreatePolicy();
        OverrideDecision first = policy.Request(
            CreateState(), ProgressState.Initial, new(OverrideKind.Emergency, EmergencyReason.Health), Now);

        OverrideDecision second = policy.Request(
            first.State, first.Progress, new(OverrideKind.Emergency, EmergencyReason.Safety), Now.AddMinutes(30));

        Assert.True(second.Accepted);
        Assert.Equal(Now.AddMinutes(60), second.State.ActiveOverride!.EndsAtUtc);
    }

    [Theory]
    [InlineData(OverrideKind.TeamRescue)]
    [InlineData(OverrideKind.Emergency)]
    [InlineData(OverrideKind.Entertainment)]
    public void Emergency_PreemptsAnyActiveOverride_AndStartsAFullThirtyMinuteWindow(
        OverrideKind activeKind)
    {
        OverridePolicy policy = CreatePolicy("game.exe");
        OverrideDecision active = policy.Request(
            CreateState(),
            ProgressState.Initial,
            Request(activeKind),
            Now);
        DateTimeOffset emergencyRequestedAt = Now.AddMinutes(1);

        OverrideDecision emergency = policy.Request(
            active.State,
            active.Progress,
            new(OverrideKind.Emergency, EmergencyReason.Safety),
            emergencyRequestedAt);

        Assert.True(active.Accepted);
        Assert.True(emergency.Accepted);
        Assert.Equal(OverrideKind.Emergency, emergency.State.ActiveOverride!.Kind);
        Assert.Equal(emergencyRequestedAt, emergency.State.ActiveOverride.StartsAtUtc);
        Assert.Equal(emergencyRequestedAt.AddMinutes(30), emergency.State.ActiveOverride.EndsAtUtc);
        Assert.True(emergency.State.EmergencyUsed);
    }

    [Theory]
    [InlineData(OverrideKind.TeamRescue, OverrideKind.Entertainment)]
    [InlineData(OverrideKind.Emergency, OverrideKind.Entertainment)]
    [InlineData(OverrideKind.Entertainment, OverrideKind.TeamRescue)]
    public void ActiveOverride_CannotBePreemptedByANonEmergencyRequest(
        OverrideKind firstKind,
        OverrideKind secondKind)
    {
        OverridePolicy policy = CreatePolicy("game.exe");
        OverrideDecision first = policy.Request(
            CreateState(),
            ProgressState.Initial,
            Request(firstKind),
            Now);

        OverrideDecision second = policy.Request(
            first.State,
            first.Progress,
            Request(secondKind),
            Now.AddMinutes(1));

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.Equal(OverrideError.OverrideAlreadyActive, second.Error);
        Assert.Equal(first.State.ActiveOverride, second.State.ActiveOverride);
    }

    [Fact]
    public void Entertainment_CoolsForExactlyTenMinutes_ThenRunsForExactlyTwentyMinutes()
    {
        OverrideDecision decision = CreatePolicy().Request(
            CreateState(), ProgressState.Initial, new(OverrideKind.Entertainment, null), Now);

        Assert.True(decision.Accepted);
        Assert.Equal(Now.AddMinutes(10), decision.State.ActiveOverride!.StartsAtUtc);
        Assert.Equal(Now.AddMinutes(30), decision.State.ActiveOverride.EndsAtUtc);
        Assert.Equal(NightPhase.CoolingOff, OverridePolicy.ResolvePhase(decision.State, NightPhase.LandingLocked, Now));
        Assert.Equal(NightPhase.CoolingOff, OverridePolicy.ResolvePhase(decision.State, NightPhase.LandingLocked, Now.AddMinutes(10).AddTicks(-1)));
        Assert.Equal(NightPhase.OverrideActive, OverridePolicy.ResolvePhase(decision.State, NightPhase.LandingLocked, Now.AddMinutes(10)));
        Assert.Equal(NightPhase.OverrideActive, OverridePolicy.ResolvePhase(decision.State, NightPhase.LandingLocked, Now.AddMinutes(30).AddTicks(-1)));
        Assert.Equal(NightPhase.LandingLocked, OverridePolicy.ResolvePhase(decision.State, NightPhase.LandingLocked, Now.AddMinutes(30)));
    }

    [Fact]
    public void Entertainment_CannotRenewOrBeRequestedAgainForSameNight()
    {
        OverridePolicy policy = CreatePolicy();
        OverrideDecision first = policy.Request(
            CreateState(), ProgressState.Initial, new(OverrideKind.Entertainment, null), Now);

        OverrideDecision duringCooling = policy.Request(
            first.State, first.Progress, new(OverrideKind.Entertainment, null), Now.AddMinutes(5));
        OverrideDecision afterEnd = policy.Request(
            first.State, first.Progress, new(OverrideKind.Entertainment, null), Now.AddMinutes(31));

        Assert.False(duringCooling.Accepted);
        Assert.Equal(OverrideError.AlreadyUsedTonight, duringCooling.Error);
        Assert.False(afterEnd.Accepted);
        Assert.Equal(OverrideError.AlreadyUsedTonight, afterEnd.Error);
    }

    [Fact]
    public void PolicySnapshot_PreservesOldConstructorAndAddsFailOpenOverrideStatus()
    {
        NightWindow window = new(new(2026, 7, 6), Now, Now, Now, Now, Now);
        PolicySnapshot existing = new(Now, NightPhase.Free, window, [], []);
        ActiveOverride activeOverride = new(OverrideKind.Emergency, Now, Now, Now.AddMinutes(30), []);
        PolicySnapshot degraded = new(Now, NightPhase.OverrideActive, window, [], [], false, true, activeOverride);

        Assert.True(existing.EnforcementEnabled);
        Assert.False(existing.IsDegraded);
        Assert.Null(existing.ActiveOverride);
        Assert.False(degraded.EnforcementEnabled);
        Assert.True(degraded.IsDegraded);
        Assert.Same(activeOverride, degraded.ActiveOverride);
    }

    private static NightState CreateState() => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        new DateOnly(2026, 7, 6),
        Now,
        NightPhase.LandingLocked,
        null,
        false,
        false,
        false,
        false,
        false,
        false);

    private static OverridePolicy CreatePolicy(params string[] identifiers) =>
        new(new MutableSnapshotProvider([.. identifiers]));

    private static OverrideRequest Request(OverrideKind kind) => new(
        kind,
        kind == OverrideKind.Emergency ? EmergencyReason.Health : null);

    private sealed class MutableSnapshotProvider(
        System.Collections.Immutable.ImmutableArray<string> snapshot) : IAllowedProcessSnapshotProvider
    {
        private System.Collections.Immutable.ImmutableArray<string> _snapshot = snapshot;

        public System.Collections.Immutable.ImmutableArray<string> GetSnapshot() => _snapshot;

        public void Replace(System.Collections.Immutable.ImmutableArray<string> replacement) =>
            _snapshot = replacement;
    }

    private sealed class ThrowingSnapshotProvider : IAllowedProcessSnapshotProvider
    {
        public System.Collections.Immutable.ImmutableArray<string> GetSnapshot() =>
            throw new IOException("configured-rules-unavailable");
    }
}
