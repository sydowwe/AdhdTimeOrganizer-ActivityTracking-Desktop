using System.Diagnostics;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Helpers;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Native;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop;

public enum PauseChoice { FifteenMinutes, OneHour, UntilTomorrowMorning, UntilResumed }

public sealed class TrayApplicationContext : ApplicationContext
{
    // NotifyIcon.Text throws past 127 characters.
    private const int MaxTrayTextLength = 127;

    /// <summary>The distinct-entries grid filtered to what no rule covers, where each row can be mapped.</summary>
    internal const string UncoveredEntriesPath = "/activity-tracking/desktop/settings/distinctEntries?coverage=Uncovered";

    /// <summary>The merged dashboard: every tracker's day, not just this PC's.</summary>
    internal const string DashboardPath = "/activity-tracking/unified";

    private const int TopActivityCount = 5;

    /// <summary>How many of today's lines Settings offers for the time chime, beside the distractions and earlier choices.</summary>
    private const int SettingsChimeLines = 20;

    /// <summary>How much of the log "Copy diagnostics" carries.</summary>
    private const int DiagnosticsLines = 200;

    private readonly NotifyIcon _trayIcon;
    private readonly TrayIcons _icons;
    private readonly ContextMenuStrip _contextMenu;
    private TrayIconState _iconState = TrayIconState.Normal;
    private readonly AppConfig _config;
    private readonly ApiClient _apiClient;
    private readonly NudgeService _nudges;
    private readonly BreakReporter _breakReporter;
    private readonly ActivityTrackerService _tracker;
    private readonly UpdateService _updates = new();
    private readonly System.Windows.Forms.Timer _statusTimer;

    private readonly ToolStripMenuItem _glanceItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _mapAppsItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _signInOutItem;
    private readonly ToolStripMenuItem _updateItem;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _refusedCapturesItem;
    private readonly ToolStripMenuItem _quietItem;
    private readonly ToolStripMenuItem _nudgesBackOnItem;
    private readonly GlobalHotkey _hotkey;

    private HotkeyRegistration _hotkeyState;
    private CaptureForm? _captureForm;

    private bool _isInitializing = true;

    // The mapping prompt is a balloon once, right after drift nudges are turned on — not on every start while they
    // stay on; the menu item stays while it applies.
    private bool _mappingPromptShown = true;
    private bool _balloonOpensMapping;

    /// <summary>What <see cref="AppConfig.Load(string)"/> had to do to the config, said once after start; null when nothing.</summary>
    private readonly (string Title, string Text)? _configNotice;

    /// <summary>Whether to say "running" once tracking has started: a start the user made, from the Start menu or the exe.</summary>
    private readonly bool _announceStart;

    /// <param name="announceStart">See <see cref="StartedByUser"/>.</param>
    public TrayApplicationContext(AppConfig config, (string Title, string Text)? configNotice = null, bool announceStart = false)
    {
        _config = config;
        _configNotice = configNotice;
        _announceStart = announceStart;
        _apiClient = new ApiClient(config);
        _breakReporter = new BreakReporter(_apiClient.SendBreakAsync);

        // Button presses on a nudge are handed to this, the UI, thread.
        _nudges = new NudgeService(config, new WindowsFormsSynchronizationContext(), signIn: SignInFromNotice,
            startBlock: plannerTaskId => _apiClient.StartBlockAsync(plannerTaskId, CancellationToken.None),
            skipBlock: plannerTaskId => _apiClient.SkipBlockAsync(plannerTaskId, CancellationToken.None),
            capture: OpenCapture, reportBreak: _breakReporter.Report, resume: ResumeFromReminder);
        _tracker = new ActivityTrackerService(config, _apiClient, _nudges);
        var ui = new WindowsFormsSynchronizationContext();
        _apiClient.SignedIn += () => ui.Post(_ => OnSignedIn(), null);

        // On this, the UI, thread: WM_HOTKEY comes through its message loop (see GlobalHotkey).
        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += OpenCapture;
        _captureItem = new ToolStripMenuItem("Capture a thought…", null, (_, _) => OpenCapture());
        // Thoughts the server refused stay on disk and are never sent again; this is how the user learns of them.
        _refusedCapturesItem = new ToolStripMenuItem("", null, (_, _) => OpenFolder(CaptureQueue.DefaultDirectory)) { Visible = false };

        // Opens today's breakdown. A submenu rather than a left-click flyout: a single click is half of every double-click,
        // which opens this menu. Filled when opened, so a refresh never rebuilds it under the mouse.
        _glanceItem = new ToolStripMenuItem("Today on this PC: 0 min");
        _glanceItem.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
        _glanceItem.DropDownOpening += (_, _) => FillTodayBreakdown();
        _statusItem = new ToolStripMenuItem("Status: Starting…") { Enabled = false };
        _mapAppsItem = new ToolStripMenuItem("Map apps for drift nudges…", null, (_, _) => OpenWebApp(UncoveredEntriesPath)) { Visible = false };
        _toggleItem = new ToolStripMenuItem("Start Tracking", null, OnToggleTracking);

        var pauseForItem = new ToolStripMenuItem("Pause for");
        pauseForItem.DropDownItems.Add("15 minutes", null, (_, _) => PauseFor(PauseChoice.FifteenMinutes));
        pauseForItem.DropDownItems.Add("1 hour", null, (_, _) => PauseFor(PauseChoice.OneHour));
        pauseForItem.DropDownItems.Add("Until tomorrow morning", null, (_, _) => PauseFor(PauseChoice.UntilTomorrowMorning));
        pauseForItem.DropDownItems.Add("Until I resume", null, (_, _) => PauseFor(PauseChoice.UntilResumed));

        // Toasts off for a while, recording on (prompt 25): a pause would lose the hour's record.
        _quietItem = new ToolStripMenuItem(QuietMenuText);
        _quietItem.DropDownItems.Add("30 minutes", null, (_, _) => QuietFor(QuietChoice.ThirtyMinutes));
        _quietItem.DropDownItems.Add("1 hour", null, (_, _) => QuietFor(QuietChoice.OneHour));
        _quietItem.DropDownItems.Add("Until tomorrow morning", null, (_, _) => QuietFor(QuietChoice.UntilTomorrowMorning));
        _quietItem.DropDownItems.Add("Until I turn them back on", null, (_, _) => QuietFor(QuietChoice.UntilTurnedBackOn));
        _quietItem.DropDownItems.Add(new ToolStripSeparator());
        _nudgesBackOnItem = new ToolStripMenuItem("Turn nudges back on", null, (_, _) => EndQuiet()) { Enabled = false };
        _quietItem.DropDownItems.Add(_nudgesBackOnItem);
        // Before the item reads the state: moves an entry an older build left, and repoints one whose exe has gone.
        AutoStartHelper.Current.Reconcile();
        _autoStartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutoStart) { CheckOnClick = true };
        RefreshAutoStartItem();
        _signInOutItem = new ToolStripMenuItem("Sign in…", null, OnSignInOut);
        // Hidden until a downloaded release waits; it would apply at the next start anyway.
        _updateItem = new ToolStripMenuItem("Restart to update", null, OnRestartToUpdate) { Visible = false };

        var contextMenu = _contextMenu = new ContextMenuStrip();
        // Windows' own startup settings can change it while the tracker runs.
        contextMenu.Opening += (_, _) => RefreshAutoStartItem();
        contextMenu.Items.Add(_glanceItem);
        contextMenu.Items.Add(_statusItem);
        contextMenu.Items.Add(_mapAppsItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_captureItem);
        contextMenu.Items.Add(_refusedCapturesItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_toggleItem);
        contextMenu.Items.Add(pauseForItem);
        contextMenu.Items.Add(_quietItem);
        contextMenu.Items.Add(_autoStartItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Settings…", null, OnSettings);
        contextMenu.Items.Add("Privacy…", null, OnPrivacy);
        contextMenu.Items.Add("Open log folder", null, OnOpenLogFolder);
        contextMenu.Items.Add("Copy diagnostics", null, OnCopyDiagnostics);
        contextMenu.Items.Add(_signInOutItem);
#if DEBUG
        var testNudge = new ToolStripMenuItem("Show test nudge");
        testNudge.DropDownItems.Add("Hyperfocus", null, (_, _) => _nudges.ShowTest(NudgeKind.Stretch));
        testNudge.DropDownItems.Add("Same app", null, (_, _) => _nudges.ShowTest(NudgeKind.AppStretch));
        testNudge.DropDownItems.Add("Break over", null, (_, _) => _nudges.ShowTest(NudgeKind.BreakOver));
        testNudge.DropDownItems.Add("Drift", null, (_, _) => _nudges.ShowTest(NudgeKind.Drift));
        testNudge.DropDownItems.Add("Start ramp", null, (_, _) => _nudges.ShowTest(NudgeKind.StartRamp));
        testNudge.DropDownItems.Add("Block ending", null, (_, _) => _nudges.ShowTest(NudgeKind.BlockEnd));
        testNudge.DropDownItems.Add("Time chime", null, (_, _) => _nudges.ShowTest(NudgeKind.TimeChime));
        testNudge.DropDownItems.Add("Upload problem", null, (_, _) => _nudges.ShowTest(NudgeKind.UploadHealth));
        testNudge.DropDownItems.Add("Still paused", null, (_, _) => _nudges.ShowTest(NudgeKind.PauseReminder));
        testNudge.DropDownItems.Add("Recording problem", null, (_, _) => _nudges.ShowTest(NudgeKind.RecordingHealth));
        contextMenu.Items.Add(testNudge);
#endif
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(new ToolStripMenuItem(UpdatePolicy.VersionText(AppVersion.Current, UpdateService.IsInstalled)) { Enabled = false });
        contextMenu.Items.Add(_updateItem);
        contextMenu.Items.Add("Exit", null, OnExit);

        // Normal, paused and "needs you", drawn once; RefreshStatus swaps them (see TrayIconStates).
        using (var iconStream = typeof(TrayApplicationContext).Assembly
                   .GetManifestResourceStream("AdhdTimeOrganizer.ActivityTracking.Desktop.timeorganizer-icon.ico"))
        using (var baseIcon = iconStream is not null ? new Icon(iconStream) : (Icon)SystemIcons.Application.Clone())
            _icons = new TrayIcons(baseIcon);

        _trayIcon = new NotifyIcon
        {
            Icon = _icons.For(_iconState),
            Text = "Desktop Activity Tracker",
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _trayIcon.DoubleClick += OnTrayDoubleClick;
        _trayIcon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonOpensMapping) OpenWebApp(UncoveredEntriesPath);
            _balloonOpensMapping = false;
        };
        _trayIcon.BalloonTipClosed += (_, _) => _balloonOpensMapping = false;

        RegisterCaptureHotkey(announceProblem: true);

        // Status is pulled on the UI thread rather than pushed from the tracker's threads, so nothing
        // here is ever touched cross-thread.
        _statusTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        _updates.Start();
        _ = InitializeAsync();
    }

    /// <summary>
    /// With a saved token, tracking starts at once and the session is restored in the background (review L8); the order is
    /// <see cref="SessionStartup"/>'s.
    /// </summary>
    private async Task InitializeAsync()
    {
        _statusItem.Text = "Status: Connecting…";

        await new SessionStartup(
            hasSavedToken: () => _apiClient.HasSession,
            restore: _apiClient.TryRestoreSessionAsync,
            startTracking: StartUnlessPaused,
            // After the pause balloon, which it replaces: a damaged config matters more.
            showConfigNotice: () =>
            {
                if (_configNotice is not { } notice) return;
                _balloonOpensMapping = false;
                _trayIcon.ShowBalloonTip(20_000, notice.Title, notice.Text, ToolTipIcon.Warning);
            },
            showLogin: ShowLogin).RunAsync(onStarted: () =>
        {
            _isInitializing = false;
            RefreshStatus();
            AnnounceStart();
        });

        RefreshStatus();
    }

    /// <summary>
    /// The Run entry and the updater start the tracker with <c>--minimized</c>: those starts stay silent. Any other start is
    /// the user's — the Start menu, the exe, the installer's first run — and a tray app gives no sign of it otherwise.
    /// </summary>
    internal static bool StartedByUser(IEnumerable<string> args) =>
        !args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

    /// <summary>"It's running", once, for a start the user made — only while recording; a pause and a sign-in say their own.</summary>
    private void AnnounceStart()
    {
        if (!_announceStart || !_tracker.IsRunning) return;
        _balloonOpensMapping = false;
        _trayIcon.ShowBalloonTip(5_000, "Activity Tracker is running",
            "It records from the notification area. Right-click its icon for the menu.", ToolTipIcon.Info);
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

        _balloonOpensMapping = false;
        _trayIcon.ShowBalloonTip(10_000, "Activity Tracker — paused",
            $"{DescribePause(_config.PausedUntilUtc)}. Double-click the tray icon to resume.", ToolTipIcon.Info);
        // That was the reminder for now: a restart doesn't add a "still paused" toast to it.
        _nudges.OnPauseAnnounced(now, PauseSnapshotNow());
    }

    private PauseSnapshot PauseSnapshotNow() =>
        new(_config.PausedUntilUtc, _config.PausedSinceUtc, Native.Win32.GetIdleTime());

    private void ShowLogin()
    {
        _config.Save();
        using var loginForm = new LoginForm(_apiClient, _config);
        // Tracking starts on ApiClient.SignedIn (OnSignedIn), not on the form's answer: a password login or a code exchange
        // still in flight when the form is closed may succeed afterwards, and the form has already said "cancelled".
        loginForm.ShowDialog();
        RefreshStatus();
    }

    /// <summary>A sign-in saved a session — from the form, or after it closed. On the UI thread.</summary>
    private void OnSignedIn()
    {
        StartUnlessPaused();
        _tracker.RequestSend();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_isInitializing) return;

        _tracker.ResumeIfPauseOver(DateTime.UtcNow);

        var status = _tracker.GetStatus();
        var hasSession = _apiClient.HasSession;
        var nowUtc = DateTime.UtcNow;

        // A quiet period that has run out is cleared, so the config — and a restart — says nudges are on again.
        if (QuietNudges.HasEnded(_config.NudgesQuietUntilUtc, nowUtc)) ClearQuiet();
        var quiet = QuietNudges.Describe(_config.NudgesQuietUntilUtc, nowUtc, TimeZoneInfo.Local);
        _quietItem.Text = quiet ?? QuietMenuText;
        _nudgesBackOnItem.Enabled = quiet is not null;

        // A session ended by the server, uploads stuck for a day, windows refused: a toast, once, and a repeat at most
        // daily. It replaces the "signed out" balloon, which could not be withdrawn or switched off.
        _nudges.OnUploadStatus(nowUtc, new UploadSnapshot(
            hasSession, status.PausedUntilUtc is { } pausedUntil && pausedUntil > nowUtc,
            status.PendingWindows, status.OldestPendingUtc, status.RejectedWindows,
            status.LastSendResult == SendResult.RetryLater ? status.LastStatusCode : null));
        // "Tracking has been paused for 4 h": from here, because the poll loop is stopped while paused.
        _nudges.OnPauseStatus(nowUtc, PauseSnapshotNow());
        // Is recording itself working? From here too, with the idle time read here: the poll loop's health is the question.
        _nudges.OnRecordingStatus(nowUtc, new RecordingSnapshot(
            status.IsRunning, status.PausedUntilUtc is { } paused && paused > nowUtc, status.IsLocked, Native.Win32.GetIdleTime(),
            status.PollFailingSinceUtc, status.LastPollUtc, status.UnrecordedFor));

        var iconState = TrayIconStates.For(status, hasSession, nowUtc);
        if (iconState != _iconState)
        {
            _iconState = iconState;
            _trayIcon.Icon = _icons.For(iconState);
        }

        var breakLeft = _nudges.GetStatus().BreakEndsUtc - nowUtc;
        var text = DescribeStatus(status, hasSession, breakLeft, quiet);
        var glance = DescribeGlance(status, nowUtc);
        _glanceItem.Text = glance;
        _statusItem.Text = $"Status: {text}";
        // The glance first: a long status is truncated from the end.
        _trayIcon.Text = Truncate($"{glance}\n{text}", MaxTrayTextLength);
        _toggleItem.Text = status.IsRunning ? "Pause Tracking" : status.PausedUntilUtc is not null ? "Resume Tracking" : "Start Tracking";
        _signInOutItem.Text = hasSession ? "Sign out" : "Sign in…";

        var restart = UpdatePolicy.RestartText(_updates.PendingVersion);
        _updateItem.Visible = restart is not null;
        if (restart is not null) _updateItem.Text = restart;

        RefreshMappingPrompt(status.Coverage);
        RefreshCaptureItems();
    }

    private void RefreshCaptureItems()
    {
        var captures = _tracker.GetCaptureStatus();
        _captureItem.Text = DescribeCaptureItem(_hotkeyState, _config.CaptureHotkey, captures);
        var refused = DescribeRefusedCaptures(captures);
        _refusedCapturesItem.Visible = refused is not null;
        if (refused is not null) _refusedCapturesItem.Text = refused;
    }

    /// <summary>"2 thoughts the server refused — open folder", or null when there are none. A count only.</summary>
    internal static string? DescribeRefusedCaptures(CaptureStatus? captures) => captures?.Refused switch
    {
        null or <= 0 => null,
        1 => "1 thought the server refused — open folder",
        var n => $"{n} thoughts the server refused — open folder"
    };

    // ── Quick capture ────────────────────────────────────────────────────────

    /// <summary>
    /// (Re)registers the capture hotkey from the config. A chord another app holds is said in the tray item, and — at start
    /// only — in a balloon, since nothing else would tell the user why the keys do nothing.
    /// </summary>
    private void RegisterCaptureHotkey(bool announceProblem)
    {
        var chord = HotkeyChord.TryParse(_config.CaptureHotkey);
        _hotkeyState = string.IsNullOrWhiteSpace(_config.CaptureHotkey) ? HotkeyRegistration.Off
            : chord is null ? HotkeyRegistration.Failed
            : _hotkey.Register(chord);
        Serilog.Log.Information("Capture hotkey: {State}", _hotkeyState);

        _captureItem.ShortcutKeyDisplayString = _hotkeyState == HotkeyRegistration.Registered ? chord!.ToString() : null;
        RefreshCaptureItems();

        if (!announceProblem || _hotkeyState is HotkeyRegistration.Off or HotkeyRegistration.Registered) return;
        _balloonOpensMapping = false;
        _trayIcon.ShowBalloonTip(10_000, "Capture hotkey not set",
            $"{DescribeHotkeyProblem(_hotkeyState, _config.CaptureHotkey)}. Pick another in Settings; \"Capture a thought…\" in this menu works meanwhile.",
            ToolTipIcon.Warning);
    }

    /// <summary>
    /// "Capture a thought… (2 waiting)" — with what is wrong with the hotkey, and why parked thoughts wait, when either is
    /// the case. Counts only: the thoughts are the user's.
    /// </summary>
    internal static string DescribeCaptureItem(HotkeyRegistration hotkey, string? hotkeyText, CaptureStatus? captures)
    {
        var notes = new List<string>();
        if (hotkey is HotkeyRegistration.Taken or HotkeyRegistration.Failed) notes.Add(DescribeHotkeyProblem(hotkey, hotkeyText));
        if (captures is { Pending: > 0 } waiting)
            notes.Add(waiting.Held switch
            {
                CaptureSendResult.NotSupported => $"{waiting.Pending} waiting: the server can't take them yet",
                CaptureSendResult.NoPriority => $"{waiting.Pending} waiting: add a task priority in the web app",
                _ => $"{waiting.Pending} waiting to upload"
            });
        return notes.Count == 0 ? "Capture a thought…" : $"Capture a thought… ({string.Join("; ", notes)})";
    }

    private static string DescribeHotkeyProblem(HotkeyRegistration hotkey, string? hotkeyText) =>
        hotkey == HotkeyRegistration.Taken ? $"{hotkeyText} is used by another app" : $"{hotkeyText} can't be used as a hotkey";

    /// <summary>
    /// Opens the capture box, or brings the open one back. Focus goes back afterwards to the window the user was in: the
    /// foreground for the hotkey and a toast's button, the window before the tray for a tray click.
    /// </summary>
    private void OpenCapture()
    {
        if (_captureForm is { IsDisposed: false } open)
        {
            open.Reactivate();
            return;
        }

        var ownPid = Environment.ProcessId;
        var returnTo = CaptureFocus.PickReturnTarget(
            [Native.Win32.GetForegroundWindow(), .. Native.ForegroundEvents.RecentForeignForegrounds()],
            h => Native.Win32.GetWindowProcessId(h) != ownPid && Native.Win32.IsRealWindow(h));
        var planLine = CaptureFocus.PlanLine(_tracker.GetStatus().LastResolved, DateTime.UtcNow, TimeZoneInfo.Local);

        _captureForm = new CaptureForm(planLine, returnTo, _tracker.Capture);
        // A modeless form disposes itself on close.
        _captureForm.FormClosed += (_, _) =>
        {
            _captureForm = null;
            RefreshStatus();
        };
        _captureForm.Show();
    }

    /// <summary>
    /// "Map Slack, Chrome and Teams to enable drift nudges": a menu item while it applies, and a balloon the first time
    /// the coverage arrives after drift nudges were turned on. Clicking either opens the uncovered entries.
    /// </summary>
    private void RefreshMappingPrompt(TrackerCoverage? coverage)
    {
        var prompt = coverage is { Uncovered.Count: > 0 } ? DescribeMappingPrompt(coverage) : null;
        _mapAppsItem.Visible = prompt is not null;
        if (prompt is null) return;

        _mapAppsItem.Text = prompt + "…";
        if (_mappingPromptShown) return;

        _mappingPromptShown = true;
        _balloonOpensMapping = true;
        _trayIcon.ShowBalloonTip(15_000, "Drift nudges need mapped apps",
            $"{prompt}. Click to map them — time in apps without a rule can't be compared with your plan.", ToolTipIcon.Info);
    }

    /// <summary>"Today on this PC: 5 h 12 m · now: Thesis". "Now" only while the last minute the server resolved is recent.</summary>
    internal static string DescribeGlance(TrackerStatus status, DateTime nowUtc)
    {
        var today = $"Today on this PC: {NudgeToast.FormatLength(status.TodayOnThisPc)}";
        return DescribeNow(status.LastResolved, nowUtc) is { } now ? $"{today} · now: {now}" : today;
    }

    /// <summary>
    /// What the latest minute's foreground time mostly mapped to, skipping ignored time; null when there is no recent
    /// minute (paused, idle, offline, an old server) — a stale "now" is worse than none.
    /// </summary>
    internal static string? DescribeNow(ResolvedMinute? last, DateTime nowUtc)
    {
        if (last is null || last.WindowStartUtc < nowUtc - DriftPolicy.FreshFor) return null;

        var top = last.Response.Resolution.FirstOrDefault(r => !r.IsIgnored);
        return top is null ? null : top.Name ?? (top.IsUnmapped ? "not mapped" : null);
    }

    /// <summary>
    /// Each line of the breakdown opens one checkable item, "Chime every 15 min" — where the user sees where the time
    /// went is where they choose to hear about it. Unmapped time has nothing to chime on and stays a plain line.
    /// </summary>
    private void FillTodayBreakdown()
    {
        _glanceItem.DropDownItems.Clear();
        var top = _tracker.GetTodayTop(TopActivityCount);
        var lines = DescribeTodayBreakdown(top);
        var settings = ChimeSettings.From(_config);
        var distractions = _nudges.GetChimeDistractions();

        for (var i = 0; i < lines.Count; i++)
        {
            if (i >= top.Count || top[i].Key == TodayTotal.UnmappedKey)
            {
                _glanceItem.DropDownItems.Add(new ToolStripMenuItem(lines[i]) { Enabled = false });
                continue;
            }

            var total = top[i];
            var isDistraction = distractions.ContainsKey(total.Key);
            var isChimed = settings.Enabled && settings.Chimes(total.Key, isDistraction);
            var line = new ToolStripMenuItem(lines[i]);
            line.DropDownItems.Add(new ToolStripMenuItem($"Chime every {(int)settings.Interval.TotalMinutes} min", null,
                (_, _) => SetChime(total, isDistraction, !isChimed)) { Checked = isChimed });
            _glanceItem.DropDownItems.Add(line);
        }
        _glanceItem.DropDownItems.Add(new ToolStripSeparator());
        _glanceItem.DropDownItems.Add("Open dashboard", null, (_, _) => OpenWebApp(DashboardPath));
    }

    /// <summary>Ticking a line also turns the chime on: asking for it is the opt-in.</summary>
    private void SetChime(ActivityTotal total, bool isDistraction, bool chime)
    {
        _config.TimeChimeChoices = ChimeChoices.With(
            _config.TimeChimeChoices, total.Key, total.Name, chime, isDistraction, _config.TimeChimeDistractions);
        if (chime) _config.TimeChimeEnabled = true;
        _config.Save();
    }

    /// <summary>"Thesis — 2 h 10 m", busiest first; one line saying there is nothing yet when there isn't.</summary>
    internal static IReadOnlyList<string> DescribeTodayBreakdown(IReadOnlyList<ActivityTotal> top) =>
        top.Count == 0
            ? ["Nothing sent yet today"]
            : top.Select(a => $"{a.Name} — {NudgeToast.FormatLength(TimeSpan.FromSeconds(a.ActiveSeconds))}").ToList();

    /// <summary>"Map Slack, Chrome and Teams to enable drift nudges".</summary>
    internal static string DescribeMappingPrompt(TrackerCoverage coverage)
    {
        var names = coverage.Uncovered.Select(a => a.ProductName.Length > 0 ? a.ProductName : a.ProcessName).ToArray();
        var list = names.Length == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";
        return $"Map {list} to enable drift nudges";
    }

    private void OpenWebApp(string pathAndQuery)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _config.WebAppUri(pathAndQuery).AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open the web app");
        }
    }

    /// <param name="quiet">"Nudges quiet until 14:30" (<see cref="QuietNudges.Describe"/>), or null.</param>
    internal static string DescribeStatus(TrackerStatus status, bool hasSession, TimeSpan? breakLeft = null, string? quiet = null)
    {
        var state = !status.IsRunning ? DescribePause(status.PausedUntilUtc)
            : status.IsLocked ? "Locked — not recording"
            : status.LastPollError is { } error ? $"Error: {error}"
            : "Tracking";

        // Put first after the state: with a long error the tooltip is truncated from the end.
        if (breakLeft is { } left && left > TimeSpan.Zero)
            state += $" · on a break, {Math.Ceiling(left.TotalMinutes):0} min left";

        // Recording goes on; only the toasts wait. Said while it lasts, so a quiet afternoon is not a broken tracker.
        if (quiet is { Length: > 0 })
            state += $" · {char.ToLowerInvariant(quiet[0])}{quiet[1..]}";

        // One file per finished minute; the one just written is normally sent within seconds.
        var queued = status.PendingWindows > 1 ? $" · {status.PendingWindows} min unsent" : "";

        // Minutes the server refused: gone for good, and the one stall that never shows as "offline".
        var dropped = status.RejectedWindows > 0 ? $" · {status.RejectedWindows} min dropped" : "";

        var connection = !hasSession ? " · signed out"
            : status.LastSendResult == SendResult.RetryLater && status.PendingWindows > 0 ? $" · {UploadHealthPolicy.WhyWaiting(status.LastStatusCode)}"
            : status.LastSentUtc is { } sent ? $" · sent {sent.ToLocalTime():HH:mm}"
            : "";

        return state + connection + queued + dropped;
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
                // The next 06:00 on the user's clock: tonight's pause ends tomorrow, one at 02:00 later today. The same
                // morning "Quiet nudges for" ends at.
                return QuietNudges.NextMorningUtc(utcNow, zone);
            default:
                return DateTime.MaxValue;
        }
    }

    private void PauseFor(PauseChoice choice)
    {
        _tracker.Pause(PauseEnd(choice, DateTime.UtcNow, TimeZoneInfo.Local));
        RefreshStatus();
    }

    private const string QuietMenuText = "Quiet nudges for";

    /// <summary>Saved at once, like a pause, so a restart keeps it. Recording is not touched.</summary>
    private void QuietFor(QuietChoice choice)
    {
        var until = QuietNudges.Until(choice, DateTime.UtcNow, TimeZoneInfo.Local);
        _config.NudgesQuietUntilUtc = until;
        _config.Save();
        Serilog.Log.Information("Nudges quiet until {Until:u}", until);
        RefreshStatus();
    }

    /// <summary>"Turn nudges back on". A nudge held through the quiet shows at the next poll if it is still true.</summary>
    private void EndQuiet()
    {
        ClearQuiet();
        RefreshStatus();
    }

    /// <summary>Also for a quiet period that ran out, from <see cref="RefreshStatus"/>.</summary>
    private void ClearQuiet()
    {
        if (_config.NudgesQuietUntilUtc is null) return;

        _config.NudgesQuietUntilUtc = null;
        _config.Save();
        Serilog.Log.Information("Nudges back on");
    }

    private void OnPrivacy(object? sender, EventArgs e)
    {
        using var privacyForm = new PrivacyForm(_config);
        if (privacyForm.ShowDialog() == DialogResult.OK)
            _config.Save(); // the poll loop reads the rules live, so they apply from its next poll
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";

    /// <summary>
    /// Never stops recording: it was a pause "until I resume", and the log showed it pressed by accident — every one undone
    /// within seconds. Signed out it signs in; paused it resumes (the start-up balloon says so); otherwise it opens the menu.
    /// </summary>
    private void OnTrayDoubleClick(object? sender, EventArgs e)
    {
        switch (DoubleClickAction(_apiClient.HasSession, _tracker.IsPaused(DateTime.UtcNow)))
        {
            case TrayDoubleClick.SignIn:
                ShowLogin();
                break;
            case TrayDoubleClick.Resume:
                _tracker.Resume();
                RefreshStatus();
                break;
            default:
                ShowContextMenu();
                break;
        }
    }

    internal enum TrayDoubleClick { SignIn, Resume, OpenMenu }

    internal static TrayDoubleClick DoubleClickAction(bool hasSession, bool isPaused) =>
        !hasSession ? TrayDoubleClick.SignIn : isPaused ? TrayDoubleClick.Resume : TrayDoubleClick.OpenMenu;

    /// <summary>
    /// The menu a right-click opens. <see cref="NotifyIcon"/>'s own private <c>ShowContextMenu</c> brings it to the front
    /// first, so a click elsewhere closes it; a plain <c>Show</c> would leave it hanging. If a WinForms update renames it,
    /// the fallback still opens the menu (<c>SmallRuleTests</c> pins that the method exists).
    /// </summary>
    private void ShowContextMenu()
    {
        if (NotifyIconShowContextMenu is { } show)
        {
            show.Invoke(_trayIcon, null);
            return;
        }

        _contextMenu.Show(Cursor.Position);
    }

    internal static readonly System.Reflection.MethodInfo? NotifyIconShowContextMenu = typeof(NotifyIcon).GetMethod(
        "ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes);

    /// <summary>"Resume" on a pause reminder, on this thread. A reminder can outlive the pause, so it does nothing once tracking runs.</summary>
    private void ResumeFromReminder()
    {
        if (!_tracker.IsPaused(DateTime.UtcNow)) return;

        _tracker.Resume();
        RefreshStatus();
    }

    /// <summary>"Sign in" on an upload notice. A notice can outlive the problem, so it does nothing once signed in.</summary>
    private void SignInFromNotice()
    {
        if (!_apiClient.HasSession) ShowLogin();
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
        AutoStartHelper.Current.SetEnabled(_autoStartItem.Checked);
        RefreshAutoStartItem();
    }

    /// <summary>Ticked only when Windows will really start the tracker; says so when the user switched it off in Windows.</summary>
    private void RefreshAutoStartItem()
    {
        var state = AutoStartHelper.Current.GetState();
        _autoStartItem.Checked = state == AutoStartState.On;
        _autoStartItem.Text = DescribeAutoStart(state);
    }

    internal static string DescribeAutoStart(AutoStartState state) =>
        state == AutoStartState.DisabledInWindows ? "Start with Windows (turned off in Windows settings)" : "Start with Windows";

    private void OnSettings(object? sender, EventArgs e)
    {
        var previousUrl = _config.ApiBaseUrl;
        var previousPollInterval = _config.PollIntervalMs;
        var hadDriftNudges = _config.DriftNudgesEnabled;
        var previousHotkey = _config.CaptureHotkey;

        using var settingsForm = new SettingsForm(_config, ChimeChoices.Candidates(
            _tracker.GetTodayTop(SettingsChimeLines), _nudges.GetChimeDistractions(), _config.TimeChimeChoices));
        if (settingsForm.ShowDialog() != DialogResult.OK) return;

        _config.Save();

        // A new chord another app holds is said at once, like at start; the tray item keeps saying it.
        if (!string.Equals(previousHotkey, _config.CaptureHotkey, StringComparison.Ordinal))
            RegisterCaptureHotkey(announceProblem: true);

        // Turned on: ask for the coverage now, and say once what is missing.
        if (_config.DriftNudgesEnabled && !hadDriftNudges)
        {
            _mappingPromptShown = false;
            _tracker.RefreshCoverage();
        }

        if (_tracker.IsRunning && previousPollInterval != _config.PollIntervalMs)
        {
            _tracker.Stop();
            _tracker.Start();
        }

        // A session belongs to the server that issued it — and is revoked there, not on the new one.
        if (!string.Equals(previousUrl, _config.ApiBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            _ = _apiClient.SignOut(previousUrl);
            ShowLogin();
        }

        RefreshStatus();
    }

    private void OnOpenLogFolder(object? sender, EventArgs e) => OpenFolder(Program.LogDirectory);

    private static void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open a folder");
        }
    }

    /// <summary>For a bug report: the state, the settings as counts and the log's tail, on the clipboard.</summary>
    private void OnCopyDiagnostics(object? sender, EventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagnosticsReport.Format(
                DiagnosticsEnvironment.Current(), _config, _tracker.GetStatus(), _tracker.ServerRuleCount, _tracker.LastKeepAlive,
                DiagnosticsLog.Tail(Program.LogDirectory, DiagnosticsLines), DateTime.UtcNow,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

            _balloonOpensMapping = false;
            _trayIcon.ShowBalloonTip(3_000, "Diagnostics copied", "Paste them into your bug report.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another program for a moment.
            Serilog.Log.Warning(ex, "Could not copy diagnostics");
        }
    }

    private void OnSignInOut(object? sender, EventArgs e)
    {
        if (!_apiClient.HasSession)
        {
            ShowLogin();
            return;
        }

        // Signing out keeps tracking into the outbox; it is sent after the next sign-in. The server's revoke runs on its
        // own, not waited for.
        _ = _apiClient.SignOut();
        RefreshStatus();
        ShowLogin();
    }

    /// <summary>
    /// The updater is started first and waits for this process to end; then the ordinary exit runs, so the update costs
    /// nothing the Exit item wouldn't (see <see cref="UpdateService"/>). If the updater can't start, nothing exits.
    /// </summary>
    private void OnRestartToUpdate(object? sender, EventArgs e)
    {
        if (_updates.PrepareRestart())
            OnExit(sender, e);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        _captureForm?.ParkAndClose(); // before the tracker goes: a typed thought is parked, not lost to the exit
        _hotkey.Dispose();
        _updates.Dispose();
        _tracker.Dispose(); // flushes the minute in progress to the outbox
        _nudges.Dispose();  // after the poll loop, which feeds it; clears its toasts
        _breakReporter.Dispose(); // a report not sent yet is let go: it only ever served the phone's nudge
        _apiClient.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _icons.Dispose(); // after the NotifyIcon that shows one
        Application.Exit();
    }
}
