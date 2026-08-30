using System.Collections.Immutable;

namespace NightGate.Desktop.Tests;

public sealed class Win32ProcessCatalogTests
{
    private static readonly DateTimeOffset StartUtc =
        new(2026, 7, 14, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompleteScanRequiresDurableSeverHandshakeBeforeAuthoritativeRecovery()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(Row(41, 0, "game.exe")),
            Snapshot.Completed(Row(41, 0, "game.exe")));
        FakeIdentityReader identities = new();
        identities.Enqueue(41, Success(41, StartUtc.AddSeconds(-10), @"C:\Games\game.exe"));
        identities.Enqueue(41, Success(41, StartUtc.AddSeconds(-10), @"C:\Games\game.exe"));
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        DurableProcessSourceContinuity continuity = Continuity("epoch-lost", "epoch-recovery");
        Win32ProcessCatalog catalog = new(native, identities, continuity, clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");

        ProcessObservationBatchEvidence lost = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.AuthoritativeSnapshot, binding));

        Assert.Equal(ProcessGateSourceStatus.Available, lost.Status);
        Assert.Equal(ProcessObservationBatchKind.StartDelta, lost.BatchKind);
        Assert.True(lost.IsComplete);
        Assert.False(lost.IsAuthoritativeAllProcessCatalog);
        Assert.False(lost.CreationTimelineTrusted);
        Assert.True(lost.ContinuityLost);
        Assert.Equal("epoch-lost", lost.ObserverEpoch);
        ProcessObservation first = Assert.Single(lost.Observations);
        Assert.Equal(ParentLinkKind.Unknown, first.Parent.Kind);

        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "epoch-lost",
            1));
        clock.Advance(TimeSpan.FromMilliseconds(250));

        ProcessObservationBatchEvidence recovery = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, binding with
            {
                PolicyRevision = 2,
                EvaluationIdentity = "evaluation-2",
                PayloadFingerprint = "payload-2",
                EvaluatedAtUtc = binding.EvaluatedAtUtc.AddMilliseconds(250),
            }));

        Assert.Equal(ProcessGateSourceStatus.Available, recovery.Status);
        Assert.Equal(ProcessObservationBatchKind.AuthoritativeSnapshot, recovery.BatchKind);
        Assert.True(recovery.IsComplete);
        Assert.True(recovery.IsAuthoritativeAllProcessCatalog);
        Assert.True(recovery.CreationTimelineTrusted);
        Assert.False(recovery.ContinuityLost);
        Assert.Equal("epoch-recovery", recovery.ObserverEpoch);
        ProcessObservation second = Assert.Single(recovery.Observations);
        Assert.Equal(ParentLinkKind.None, second.Parent.Kind);
        Assert.Equal(2, native.CreateCalls);
    }

    [Fact]
    public async Task BasenameIsOnlyCandidateFilterAndExactPathDecides()
    {
        FakeCatalogNative native = new(Snapshot.Completed(
            Row(51, 0, "GAME.EXE"),
            Row(52, 0, "unrelated.exe")));
        FakeIdentityReader identities = new();
        identities.Enqueue(51, Success(
            51,
            StartUtc.AddSeconds(-5),
            @"D:\Other\game.exe"));
        identities.Enqueue(52, ProcessCatalogIdentityReadResult.Failure(
            Win32ProcessIdentityReadStatus.AccessDenied));
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);

        ProcessObservationBatchEvidence result = await catalog.ReadBatchAsync(
            new(
                ProcessObservationBatchKind.StartDelta,
                Binding(@"C:\Games\game.exe")));

        Assert.Equal(ProcessGateSourceStatus.Available, result.Status);
        Assert.True(result.IsComplete);
        Assert.Empty(result.Observations);
        Assert.Equal([51], identities.ReadPids);
    }

    [Theory]
    [InlineData((int)Win32ProcessIdentityReadStatus.AccessDenied)]
    [InlineData((int)Win32ProcessIdentityReadStatus.Exited)]
    [InlineData((int)Win32ProcessIdentityReadStatus.Ambiguous)]
    [InlineData((int)Win32ProcessIdentityReadStatus.Unavailable)]
    public async Task RelevantCandidateIdentityFailureNeverBecomesCompleteOrAuthoritative(
        int failureValue)
    {
        Win32ProcessIdentityReadStatus failure =
            (Win32ProcessIdentityReadStatus)failureValue;
        FakeCatalogNative native = new(Snapshot.Completed(Row(61, 0, "game.exe")));
        FakeIdentityReader identities = new();
        identities.Enqueue(61, ProcessCatalogIdentityReadResult.Failure(failure));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            new FakeCatalogClock(StartUtc, TimeSpan.FromHours(8)));

        ProcessObservationBatchEvidence result = await catalog.ReadBatchAsync(
            new(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                Binding(@"C:\Games\game.exe")));

        Assert.Equal(ProcessGateSourceStatus.Unavailable, result.Status);
        Assert.False(result.IsComplete);
        Assert.False(result.IsAuthoritativeAllProcessCatalog);
        Assert.False(result.CreationTimelineTrusted);
        Assert.True(result.ContinuityLost);
    }

    [Fact]
    public async Task PidZeroToolhelpRowIsIgnoredWithoutBreakingCompleteSnapshot()
    {
        FakeCatalogNative native = new(Snapshot.Completed(
            Row(0, 0, "System Idle Process"),
            Row(66, 0, "game.exe")));
        FakeIdentityReader identities = new();
        identities.Enqueue(66, Success(66, StartUtc.AddSeconds(-2), @"C:\Games\game.exe"));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            new FakeCatalogClock(StartUtc, TimeSpan.FromHours(8)));

        ProcessObservationBatchEvidence result = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Binding(@"C:\Games\game.exe")));

        Assert.Equal(ProcessGateSourceStatus.Available, result.Status);
        Assert.True(result.IsComplete);
        Assert.Equal(66, Assert.Single(result.Observations).Identity!.Key.Pid);
        Assert.Equal([66], identities.ReadPids);
    }

    [Theory]
    [InlineData("invalid-handle")]
    [InlineData("first-error")]
    [InlineData("next-error")]
    public async Task ToolhelpFailuresNeverBecomeAuthoritative(string scenario)
    {
        Snapshot snapshot = scenario switch
        {
            "invalid-handle" => Snapshot.Invalid(),
            "first-error" => Snapshot.FirstFailure(Win32Error.AccessDenied),
            _ => Snapshot.NextFailure(
                Win32Error.AccessDenied,
                Row(71, 0, "unrelated.exe")),
        };
        Win32ProcessCatalog catalog = new(
            new FakeCatalogNative(snapshot),
            new FakeIdentityReader(),
            Continuity("lost", "recover"),
            new FakeCatalogClock(StartUtc, TimeSpan.FromHours(8)));

        ProcessObservationBatchEvidence result = await catalog.ReadBatchAsync(
            new(
                ProcessObservationBatchKind.AuthoritativeSnapshot,
                Binding(@"C:\Games\game.exe")));

        Assert.Equal(ProcessGateSourceStatus.Unavailable, result.Status);
        Assert.False(result.IsComplete);
        Assert.False(result.IsAuthoritativeAllProcessCatalog);
        Assert.True(result.ContinuityLost);
    }

    [Fact]
    public async Task TrustedDeltaWaitsForCadenceAndPublishesOnlyNewExactKeys()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(Row(81, 0, "game.exe")),
            Snapshot.Completed(Row(81, 0, "game.exe")),
            Snapshot.Completed(
                Row(81, 0, "game.exe"),
                Row(82, 0, "game.exe")));
        FakeIdentityReader identities = new();
        DateTimeOffset firstCreation = StartUtc.AddSeconds(-20);
        identities.Enqueue(81, Success(81, firstCreation, @"C:\Games\game.exe"));
        identities.Enqueue(81, Success(81, firstCreation, @"C:\Games\game.exe"));
        identities.Enqueue(81, Success(81, firstCreation, @"C:\Games\game.exe"));
        identities.Enqueue(82, Success(82, StartUtc.AddSeconds(-2), @"C:\Games\game.exe"));
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");

        ProcessObservationBatchEvidence first = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, binding));
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            first.ObserverEpoch!,
            1));
        ProcessObservationBatchEvidence recovery = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 2)));
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            recovery.ObserverEpoch!,
            2));

        ProcessObservationBatchEvidence delta = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 3)));

        Assert.Equal([TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)],
            clock.Delays);
        Assert.Equal(ProcessObservationBatchKind.StartDelta, delta.BatchKind);
        Assert.True(delta.IsComplete);
        Assert.False(delta.IsAuthoritativeAllProcessCatalog);
        Assert.True(delta.CreationTimelineTrusted);
        Assert.False(delta.ContinuityLost);
        Assert.Equal(82, Assert.Single(delta.Observations).Identity!.Key.Pid);
    }

    [Fact]
    public async Task InaccessibleNoncandidateParentOnlyMakesThatParentLinkUnknown()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(
                Row(91, 90, "game.exe"),
                Row(90, 0, "launcher.exe")));
        FakeIdentityReader identities = new();
        identities.Enqueue(91, Success(91, StartUtc.AddSeconds(-5), @"C:\Games\game.exe"));
        identities.Enqueue(90, ProcessCatalogIdentityReadResult.Failure(
            Win32ProcessIdentityReadStatus.AccessDenied));
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");

        ProcessObservationBatchEvidence first = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, binding));
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            first.ObserverEpoch!,
            1));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        ProcessObservationBatchEvidence recovery = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 2)));

        Assert.True(recovery.IsAuthoritativeAllProcessCatalog);
        Assert.True(recovery.CreationTimelineTrusted);
        Assert.Equal(
            ParentLinkKind.Unknown,
            Assert.Single(recovery.Observations).Parent.Kind);
    }

    [Fact]
    public async Task InaccessibleScopedParentDuringSeparateParentProofPoisonsSnapshot()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(
                Row(101, 100, "game.exe"),
                Row(100, 0, "game.exe")));
        FakeIdentityReader identities = new();
        identities.Enqueue(101, Success(101, StartUtc.AddSeconds(-5), @"C:\Games\game.exe"));
        identities.Enqueue(100, Success(100, StartUtc.AddSeconds(-10), @"C:\Games\game.exe"));
        identities.Enqueue(100, ProcessCatalogIdentityReadResult.Failure(
            Win32ProcessIdentityReadStatus.AccessDenied));
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");

        ProcessObservationBatchEvidence first = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, binding));
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            first.ObserverEpoch!,
            1));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        ProcessObservationBatchEvidence recovery = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 2)));

        Assert.Equal(ProcessGateSourceStatus.Unavailable, recovery.Status);
        Assert.False(recovery.IsComplete);
        Assert.False(recovery.IsAuthoritativeAllProcessCatalog);
        Assert.True(recovery.ContinuityLost);
    }

    [Fact]
    public async Task AuthoritativeSnapshotProjectsExactParentFromSameSnapshotRows()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(
                Row(111, 110, "game.exe"),
                Row(110, 0, "launcher.exe")));
        FakeIdentityReader identities = new();
        DateTimeOffset parentCreation = StartUtc.AddSeconds(-20);
        identities.Enqueue(111, Success(111, StartUtc.AddSeconds(-5), @"C:\Games\game.exe"));
        identities.Enqueue(110, Success(110, parentCreation, @"C:\Tools\launcher.exe"));
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");

        ProcessObservationBatchEvidence first = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, binding));
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            first.ObserverEpoch!,
            1));
        ProcessObservationBatchEvidence recovery = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 2)));

        ParentLink parent = Assert.Single(recovery.Observations).Parent;
        Assert.Equal(ParentLinkKind.Exact, parent.Kind);
        Assert.Equal(new ProcessInstanceKey(110, parentCreation.UtcTicks), parent.ExactParent);
    }

    [Fact]
    public async Task ParentCreatedAfterChildRemainsUnknown()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(
                Row(121, 120, "game.exe"),
                Row(120, 0, "launcher.exe")));
        FakeIdentityReader identities = new();
        identities.Enqueue(121, Success(121, StartUtc.AddSeconds(-10), @"C:\Games\game.exe"));
        identities.Enqueue(120, Success(120, StartUtc.AddSeconds(-2), @"C:\Tools\launcher.exe"));
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");

        ProcessObservationBatchEvidence first = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, binding));
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            first.ObserverEpoch!,
            1));
        ProcessObservationBatchEvidence recovery = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 2)));

        Assert.Equal(
            ParentLinkKind.Unknown,
            Assert.Single(recovery.Observations).Parent.Kind);
    }

    [Fact]
    public async Task ChildCreatedAfterSnapshotStartKeepsParentZeroUnknown()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(Row(131, 0, "game.exe")));
        FakeIdentityReader identities = new();
        identities.Enqueue(131, Success(
            131,
            StartUtc.AddSeconds(1),
            @"C:\Games\game.exe"));
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");

        ProcessObservationBatchEvidence first = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, binding));
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            first.ObserverEpoch!,
            1));
        ProcessObservationBatchEvidence recovery = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 2)));

        Assert.True(recovery.IsAuthoritativeAllProcessCatalog);
        Assert.Equal(
            ParentLinkKind.Unknown,
            Assert.Single(recovery.Observations).Parent.Kind);
    }

    [Fact]
    public async Task DeltaParentIsUnknownAndLaterDueAuthoritativeSnapshotCanProveIt()
    {
        Win32ProcessCatalogEntry childRow = Row(141, 140, "game.exe");
        Win32ProcessCatalogEntry parentRow = Row(140, 0, "launcher.exe");
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(),
            Snapshot.Completed(childRow, parentRow),
            Snapshot.Completed(childRow, parentRow));
        FakeIdentityReader identities = new();
        DateTimeOffset childCreation = StartUtc.AddSeconds(-5);
        DateTimeOffset parentCreation = StartUtc.AddSeconds(-20);
        identities.Enqueue(141, Success(141, childCreation, @"C:\Games\game.exe"));
        identities.Enqueue(141, Success(141, childCreation, @"C:\Games\game.exe"));
        identities.Enqueue(140, Success(140, parentCreation, @"C:\Tools\launcher.exe"));
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");

        await RecoverAndTrustAsync(catalog, binding);
        ProcessObservationBatchEvidence delta = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 3)));
        Assert.Equal(ParentLinkKind.Unknown, Assert.Single(delta.Observations).Parent.Kind);

        clock.Advance(TimeSpan.FromMilliseconds(1750));
        ProcessObservationBatchEvidence authoritative = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 4)));

        Assert.Equal(ProcessObservationBatchKind.AuthoritativeSnapshot, authoritative.BatchKind);
        Assert.True(authoritative.IsAuthoritativeAllProcessCatalog);
        ParentLink parent = Assert.Single(authoritative.Observations).Parent;
        Assert.Equal(ParentLinkKind.Exact, parent.Kind);
        Assert.Equal(new ProcessInstanceKey(140, parentCreation.UtcTicks), parent.ExactParent);
    }

    [Fact]
    public async Task ExactPidReuseReturnsActualLiveIdentityWithoutCatalogAuthority()
    {
        FakeCatalogNative native = new(Snapshot.Completed(), Snapshot.Completed());
        FakeIdentityReader identities = new();
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        await RecoverAndTrustAsync(catalog, binding);
        DateTimeOffset oldCreation = StartUtc.AddMinutes(-5);
        DateTimeOffset reusedCreation = StartUtc.AddSeconds(-1);
        identities.Enqueue(151, Success(
            151,
            reusedCreation,
            @"C:\Games\other.exe"));

        ProcessObservationBatchEvidence exact = await catalog.ReadExactAsync(
            Target(151, oldCreation, @"C:\Games\game.exe"),
            Next(binding, 3));

        Assert.Equal(ProcessGateSourceStatus.Available, exact.Status);
        Assert.Equal(ProcessObservationBatchKind.StartDelta, exact.BatchKind);
        Assert.True(exact.IsComplete);
        Assert.False(exact.IsAuthoritativeAllProcessCatalog);
        Assert.True(exact.CreationTimelineTrusted);
        Assert.False(exact.ContinuityLost);
        ProcessObservation actual = Assert.Single(exact.Observations);
        Assert.Equal(new ProcessInstanceKey(151, reusedCreation.UtcTicks), actual.Identity!.Key);
        Assert.Equal(@"C:\Games\other.exe", actual.Identity.ExecutablePath);
        Assert.Equal(ParentLinkKind.Unknown, actual.Parent.Kind);
    }

    [Theory]
    [InlineData((int)Win32ProcessIdentityReadStatus.Exited, true)]
    [InlineData((int)Win32ProcessIdentityReadStatus.NotFound, true)]
    [InlineData((int)Win32ProcessIdentityReadStatus.AccessDenied, false)]
    [InlineData((int)Win32ProcessIdentityReadStatus.Ambiguous, false)]
    public async Task ExactExitIsCompleteEmptyButAmbiguitySeversContinuity(
        int statusValue,
        bool provedExit)
    {
        FakeCatalogNative native = new(Snapshot.Completed(), Snapshot.Completed());
        FakeIdentityReader identities = new();
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        await RecoverAndTrustAsync(catalog, binding);
        identities.Enqueue(161, ProcessCatalogIdentityReadResult.Failure(
            (Win32ProcessIdentityReadStatus)statusValue));

        ProcessObservationBatchEvidence exact = await catalog.ReadExactAsync(
            Target(161, StartUtc.AddMinutes(-1), @"C:\Games\game.exe"),
            Next(binding, 3));

        Assert.Equal(
            provedExit ? ProcessGateSourceStatus.Available : ProcessGateSourceStatus.Unavailable,
            exact.Status);
        Assert.Equal(provedExit, exact.IsComplete);
        Assert.Empty(exact.Observations);
        Assert.Equal(provedExit, exact.CreationTimelineTrusted);
        Assert.Equal(!provedExit, exact.ContinuityLost);
    }

    [Fact]
    public async Task OverlappingReadHasNoClockAndForcesActiveSampleUntrusted()
    {
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(),
            Snapshot.Completed(Row(171, 0, "game.exe")));
        native.BeforeFirst = () =>
        {
            if (native.CreateCalls == 3)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        FakeIdentityReader identities = new();
        identities.Enqueue(171, Success(171, StartUtc.AddSeconds(-2), @"C:\Games\game.exe"));
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        await RecoverAndTrustAsync(catalog, binding);

        Task<ProcessObservationBatchEvidence> active = Task.Run(async () =>
            await catalog.ReadBatchAsync(new(
                ProcessObservationBatchKind.StartDelta,
                Next(binding, 3))));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        ProcessObservationBatchEvidence overlap;
        try
        {
            overlap = await catalog.ReadExactAsync(
                Target(999, StartUtc.AddSeconds(-1), @"C:\Games\game.exe"),
                Next(binding, 3));
        }
        finally
        {
            release.Set();
        }

        ProcessObservationBatchEvidence inFlight = await active;
        Assert.Equal(ProcessGateSourceStatus.Unavailable, overlap.Status);
        Assert.Null(overlap.ClockSample);
        Assert.True(overlap.ContinuityLost);
        Assert.Equal(ProcessGateSourceStatus.Unavailable, inFlight.Status);
        Assert.False(inFlight.IsAuthoritativeAllProcessCatalog);
        Assert.False(inFlight.CreationTimelineTrusted);
        Assert.True(inFlight.ContinuityLost);
    }

    [Fact]
    public async Task CancellationDuringCadenceDelayLeavesDurableLossAndCanHandshakeAgain()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(),
            Snapshot.Completed());
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            new FakeIdentityReader(),
            Continuity("lost", "recover", "after-cancel"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        await RecoverAndTrustAsync(catalog, binding);
        TaskCompletionSource delayStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        clock.DelayHandler = (_, token) =>
        {
            delayStarted.TrySetResult();
            return new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, token));
        };
        using CancellationTokenSource cancellation = new();

        Task cancelled = catalog.ReadBatchAsync(
                new(ProcessObservationBatchKind.StartDelta, Next(binding, 3)),
                cancellation.Token)
            .AsTask();
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        clock.DelayHandler = null;
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "recover",
            3));
        ProcessObservationBatchEvidence recovery = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 4)));
        Assert.Equal("after-cancel", recovery.ObserverEpoch);
        Assert.True(recovery.IsAuthoritativeAllProcessCatalog);
        Assert.True(recovery.CreationTimelineTrusted);
    }

    [Fact]
    public async Task ClockFailureSeversTrustedSourceBeforeReturningUnavailable()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(),
            Snapshot.Completed());
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            new FakeIdentityReader(),
            Continuity("lost", "recover", "after-clock"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        await RecoverAndTrustAsync(catalog, binding);
        clock.ThrowOnNextCapture = true;

        ProcessObservationBatchEvidence failure = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 3)));

        Assert.Equal(ProcessGateSourceStatus.Unavailable, failure.Status);
        Assert.True(failure.ContinuityLost);
        Assert.Equal(2, native.CreateCalls);
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            "recover",
            3));
        ProcessObservationBatchEvidence recovery = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 4)));
        Assert.Equal("after-clock", recovery.ObserverEpoch);
        Assert.True(recovery.IsAuthoritativeAllProcessCatalog);
    }

    [Fact]
    public async Task TransientLossPersistenceFailureCannotPublishFromOldTrustedCheckpoint()
    {
        MemoryContinuityStore store = new();
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(),
            Snapshot.Completed());
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            new FakeIdentityReader(),
            Continuity(store, "lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        await RecoverAndTrustAsync(catalog, binding);
        catalog.NotifyDiscontinuity();
        store.ForcedSaveStatus = ProcessSourceContinuityStoreSaveStatus.Unavailable;

        ProcessObservationBatchEvidence unavailable = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 3)));
        Assert.Equal(ProcessGateSourceStatus.Unavailable, unavailable.Status);
        Assert.Equal(2, native.CreateCalls);

        store.ForcedSaveStatus = null;
        ProcessObservationBatchEvidence lost = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 4)));
        Assert.Equal(ProcessGateSourceStatus.Available, lost.Status);
        Assert.True(lost.IsComplete);
        Assert.False(lost.CreationTimelineTrusted);
        Assert.True(lost.ContinuityLost);
        Assert.Equal(3, native.CreateCalls);
    }

    [Fact]
    public async Task DelayBetweenSchedulingAndNativeStartOverTwoSecondsSeversContinuity()
    {
        MemoryContinuityStore store = new();
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(),
            Snapshot.Completed());
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            new FakeIdentityReader(),
            Continuity(store, "lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        await RecoverAndTrustAsync(catalog, binding);
        int loads = 0;
        store.BeforeLoad = () =>
        {
            if (++loads == 2)
            {
                clock.Advance(TimeSpan.FromMilliseconds(2001));
            }
        };

        ProcessObservationBatchEvidence result = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 3)));

        Assert.Equal(ProcessGateSourceStatus.Unavailable, result.Status);
        Assert.False(result.IsAuthoritativeAllProcessCatalog);
        Assert.False(result.CreationTimelineTrusted);
        Assert.True(result.ContinuityLost);
    }

    [Fact]
    public async Task ParentQueryDurationOverTwoSecondsCannotPublishRecoveryCandidate()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(
                Row(181, 180, "game.exe"),
                Row(180, 0, "launcher.exe")));
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        FakeIdentityReader identities = new()
        {
            BeforeRead = pid =>
            {
                if (pid == 180)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(2001));
                }
            },
        };
        identities.Enqueue(181, Success(181, StartUtc.AddSeconds(-5), @"C:\Games\game.exe"));
        identities.Enqueue(180, Success(180, StartUtc.AddSeconds(-10), @"C:\Tools\launcher.exe"));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        ProcessObservationBatchEvidence first = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, binding));
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            first.ObserverEpoch!,
            1));

        ProcessObservationBatchEvidence result = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 2)));

        Assert.Equal(ProcessGateSourceStatus.Unavailable, result.Status);
        Assert.NotNull(result.ClockSample);
        Assert.True(
            result.ClockSample!.CompletedMonotonic
                - result.ClockSample.StartedMonotonic > TimeSpan.FromSeconds(2));
        Assert.False(result.IsAuthoritativeAllProcessCatalog);
        Assert.True(result.ContinuityLost);
    }

    [Fact]
    public async Task InactiveReadDoesNotSampleAndNextActivationStartsLost()
    {
        FakeCatalogNative native = new(Snapshot.Completed());
        Win32ProcessCatalog catalog = new(
            native,
            new FakeIdentityReader(),
            Continuity("lost", "recover"),
            new FakeCatalogClock(StartUtc, TimeSpan.FromHours(8)));
        ProcessCatalogPolicyBinding inactive = Binding(@"C:\Games\game.exe") with
        {
            MonitoringActive = false,
        };

        ProcessObservationBatchEvidence dormant = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, inactive));
        Assert.Equal(0, native.CreateCalls);
        Assert.Equal(ProcessGateSourceStatus.Available, dormant.Status);

        ProcessObservationBatchEvidence activation = await catalog.ReadBatchAsync(
            new(
                ProcessObservationBatchKind.StartDelta,
                Next(inactive, 2) with { MonitoringActive = true }));
        Assert.Equal(1, native.CreateCalls);
        Assert.True(activation.IsComplete);
        Assert.False(activation.CreationTimelineTrusted);
        Assert.True(activation.ContinuityLost);
    }

    [Fact]
    public async Task ConflictingPolicyReplaySeversWithoutSecondNativeSnapshot()
    {
        FakeCatalogNative native = new(Snapshot.Completed());
        Win32ProcessCatalog catalog = new(
            native,
            new FakeIdentityReader(),
            Continuity("lost", "recover"),
            new FakeCatalogClock(StartUtc, TimeSpan.FromHours(8)));
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        await catalog.ReadBatchAsync(new(ProcessObservationBatchKind.StartDelta, binding));

        ProcessObservationBatchEvidence conflict = await catalog.ReadBatchAsync(new(
            ProcessObservationBatchKind.StartDelta,
            binding with
            {
                CanonicalExecutablePaths = [@"C:\Games\other.exe"],
            }));

        Assert.Equal(ProcessGateSourceStatus.Unavailable, conflict.Status);
        Assert.True(conflict.ContinuityLost);
        Assert.Equal(1, native.CreateCalls);
    }

    [Fact]
    public async Task RecoveryCandidateDeltaKeepsCreationEvidenceWhileRecoveryAckIsPending()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(),
            Snapshot.Completed());
        Win32ProcessCatalog catalog = new(
            native,
            new FakeIdentityReader(),
            Continuity("lost", "recover"),
            new FakeCatalogClock(StartUtc, TimeSpan.FromHours(8)));
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        ProcessObservationBatchEvidence first = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, binding));
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            first.ObserverEpoch!,
            1));
        ProcessObservationBatchEvidence candidate = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 2)));
        Assert.True(candidate.IsAuthoritativeAllProcessCatalog);

        ProcessObservationBatchEvidence pending = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 3)));

        Assert.Equal(ProcessObservationBatchKind.StartDelta, pending.BatchKind);
        Assert.True(pending.IsComplete);
        Assert.False(pending.IsAuthoritativeAllProcessCatalog);
        Assert.True(pending.CreationTimelineTrusted);
        Assert.False(pending.ContinuityLost);
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            candidate.ObserverEpoch!,
            2));
    }

    [Fact]
    public async Task ExactReadDoesNotMoveCatalogCadence()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(),
            Snapshot.Completed());
        FakeIdentityReader identities = new();
        FakeCatalogClock clock = new(StartUtc, TimeSpan.FromHours(8));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            clock);
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        await RecoverAndTrustAsync(catalog, binding);
        identities.Enqueue(191, ProcessCatalogIdentityReadResult.Failure(
            Win32ProcessIdentityReadStatus.NotFound));

        ProcessObservationBatchEvidence exact = await catalog.ReadExactAsync(
            Target(191, StartUtc.AddSeconds(-1), @"C:\Games\game.exe"),
            Next(binding, 3));
        ProcessObservationBatchEvidence batch = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 4)));

        Assert.True(exact.IsComplete);
        Assert.True(batch.IsComplete);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)],
            clock.Delays);
    }

    [Fact]
    public async Task MissingParentRowStaysUnknownWithoutPoisoningAuthority()
    {
        FakeCatalogNative native = new(
            Snapshot.Completed(),
            Snapshot.Completed(Row(201, 999, "game.exe")));
        FakeIdentityReader identities = new();
        identities.Enqueue(201, Success(201, StartUtc.AddSeconds(-3), @"C:\Games\game.exe"));
        Win32ProcessCatalog catalog = new(
            native,
            identities,
            Continuity("lost", "recover"),
            new FakeCatalogClock(StartUtc, TimeSpan.FromHours(8)));
        ProcessCatalogPolicyBinding binding = Binding(@"C:\Games\game.exe");
        ProcessObservationBatchEvidence first = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, binding));
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            first.ObserverEpoch!,
            1));

        ProcessObservationBatchEvidence recovery = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 2)));

        Assert.True(recovery.IsAuthoritativeAllProcessCatalog);
        Assert.Equal(
            ParentLinkKind.Unknown,
            Assert.Single(recovery.Observations).Parent.Kind);
        Assert.Equal([201], identities.ReadPids);
    }

    private static ProcessCatalogPolicyBinding Binding(params string[] paths) => new(
        1,
        "evaluation-1",
        "payload-1",
        StartUtc,
        new DateOnly(2026, 7, 14),
        true,
        "S-1-5-21-100-200-300-1001",
        1,
        paths.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray());

    private static ProcessCatalogPolicyBinding Next(
        ProcessCatalogPolicyBinding binding,
        long revision) => binding with
    {
        PolicyRevision = revision,
        EvaluationIdentity = $"evaluation-{revision}",
        PayloadFingerprint = $"payload-{revision}",
        EvaluatedAtUtc = binding.EvaluatedAtUtc.AddMilliseconds(250 * (revision - 1)),
    };

    private static ProcessExactTarget Target(
        int pid,
        DateTimeOffset creation,
        string path) => new(
        new ProcessInstanceKey(pid, creation.UtcTicks),
        creation,
        path,
        "S-1-5-21-100-200-300-1001",
        1,
        "game",
        StartUtc,
        new DateOnly(2026, 7, 14),
        "rule-fingerprint",
        1,
        "evaluation-1",
        "payload-1",
        StartUtc);

    private static async Task RecoverAndTrustAsync(
        Win32ProcessCatalog catalog,
        ProcessCatalogPolicyBinding binding)
    {
        ProcessObservationBatchEvidence first = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, binding));
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.TrustSeverPersisted,
            first.ObserverEpoch!,
            1));
        ProcessObservationBatchEvidence recovery = await catalog.ReadBatchAsync(
            new(ProcessObservationBatchKind.StartDelta, Next(binding, 2)));
        Assert.True(recovery.IsAuthoritativeAllProcessCatalog);
        await catalog.AcknowledgeAsync(new(
            ProcessObservationAcknowledgementKind.AuthoritativeRecoveryPersisted,
            recovery.ObserverEpoch!,
            2));
    }

    private static Win32ProcessCatalogEntry Row(int pid, int parentPid, string name) =>
        new(pid, parentPid, name);

    private static ProcessCatalogIdentityReadResult Success(
        int pid,
        DateTimeOffset created,
        string path) => ProcessCatalogIdentityReadResult.Success(new(
            new ProcessInstanceKey(pid, created.UtcTicks),
            created,
            path,
            "S-1-5-21-100-200-300-1001",
            1));

    private static DurableProcessSourceContinuity Continuity(params string[] epochs) =>
        new(new MemoryContinuityStore(), new QueueEpochFactory(epochs));

    private static DurableProcessSourceContinuity Continuity(
        MemoryContinuityStore store,
        params string[] epochs) => new(store, new QueueEpochFactory(epochs));

    private sealed record Snapshot(
        SafeWin32ProcessSnapshotHandle? Handle,
        IReadOnlyList<Win32ProcessCatalogEntry> Entries,
        int? FirstError,
        int? NextError)
    {
        internal static Snapshot Completed(params Win32ProcessCatalogEntry[] entries) =>
            new(new SafeWin32ProcessSnapshotHandle((nint)123, ownsHandle: false), entries, null, null);

        internal static Snapshot Invalid() => new(null, [], null, null);

        internal static Snapshot FirstFailure(int error) => new(
            new SafeWin32ProcessSnapshotHandle((nint)123, ownsHandle: false),
            [],
            error,
            null);

        internal static Snapshot NextFailure(
            int error,
            params Win32ProcessCatalogEntry[] entries) => new(
            new SafeWin32ProcessSnapshotHandle((nint)123, ownsHandle: false),
            entries,
            null,
            error);
    }

    private sealed class FakeCatalogNative(params Snapshot[] snapshots)
        : IWin32ProcessCatalogNative
    {
        private readonly Queue<Snapshot> _snapshots = new(snapshots);
        private Snapshot? _current;
        private int _index;

        internal int CreateCalls { get; private set; }

        internal Action? BeforeFirst { get; set; }

        public SafeWin32ProcessSnapshotHandle? CreateProcessSnapshot(out int error)
        {
            CreateCalls++;
            _current = _snapshots.Dequeue();
            _index = 0;
            error = _current.Handle is null ? Win32Error.InvalidHandle : Win32Error.Success;
            return _current.Handle;
        }

        public Win32ProcessCatalogMoveResult ReadFirst(SafeWin32ProcessSnapshotHandle snapshot)
        {
            BeforeFirst?.Invoke();
            if (_current!.FirstError is { } error)
            {
                return Win32ProcessCatalogMoveResult.Failure(error);
            }

            return _current.Entries.Count == 0
                ? Win32ProcessCatalogMoveResult.Completed()
                : Win32ProcessCatalogMoveResult.Entry(_current.Entries[_index++]);
        }

        public Win32ProcessCatalogMoveResult ReadNext(SafeWin32ProcessSnapshotHandle snapshot)
        {
            if (_current!.NextError is { } error)
            {
                return Win32ProcessCatalogMoveResult.Failure(error);
            }

            return _index >= _current.Entries.Count
                ? Win32ProcessCatalogMoveResult.Completed()
                : Win32ProcessCatalogMoveResult.Entry(_current.Entries[_index++]);
        }
    }

    private sealed class FakeIdentityReader : IProcessCatalogIdentityReader
    {
        private readonly Dictionary<int, Queue<ProcessCatalogIdentityReadResult>> _reads = [];

        internal List<int> ReadPids { get; } = [];

        internal Action<int>? BeforeRead { get; init; }

        internal void Enqueue(int pid, ProcessCatalogIdentityReadResult result)
        {
            if (!_reads.TryGetValue(pid, out Queue<ProcessCatalogIdentityReadResult>? queue))
            {
                queue = new();
                _reads.Add(pid, queue);
            }

            queue.Enqueue(result);
        }

        public ProcessCatalogIdentityReadResult Read(int pid)
        {
            BeforeRead?.Invoke(pid);
            ReadPids.Add(pid);
            return _reads[pid].Dequeue();
        }
    }

    private sealed class FakeCatalogClock(
        DateTimeOffset utc,
        TimeSpan monotonic) : IProcessCatalogClock
    {
        private DateTimeOffset _utc = utc;
        private TimeSpan _monotonic = monotonic;

        internal List<TimeSpan> Delays { get; } = [];

        internal Func<TimeSpan, CancellationToken, ValueTask>? DelayHandler { get; set; }

        internal bool ThrowOnNextCapture { get; set; }

        public ProcessCatalogClockInstant Capture()
        {
            if (ThrowOnNextCapture)
            {
                ThrowOnNextCapture = false;
                throw new InvalidOperationException("clock unavailable");
            }

            return new(_utc, _monotonic);
        }

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            if (DelayHandler is not null)
            {
                return DelayHandler(delay, cancellationToken);
            }

            Advance(delay);
            return ValueTask.CompletedTask;
        }

        internal void Advance(TimeSpan value)
        {
            _utc += value;
            _monotonic += value;
        }
    }

    private sealed class MemoryContinuityStore : IProcessSourceContinuityStore
    {
        private ProcessSourceContinuityCheckpoint? _checkpoint;

        internal Action? BeforeLoad { get; set; }

        internal ProcessSourceContinuityStoreSaveStatus? ForcedSaveStatus { get; set; }

        public ValueTask<ProcessSourceContinuityStoreLoadResult> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            BeforeLoad?.Invoke();
            return ValueTask.FromResult(new ProcessSourceContinuityStoreLoadResult(
                _checkpoint is null
                    ? ProcessSourceContinuityStoreLoadStatus.Missing
                    : ProcessSourceContinuityStoreLoadStatus.Found,
                _checkpoint));
        }

        public ValueTask<ProcessSourceContinuityStoreSaveResult> CompareExchangeAsync(
            long? expectedVersion,
            ProcessSourceContinuityCheckpoint replacement,
            CancellationToken cancellationToken = default)
        {
            if (ForcedSaveStatus is { } forced)
            {
                return ValueTask.FromResult(new ProcessSourceContinuityStoreSaveResult(
                    forced,
                    null));
            }

            if ((_checkpoint is null ? null : _checkpoint.Version) != expectedVersion)
            {
                return ValueTask.FromResult(new ProcessSourceContinuityStoreSaveResult(
                    ProcessSourceContinuityStoreSaveStatus.Conflict,
                    _checkpoint));
            }

            _checkpoint = replacement;
            return ValueTask.FromResult(new ProcessSourceContinuityStoreSaveResult(
                ProcessSourceContinuityStoreSaveStatus.Saved,
                replacement));
        }
    }

    private sealed class QueueEpochFactory(IEnumerable<string> values)
        : IProcessObserverEpochFactory
    {
        private readonly Queue<string> _values = new(values);

        public string CreateEpoch() => _values.Dequeue();
    }
}
