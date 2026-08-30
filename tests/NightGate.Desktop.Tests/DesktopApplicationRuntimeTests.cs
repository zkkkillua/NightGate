using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Windows.Threading;
using NightGate.Desktop;

namespace NightGate.Desktop.Tests;

public sealed class DesktopApplicationRuntimeTests
{
    [Fact]
    public async Task RunningGameObserver_UpdatesLargeCountdownIndependentlyEveryThreeSeconds()
    {
        ScriptedPolicyClient client = new(GameWindowPolicy());
        ControlledRuntimeClock clock = new();
        RecordingGameDetector detector = new() { IsRunning = true };
        RecordingCountdownPresenter presenter = new();
        DesktopApplicationRuntime runtime = Runtime(
            client, Dashboard(), new RecordingDispatcher(), clock,
            new RecordingSessionSource(), new RecordingProcessGate(),
            new FixedIdentityProvider(new("S-1-5-21-42", 9)),
            countdownController: new(presenter), runningGameDetector: detector);
        try
        {
            await runtime.StartAsync();
            await EventuallyAsync(() => presenter.Last?.Kind == CommitmentCountdownKind.GameGraceToLock);
            Assert.Equal("S-1-5-21-42", detector.Identity!.UserSid);
            Assert.Equal(9, detector.Identity.SessionId);
            Assert.Equal(@"C:\Games\Game.exe", Assert.Single(detector.Rules!).RootExecutablePath);
            await clock.WaitForDelayAsync(TimeSpan.FromSeconds(3));
            detector.IsRunning = false;
            clock.ReleaseOne(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
            await EventuallyAsync(() => detector.CallCount == 2 && presenter.Last is null);
            Assert.Equal(1, client.CallCount);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunningGameObserver_DoesNotScanDuringTheFreeEntertainmentPeriod()
    {
        RecordingGameDetector detector = new() { IsRunning = true };
        RecordingCountdownPresenter presenter = new();
        DesktopApplicationRuntime runtime = Runtime(
            new ScriptedPolicyClient(Policy(DesktopNightPhase.Free)), Dashboard(),
            new RecordingDispatcher(), new ControlledRuntimeClock(),
            new RecordingSessionSource(), new RecordingProcessGate(),
            new FixedIdentityProvider(new("S-1-5-21-42", 9)),
            countdownController: new(presenter), runningGameDetector: detector);
        try
        {
            await runtime.StartAsync();
            Assert.Equal(0, detector.CallCount);
            Assert.Null(presenter.Last);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task DelayedGameDetection_CannotRestoreAnOverlayAfterPolicyLoss()
    {
        TaskCompletionSource<bool> detection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingGameDetector detector = new() { Pending = detection.Task };
        ScriptedPolicyClient client = new(GameWindowPolicy());
        client.Enqueue(DesktopPolicyResult.FailOpen("offline"));
        ControlledRuntimeClock clock = new();
        RecordingCountdownPresenter presenter = new();
        RecordingProcessGate processes = new();
        DesktopApplicationRuntime runtime = Runtime(
            client, Dashboard(), new RecordingDispatcher(), clock,
            new RecordingSessionSource(), processes,
            new FixedIdentityProvider(new("S-1-5-21-42", 9)),
            countdownController: new(presenter), runningGameDetector: detector);
        try
        {
            await runtime.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await EventuallyAsync(() => detector.CallCount == 1);
            Assert.Equal(1, detector.CallCount);
            Assert.Single(processes.Requests);
            clock.ReleaseOne(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            await EventuallyAsync(() => client.CallCount >= 2);
            detection.SetResult(true);
            await clock.WaitForDelayAsync(TimeSpan.FromSeconds(3));
            Assert.Null(presenter.Last);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task GameDetectionFailure_DoesNotDisableProtectionAndStopCancelsPendingDetection()
    {
        RecordingGameDetector detector = new() { ThrowOnRead = true };
        ControlledRuntimeClock clock = new();
        RecordingProcessGate processes = new();
        DesktopApplicationRuntime runtime = Runtime(
            new ScriptedPolicyClient(GameWindowPolicy()), Dashboard(),
            new RecordingDispatcher(), clock, new RecordingSessionSource(), processes,
            new FixedIdentityProvider(new("S-1-5-21-42", 9)),
            countdownController: new(new RecordingCountdownPresenter()),
            runningGameDetector: detector);
        await runtime.StartAsync();
        Assert.Single(processes.Requests);
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(3));
        detector.ThrowOnRead = false;
        detector.Pending = new TaskCompletionSource<bool>().Task;
        clock.ReleaseOne(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        await EventuallyAsync(() => detector.CallCount == 2);
        await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(processes.Disposed);
    }

    [Fact]
    public async Task UncancellableGameDetection_TimesOutWithoutPilingUpAndCannotBlockExit()
    {
        TaskCompletionSource<bool> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingGameDetector detector = new()
        {
            Pending = pending.Task,
            IgnoreCancellation = true,
        };
        ControlledRuntimeClock clock = new();
        RecordingProcessGate processes = new();
        RecordingCountdownPresenter presenter = new();
        DesktopApplicationRuntime runtime = Runtime(
            new ScriptedPolicyClient(GameWindowPolicy()), Dashboard(),
            new RecordingDispatcher(), clock, new RecordingSessionSource(), processes,
            new FixedIdentityProvider(new("S-1-5-21-42", 9)),
            countdownController: new(presenter), runningGameDetector: detector);
        try
        {
            await runtime.StartAsync().WaitAsync(TimeSpan.FromSeconds(1));
            await EventuallyAsync(() => detector.CallCount == 1);
            await clock.WaitForDelayAsync(TimeSpan.FromSeconds(3));
            Assert.False(pending.Task.IsCompleted);
            clock.ReleaseOne(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
            await clock.WaitForDelayAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, detector.CallCount);
            Assert.Null(presenter.Last);
            await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(processes.Disposed);
        }
        finally
        {
            pending.TrySetResult(true);
            await runtime.DisposeAsync();
        }
        Assert.Null(presenter.Last);
    }

    [Fact]
    public async Task Start_IsIdempotentPollsAndEvaluatesImmediatelyAndPublishesSleepOnUi()
    {
        DesktopPolicyResult policy = Policy(DesktopNightPhase.Free);
        ScriptedPolicyClient client = new(policy);
        ControlledRuntimeClock clock = new();
        RecordingProcessGate process = new();
        RecordingDispatcher dispatcher = new();
        DashboardViewModel dashboard = Dashboard();
        RecordingSessionSource sessions = new();
        DesktopApplicationRuntime runtime = Runtime(
            client,
            dashboard,
            dispatcher,
            clock,
            sessions,
            process,
            new FixedIdentityProvider(new("S-1-5-21-42", 9)));

        await runtime.StartAsync();
        await runtime.StartAsync();

        Assert.Equal(1, client.CallCount);
        ProcessGateRunRequest request = Assert.Single(process.Requests);
        Assert.Equal(ProcessObservationBatchKind.StartDelta, request.BatchKind);
        Assert.Equal("S-1-5-21-42", request.InteractiveUserSid);
        Assert.Equal(9, request.InteractiveSessionId);
        Assert.Equal(1, dispatcher.InvocationCount);
        Assert.Contains("接通电源 30 分钟", dashboard.Presentation.SleepTimeoutText);
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(30));
        Assert.True(clock.PendingDurations.Count(delay => delay == TimeSpan.FromMilliseconds(250)) >= 2);

        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task CountdownVisualFailure_DoesNotDisableLockOrProcessProtection()
    {
        DesktopPolicyResult healthy = Policy(DesktopNightPhase.Grace);
        DesktopPolicySnapshotDto snapshot = healthy.ExecutablePolicy! with
        {
            EvaluatedAt = healthy.ExecutablePolicy!.Window.Lock.AddMinutes(-5),
        };
        DesktopPolicyResult finalGrace = healthy with
        {
            Status = healthy.Status! with { Policy = snapshot },
        };
        RecordingProcessGate process = new();
        RecordingLocker locker = new();
        ThrowAlwaysCountdownPresenter countdownPresenter = new();
        CommitmentCountdownController countdown = new(countdownPresenter);
        DesktopApplicationRuntime runtime = Runtime(
            new ScriptedPolicyClient(finalGrace),
            Dashboard(),
            new RecordingDispatcher(),
            new ControlledRuntimeClock(),
            new RecordingSessionSource(),
            process,
            new FixedIdentityProvider(new("S-1-5-21-42", 9)),
            new LockSessionController(
                locker,
                new RecordingOverlay(),
                new NullLockSink()),
            countdownController: countdown);

        Exception? failure = await Record.ExceptionAsync(runtime.StartAsync);

        Assert.Null(failure);
        Assert.Single(process.Requests);
        Assert.True(countdownPresenter.Attempts >= 1);
        Assert.Equal(0, locker.CallCount);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task PolicyLoss_CancelsOverlayAndPreventsFurtherProcessEvaluation()
    {
        ScriptedPolicyClient client = new(Policy(DesktopNightPhase.LandingLocked));
        client.Enqueue(DesktopPolicyResult.FailOpen("service-lost"));
        ControlledRuntimeClock clock = new();
        RecordingProcessGate process = new();
        RecordingDispatcher dispatcher = new();
        RecordingSessionSource sessions = new();
        RecordingLocker locker = new();
        RecordingOverlay overlay = new();
        LockSessionController lockController = new(locker, overlay, new NullLockSink());
        DesktopApplicationRuntime runtime = Runtime(
            client,
            Dashboard(),
            dispatcher,
            clock,
            sessions,
            process,
            new FixedIdentityProvider(new("S-1-5-21-42", 9)),
            lockController);

        await runtime.StartAsync();
        sessions.Raise(CurrentSessionEventKind.Locked, TimeSpan.FromSeconds(1));
        sessions.Raise(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(2));
        Assert.Equal(1, overlay.ShowCount);
        Assert.Single(process.Requests);

        clock.ReleaseOne(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => client.CallCount >= 2);
        await EventuallyAsync(() => overlay.HideCount >= 1);
        clock.ReleaseAll(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        await Task.Yield();

        Assert.Single(process.Requests);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task TransientPolicyFault_RecoversWithoutBusyLoopAndThenEvaluates()
    {
        ScriptedPolicyClient client = new(DesktopPolicyResult.FailOpen("offline"));
        client.Enqueue(Policy(DesktopNightPhase.Grace));
        ControlledRuntimeClock clock = new();
        RecordingProcessGate process = new();
        DesktopApplicationRuntime runtime = Runtime(
            client,
            Dashboard(),
            new RecordingDispatcher(),
            clock,
            new RecordingSessionSource(),
            process,
            new FixedIdentityProvider(new("S-1-5-21-42", 9)));

        await runtime.StartAsync();
        Assert.Empty(process.Requests);
        await clock.WaitForDelayAsync(TimeSpan.FromSeconds(1));

        clock.ReleaseOne(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => client.CallCount >= 2);
        Assert.Empty(process.Requests);
        clock.ReleaseAll(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        await EventuallyAsync(() => process.Requests.Count == 1);
        Assert.Contains(TimeSpan.FromSeconds(1), clock.AllDurations);

        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task FailOpenPolicy_StillForwardsActualLockFactWithoutRelockEffects()
    {
        ControlledRuntimeClock clock = new();
        RecordingSessionSource sessions = new();
        RecordingLocker locker = new();
        RecordingOverlay overlay = new();
        RecordingActualLockSink sink = new();
        LockSessionController lockController = new(locker, overlay, sink);
        DesktopApplicationRuntime runtime = Runtime(
            new ScriptedPolicyClient(DesktopPolicyResult.FailOpen("offline")),
            Dashboard(),
            new RecordingDispatcher(),
            clock,
            sessions,
            new RecordingProcessGate(),
            new FixedIdentityProvider(new("S-1-5-21-42", 9)),
            lockController);

        await runtime.StartAsync();
        sessions.Raise(CurrentSessionEventKind.Locked, TimeSpan.FromSeconds(1));
        sessions.Raise(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(2));
        await EventuallyAsync(() => sink.WorkstationLockCount == 1);

        Assert.Equal(0, locker.CallCount);
        Assert.Equal(0, overlay.ShowCount);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task PolicyPump_IsSingleFlightWhileARequestIsBlocked()
    {
        ScriptedPolicyClient client = new(Policy(DesktopNightPhase.Grace));
        TaskCompletionSource<DesktopPolicyResult> blocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Enqueue(async cancellationToken =>
            await blocked.Task.WaitAsync(cancellationToken));
        ControlledRuntimeClock clock = new();
        DesktopApplicationRuntime runtime = Runtime(
            client,
            Dashboard(),
            new RecordingDispatcher(),
            clock,
            new RecordingSessionSource(),
            new RecordingProcessGate(),
            new FixedIdentityProvider(new("S-1-5-21-42", 9)));

        await runtime.StartAsync();
        clock.ReleaseOne(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        await EventuallyAsync(() => client.CallCount == 2);
        clock.ReleaseAll(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        await Task.Yield();

        Assert.Equal(2, client.CallCount);
        Assert.Equal(1, client.MaximumConcurrency);
        blocked.SetResult(Policy(DesktopNightPhase.Grace));
        await runtime.DisposeAsync();
    }

    [Theory]
    [InlineData(CurrentSessionEventKind.Unlocked)]
    [InlineData(CurrentSessionEventKind.Logon)]
    public async Task HealthyPolicyRefreshInFlight_ContinuesForwardingSessionEvents(
        CurrentSessionEventKind eventKind)
    {
        ScriptedPolicyClient client = new(Policy(DesktopNightPhase.LandingLocked));
        TaskCompletionSource<DesktopPolicyResult> blocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Enqueue(async cancellationToken =>
            await blocked.Task.WaitAsync(cancellationToken));
        ControlledRuntimeClock clock = new();
        RecordingSessionSource sessions = new();
        RecordingOverlay overlay = new();
        LockSessionController lockController = new(
            new RecordingLocker(),
            overlay,
            new NullLockSink());
        DesktopApplicationRuntime runtime = Runtime(
            client,
            Dashboard(),
            new RecordingDispatcher(),
            clock,
            sessions,
            new RecordingProcessGate(),
            new FixedIdentityProvider(new("S-1-5-21-42", 9)),
            lockController);

        try
        {
            await runtime.StartAsync();
            clock.ReleaseOne(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            await EventuallyAsync(() => client.CallCount == 2);

            sessions.Raise(CurrentSessionEventKind.Locked, TimeSpan.FromSeconds(1));
            sessions.Raise(eventKind, TimeSpan.FromSeconds(2));

            await EventuallyAsync(() => overlay.ShowCount == 1);
        }
        finally
        {
            blocked.TrySetResult(Policy(DesktopNightPhase.LandingLocked));
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task HealthyPolicyRefreshInFlight_ProcessCadenceRereadsIdentity()
    {
        ScriptedPolicyClient client = new(Policy(DesktopNightPhase.Grace));
        TaskCompletionSource<DesktopPolicyResult> blocked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Enqueue(async cancellationToken =>
            await blocked.Task.WaitAsync(cancellationToken));
        ControlledRuntimeClock clock = new();
        RecordingProcessGate process = new();
        SequenceIdentityProvider identity = new(
            new("S-1-5-21-42", 9),
            new("S-1-5-21-84", 12));
        DesktopApplicationRuntime runtime = Runtime(
            client,
            Dashboard(),
            new RecordingDispatcher(),
            clock,
            new RecordingSessionSource(),
            process,
            identity);

        try
        {
            await runtime.StartAsync();
            clock.ReleaseOne(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            await EventuallyAsync(() => client.CallCount == 2);
            clock.ReleaseAll(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

            await identity.SecondRead.Task.WaitAsync(TimeSpan.FromSeconds(1));
            ProcessGateRunRequest refreshed = process.Requests.Last();
            Assert.Equal("S-1-5-21-84", refreshed.InteractiveUserSid);
            Assert.Equal(12, refreshed.InteractiveSessionId);
        }
        finally
        {
            blocked.TrySetResult(Policy(DesktopNightPhase.Grace));
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task PolicyLoss_CancelsInFlightProcessEvaluationGeneration()
    {
        ScriptedPolicyClient client = new(Policy(DesktopNightPhase.Grace));
        client.Enqueue(DesktopPolicyResult.FailOpen("service-lost"));
        ControlledRuntimeClock clock = new();
        BlockingSecondProcessGate process = new();
        DesktopApplicationRuntime runtime = Runtime(
            client,
            Dashboard(),
            new RecordingDispatcher(),
            clock,
            new RecordingSessionSource(),
            process,
            new FixedIdentityProvider(new("S-1-5-21-42", 9)));

        try
        {
            await runtime.StartAsync();
            clock.ReleaseOne(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
            await process.SecondEvaluationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            clock.ReleaseOne(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            await EventuallyAsync(() => client.CallCount == 2);

            await process.SecondEvaluationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(process.SecondEvaluationToken.IsCancellationRequested);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task PolicyLoss_CancelsProcessGenerationBeforeUiFailOpenApplication()
    {
        ScriptedPolicyClient client = new(Policy(DesktopNightPhase.Grace));
        client.Enqueue(DesktopPolicyResult.FailOpen("service-lost"));
        ControlledRuntimeClock clock = new();
        BlockingSecondProcessGate process = new();
        BlockingSecondDispatcher dispatcher = new();
        DesktopApplicationRuntime runtime = Runtime(
            client,
            Dashboard(),
            dispatcher,
            clock,
            new RecordingSessionSource(),
            process,
            new FixedIdentityProvider(new("S-1-5-21-42", 9)));

        try
        {
            await runtime.StartAsync();
            clock.ReleaseOne(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
            await process.SecondEvaluationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            clock.ReleaseOne(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            await dispatcher.SecondInvocationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await process.SecondEvaluationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(process.SecondEvaluationToken.IsCancellationRequested);
        }
        finally
        {
            dispatcher.ReleaseSecondInvocation.TrySetResult();
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task SessionEventsUseTheirMonotonicTimestampAndTickRelocksOnce()
    {
        ControlledRuntimeClock clock = new();
        RecordingSessionSource sessions = new();
        RecordingLocker locker = new();
        RecordingOverlay overlay = new();
        LockSessionController lockController = new(locker, overlay, new NullLockSink());
        DesktopApplicationRuntime runtime = Runtime(
            new ScriptedPolicyClient(Policy(DesktopNightPhase.LandingLocked)),
            Dashboard(),
            new RecordingDispatcher(),
            clock,
            sessions,
            new RecordingProcessGate(),
            new FixedIdentityProvider(new("S-1-5-21-42", 9)),
            lockController);

        await runtime.StartAsync();
        sessions.Raise(CurrentSessionEventKind.Locked, TimeSpan.FromSeconds(1));
        sessions.Raise(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(4));
        Assert.Equal(1, overlay.ShowCount);

        clock.Monotonic = TimeSpan.FromSeconds(14);
        clock.ReleaseAll(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(10));
        await EventuallyAsync(() => locker.CallCount == 2);

        Assert.Equal(1, overlay.HideCount);
        Assert.Equal(2, locker.CallCount);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task AcceptedTeamRescueFromRelockOverlay_CancelsPendingRelockImmediately()
    {
        DesktopPolicyResult activePolicy = Policy(DesktopNightPhase.OverrideActive);
        DateTimeOffset startedAt = activePolicy.ExecutablePolicy!.EvaluatedAt;
        DesktopActiveOverrideDto activeOverride = new(
            DesktopOverrideKind.TeamRescue,
            startedAt,
            startedAt,
            startedAt.AddMinutes(20),
            ["game"]);
        activePolicy = activePolicy with
        {
            Status = activePolicy.Status! with
            {
                Policy = activePolicy.ExecutablePolicy with
                {
                    ActiveOverride = activeOverride,
                },
            },
        };
        DashboardViewModel dashboard = new(new FixedOverrideGateway(new(
            true,
            null,
            new(
                DesktopOverrideKind.TeamRescue,
                startedAt,
                startedAt.AddMinutes(20)),
            activePolicy)));
        ControlledRuntimeClock clock = new();
        RecordingSessionSource sessions = new();
        RecordingLocker locker = new();
        RecordingOverlay overlay = new();
        DesktopApplicationRuntime runtime = Runtime(
            new ScriptedPolicyClient(Policy(DesktopNightPhase.LandingLocked)),
            dashboard,
            new RecordingDispatcher(),
            clock,
            sessions,
            new RecordingProcessGate(),
            new FixedIdentityProvider(new("S-1-5-21-42", 9)),
            new LockSessionController(locker, overlay, new NullLockSink()));

        await runtime.StartAsync();
        sessions.Raise(CurrentSessionEventKind.Locked, TimeSpan.FromSeconds(1));
        sessions.Raise(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(2));
        Assert.Equal(1, locker.CallCount);
        Assert.Equal(1, overlay.ShowCount);

        DesktopOverrideResult result = await dashboard.RequestTeamRescueAsync();
        Assert.True(result.Accepted);

        clock.Monotonic = TimeSpan.FromSeconds(12);
        clock.ReleaseAll(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        await Task.Yield();

        Assert.Equal(1, locker.CallCount);
        Assert.Equal(1, overlay.HideCount);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Stop_CancelsAwaitsUnsubscribesAndDisposesOwnedRuntimePieces()
    {
        ControlledRuntimeClock clock = new();
        RecordingSessionSource sessions = new();
        RecordingProcessGate process = new();
        DesktopApplicationRuntime runtime = Runtime(
            new ScriptedPolicyClient(Policy(DesktopNightPhase.Free)),
            Dashboard(),
            new RecordingDispatcher(),
            clock,
            sessions,
            process,
            new FixedIdentityProvider(new("S-1-5-21-42", 9)));

        await runtime.StartAsync();
        await runtime.StopAsync();
        await runtime.StopAsync();
        int before = sessions.SubscriberCount;
        sessions.Raise(CurrentSessionEventKind.Unlocked, TimeSpan.FromSeconds(9));

        Assert.Equal(0, before);
        Assert.True(sessions.Disposed);
        Assert.True(process.Disposed);
        Assert.All(clock.PendingRequests, request => Assert.True(request.Task.IsCompleted));
    }

    [Fact]
    public async Task StopAsync_DisposesSessionLifetimeOnItsOwningDispatcher()
    {
        await RunOnDispatcherThreadAsync(async dispatcher =>
        {
            int ownerThreadId = Environment.CurrentManagedThreadId;
            DispatcherOwnedSessionSource sessions = new(ownerThreadId);
            DesktopApplicationRuntime runtime = Runtime(
                new ScriptedPolicyClient(Policy(DesktopNightPhase.Free)),
                Dashboard(),
                new WpfDesktopUiDispatcher(dispatcher),
                new ControlledRuntimeClock(),
                sessions,
                new RecordingProcessGate(),
                new FixedIdentityProvider(new("S-1-5-21-42", 9)),
                sessionEventLifetime: sessions);

            await runtime.StartAsync();
            await runtime.StopAsync();

            Assert.Equal(ownerThreadId, sessions.DisposeThreadId);
        });
    }

    private static DesktopApplicationRuntime Runtime(
        IDesktopPolicyClient client,
        DashboardViewModel dashboard,
        IDesktopUiDispatcher dispatcher,
        IDesktopRuntimeClock clock,
        ICurrentSessionEventSource sessions,
        IProcessGateRuntime process,
        ICurrentInteractiveIdentityProvider identity,
        LockSessionController? lockController = null,
        IDisposable? sessionEventLifetime = null,
        CommitmentCountdownController? countdownController = null,
        IRunningGameDetector? runningGameDetector = null) => new(
            client,
            dashboard,
            lockController ?? new(
                new RecordingLocker(),
                new RecordingOverlay(),
                new NullLockSink()),
            dispatcher,
            new FixedSleepReader(),
            new DesktopPolicyPollModel(),
            clock,
            sessions,
            identity,
            process,
            sessionEventLifetime ?? sessions as IDisposable,
            countdownController,
            runningGameDetector);

    private static DesktopPolicyResult GameWindowPolicy()
    {
        DesktopPolicyResult policy = Policy(DesktopNightPhase.Grace);
        return policy with
        {
            Status = policy.Status! with
            {
                Policy = policy.ExecutablePolicy! with
                {
                    EvaluatedAt = policy.ExecutablePolicy!.Window.Lock.AddMinutes(-20),
                },
            },
        };
    }

    private sealed class RecordingCountdownPresenter : ICommitmentCountdownPresenter
    {
        private CommitmentCountdownPresentation? _last;
        public CommitmentCountdownPresentation? Last => Volatile.Read(ref _last);
        public void Apply(CommitmentCountdownPresentation? presentation) =>
            Volatile.Write(ref _last, presentation);
    }

    private sealed class RecordingGameDetector : IRunningGameDetector
    {
        public bool IsRunning { get; set; }
        public bool ThrowOnRead { get; set; }
        public bool IgnoreCancellation { get; set; }
        public Task<bool>? Pending { get; set; }
        public int CallCount { get; private set; }
        public CurrentInteractiveIdentity? Identity { get; private set; }
        public IReadOnlyList<DesktopAppRuleDto>? Rules { get; private set; }

        public async ValueTask<bool> HasRunningGameAsync(
            IReadOnlyList<DesktopAppRuleDto> rules,
            CurrentInteractiveIdentity identity,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Rules = rules;
            Identity = identity;
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("Visual detection unavailable.");
            }
            return Pending is { } pending
                ? await (IgnoreCancellation ? pending : pending.WaitAsync(cancellationToken))
                : IsRunning;
        }
    }

    private static DashboardViewModel Dashboard() => new(new NullOverrideGateway());

    private static DesktopPolicyResult Policy(DesktopNightPhase phase)
    {
        DateOnly night = new(2026, 7, 14);
        DateTimeOffset evaluated =
            new(2026, 7, 14, 22, 0, 0, TimeSpan.FromHours(8));
        DesktopPolicySnapshotDto snapshot = new(
            evaluated,
            phase,
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
                [],
                DesktopAppRuleCategory.Game,
                35,
                true)],
            [],
            true,
            false,
            null);
        return new(true, false, null, new(true, false, null, snapshot));
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task RunOnDispatcherThreadAsync(
        Func<Dispatcher, Task> operation)
    {
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await operation(dispatcher);
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "NightGate Desktop runtime test dispatcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!thread.Join(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("The test dispatcher did not shut down.");
        }
    }

    private sealed class ScriptedPolicyClient : IDesktopPolicyClient
    {
        private readonly ConcurrentQueue<
            Func<CancellationToken, ValueTask<DesktopPolicyResult>>> _queued = new();
        private DesktopPolicyResult _last;
        private int _concurrency;
        private int _maximumConcurrency;

        public ScriptedPolicyClient(DesktopPolicyResult first)
        {
            _last = first;
            Enqueue(first);
        }

        public int CallCount { get; private set; }

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public void Enqueue(DesktopPolicyResult value) =>
            Enqueue(_ => ValueTask.FromResult(value));

        public void Enqueue(
            Func<CancellationToken, ValueTask<DesktopPolicyResult>> operation) =>
            _queued.Enqueue(operation);

        public async ValueTask<DesktopPolicyResult> GetPolicyAsync(
            CancellationToken cancellationToken = default)
        {
            int concurrent = Interlocked.Increment(ref _concurrency);
            InterlockedExtensions.Max(ref _maximumConcurrency, concurrent);
            CallCount++;
            try
            {
                if (!_queued.TryDequeue(out var operation))
                {
                    return _last;
                }

                _last = await operation(cancellationToken);
                return _last;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        public ValueTask<DesktopRecordEventResult> RecordEventAsync(
            PrivacySafeEventKind kind,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesktopRecordEventResult(true, null));
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int candidate)
        {
            int current;
            do
            {
                current = Volatile.Read(ref location);
                if (candidate <= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref location, candidate, current) != current);
        }
    }

    private sealed class RecordingProcessGate : IProcessGateRuntime
    {
        public List<ProcessGateRunRequest> Requests { get; } = [];

        public bool Disposed { get; private set; }

        public Task<ProcessGateRunResult> EvaluateAsync(
            ProcessGateRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new ProcessGateRunResult(
                ImmutableArray<ProcessGateOrchestrationOutcome>.Empty));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSecondProcessGate : IProcessGateRuntime
    {
        private int _callCount;

        public TaskCompletionSource SecondEvaluationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondEvaluationCancelled { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken SecondEvaluationToken { get; private set; }

        public async Task<ProcessGateRunResult> EvaluateAsync(
            ProcessGateRunRequest request,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                return new(ImmutableArray<ProcessGateOrchestrationOutcome>.Empty);
            }

            SecondEvaluationToken = cancellationToken;
            SecondEvaluationStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SecondEvaluationCancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("The process evaluation was not cancelled.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingDispatcher : IDesktopUiDispatcher
    {
        public int InvocationCount { get; private set; }

        public ValueTask InvokeAsync(
            Action action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSecondDispatcher : IDesktopUiDispatcher
    {
        private int _invocationCount;

        public TaskCompletionSource SecondInvocationStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSecondInvocation { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask InvokeAsync(
            Action action,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _invocationCount) == 2)
            {
                SecondInvocationStarted.TrySetResult();
                await ReleaseSecondInvocation.Task.WaitAsync(cancellationToken);
            }

            action();
        }
    }

    private sealed class FixedSleepReader : ISleepTimeoutReader
    {
        public SleepTimeoutSnapshot Read() => new(
            DesktopPowerSource.Ac,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(15));
    }

    private sealed class FixedIdentityProvider(CurrentInteractiveIdentity value) :
        ICurrentInteractiveIdentityProvider
    {
        public CurrentInteractiveIdentity Read() => value;
    }

    private sealed class SequenceIdentityProvider(
        params CurrentInteractiveIdentity[] values) :
        ICurrentInteractiveIdentityProvider
    {
        private int _index;

        public TaskCompletionSource SecondRead { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public CurrentInteractiveIdentity Read()
        {
            int index = Interlocked.Increment(ref _index) - 1;
            if (index == 1)
            {
                SecondRead.TrySetResult();
            }

            return values[Math.Min(index, values.Length - 1)];
        }
    }

    private sealed class RecordingSessionSource : ICurrentSessionEventSource, IDisposable
    {
        private EventHandler<CurrentSessionChangedEventArgs>? _sessionChanged;

        public event EventHandler<CurrentSessionChangedEventArgs>? SessionChanged
        {
            add => _sessionChanged += value;
            remove => _sessionChanged -= value;
        }

        public int SubscriberCount => _sessionChanged?.GetInvocationList().Length ?? 0;

        public bool Disposed { get; private set; }

        public void Raise(CurrentSessionEventKind kind, TimeSpan monotonic) =>
            _sessionChanged?.Invoke(this, new(kind, monotonic));

        public void Dispose()
        {
            Disposed = true;
            _sessionChanged = null;
        }
    }

    private sealed class DispatcherOwnedSessionSource(int ownerThreadId) :
        ICurrentSessionEventSource,
        IDisposable
    {
        public event EventHandler<CurrentSessionChangedEventArgs>? SessionChanged
        {
            add { }
            remove { }
        }

        public int? DisposeThreadId { get; private set; }

        public void Dispose()
        {
            DisposeThreadId = Environment.CurrentManagedThreadId;
            Assert.Equal(ownerThreadId, DisposeThreadId);
        }
    }

    private sealed class RecordingLocker : IWorkstationLocker
    {
        public int CallCount { get; private set; }

        public bool TryLock()
        {
            CallCount++;
            return true;
        }
    }

    private sealed class RecordingOverlay : IRestrictedOverlayPresenter
    {
        public int ShowCount { get; private set; }

        public int HideCount { get; private set; }

        public void Show(RestrictedOverlayPresentation presentation) => ShowCount++;

        public void Update(RestrictedOverlayPresentation presentation)
        {
        }

        public void Hide() => HideCount++;
    }

    private sealed class ThrowAlwaysCountdownPresenter :
        ICommitmentCountdownPresenter
    {
        public int Attempts { get; private set; }

        public void Apply(CommitmentCountdownPresentation? presentation)
        {
            Attempts++;
            throw new InvalidOperationException("visual-only failure");
        }
    }

    private sealed class NullLockSink : ILockWorkflowEventSink
    {
        public void ReportMissedLock(LockAttemptKind attemptKind)
        {
        }

        public void ReportWorkstationLocked()
        {
        }
    }

    private sealed class RecordingActualLockSink : ILockWorkflowEventSink
    {
        public int WorkstationLockCount { get; private set; }

        public void ReportMissedLock(LockAttemptKind attemptKind)
        {
        }

        public void ReportWorkstationLocked() => WorkstationLockCount++;
    }

    private sealed class NullOverrideGateway : IDesktopOverrideGateway
    {
        public ValueTask<DesktopOverrideResult> RequestAsync(
            DesktopOverrideRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesktopOverrideResult(
                false,
                "not-used",
                null,
                DesktopPolicyResult.FailOpen("not-used")));
    }

    private sealed class FixedOverrideGateway(DesktopOverrideResult result) :
        IDesktopOverrideGateway
    {
        public ValueTask<DesktopOverrideResult> RequestAsync(
            DesktopOverrideRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ControlledRuntimeClock : IDesktopRuntimeClock
    {
        private readonly object _sync = new();
        private readonly List<DelayRequest> _requests = [];

        public DateTimeOffset Now { get; private set; } =
            new(2026, 7, 14, 22, 0, 0, TimeSpan.FromHours(8));

        public TimeSpan Monotonic { get; set; }

        public TimeSpan MonotonicNow => Monotonic;

        public IReadOnlyList<DelayRequest> PendingRequests
        {
            get
            {
                lock (_sync)
                {
                    return _requests.ToArray();
                }
            }
        }

        public IReadOnlyList<TimeSpan> PendingDurations => PendingRequests
            .Where(request => !request.Task.IsCompleted)
            .Select(request => request.Duration)
            .ToArray();

        public IReadOnlyList<TimeSpan> AllDurations => PendingRequests
            .Select(request => request.Duration)
            .ToArray();

        public ValueTask DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            DelayRequest request = new(delay, cancellationToken);
            lock (_sync)
            {
                _requests.Add(request);
            }

            return new(request.Task);
        }

        public async Task WaitForDelayAsync(TimeSpan duration) =>
            await EventuallyAsync(() => PendingDurations.Contains(duration));

        public void ReleaseOne(TimeSpan duration, TimeSpan advance)
        {
            DelayRequest? request;
            lock (_sync)
            {
                request = _requests.FirstOrDefault(item =>
                    !item.Task.IsCompleted && item.Duration == duration);
            }

            Assert.NotNull(request);
            Advance(advance);
            request.Complete();
        }

        public void ReleaseAll(TimeSpan duration, TimeSpan advance)
        {
            DelayRequest[] requests;
            lock (_sync)
            {
                requests = _requests.Where(item =>
                        !item.Task.IsCompleted && item.Duration == duration)
                    .ToArray();
            }

            Advance(advance);
            foreach (DelayRequest request in requests)
            {
                request.Complete();
            }
        }

        private void Advance(TimeSpan amount)
        {
            Monotonic += amount;
            Now += amount;
        }
    }

    public sealed class DelayRequest
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public DelayRequest(TimeSpan duration, CancellationToken cancellationToken)
        {
            Duration = duration;
            _registration = cancellationToken.Register(() =>
                _completion.TrySetCanceled(cancellationToken));
        }

        public TimeSpan Duration { get; }

        public Task Task => _completion.Task;

        public void Complete()
        {
            _registration.Dispose();
            _completion.TrySetResult();
        }
    }
}
