using System.Diagnostics;
using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Native;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// Hyperfocus, break and drift nudges: feeds each poll to <see cref="HyperfocusPolicy"/> and each sent minute's
/// server verdict to <see cref="DriftPolicy"/>, and shows what they ask for. Nothing about a nudge reaches the server;
/// the drift nudge only reads what the heartbeat answered.
/// </summary>
/// <remarks>
/// <para>
/// Fed by the tracker's poll loop, so pausing tracking pauses nudges too: a paused tracker isn't watching,
/// and the policy counts the paused time as a break if it was long enough. The break timer has its own
/// timer, so the "break's over" nudge comes even while paused. Drift is fed from the send loop, a minute
/// at a time, and a paused tracker sends no new minutes.
/// </para>
/// <para>
/// A call with no keyboard input is not a break: while an app holds the microphone the user counts as
/// present here. That is for nudges only — what the tracker records and sends doesn't change.
/// </para>
/// <para>
/// Threads: polls arrive on the poll loop's thread, minutes on the send loop's, the break timer on the thread
/// pool, button presses on the thread pool and are posted to the UI thread (they can change and save the config,
/// which the Settings form edits there). Everything that touches a policy holds <see cref="_lock"/>. No WinForms
/// objects here.
/// </para>
/// </remarks>
public sealed class NudgeService : IDisposable
{
    private static readonly TimeSpan MicrophoneCheckEvery = TimeSpan.FromSeconds(10);

    /// <summary>"Switch plan" opens today's planner.</summary>
    internal const string PlannerPath = "/day-planner";

    public static string DefaultDriftStatePath { get; } = Path.Combine(AppConfig.ConfigDirectory, "drift.json");

    private readonly AppConfig _config;
    private readonly SynchronizationContext? _uiContext;
    private readonly INudgeNotifier _notifier;
    private readonly Action<Uri> _openInBrowser;
    private readonly Action? _signIn;
    private readonly Action<string> _openFolder;
    private readonly string _driftStatePath;
    private readonly ILogger _log = Log.ForContext<NudgeService>();

    private readonly Lock _lock = new();
    private readonly Lock _driftSaveGate = new();
    private readonly HyperfocusPolicy _policy = new(TimeSpan.FromSeconds(10));
    private readonly DriftPolicy _drift = new();
    private readonly UploadHealthPolicy _upload = new();
    private readonly System.Threading.Timer _breakTimer;

    private DateTime _microphoneCheckedUtc = DateTime.MinValue;
    private bool _isMicrophoneInUse;
    private string? _quietReason;

    // When each family last showed a toast, for the gap between the two (under _lock).
    private DateTime? _hyperfocusShownUtc;
    private DateTime? _driftShownUtc;
    private bool _driftChanged;

    /// <param name="uiContext">Where button presses are handled; the tray's.</param>
    /// <param name="signIn">"Sign in" on an upload notice; opens the sign-in form on the UI thread. Null does nothing.</param>
    public NudgeService(AppConfig config, SynchronizationContext? uiContext, Action? signIn = null)
        : this(config, uiContext, onActivated => new NudgeNotifier(onActivated), OpenInBrowser, DefaultDriftStatePath, signIn)
    {
    }

    /// <param name="createNotifier">Given the button-press handler; tests pass a fake, so nothing registers with Windows.</param>
    /// <param name="openInBrowser">"Switch plan"; tests record it instead.</param>
    /// <param name="driftStatePath">Where <see cref="DriftState"/> is kept across restarts.</param>
    /// <param name="signIn">"Sign in"; tests count it.</param>
    /// <param name="openFolder">"Open log folder"; tests record the path instead.</param>
    internal NudgeService(AppConfig config, SynchronizationContext? uiContext,
        Func<Action<NudgeActivation>, INudgeNotifier> createNotifier, Action<Uri> openInBrowser, string driftStatePath,
        Action? signIn = null, Action<string>? openFolder = null)
    {
        _config = config;
        _uiContext = uiContext;
        _openInBrowser = openInBrowser;
        _signIn = signIn;
        _openFolder = openFolder ?? OpenFolder;
        _driftStatePath = driftStatePath;
        if (LoadDriftState() is { } state) _drift.Restore(state);
        _breakTimer = new System.Threading.Timer(_ => Evaluate(DateTime.UtcNow));
        _notifier = createNotifier(OnActivated);
    }

    /// <summary>
    /// One poll, from the poll loop. <paramref name="isPresent"/> is the tracker's own verdict (input, or
    /// sound in the foreground); <paramref name="foreground"/> is null when away or on an ignored window.
    /// </summary>
    public void OnPoll(DateTime nowUtc, TimeSpan maxObservedGap, bool isLocked, bool isPresent, ActivitySample? foreground)
    {
        try
        {
            var settings = NudgeSettings.From(_config);
            lock (_lock)
            {
                var present = isPresent || (!isLocked && IsMicrophoneInUse(nowUtc));
                var app = present ? foreground : null;

                _policy.MaxObservedGap = maxObservedGap;
                _policy.Record(nowUtc, present,
                    app is null ? null : $"{app.ProcessName}|{app.ExecutablePath}",
                    app is null ? null : app.ProductName ?? app.ProcessName,
                    settings);
            }

            Evaluate(nowUtc);
        }
        catch (Exception ex)
        {
            // A failed nudge must never cost a tracked minute: the caller's catch would report a poll error.
            _log.Error(ex, "Nudge evaluation failed");
        }
    }

    /// <summary>
    /// One minute the server stored, with what it resolved to — from the send loop, oldest first. A backlog minute is
    /// ignored by the policy: it says nothing about now.
    /// </summary>
    public void OnMinuteResolved(DateTime nowUtc, MinuteVerdict minute)
    {
        try
        {
            lock (_lock)
            {
                _drift.Record(nowUtc, minute);
                _driftChanged = true;
            }
            Evaluate(nowUtc);
        }
        catch (Exception ex)
        {
            // A failed nudge must never cost a sent minute: the send loop would retry a window already stored.
            _log.Error(ex, "Drift evaluation failed");
        }
    }

    /// <summary>
    /// How the tracker is doing at getting minutes to the server, from the tray's timer (the UI thread). Not a nudge:
    /// it is not fed by the poll loop, so it keeps working through a pause — the policy itself holds the notice back
    /// while paused — and it ignores the quiet conditions (Windows holds toasts under full screen on its own).
    /// </summary>
    public void OnUploadStatus(DateTime nowUtc, UploadSnapshot snapshot)
    {
        try
        {
            UploadHealthChange change;
            lock (_lock) change = _upload.Evaluate(nowUtc, snapshot, _config.UploadNoticesEnabled);

            if (change.Withdraw) _notifier.Remove(NudgeKind.UploadHealth);
            if (change.Show is not { } notice) return;

            // Counts only: nothing the user wrote is ever in an upload notice, or in its log line.
            _log.Information("Upload notice {Problem}: {Pending} min waiting, {Dropped} dropped", notice.Problem, notice.PendingWindows, notice.Dropped);
            _notifier.Show(new Nudge(1, NudgeKind.UploadHealth, notice.StalledFor, null, Upload: notice));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Upload health evaluation failed");
        }
    }

    public NudgeStatus GetStatus()
    {
        lock (_lock) return _policy.GetStatus(DateTime.UtcNow);
    }

    /// <summary>Shows a nudge of <paramref name="kind"/> right away, with working buttons. For trying toasts by hand.</summary>
    public void ShowTest(NudgeKind kind)
    {
        Nudge nudge;
        lock (_lock)
            nudge = kind switch
            {
                NudgeKind.Drift => _drift.IssueForTest(DateTime.UtcNow),
                NudgeKind.UploadHealth => new Nudge(1, kind, TimeSpan.FromHours(26), null,
                    Upload: new UploadNotice(UploadProblem.Stalled, 90, TimeSpan.FromHours(26), 0)),
                _ => _policy.IssueForTest(kind, DateTime.UtcNow)
            };
        _notifier.Show(nudge);
    }

    /// <remarks>
    /// The two families keep <see cref="HyperfocusPolicy.MinGapBetweenKinds"/> apart, the way the hyperfocus kinds keep
    /// apart among themselves: a due one is held, like under a quiet condition, rather than two toasts landing a minute
    /// apart. The break-over nudge is exempt, as it is from quiet conditions — the user set it.
    /// </remarks>
    private void Evaluate(DateTime nowUtc)
    {
        Nudge? nudge;
        Nudge? drift;
        IReadOnlyList<NudgeKind> withdrawn;
        bool driftWithdrawn;
        DateTime? breakEndsUtc;
        DriftState? driftState = null;

        var settings = NudgeSettings.From(_config);
        var driftSettings = DriftSettings.From(_config);
        lock (_lock)
        {
            nudge = _policy.TakeDue(nowUtc, settings, () => IsQuiet(nowUtc) || IsWithinGap(_driftShownUtc, nowUtc));
            if (nudge is not null) _hyperfocusShownUtc = nowUtc;

            drift = _drift.TakeDue(nowUtc, driftSettings, () => IsQuiet(nowUtc) || IsWithinGap(_hyperfocusShownUtc, nowUtc));
            if (drift is not null)
            {
                _driftShownUtc = nowUtc;
                _driftChanged = true;
            }

            withdrawn = _policy.TakeWithdrawn();
            driftWithdrawn = _drift.TakeWithdrawn();
            breakEndsUtc = _policy.GetStatus(nowUtc).BreakEndsUtc;

            if (_driftChanged)
            {
                driftState = _drift.Snapshot();
                _driftChanged = false;
            }
        }

        if (driftState is not null) SaveDriftState(driftState);

        foreach (var kind in withdrawn) _notifier.Remove(kind);
        if (driftWithdrawn) _notifier.Remove(NudgeKind.Drift);

        if (nudge is not null)
        {
            _log.Information("Nudge {Kind} after {Minutes} min", nudge.Kind, (int)nudge.Length.TotalMinutes);
            _notifier.Show(nudge);
        }

        if (drift is not null)
        {
            // No names: the planned activity and what the time went to are both the user's own words.
            _log.Information("Nudge {Kind} after {Minutes} min off plan", drift.Kind, (int)drift.Length.TotalMinutes);
            _notifier.Show(drift);
        }

        ScheduleBreakTimer(breakEndsUtc, nowUtc);
    }

    private void ScheduleBreakTimer(DateTime? breakEndsUtc, DateTime nowUtc)
    {
        try
        {
            if (breakEndsUtc is { } ends)
            {
                var due = ends - nowUtc;
                _breakTimer.Change(due > TimeSpan.Zero ? due + TimeSpan.FromMilliseconds(100) : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _breakTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
        catch (ObjectDisposedException) { /* exiting */ }
    }

    /// <summary>Called under <see cref="_lock"/>, and only when a stretch nudge is due.</summary>
    private bool IsQuiet(DateTime nowUtc)
    {
        var reason = _config.QuietWhenFullscreen && Win32.IsShellHoldingNotifications() ? "full screen or presenting"
            : _config.QuietWhenMicrophoneInUse && IsMicrophoneInUse(nowUtc) ? "microphone in use"
            : null;

        if (reason is not null && reason != _quietReason)
            _log.Information("Holding a nudge back: {Reason}", reason);
        _quietReason = reason;

        return reason is not null;
    }

    /// <summary>A registry walk, so cached for a few seconds. Called under <see cref="_lock"/>.</summary>
    private bool IsMicrophoneInUse(DateTime nowUtc)
    {
        if (nowUtc - _microphoneCheckedUtc >= MicrophoneCheckEvery || nowUtc < _microphoneCheckedUtc)
        {
            _isMicrophoneInUse = MicrophoneUsage.IsInUse();
            _microphoneCheckedUtc = nowUtc;
        }
        return _isMicrophoneInUse;
    }

    /// <summary>A button press, from the notifier's thread-pool thread (or a test).</summary>
    internal void OnActivated(NudgeActivation activation)
    {
        if (_uiContext is null) Handle(activation);
        else _uiContext.Post(_ => Handle(activation), null);
    }

    private void Handle(NudgeActivation activation)
    {
        try
        {
            if (activation.Kind == NudgeKind.UploadHealth)
            {
                HandleUploadResponse(activation.Response);
                return;
            }

            var isDrift = activation.Kind == NudgeKind.Drift;
            if (activation.Response == NudgeResponse.TurnOff)
                TurnOff(isDrift);
            if (activation.Response == NudgeResponse.SwitchPlan)
                OpenPlanner();

            var nowUtc = DateTime.UtcNow;
            bool isCurrent;
            lock (_lock)
            {
                isCurrent = isDrift
                    ? _drift.Respond(activation.NudgeId, activation.Response, nowUtc)
                    : _policy.Respond(activation.NudgeId, activation.Response, nowUtc, NudgeSettings.From(_config));
                _driftChanged |= isDrift && isCurrent;
            }

            if (isCurrent) _log.Information("Nudge {Kind} answered: {Response}", activation.Kind, activation.Response);
            else _log.Debug("Ignored {Response} on {Kind} nudge {NudgeId}: no longer current", activation.Response, activation.Kind, activation.NudgeId);

            Evaluate(nowUtc); // withdraws toasts and (re)arms the break timer
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Handling a nudge response failed");
        }
    }

    /// <summary>
    /// An upload notice has no policy state to answer to, so a press is acted on whatever notice it came from: a stale
    /// "Sign in" finds the tray already signed in and does nothing, and "Turn off" is the switch it says it is.
    /// </summary>
    private void HandleUploadResponse(NudgeResponse response)
    {
        switch (response)
        {
            case NudgeResponse.SignIn:
                _signIn?.Invoke();
                break;
            case NudgeResponse.OpenLogs:
                _openFolder(Program.LogDirectory);
                break;
            case NudgeResponse.TurnOff when _config.UploadNoticesEnabled:
                _config.UploadNoticesEnabled = false;
                _config.Save();
                _notifier.Remove(NudgeKind.UploadHealth);
                _log.Information("Upload notices turned off from a notice");
                break;
        }
    }

    /// <summary>"Turn off" switches off the kind it was pressed on: a drift toast doesn't silence hyperfocus nudges.</summary>
    private void TurnOff(bool isDrift)
    {
        if (isDrift ? !_config.DriftNudgesEnabled : !_config.HyperfocusNudgesEnabled) return;

        if (isDrift) _config.DriftNudgesEnabled = false;
        else _config.HyperfocusNudgesEnabled = false;
        _config.Save();
        _log.Information("{Which} nudges turned off from a nudge", isDrift ? "Drift" : "Hyperfocus");
    }

    private void OpenPlanner()
    {
        try
        {
            _openInBrowser(_config.WebAppUri(PlannerPath));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not open the planner");
        }
    }

    private static void OpenInBrowser(Uri uri) =>
        Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static bool IsWithinGap(DateTime? shownUtc, DateTime nowUtc) =>
        shownUtc is { } shown && nowUtc - shown < HyperfocusPolicy.MinGapBetweenKinds;

    private DriftState? LoadDriftState()
    {
        try
        {
            return File.Exists(_driftStatePath) ? JsonSerializer.Deserialize<DriftState>(File.ReadAllText(_driftStatePath)) : null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not read the drift nudge's saved state — starting afresh");
            return null;
        }
    }

    /// <summary>Never throws: a failed save costs at most a repeated nudge after a restart.</summary>
    private void SaveDriftState(DriftState state)
    {
        try
        {
            lock (_driftSaveGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_driftStatePath)!);
                var tempPath = _driftStatePath + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(state));
                File.Move(tempPath, _driftStatePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not save the drift nudge's state");
        }
    }

    public void Dispose()
    {
        _breakTimer.Dispose();
        _notifier.Dispose();
    }
}
