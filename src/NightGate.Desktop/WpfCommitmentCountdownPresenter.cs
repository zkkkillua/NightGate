using System.Windows.Threading;

namespace NightGate.Desktop;

public sealed class WpfCommitmentCountdownPresenter :
    ICommitmentCountdownPresenter
{
    private readonly IMonitorLayoutProvider _monitorLayoutProvider;
    private CommitmentCountdownWindow? _window;

    public WpfCommitmentCountdownPresenter()
        : this(new WpfMonitorLayoutProvider())
    {
    }

    public WpfCommitmentCountdownPresenter(
        IMonitorLayoutProvider monitorLayoutProvider)
    {
        ArgumentNullException.ThrowIfNull(monitorLayoutProvider);
        _monitorLayoutProvider = monitorLayoutProvider;
    }

    public void Apply(CommitmentCountdownPresentation? presentation) =>
        InvokeOnUiThread(() => ApplyCore(presentation));

    private void ApplyCore(CommitmentCountdownPresentation? presentation)
    {
        if (presentation is null)
        {
            HideCore();
            return;
        }

        CommitmentCountdownWindow window =
            _window ?? new CommitmentCountdownWindow();
        try
        {
            window.UpdatePresentation(presentation);
            if (!window.IsVisible)
            {
                window.Show();
            }

            if (!window.TryConfigureAndPlace(_monitorLayoutProvider))
            {
                throw new InvalidOperationException(
                    "The commitment countdown could not enter passive topmost mode.");
            }

            _window = window;
        }
        catch
        {
            _window = window;
            try
            {
                HideCore();
            }
            catch (Exception)
            {
                // Keep the HWND reference so the controller can retry cleanup.
            }
            throw;
        }
    }

    private void HideCore()
    {
        CommitmentCountdownWindow? window = _window;
        if (window is null)
        {
            return;
        }

        try
        {
            window.Opacity = 0;
            window.Hide();
        }
        catch (Exception)
        {
            // Closing is still attempted; retaining the reference enables retry.
        }

        try
        {
            window.Close();
            _window = null;
        }
        catch (Exception exception)
        {
            _window = window;
            throw new InvalidOperationException(
                "The passive countdown window could not be closed.",
                exception);
        }
    }

    private static void InvokeOnUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null
            || dispatcher.HasShutdownStarted
            || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action, DispatcherPriority.Background);
    }
}
