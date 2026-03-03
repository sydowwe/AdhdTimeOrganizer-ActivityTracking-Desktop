using DesktopActivityTracker.Helpers;
using DesktopActivityTracker.Models;
using DesktopActivityTracker.Services;

namespace DesktopActivityTracker;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly AppConfig _config;
    private readonly ApiClient _apiClient;
    private readonly ActivityTrackerService _tracker;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _autoStartItem;

    public TrayApplicationContext(AppConfig config)
    {
        _config = config;
        _apiClient = new ApiClient(config);
        _tracker = new ActivityTrackerService(config, _apiClient);

        _statusItem = new ToolStripMenuItem("Status: Not authenticated") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Start Tracking", null, OnToggleTracking) { Enabled = false };
        _autoStartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutoStart)
        {
            Checked = AutoStartHelper.IsEnabled(),
            CheckOnClick = true
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_statusItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_toggleItem);
        contextMenu.Items.Add(_autoStartItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Settings...", null, OnSettings);
        contextMenu.Items.Add("Logout", null, OnLogout);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, OnExit);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application, // TODO: Replace with custom icon
            Text = "Desktop Activity Tracker",
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _trayIcon.DoubleClick += OnToggleTracking;

        _tracker.StatusChanged += status =>
        {
            _statusItem.Text = $"Status: {status}";
            _trayIcon.Text = $"Activity Tracker - {status}";
            _toggleItem.Text = _tracker.IsRunning ? "Pause Tracking" : "Start Tracking";
        };

        // Try to authenticate and start
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _statusItem.Text = "Status: Connecting...";

        if (await _apiClient.TryRestoreSessionAsync())
        {
            _statusItem.Text = "Status: Authenticated";
            _toggleItem.Enabled = true;

            // Auto-start tracking
            _tracker.Start();
            _toggleItem.Text = "Pause Tracking";
        }
        else
        {
            ShowLogin();
        }
    }

    private void ShowLogin()
    {
        using var loginForm = new LoginForm(_apiClient);
        if (loginForm.ShowDialog() == DialogResult.OK)
        {
            _statusItem.Text = "Status: Authenticated";
            _toggleItem.Enabled = true;
            _tracker.Start();
            _toggleItem.Text = "Pause Tracking";
        }
        else
        {
            _statusItem.Text = "Status: Not authenticated";
        }
    }

    private void OnToggleTracking(object? sender, EventArgs e)
    {
        if (!_apiClient.IsAuthenticated)
        {
            ShowLogin();
            return;
        }

        if (_tracker.IsRunning)
            _tracker.Stop();
        else
            _tracker.Start();
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        AutoStartHelper.SetEnabled(_autoStartItem.Checked);
    }

    private void OnSettings(object? sender, EventArgs e)
    {
        // TODO: Settings form for API URL, poll interval, idle threshold
        MessageBox.Show(
            $"API URL: {_config.ApiBaseUrl}\nPoll Interval: {_config.PollIntervalMs}ms\nIdle Threshold: {_config.IdleThresholdSeconds}s",
            "Settings",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OnLogout(object? sender, EventArgs e)
    {
        _tracker.Stop();
        _config.RefreshToken = null;
        _config.Save();
        _toggleItem.Enabled = false;
        _statusItem.Text = "Status: Logged out";
        _toggleItem.Text = "Start Tracking";

        ShowLogin();
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _tracker.Stop();
        _tracker.Dispose();
        _apiClient.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }
}
