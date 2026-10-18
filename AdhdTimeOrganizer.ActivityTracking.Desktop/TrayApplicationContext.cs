using System.Diagnostics;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Helpers;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop;

public enum PauseChoice { FifteenMinutes, OneHour, UntilTomorrowMorning, UntilResumed }

public sealed class TrayApplicationContext : ApplicationContext
{
    // NotifyIcon.Text throws past 127 characters.
    private const int MaxTrayTextLength = 127;

    // "Until tomorrow morning" ends here, local time.
    private static readonly TimeSpan MorningResumeTime = TimeSpan.FromHours(6);

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

        var pauseForItem = new ToolStripMenuItem("Pause for");
        pauseForItem.DropDownItems.Add("15 minutes", null, (_, _) => PauseFor(PauseChoice.FifteenMinutes));
        pauseForItem.DropDownItems.Add("1 hour", null, (_, _) => PauseFor(PauseChoice.OneHour));
        pauseForItem.DropDownItems.Add("Until tomorrow morning", null, (_, _) => PauseFor(PauseChoice.UntilTomorrowMorning));
        pauseForItem.DropDownItems.Add("Until I resume", null, (_, _) => PauseFor(PauseChoice.UntilResumed));
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
        contextMenu.Items.Add(pauseForItem);
        contextMenu.Items.Add(_autoStartItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Settings…", null, OnSettings);
        contextMenu.Items.Add("Privacy…", null, OnPrivacy);
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
            StartUnlessPaused();
        else
            ShowLogin();

        RefreshStatus();
    }

    /// <summary>
    /// Starts tracking, unless the user paused it and the pause has not run out — a pause is saved, so it
    /// outlives a restart. Says so once, since nothing else would.
    /// </summary>
    private void StartUnlessPaused()
    {
        var now = DateTime.UtcNow;
        if (_tracker.ResumeIfPauseOver(now)) return;

        if (!_tracker.IsPaused(now))
        {
            _tracker.Start();
            return;
        }

        _trayIcon.ShowBalloonTip(10_000, "Activity Tracker — paused",
            $"{DescribePause(_config.PausedUntilUtc)}. Double-click the tray icon to resume.", ToolTipIcon.Info);
    }

    private void ShowLogin()
    {
        _config.Save();
        using var loginForm = new LoginForm(_apiClient);
        if (loginForm.ShowDialog() == DialogResult.OK)
        {
            _hadSession = true;
            StartUnlessPaused();
            _tracker.RequestSend();
        }

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_isInitializing) return;

        _tracker.ResumeIfPauseOver(DateTime.UtcNow);

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
        _toggleItem.Text = status.IsRunning ? "Pause Tracking" : status.PausedUntilUtc is not null ? "Resume Tracking" : "Start Tracking";
        _signInOutItem.Text = hasSession ? "Sign out" : "Sign in…";
    }

    internal static string DescribeStatus(TrackerStatus status, bool hasSession, TimeSpan? breakLeft = null)
    {
        var state = !status.IsRunning ? DescribePause(status.PausedUntilUtc)
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

    internal static string DescribePause(DateTime? pausedUntilUtc)
    {
        if (pausedUntilUtc is not { } until || until == DateTime.MaxValue) return "Paused";

        var local = DateTime.SpecifyKind(until, DateTimeKind.Utc).ToLocalTime();
        var day = local.Date == DateTime.Now.Date ? "" : local.Date == DateTime.Now.Date.AddDays(1) ? "tomorrow " : $"{local:ddd} ";
        return $"Paused until {day}{local:HH:mm}";
    }

    /// <summary>When a pause chosen at <paramref name="utcNow"/> ends; <see cref="DateTime.MaxValue"/> for "until I resume".</summary>
    internal static DateTime PauseEnd(PauseChoice choice, DateTime utcNow, TimeZoneInfo zone)
    {
        switch (choice)
        {
            case PauseChoice.FifteenMinutes:
                return utcNow.AddMinutes(15);
            case PauseChoice.OneHour:
                return utcNow.AddHours(1);
            case PauseChoice.UntilTomorrowMorning:
                // The next 06:00 on the user's clock: tonight's pause ends tomorrow, one at 02:00 later today.
                var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
                var morning = local.Date + MorningResumeTime;
                if (morning <= local) morning = morning.AddDays(1);
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(morning, DateTimeKind.Unspecified), zone);
            default:
                return DateTime.MaxValue;
        }
    }

    private void PauseFor(PauseChoice choice)
    {
        _tracker.Pause(PauseEnd(choice, DateTime.UtcNow, TimeZoneInfo.Local));
        RefreshStatus();
    }

    private void OnPrivacy(object? sender, EventArgs e)
    {
        using var privacyForm = new PrivacyForm(_config);
        if (privacyForm.ShowDialog() == DialogResult.OK)
            _config.Save(); // the poll loop reads the rules live, so they apply from its next poll
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
            _tracker.Pause(DateTime.MaxValue);
        else
            _tracker.Resume();

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
