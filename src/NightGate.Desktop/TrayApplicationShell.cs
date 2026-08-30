using System.ComponentModel;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace NightGate.Desktop;

public static class TrayExitPrompt
{
    public const string Message =
        "收尾防的是临时冲动，不防管理员或主动停用。退出后，今晚的电脑保护将停止。";

    internal static bool Confirm(IConfirmationDialogService dialogs, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        return dialogs.Confirm(owner, ConfirmationDialogRequests.ExitApplication);
    }
}

public sealed class TrayApplicationShell : IDisposable
{
    private readonly MainWindow _dashboard;
    private readonly System.Windows.Application _application;
    private readonly Func<Task>? _beforeExitAsync;
    private readonly IConfirmationDialogService _confirmationDialogs;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Icon _trayIcon;
    private readonly Forms.NotifyIcon _notifyIcon;
    private bool _isExiting;
    private bool _exitRequested;
    private bool _disposed;

    public TrayApplicationShell(
        MainWindow dashboard,
        System.Windows.Application application,
        Func<Task>? beforeExitAsync = null)
        : this(
            dashboard,
            application,
            beforeExitAsync,
            FluentConfirmationDialogService.Instance)
    {
    }

    internal TrayApplicationShell(
        MainWindow dashboard,
        System.Windows.Application application,
        Func<Task>? beforeExitAsync,
        IConfirmationDialogService confirmationDialogs)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(confirmationDialogs);
        _dashboard = dashboard;
        _application = application;
        _beforeExitAsync = beforeExitAsync;
        _confirmationDialogs = confirmationDialogs;

        Forms.ToolStripMenuItem openItem = new("打开收尾");
        openItem.Click += (_, _) => OpenDashboard();
        Forms.ToolStripMenuItem exitItem = new("退出");
        exitItem.Click += (_, _) => RequestExit();
        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(openItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _trayIcon = DesktopIconResource.CreateTrayIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _trayIcon,
            Text = "收尾——早点结束电脑娱乐",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => OpenDashboard();
        _dashboard.Closing += DashboardClosing;
    }

    public void OpenDashboard()
    {
        if (_disposed)
        {
            return;
        }

        if (!_dashboard.Dispatcher.CheckAccess())
        {
            _dashboard.Dispatcher.BeginInvoke(OpenDashboard);
            return;
        }

        if (_dashboard.WindowState == WindowState.Minimized)
        {
            _dashboard.WindowState = WindowState.Normal;
        }
        _dashboard.Show();
        _dashboard.Activate();
    }

    public void RequestExit() => _ = RequestExitAsync();

    public void ShowNotice(DesktopNoticePresentation notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (_disposed)
        {
            return;
        }

        if (!_dashboard.Dispatcher.CheckAccess())
        {
            _dashboard.Dispatcher.BeginInvoke(() => ShowNotice(notice));
            return;
        }

        try
        {
            _notifyIcon.BalloonTipTitle = notice.Title;
            _notifyIcon.BalloonTipText = notice.Message;
            _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(8_000);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException)
        {
            // A notification failure cannot affect the underlying policy.
        }
    }

    private async Task RequestExitAsync()
    {
        if (_disposed || _exitRequested)
        {
            return;
        }

        if (!_dashboard.Dispatcher.CheckAccess())
        {
            _ = _dashboard.Dispatcher.BeginInvoke(RequestExit);
            return;
        }

        if (!TrayExitPrompt.Confirm(_confirmationDialogs, _dashboard))
        {
            return;
        }

        _exitRequested = true;
        if (_beforeExitAsync is not null)
        {
            try
            {
                await _beforeExitAsync();
            }
            catch (Exception)
            {
                // Protection is already fail-open; an exit cleanup fault cannot trap the user.
            }
        }

        _isExiting = true;
        Dispose();
        _dashboard.Close();
        _application.Shutdown();
    }

    private void DashboardClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        _dashboard.Hide();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dashboard.Closing -= DashboardClosing;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
        _menu.Dispose();
    }
}
