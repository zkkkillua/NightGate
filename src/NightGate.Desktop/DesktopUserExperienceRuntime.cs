using System.ComponentModel;

namespace NightGate.Desktop;

public sealed class DesktopUserExperienceRuntime : IAsyncDisposable
{
    private static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(15);
    private const int RefreshEveryTicks = 4;
    private readonly UserExperienceViewModel _viewModel;
    private readonly IDesktopUiDispatcher _dispatcher;
    private readonly IDesktopRuntimeClock _clock;
    private readonly TimeSpan _cadence;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lifecycle = new();
    private Task? _startTask;
    private Task? _loop;
    private Task? _stopTask;
    private int _firstUseDashboardRequested;

    public DesktopUserExperienceRuntime(
        UserExperienceViewModel viewModel,
        IDesktopUiDispatcher dispatcher,
        IDesktopRuntimeClock clock)
        : this(viewModel, dispatcher, clock, DefaultCadence)
    {
    }

    internal DesktopUserExperienceRuntime(
        UserExperienceViewModel viewModel,
        IDesktopUiDispatcher dispatcher,
        IDesktopRuntimeClock clock,
        TimeSpan cadence)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(clock);
        if (cadence <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cadence));
        }

        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _clock = clock;
        _cadence = cadence;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
    }

    internal event EventHandler? FirstUseDashboardRequested;

    public Task StartAsync()
    {
        lock (_lifecycle)
        {
            if (_stopTask is not null)
            {
                throw new ObjectDisposedException(nameof(DesktopUserExperienceRuntime));
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
        await RunSafeOnUiAsync(
            () => _viewModel.RefreshAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await RunSafeOnUiAsync(
            () => _viewModel.PollNoticeAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _loop = RunLoopAsync(cancellationToken);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        int ticks = 0;
        try
        {
            while (true)
            {
                await _clock.DelayAsync(_cadence, cancellationToken).ConfigureAwait(false);
                await RunSafeOnUiAsync(
                    () => _viewModel.PollNoticeAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                ticks++;
                if (ticks % RefreshEveryTicks == 0)
                {
                    await RunSafeOnUiAsync(
                        () => _viewModel.RefreshAsync(cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSafeOnUiAsync<T>(
        Func<ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        Task<T>? operation = null;
        try
        {
            await _dispatcher.InvokeAsync(
                () => operation = action().AsTask(),
                cancellationToken).ConfigureAwait(false);
            if (operation is not null)
            {
                _ = await operation.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // User-state and notice failures must never stop enforcement or trap the desktop.
        }
    }

    private async Task StopCoreAsync()
    {
        _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        _stopping.Cancel();
        Task? start = _startTask;
        Task? loop = _loop;
        if (start is not null)
        {
            await IgnoreCancellationAsync(start).ConfigureAwait(false);
        }

        loop ??= _loop;
        if (loop is not null)
        {
            await IgnoreCancellationAsync(loop).ConfigureAwait(false);
        }

        _stopping.Dispose();
    }

    private void ViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (!_viewModel.IsAvailable
            || (_viewModel.IsOnboardingComplete
                && !_viewModel.HasPendingProgression)
            || Interlocked.Exchange(ref _firstUseDashboardRequested, 1) != 0)
        {
            return;
        }

        FirstUseDashboardRequested?.Invoke(this, EventArgs.Empty);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
