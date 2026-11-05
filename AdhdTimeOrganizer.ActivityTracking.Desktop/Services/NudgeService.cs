using System.Diagnostics;
using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Native;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// Hyperfocus, break, drift and start-ramp nudges: feeds each poll to <see cref="HyperfocusPolicy"/>, each sent minute's
/// server verdict to <see cref="DriftPolicy"/> and its plan to <see cref="StartRampPolicy"/>, and shows what they ask for.
/// The drift nudge and the ramp only read what the heartbeat answered; the one thing that reaches the server is the
/// ramp's Start now, which starts the block in the planner.
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

    /// <summary>The start ramp speaks only to someone at the computer: a poll found them present this recently.</summary>
    private static readonly TimeSpan RampPresentFor = TimeSpan.FromMinutes(1);

    /// <summary>"Switch plan" opens today's planner.</summary>
    internal const string PlannerPath = "/day-planner";

    /// <summary>
    /// "Start now" opens the web app's home with the block's timer already running (its <c>useTrackLink</c>). A web app
    /// from before the link just opens home.
    /// </summary>
    internal static string TrackPath(long plannerTaskId) => $"/?track={plannerTaskId}";

    /// <summary>
    /// "Review" opens the distinct-entries grid on what the evening's day left unmapped, busiest first. <c>date</c> names
    /// the day — yesterday's date for an evening past midnight — and wins over <c>today</c>, which stays for a web app
    /// from before <c>date</c>.
    /// </summary>
    internal static string UnmappedReviewPath(DateOnly? date) =>
        "/activity-tracking/desktop/settings/distinctEntries?coverage=Uncovered&today=true"
        + (date is { } d ? $"&date={d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}" : "");

    /// <summary>The invite on screen, for the day its Review opens; set on the send loop, read on the toast's thread.</summary>
    private volatile UnmappedInvite? _review;

    public static string DefaultDriftStatePath { get; } = Path.Combine(AppConfig.ConfigDirectory, "drift.json");

    private const string StartRampStateFileName = "start-ramp.json";

    private readonly AppConfig _config;
    private readonly SynchronizationContext? _uiContext;
    private readonly INudgeNotifier _notifier;
    private readonly Action<Uri> _openInBrowser;
    private readonly Action? _signIn;
    private readonly Action<string> _openFolder;
    private readonly Func<long, Task<bool>> _startBlock;
    private readonly string _driftStatePath;
    private readonly string _rampStatePath;
    private readonly ILogger _log = Log.ForContext<NudgeService>();

    private readonly Lock _lock = new();
    private readonly Lock _stateSaveGate = new();
    private readonly HyperfocusPolicy _policy = new(TimeSpan.FromSeconds(10));
    private readonly DriftPolicy _drift = new();
    private readonly StartRampPolicy _ramp = new();
    private readonly UploadHealthPolicy _upload = new();
    private readonly System.Threading.Timer _breakTimer;

    private DateTime _microphoneCheckedUtc = DateTime.MinValue;
    private bool _isMicrophoneInUse;
    private string? _quietReason;
    private DateTime _presentUtc = DateTime.MinValue; // the last poll that found someone at the computer (under _lock)

    // When each family last showed a toast, for the gap between them (under _lock).
    private DateTime? _hyperfocusShownUtc;
    private DateTime? _driftShownUtc;
    private DateTime? _rampShownUtc;
    private bool _driftChanged;
    private bool _rampChanged;

    /// <param name="uiContext">Where button presses are handled; the tray's.</param>
    /// <param name="signIn">"Sign in" on an upload notice; opens the sign-in form on the UI thread. Null does nothing.</param>
    /// <param name="startBlock">The start ramp's "Start now" on the server; null starts nothing there.</param>
    public NudgeService(AppConfig config, SynchronizationContext? uiContext, Action? signIn = null,
        Func<long, Task<bool>>? startBlock = null)
        : this(config, uiContext, onActivated => new NudgeNotifier(onActivated), OpenInBrowser, DefaultDriftStatePath, signIn,
            startBlock: startBlock)
    {
    }

    /// <param name="createNotifier">Given the button-press handler; tests pass a fake, so nothing registers with Windows.</param>
    /// <param name="openInBrowser">"Switch plan", "Start now"; tests record it instead.</param>
    /// <param name="driftStatePath">
    /// Where <see cref="DriftState"/> is kept across restarts; <see cref="StartRampState"/> is kept beside it.
    /// </param>
    /// <param name="signIn">"Sign in"; tests count it.</param>
    /// <param name="openFolder">"Open log folder"; tests record the path instead.</param>
    /// <param name="startBlock">"Start now" on the server; tests record the id instead.</param>
    internal NudgeService(AppConfig config, SynchronizationContext? uiContext,
        Func<Action<NudgeActivation>, INudgeNotifier> createNotifier, Action<Uri> openInBrowser, string driftStatePath,
        Action? signIn = null, Action<string>? openFolder = null, Func<long, Task<bool>>? startBlock = null)
    {
        _config = config;
        _uiContext = uiContext;
        _openInBrowser = openInBrowser;
        _signIn = signIn;
        _openFolder = openFolder ?? OpenFolder;
        _startBlock = startBlock ?? (_ => Task.FromResult(false));
        _driftStatePath = driftStatePath;
        _rampStatePath = Path.Combine(Path.GetDirectoryName(driftStatePath)!, StartRampStateFileName);
        if (LoadState<DriftState>(_driftStatePath, "drift nudge") is { } drift) _drift.Restore(drift);
        if (LoadState<StartRampState>(_rampStatePath, "start ramp") is { } ramp) _ramp.Restore(ramp);
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
                if (present && !isLocked) _presentUtc = nowUtc;

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
    /// One minute the server stored, with what it resolved to and, from a server that says, the plan around it — from the
    /// send loop, oldest first. A backlog minute is ignored by the policies: it says nothing about now.
    /// </summary>
    public void OnMinuteResolved(DateTime nowUtc, MinuteVerdict minute, PlanView? plan = null)
    {
        try
        {
            lock (_lock)
            {
                _drift.Record(nowUtc, minute);
                _driftChanged = true;
                if (plan is not null) _ramp.Record(nowUtc, plan);
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

    /// <summary>
    /// The evening invite to map today's unmapped time, from <see cref="UnmappedReviewService"/> on the send loop's thread.
    /// Not a nudge: no quiet conditions (Windows holds toasts itself) and no pause of its own to honour — the service
    /// already stayed quiet while tracking was paused.
    /// </summary>
    public void ShowUnmappedReview(UnmappedInvite invite)
    {
        try
        {
            _review = invite;
            _notifier.Show(new Nudge(1, NudgeKind.UnmappedReview, invite.Unmapped, null, Review: invite));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Evening review invite failed");
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
                NudgeKind.StartRamp => _ramp.IssueForTest(DateTime.UtcNow),
                NudgeKind.UploadHealth => new Nudge(1, kind, TimeSpan.FromHours(26), null,
                    Upload: new UploadNotice(UploadProblem.Stalled, 90, TimeSpan.FromHours(26), 0)),
                NudgeKind.UnmappedReview => new Nudge(1, kind, TimeSpan.FromMinutes(130), null, Review: new UnmappedInvite(
                    TimeSpan.FromMinutes(130), [new InviteApp("Discord", TimeSpan.FromMinutes(50), false), new InviteApp("Chrome", TimeSpan.FromMinutes(30), true)], 1)),
                _ => _policy.IssueForTest(kind, DateTime.UtcNow)
            };
        _notifier.Show(nudge);
    }

    /// <remarks>
    /// The three families keep <see cref="HyperfocusPolicy.MinGapBetweenKinds"/> apart, the way the hyperfocus kinds keep
    /// apart among themselves: a due one is held, like under a quiet condition, rather than two toasts landing a minute
    /// apart. The start ramp goes first — a block's start is the one moment that won't come again. The break-over nudge is
    /// exempt, as it is from quiet conditions — the user set it.
    /// </remarks>
    private void Evaluate(DateTime nowUtc)
    {
        Nudge? ramp;
        Nudge? nudge;
        Nudge? drift;
        IReadOnlyList<NudgeKind> withdrawn;
        bool driftWithdrawn;
        bool rampWithdrawn;
        DateTime? breakEndsUtc;
        DriftState? driftState = null;
        StartRampState? rampState = null;

        var settings = NudgeSettings.From(_config);
        var driftSettings = DriftSettings.From(_config);
        var rampSettings = StartRampSettings.From(_config);
        lock (_lock)
        {
            ramp = _ramp.TakeDue(nowUtc, rampSettings, nowUtc - _presentUtc <= RampPresentFor,
                () => IsQuiet(nowUtc) || IsWithinGap(_hyperfocusShownUtc, nowUtc) || IsWithinGap(_driftShownUtc, nowUtc),
                out var rampChanged);
            _rampChanged |= rampChanged;
            if (ramp is not null) _rampShownUtc = nowUtc;

            nudge = _policy.TakeDue(nowUtc, settings,
                () => IsQuiet(nowUtc) || IsWithinGap(_driftShownUtc, nowUtc) || IsWithinGap(_rampShownUtc, nowUtc));
            if (nudge is not null) _hyperfocusShownUtc = nowUtc;

            drift = _drift.TakeDue(nowUtc, driftSettings,
                () => IsQuiet(nowUtc) || IsWithinGap(_hyperfocusShownUtc, nowUtc) || IsWithinGap(_rampShownUtc, nowUtc));
            if (drift is not null)
            {
                _driftShownUtc = nowUtc;
                _driftChanged = true;
            }

            withdrawn = _policy.TakeWithdrawn();
            driftWithdrawn = _drift.TakeWithdrawn();
            rampWithdrawn = _ramp.TakeWithdrawn();
            breakEndsUtc = _policy.GetStatus(nowUtc).BreakEndsUtc;

            if (_driftChanged)
            {
                driftState = _drift.Snapshot();
                _driftChanged = false;
            }
            if (_rampChanged)
            {
                rampState = _ramp.Snapshot();
                _rampChanged = false;
            }
        }

        if (driftState is not null) SaveState(_driftStatePath, driftState, "drift nudge");
        if (rampState is not null) SaveState(_rampStatePath, rampState, "start ramp");

        foreach (var kind in withdrawn) _notifier.Remove(kind);
        if (driftWithdrawn) _notifier.Remove(NudgeKind.Drift);
        if (rampWithdrawn) _notifier.Remove(NudgeKind.StartRamp);

        if (ramp is not null)
        {
            // No name: the planned activity is the user's own words.
            _log.Information("Start ramp for block {PlannerTaskId}, {LateMinutes} min after its start",
                ramp.StartRamp!.PlannerTaskId, (int)ramp.StartRamp.LateBy.TotalMinutes);
            _notifier.Show(ramp);
        }

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

            if (activation.Kind == NudgeKind.UnmappedReview)
            {
                HandleReviewResponse(activation.Response);
                return;
            }

            if (activation.Kind == NudgeKind.StartRamp)
            {
                HandleRampResponse(activation);
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

    /// <summary>
    /// The evening invite has no state to answer to — the evening was marked handled when it was shown — so either button
    /// just closes the toast, and "Review" first opens the page. Turning the invite off is a Settings switch.
    /// </summary>
    private void HandleReviewResponse(NudgeResponse response)
    {
        if (response == NudgeResponse.Review)
        {
            try
            {
                // A toast answered after a restart has no invite held: today's grid is the honest fallback.
                _openInBrowser(_config.WebAppUri(UnmappedReviewPath(_review?.Date)));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not open the evening review");
            }
        }

        _notifier.Remove(NudgeKind.UnmappedReview);
        _log.Information("Evening review answered: {Response}", response);
    }

    /// <summary>
    /// A press on a start-ramp toast. <b>Start now</b> starts the block on the server and opens its timer in the web app,
    /// side by side: the browser does not wait for the server, and the server's start counts even if the browser never
    /// gets there. <b>Not this one</b> also ends drift nudges in the block. <b>In 2 minutes</b> is the policy's alone.
    /// </summary>
    private void HandleRampResponse(NudgeActivation activation)
    {
        var nowUtc = DateTime.UtcNow;
        PlannedBlock? block;
        lock (_lock)
        {
            block = _ramp.Respond(activation.NudgeId, activation.Response, nowUtc, out var changed);
            _rampChanged |= changed;
            if (block is not null && activation.Response == NudgeResponse.NotThisOne)
            {
                _drift.QuietBlock(block.PlannerTaskId);
                _driftChanged = true;
            }
        }

        if (block is null)
        {
            _log.Debug("Ignored {Response} on start ramp {NudgeId}: no longer current", activation.Response, activation.NudgeId);
            return;
        }

        _log.Information("Start ramp for block {PlannerTaskId} answered: {Response}", block.PlannerTaskId, activation.Response);
        if (activation.Response == NudgeResponse.StartNow) StartNow(block.PlannerTaskId);

        Evaluate(nowUtc); // saves the state, withdraws a drift toast for a block now quiet
    }

    private void StartNow(long plannerTaskId)
    {
        // The test toast's block is not a planner task: open the page, start nothing.
        if (plannerTaskId > 0)
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!await _startBlock(plannerTaskId))
                        _log.Information("Block {PlannerTaskId} was not started on the server", plannerTaskId);
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Starting block {PlannerTaskId} failed", plannerTaskId);
                }
            });

        try
        {
            _openInBrowser(_config.WebAppUri(TrackPath(plannerTaskId)));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not open the block's timer");
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

    /// <param name="what">The policy, for the log.</param>
    private T? LoadState<T>(string path, string what) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not read the {What}'s saved state — starting afresh", what);
            return null;
        }
    }

    /// <summary>Never throws: a failed save costs at most a repeated nudge after a restart.</summary>
    private void SaveState<T>(string path, T state, string what)
    {
        try
        {
            lock (_stateSaveGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, JsonSerializer.Serialize(state));
                File.Move(tempPath, path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not save the {What}'s state", what);
        }
    }

    public void Dispose()
    {
        _breakTimer.Dispose();
        _notifier.Dispose();
    }
}
