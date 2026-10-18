using System.Diagnostics;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Helpers;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop;

public sealed class TrayApplicationContext : ApplicationContext
{
    // NotifyIcon.Text throws past 127 characters.
    private const int MaxTrayTextLength = 127;

    private readonly NotifyIcon _trayIcon;
    private readonly AppConfig _config;
    private readonly ApiClient _apiClient;
    private readonly NudgeService _nudges;
    private readonly ActivityTrackerService _tracker;
    private readonly System.Windows.Forms.Timer _statusTimer;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _signInOutItem;

    private bool _isInitializing = true;
    private bool _hadSession;

    public TrayApplicationContext(AppConfig config)
    {
        _config = config;
        _apiClient = new ApiClient(config);

        // Button presses on a nudge are handed to this, the UI, thread.
        _nudges = new NudgeService(config, new WindowsFormsSynchronizationContext());
        _tracker = new ActivityTrackerService(config, _apiClient, _nudges);

        _statusItem = new ToolStripMenuItem("Status: Starting…") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Start Tracking", null, OnToggleTracking);
        _autoStartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutoStart)
        {
            Checked = AutoStartHelper.IsEnabled(),
            CheckOnClick = true
        };
        _signInOutItem = new ToolStripMenuItem("Sign in…", null, OnSignInOut);

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(_statusItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_toggleItem);
        contextMenu.Items.Add(_autoStartItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Settings…", null, OnSettings);
        contextMenu.Items.Add("Open log folder", null, OnOpenLogFolder);
        contextMenu.Items.Add(_signInOutItem);
#if DEBUG
        var testNudge = new ToolStripMenuItem("Show test nudge");
        testNudge.DropDownItems.Add("Hyperfocus", null, (_, _) => _nudges.ShowTest(NudgeKind.Stretch));
        testNudge.DropDownItems.Add("Same app", null, (_, _) => _nudges.ShowTest(NudgeKind.AppStretch));
        testNudge.DropDownItems.Add("Break over", null, (_, _) => _nudges.ShowTest(NudgeKind.BreakOver));
        contextMenu.Items.Add(testNudge);
#endif
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, OnExit);

        using var iconStream = typeof(TrayApplicationContext).Assembly
            .GetManifestResourceStream("AdhdTimeOrganizer.ActivityTracking.Desktop.timeorganizer-icon.ico");
        _trayIcon = new NotifyIcon
        {
            Icon = iconStream is not null ? new Icon(iconStream) : SystemIcons.Application,
            Text = "Desktop Activity Tracker",
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _trayIcon.DoubleClick += OnTrayDoubleClick;

        // Status is pulled on the UI thread rather than pushed from the tracker's threads, so nothing
        // here is ever touched cross-thread.
        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _statusItem.Text = "Status: Connecting…";

        var hasSession = await _apiClient.TryRestoreSessionAsync();
        _isInitializing = false;
        _hadSession = hasSession;

        if (hasSession)
            _tracker.Start();
        else
            ShowLogin();

        RefreshStatus();
    }

    private void ShowLogin()
    {
        _config.Save();
        using var loginForm = new LoginForm(_apiClient);
        if (loginForm.ShowDialog() == DialogResult.OK)
        {
            _hadSession = true;
            _tracker.Start();
            _tracker.RequestSend();
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_isInitializing) return;

        var status = _tracker.GetStatus();
        var hasSession = _apiClient.HasSession;

        if (_hadSession && !hasSession)
        {
            // The server refused the refresh token. Tracking carries on into the outbox; say so once.
            _trayIcon.ShowBalloonTip(10_000, "Activity Tracker — signed out",
                "Your session ended. Tracking continues offline; double-click the tray icon to sign in.",
                ToolTipIcon.Warning);
        }
        _hadSession = hasSession;

        var breakLeft = _nudges.GetStatus().BreakEndsUtc - DateTime.UtcNow;
        var text = DescribeStatus(status, hasSession, breakLeft);
        _statusItem.Text = $"Status: {text}";
        _trayIcon.Text = Truncate($"Activity Tracker — {text}", MaxTrayTextLength);
        _toggleItem.Text = status.IsRunning ? "Pause Tracking" : "Start Tracking";
        _signInOutItem.Text = hasSession ? "Sign out" : "Sign in…";
    }

    internal static string DescribeStatus(TrackerStatus status, bool hasSession, TimeSpan? breakLeft = null)
    {
        var state = !status.IsRunning ? "Paused"
            : status.IsLocked ? "Locked — not recording"
            : status.LastPollError is { } error ? $"Error: {error}"
            : "Tracking";

        // Put first after the state: with a long error the tooltip is truncated from the end.
        if (breakLeft is { } left && left > TimeSpan.Zero)
            state += $" · on a break, {Math.Ceiling(left.TotalMinutes):0} min left";

        // One file per finished minute; the one just written is normally sent within seconds.
        var queued = status.PendingWindows > 1 ? $" · {status.PendingWindows} min unsent" : "";

        var connection = !hasSession ? " · signed out"
            : status.LastSendResult == SendResult.RetryLater && status.PendingWindows > 0 ? " · offline"
            : status.LastSentUtc is { } sent ? $" · sent {sent.ToLocalTime():HH:mm}"
            : "";

        return state + connection + queued;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    private void OnTrayDoubleClick(object? sender, EventArgs e)
    {
        if (!_apiClient.HasSession)
            ShowLogin();
        else
            OnToggleTracking(sender, e);
    }

    private void OnToggleTracking(object? sender, EventArgs e)
    {
        if (_tracker.IsRunning)
            _tracker.Stop();
        else
            _tracker.Start();

        RefreshStatus();
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        AutoStartHelper.SetEnabled(_autoStartItem.Checked);
    }

    private void OnSettings(object? sender, EventArgs e)
    {
        var previousUrl = _config.ApiBaseUrl;
        var previousPollInterval = _config.PollIntervalMs;

        using var settingsForm = new SettingsForm(_config);
        if (settingsForm.ShowDialog() != DialogResult.OK) return;

        _config.Save();

        if (_tracker.IsRunning && previousPollInterval != _config.PollIntervalMs)
        {
            _tracker.Stop();
            _tracker.Start();
        }

        // A session belongs to the server that issued it.
        if (!string.Equals(previousUrl, _config.ApiBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            _apiClient.SignOut();
            _hadSession = false;
            ShowLogin();
        }

        RefreshStatus();
    }

    private void OnOpenLogFolder(object? sender, EventArgs e)
    {
        Directory.CreateDirectory(Program.LogDirectory);
        Process.Start(new ProcessStartInfo { FileName = Program.LogDirectory, UseShellExecute = true });
    }

    private void OnSignInOut(object? sender, EventArgs e)
    {
        if (!_apiClient.HasSession)
        {
            ShowLogin();
            return;
        }

        // Signing out keeps tracking into the outbox; it is sent after the next sign-in.
        _apiClient.SignOut();
        _hadSession = false;
        RefreshStatus();
        ShowLogin();
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _tracker.Dispose(); // flushes the minute in progress to the outbox
        _nudges.Dispose();  // after the poll loop, which feeds it; clears its toasts
        _apiClient.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        Application.Exit();
    }
}
