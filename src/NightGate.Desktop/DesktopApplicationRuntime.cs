using System.Windows.Threading;

namespace NightGate.Desktop;

public interface IDesktopUiDispatcher
{
    ValueTask InvokeAsync(
        Action action,
        CancellationToken cancellationToken = default);
}

public interface IProcessGateRuntime : IAsyncDisposable
{
    Task<ProcessGateRunResult> EvaluateAsync(
        ProcessGateRunRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class WpfDesktopUiDispatcher : IDesktopUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDesktopUiDispatcher(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public ValueTask InvokeAsync(
        Action action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            throw new InvalidOperationException("The desktop dispatcher is unavailable.");
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return ValueTask.CompletedTask;
        }

        DispatcherOperation operation = _dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Normal,
            cancellationToken);
        return new(operation.Task);
    }
}

internal sealed class ProcessGateCoordinatorRuntime(
    ProcessGateCoordinator coordinator) : IProcessGateRuntime
{
    private readonly ProcessGateCoordinator _coordinator = coordinator
        ?? throw new ArgumentNullException(nameof(coordinator));

    public Task<ProcessGateRunResult> EvaluateAsync(
        ProcessGateRunRequest request,
        CancellationToken cancellationToken = default) =>
        _coordinator.EvaluateAsync(request, cancellationToken);

    public ValueTask DisposeAsync() => _coordinator.DisposeAsync();
}

public sealed class DesktopApplicationRuntime : IAsyncDisposable
{
    private static readonly TimeSpan ProcessCadence = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OverlayTickCadence = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GameObservationCadence = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GameObservationBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailureRetry = TimeSpan.FromSeconds(1);
    private readonly IDesktopPolicyClient _policyClient;
    private readonly DashboardViewModel _dashboard;
    private readonly LockSessionController _lockController;
    private readonly IDesktopUiDispatcher _dispatcher;
    private readonly ISleepTimeoutReader _sleepTimeoutReader;
    private readonly DesktopPolicyPollModel _pollModel;
    private readonly IDesktopRuntimeClock _clock;
    private readonly ICurrentSessionEventSource _sessionEvents;
    private readonly ICurrentInteractiveIdentityProvider _identityProvider;
    private readonly IProcessGateRuntime _processGate;
    private readonly IDisposable? _sessionEventLifetime;
    private readonly CommitmentCountdownController? _countdownController;
    private readonly IRunningGameDetector? _runningGameDetector;
    private readonly object _countdownPolicySync = new();
    private DesktopPolicyResult? _countdownPolicy;
    private TimeSpan _countdownPolicyObservedAt;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lifecycle = new();
    private readonly object _policyGenerationSync = new();
    private readonly object _sessionTasksSync = new();
    private readonly List<CancellationTokenSource> _policyGenerations = [];
    private readonly HashSet<Task> _sessionTasks = [];
    private Task? _startTask;
    private Task? _stopTask;
    private Task? _policyLoop;
    private Task? _processLoop;
    private Task? _overlayTickLoop;
    private Task? _gameObservationLoop;
    private Task<bool>? _pendingGameDetection;
    private bool _sessionSubscribed;
    private bool _overrideLifecycleSubscribed;
    private CancellationTokenSource? _activePolicyGeneration;
    private int _acceptSessionEvents;

    public DesktopApplicationRuntime(
        IDesktopPolicyClient policyClient,
        DashboardViewModel dashboard,
        LockSessionController lockController,
        IDesktopUiDispatcher dispatcher,
        ISleepTimeoutReader sleepTimeoutReader,
        DesktopPolicyPollModel pollModel,
        IDesktopRuntimeClock clock,
        ICurrentSessionEventSource sessionEvents,
        ICurrentInteractiveIdentityProvider identityProvider,
        IProcessGateRuntime processGate,
        IDisposable? sessionEventLifetime = null,
        CommitmentCountdownController? countdownController = null,
        IRunningGameDetector? runningGameDetector = null)
    {
        ArgumentNullException.ThrowIfNull(policyClient);
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(lockController);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(sleepTimeoutReader);
        ArgumentNullException.ThrowIfNull(pollModel);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sessionEvents);
        ArgumentNullException.ThrowIfNull(identityProvider);
        ArgumentNullException.ThrowIfNull(processGate);
        _policyClient = policyClient;
        _dashboard = dashboard;
        _lockController = lockController;
        _dispatcher = dispatcher;
        _sleepTimeoutReader = sleepTimeoutReader;
        _pollModel = pollModel;
        _clock = clock;
        _sessionEvents = sessionEvents;
        _identityProvider = identityProvider;
        _processGate = processGate;
        _sessionEventLifetime = sessionEventLifetime;
        _countdownController = countdownController;
        _runningGameDetector = runningGameDetector;
    }

    public Task StartAsync()
    {
        lock (_lifecycle)
        {
            if (_stopTask is not null)
            {
                throw new ObjectDisposedException(nameof(DesktopApplicationRuntime));
            }

            return _startTask ??= StartCoreAsync(_stopping.Token);
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycle)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        SubscribeOverrideLifecycle();
        SubscribeSessionEvents();
        PolicyIteration first = await PollPolicyOnceAsync(cancellationToken)
            .ConfigureAwait(false);
        if (first.PolicyAvailable)
        {
            await EvaluateProcessesOnceAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _policyLoop = RunPolicyLoopAsync(first.NextDelay, cancellationToken);
        _processLoop = RunProcessLoopAsync(cancellationToken);
        _overlayTickLoop = RunOverlayTickLoopAsync(cancellationToken);
        if (_countdownController is not null && _runningGameDetector is not null)
        {
            _gameObservationLoop = RunGameObservationLoopAsync(cancellationToken);
        }
    }

    private async Task RunGameObservationLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await ObserveRunningGamesAsync(cancellationToken).ConfigureAwait(false);
                await DelayBoundedAsync(GameObservationCadence, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The observational loop stops independently of protection.
        }
    }

    private async Task ObserveRunningGamesAsync(CancellationToken cancellationToken)
    {
        DesktopPolicyResult? sampledPolicy;
        TimeSpan sampledAt;
        lock (_countdownPolicySync)
        {
            sampledPolicy = _countdownPolicy;
            sampledAt = _countdownPolicyObservedAt;
        }

        bool hasRunningGame = false;
        try
        {
            if (sampledPolicy is not null && IsPolicyAvailable
                && CommitmentCountdownModel.IsGameReminderWindow(
                    sampledPolicy, _clock.MonotonicNow - sampledAt)
                && _identityProvider.Read() is { } identity
                && _pendingGameDetection?.IsCompleted != false)
            {
                // A native read can stall despite cancellation. Do not let a visual
                // scan block shutdown or start another scan while it is still running.
                Task<bool> detection = Task.Run(async () =>
                    await _runningGameDetector!.HasRunningGameAsync(
                        sampledPolicy.ExecutablePolicy!.AppRules,
                        identity,
                        cancellationToken)
                    .ConfigureAwait(false), CancellationToken.None);
                _pendingGameDetection = detection;
                _ = detection.ContinueWith(
                    static task => { _ = task.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted
                        | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                hasRunningGame = await detection.WaitAsync(
                        GameObservationBudget, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Unknown/denied processes do not justify the stronger visual overlay.
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                DesktopPolicyResult? current;
                TimeSpan observedAt;
                lock (_countdownPolicySync)
                {
                    current = _countdownPolicy;
                    observedAt = _countdownPolicyObservedAt;
                }
                TimeSpan now = _clock.MonotonicNow;
                bool stillApplicable = current is not null && IsPolicyAvailable
                    && CommitmentCountdownModel.HasSameGameObservationScope(sampledPolicy, current)
                    && CommitmentCountdownModel.IsGameReminderWindow(current, now - observedAt);
                _countdownController?.ObserveGamePresence(hasRunningGame && stillApplicable, now);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // This UI-only observer cannot disable the policy, lock or process loops.
        }
    }

    private async Task RunPolicyLoopAsync(
        TimeSpan initialDelay,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = initialDelay;
        try
        {
            while (true)
            {
                await DelayBoundedAsync(delay, cancellationToken).ConfigureAwait(false);
                PolicyIteration iteration = await PollPolicyOnceAsync(cancellationToken)
                    .ConfigureAwait(false);
                delay = iteration.NextDelay;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during application shutdown.
        }
    }

    private async Task RunProcessLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await DelayBoundedAsync(ProcessCadence, cancellationToken)
                    .ConfigureAwait(false);
                if (IsPolicyAvailable)
                {
                    await EvaluateProcessesOnceAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during application shutdown.
        }
    }

    private async Task RunOverlayTickLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await DelayBoundedAsync(OverlayTickCadence, cancellationToken)
                    .ConfigureAwait(false);
                if (!IsPolicyAvailable)
                {
                    continue;
                }

                TimeSpan now = _clock.MonotonicNow;
                try
                {
                    await _dispatcher.InvokeAsync(
                            () =>
                            {
                                _lockController.Tick(now);
                                TickCountdownSafely(now);
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    SetPolicyAvailable(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during application shutdown.
        }
    }

    private async ValueTask<PolicyIteration> PollPolicyOnceAsync(
        CancellationToken cancellationToken)
    {
        DesktopPolicyResult policy;
        try
        {
            policy = await _policyClient.GetPolicyAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            policy = DesktopPolicyResult.FailOpen("service-unavailable");
        }

        if (!DesktopPolicyWitnessSource.TryCreateWitness(policy, out _))
        {
            policy = DesktopPolicyResult.FailOpen(
                policy.DegradationCode ?? "policy-invalid");
            SetPolicyAvailable(false);
        }

        SleepTimeoutSnapshot? sleep = ReadSleepTimeout();
        DateTimeOffset now = _clock.Now;
        TimeSpan monotonicNow = _clock.MonotonicNow;
        bool applied = await ApplyPolicySafelyAsync(
                policy,
                now,
                monotonicNow,
                sleep,
                cancellationToken)
            .ConfigureAwait(false);
        bool available = applied && policy.ExecutablePolicy is not null;
        SetPolicyAvailable(available);

        TimeSpan nextDelay = available
            ? SafeNextPolicyDelay(policy.ExecutablePolicy!, now)
            : FailureRetry;
        return new(nextDelay, available);
    }

    private async ValueTask<bool> ApplyPolicySafelyAsync(
        DesktopPolicyResult policy,
        DateTimeOffset now,
        TimeSpan monotonicNow,
        SleepTimeoutSnapshot? sleep,
        CancellationToken cancellationToken)
    {
        try
        {
            await _dispatcher.InvokeAsync(
                    () =>
                    {
                        _dashboard.ApplyPolicy(policy, now, sleep);
                        _lockController.ObservePolicy(policy, monotonicNow);
                        _lockController.Tick(monotonicNow);
                        ObserveCountdownSafely(policy, monotonicNow);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            DesktopPolicyResult failOpen = DesktopPolicyResult.FailOpen("ui-unavailable");
            try
            {
                await _dispatcher.InvokeAsync(
                        () =>
                        {
                            try
                            {
                                _lockController.ObservePolicy(failOpen, monotonicNow);
                            }
                            finally
                            {
                                ObserveCountdownSafely(failOpen, monotonicNow);
                            }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A dead dispatcher cannot be made to perform a stale lock effect.
            }

            return false;
        }
    }

    private async ValueTask EvaluateProcessesOnceAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetPolicyGeneration(out CancellationToken policyGeneration))
        {
            return;
        }

        CurrentInteractiveIdentity? identity;
        try
        {
            identity = _identityProvider.Read();
        }
        catch (Exception)
        {
            identity = null;
        }

        if (identity is null || policyGeneration.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _processGate.EvaluateAsync(
                    new(
                        ProcessObservationBatchKind.StartDelta,
                        identity.UserSid,
                        identity.SessionId),
                    policyGeneration)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Observation, persistence, and action faults all retry without an effect.
        }
    }

    private void SubscribeSessionEvents()
    {
        lock (_lifecycle)
        {
            if (_sessionSubscribed)
            {
                return;
            }

            _sessionEvents.SessionChanged += OnSessionChanged;
            _sessionSubscribed = true;
            Volatile.Write(ref _acceptSessionEvents, 1);
        }
    }

    private void SubscribeOverrideLifecycle()
    {
        lock (_lifecycle)
        {
            if (_overrideLifecycleSubscribed)
            {
                return;
            }

            _dashboard.OverrideRequestLifecycle += ForwardOverrideLifecycleAsync;
            _overrideLifecycleSubscribed = true;
        }
    }

    private async ValueTask ForwardOverrideLifecycleAsync(
        DesktopOverrideRequestLifecycle lifecycle)
    {
        TimeSpan now = _clock.MonotonicNow;
        await _dispatcher.InvokeAsync(
                () =>
                {
                    if (lifecycle.Stage == DesktopOverrideRequestLifecycleStage.Started)
                    {
                        _lockController.OnOverrideRequestStarted(now);
                        return;
                    }

                    _lockController.OnOverrideRequestCompleted(lifecycle.Result, now);
                    if (lifecycle.Result is { Accepted: true })
                    {
                        _pollModel.MarkOverrideAccepted();
                    }
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void OnSessionChanged(
        object? sender,
        CurrentSessionChangedEventArgs args)
    {
        if (Volatile.Read(ref _acceptSessionEvents) == 0
            || (!IsPolicyAvailable && args.Kind != CurrentSessionEventKind.Locked))
        {
            return;
        }

        Task task = ForwardSessionEventAsync(args, _stopping.Token);
        lock (_sessionTasksSync)
        {
            _sessionTasks.Add(task);
        }

        _ = RemoveSessionTaskAsync(task);
    }

    private async Task ForwardSessionEventAsync(
        CurrentSessionChangedEventArgs args,
        CancellationToken cancellationToken)
    {
        try
        {
            await _dispatcher.InvokeAsync(
                    () => _lockController.OnSessionChanged(
                        args.Kind,
                        args.MonotonicTimestamp),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during application shutdown.
        }
        catch (Exception)
        {
            SetPolicyAvailable(false);
        }
    }

    private async Task RemoveSessionTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            lock (_sessionTasksSync)
            {
                _sessionTasks.Remove(task);
            }
        }
    }

    private async Task StopCoreAsync()
    {
        Volatile.Write(ref _acceptSessionEvents, 0);
        SetPolicyAvailable(false);
        lock (_lifecycle)
        {
            if (_overrideLifecycleSubscribed)
            {
                _dashboard.OverrideRequestLifecycle -= ForwardOverrideLifecycleAsync;
                _overrideLifecycleSubscribed = false;
            }

            if (_sessionSubscribed)
            {
                _sessionEvents.SessionChanged -= OnSessionChanged;
                _sessionSubscribed = false;
            }
        }

        _stopping.Cancel();
        Task? start;
        lock (_lifecycle)
        {
            start = _startTask;
        }

        await IgnoreCancellationAsync(start).ConfigureAwait(false);
        await IgnoreCancellationAsync(_policyLoop).ConfigureAwait(false);
        await IgnoreCancellationAsync(_processLoop).ConfigureAwait(false);
        await IgnoreCancellationAsync(_overlayTickLoop).ConfigureAwait(false);
        await IgnoreCancellationAsync(_gameObservationLoop).ConfigureAwait(false);

        Task[] sessionTasks;
        lock (_sessionTasksSync)
        {
            sessionTasks = _sessionTasks.ToArray();
        }
        await IgnoreCancellationAsync(Task.WhenAll(sessionTasks)).ConfigureAwait(false);

        try
        {
            TimeSpan now = _clock.MonotonicNow;
            await _dispatcher.InvokeAsync(
                    () =>
                    {
                        try
                        {
                            _lockController.ObservePolicy(
                                DesktopPolicyResult.FailOpen("application-stopping"),
                                now);
                        }
                        finally
                        {
                            try
                            {
                                _countdownController?.Clear();
                            }
                            finally
                            {
                                _sessionEventLifetime?.Dispose();
                            }
                        }
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown remains best-effort and fail-open if the UI is already gone.
        }

        try
        {
            await _processGate.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The application is already fail-open and exiting.
        }

        CancellationTokenSource[] policyGenerations;
        lock (_policyGenerationSync)
        {
            policyGenerations = _policyGenerations.ToArray();
            _policyGenerations.Clear();
        }
        foreach (CancellationTokenSource generation in policyGenerations)
        {
            generation.Dispose();
        }

        _stopping.Dispose();
    }

    private SleepTimeoutSnapshot? ReadSleepTimeout()
    {
        try
        {
            return _sleepTimeoutReader.Read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ObserveCountdownSafely(
        DesktopPolicyResult policy,
        TimeSpan monotonicNow)
    {
        try
        {
            lock (_countdownPolicySync)
            {
                _countdownPolicy = policy;
                _countdownPolicyObservedAt = monotonicNow;
            }
            _countdownController?.ObservePolicy(policy, monotonicNow);
        }
        catch (Exception)
        {
            // The compact countdown is visual-only and cannot affect enforcement.
        }
    }

    private void TickCountdownSafely(TimeSpan monotonicNow)
    {
        try
        {
            _countdownController?.Tick(monotonicNow);
        }
        catch (Exception)
        {
            // The compact countdown is visual-only and retries independently.
        }
    }

    private TimeSpan SafeNextPolicyDelay(
        DesktopPolicySnapshotDto policy,
        DateTimeOffset now)
    {
        try
        {
            TimeSpan delay = _pollModel.GetNextDelay(policy, now);
            return delay < TimeSpan.Zero ? FailureRetry : delay;
        }
        catch (Exception)
        {
            SetPolicyAvailable(false);
            return FailureRetry;
        }
    }

    private async ValueTask DelayBoundedAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        TimeSpan safeDelay = delay < TimeSpan.Zero ? FailureRetry : delay;
        try
        {
            await _clock.DelayAsync(safeDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await Task.Delay(FailureRetry, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool IsPolicyAvailable
    {
        get
        {
            lock (_policyGenerationSync)
            {
                return _activePolicyGeneration is
                {
                    IsCancellationRequested: false,
                };
            }
        }
    }

    private bool TryGetPolicyGeneration(out CancellationToken cancellationToken)
    {
        lock (_policyGenerationSync)
        {
            if (_activePolicyGeneration is not
                {
                    IsCancellationRequested: false,
                } active)
            {
                cancellationToken = default;
                return false;
            }

            cancellationToken = active.Token;
            return true;
        }
    }

    private void SetPolicyAvailable(bool available)
    {
        CancellationTokenSource? generationToCancel = null;
        lock (_policyGenerationSync)
        {
            if (available)
            {
                if (_activePolicyGeneration is null)
                {
                    CancellationTokenSource generation =
                        CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    _activePolicyGeneration = generation;
                    _policyGenerations.Add(generation);
                }

                return;
            }

            generationToCancel = _activePolicyGeneration;
            _activePolicyGeneration = null;
        }

        try
        {
            generationToCancel?.Cancel();
        }
        catch (Exception)
        {
            // Cancellation callbacks cannot restore authority to a stale policy generation.
        }
    }

    private static async Task IgnoreCancellationAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception)
        {
            // A failed pump is already fail-open and must not block shutdown.
        }
    }

    private sealed record PolicyIteration(
        TimeSpan NextDelay,
        bool PolicyAvailable);
}
