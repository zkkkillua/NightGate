using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DesktopPolicyWitnessSourceTests
{
    private static readonly DateTimeOffset EvaluatedAt =
        new(2026, 7, 14, 15, 3, 2, TimeSpan.FromHours(8));

    [Fact]
    public async Task IdenticalSnapshot_ReplaysDeterministicRestartStableWitness()
    {
        DesktopPolicyResult policy = Policy();
        DesktopPolicyWitnessSource first = new(new FixedPolicyClient(policy));
        DesktopPolicyWitnessSource restarted = new(new FixedPolicyClient(policy));

        ProcessGatePolicySourceResult firstRead = await first.ReadAsync();
        ProcessGatePolicySourceResult replay = await first.ReadAsync();
        ProcessGatePolicySourceResult afterRestart = await restarted.ReadAsync();

        Assert.Equal(ProcessGateSourceStatus.Available, firstRead.Status);
        ValidatedProcessPolicy witness = Assert.IsType<ValidatedProcessPolicy>(firstRead.Policy);
        Assert.Equal(EvaluatedAt.UtcTicks, witness.Revision);
        Assert.Equal(64, witness.PayloadFingerprint.Length);
        Assert.Equal(witness, replay.Policy);
        Assert.Equal(witness, afterRestart.Policy);
        Assert.Null(firstRead.DegradationCode);
    }

    [Fact]
    public async Task SameEvaluationTimeWithChangedContent_ProducesConflictingWitness()
    {
        DesktopPolicyWitnessSource original = new(new FixedPolicyClient(Policy()));
        DesktopPolicyWitnessSource changed = new(new FixedPolicyClient(
            Policy(snapshot => snapshot with
            {
                SiteRules = [new DesktopSiteRuleDto("video.example")],
            })));

        ValidatedProcessPolicy first = Assert.IsType<ValidatedProcessPolicy>(
            (await original.ReadAsync()).Policy);
        ValidatedProcessPolicy second = Assert.IsType<ValidatedProcessPolicy>(
            (await changed.ReadAsync()).Policy);

        Assert.Equal(first.Revision, second.Revision);
        Assert.NotEqual(first.PayloadFingerprint, second.PayloadFingerprint);
        Assert.NotEqual(first.EvaluationIdentity, second.EvaluationIdentity);
    }

    [Fact]
    public async Task DegradedOrMalformedPolicy_FailsOpenWithoutWitness()
    {
        DesktopPolicyResult malformed = Policy(snapshot => snapshot with
        {
            AppRules = null!,
        });
        DesktopPolicyResult[] unavailable =
        [
            DesktopPolicyResult.FailOpen("service-unavailable"),
            malformed,
        ];

        foreach (DesktopPolicyResult candidate in unavailable)
        {
            ProcessGatePolicySourceResult result = await new DesktopPolicyWitnessSource(
                new FixedPolicyClient(candidate)).ReadAsync();

            Assert.Equal(ProcessGateSourceStatus.Unavailable, result.Status);
            Assert.Null(result.Policy);
            Assert.False(string.IsNullOrWhiteSpace(result.DegradationCode));
        }
    }

    [Fact]
    public async Task ClientFault_FailsOpenAndCallerCancellationPropagates()
    {
        DesktopPolicyWitnessSource throwing = new(new ThrowingPolicyClient());
        ProcessGatePolicySourceResult fault = await throwing.ReadAsync();
        Assert.Equal(ProcessGateSourceStatus.Unavailable, fault.Status);
        Assert.Null(fault.Policy);

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await throwing.ReadAsync(cancellation.Token));
    }

    private static DesktopPolicyResult Policy(
        Func<DesktopPolicySnapshotDto, DesktopPolicySnapshotDto>? mutate = null)
    {
        DateOnly night = new(2026, 7, 14);
        DesktopPolicySnapshotDto snapshot = new(
            EvaluatedAt,
            DesktopNightPhase.Grace,
            new(
                night,
                new DateTimeOffset(2026, 7, 14, 21, 0, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 7, 14, 23, 35, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 7, 15, 0, 10, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 7, 15, 0, 30, 0, TimeSpan.FromHours(8)),
                new DateTimeOffset(2026, 7, 15, 8, 30, 0, TimeSpan.FromHours(8))),
            [new DesktopAppRuleDto(
                "game",
                @"C:\Games\Game.exe",
                [@"C:\Games\Helper.exe"],
                DesktopAppRuleCategory.Game,
                35,
                true)],
            [new DesktopSiteRuleDto("example.com")],
            true,
            false,
            null);
        snapshot = mutate?.Invoke(snapshot) ?? snapshot;
        return new(
            true,
            false,
            null,
            new(true, false, null, snapshot));
    }

    private sealed class FixedPolicyClient(DesktopPolicyResult policy) : IDesktopPolicyClient
    {
        public ValueTask<DesktopPolicyResult> GetPolicyAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(policy);
        }

        public ValueTask<DesktopRecordEventResult> RecordEventAsync(
            PrivacySafeEventKind kind,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesktopRecordEventResult(true, null));
    }

    private sealed class ThrowingPolicyClient : IDesktopPolicyClient
    {
        public ValueTask<DesktopPolicyResult> GetPolicyAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("policy unavailable");
        }

        public ValueTask<DesktopRecordEventResult> RecordEventAsync(
            PrivacySafeEventKind kind,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesktopRecordEventResult(false, "unavailable"));
    }
}
