using System.Diagnostics;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Native;
using Microsoft.Win32;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <param name="TodayOnThisPc">Today's recorded foreground time on this machine (<see cref="TodayTotal"/>).</param>
/// <param name="LastResolved">The newest minute the server stored, with what it resolved to; null before the first, or from an old server.</param>
/// <param name="Coverage">The mapping coverage behind the "map these apps" prompt; null unless drift nudges are on and it has been fetched.</param>
public sealed record TrackerStatus(
    bool IsRunning,
    bool IsLocked,
    int PendingWindows,
    DateTime? LastSentUtc,
    SendResult? LastSendResult,
    string? LastPollError,
    DateTime? PausedUntilUtc = null,
    TimeSpan TodayOnThisPc = default,
    ResolvedMinute? LastResolved = null,
    AgentCoverage? Coverage = null);

/// <summary>A minute the server stored and what it said about it.</summary>
public sealed record ResolvedMinute(DateTime WindowStartUtc, HeartbeatResponse Response);

/// <summary>
/// Polls the desktop, folds polls into minute windows, persists each finished window to the outbox and
/// sends the outbox.
/// </summary>
/// <remarks>
/// Recording and sending are independent: pausing tracking stops the poll loop only, and the send loop
/// runs for the app's lifetime, so windows recorded before a pause — or while offline or signed out —
/// still go out. Nothing here touches UI objects; the tray reads <see cref="GetStatus"/> on its own
/// thread. (A status event raised from the poll thread used to set <c>NotifyIcon.Text</c> directly,
/// and an over-long error message there threw out of the poll loop's own catch and ended tracking
/// silently.)
/// </remarks>
public sealed class ActivityTrackerService : IDisposable
{
    private static readonly TimeSpan SendInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OutboxRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan AgentRulesRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CoverageRefreshInterval = TimeSpan.FromHours(1);
    private const int WindowsPerSendPass = 50;

    private readonly AppConfig _config;
    private readonly ApiClient _apiClient;
    private readonly OutboxStore _outbox;
    private readonly TodayTotal _today;
    private readonly NudgeService? _nudges;
    private readonly ILogger _log = Log.ForContext<ActivityTrackerService>();

    private readonly Lock _lock = new();
    private readonly ActivityRecorder _recorder;
    private readonly PrivacyFilter _privacy = new();      // poll loop only
    private readonly AgentRules _agentRules = new();      // filtered on the send loop only

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _sendSignal = new(0, 1);
    private readonly Task _sendTask;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    private volatile bool _isLocked;
    private volatile string? _lastPollError;
    private DateTime? _lastSentUtc;
    private SendResult? _lastSendResult;
    private ResolvedMinute? _lastResolved;

    // Send loop only, except the reset on sign-out / a settings change (both just make the next pass fetch).
    private AgentCoverage? _coverage;
    private DateTime _coverageCheckedUtc = DateTime.MinValue;
    private bool _coverageNotSupported;

    public bool IsRunning => _pollCts is { IsCancellationRequested: false };

    public ActivityTrackerService(AppConfig config, ApiClient apiClient, NudgeService? nudges)
        : this(config, apiClient, new OutboxStore(OutboxStore.DefaultDirectory, OutboxRetention),
            new TodayTotal(TodayTotal.DefaultPath, TimeZoneInfo.Local), nudges)
    {
    }

    public ActivityTrackerService(AppConfig config, ApiClient apiClient, OutboxStore outbox, TodayTotal today, NudgeService? nudges = null)
    {
        _config = config;
        _apiClient = apiClient;
        _outbox = outbox;
        _today = today;
        _nudges = nudges;
        _recorder = new ActivityRecorder(MaxCreditedGapFor(PollInterval));

        // The server rules are one user's on one server; a sign-out (or a new server URL, which signs out) forgets them.
        // So is what the server last said about the user's minutes and their mapping coverage.
        _apiClient.SignedOut += _agentRules.Clear;
        _apiClient.SignedOut += ForgetServerAnswers;

        // Subscribed from the UI thread, whose message loop delivers them.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        _sendTask = Task.Run(() => SendLoopAsync(_lifetimeCts.Token));
    }

    private TimeSpan PollInterval => TimeSpan.FromMilliseconds(Math.Clamp(_config.PollIntervalMs, 250, 60_000));

    public void Start()
    {
        if (IsRunning) return;

        _log.Information(
            "Starting tracker (poll: {PollMs}ms, idle threshold: {IdleS}s, " +
            "min bg window: {MinBg}%, max bg per monitor: {MaxBg}, max passive idle: {PassiveMin}min)",
            _config.PollIntervalMs, _config.IdleThresholdSeconds,
            _config.MinBackgroundWindowPercent, _config.MaxBackgroundWindowsPerMonitor, _config.MaxPassiveIdleMinutes);

        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;
        _pollTask = Task.Run(() => PollLoopAsync(token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _log.Information("Stopping tracker");
        _pollCts?.Cancel();
    }

    /// <summary>Whether a pause is in force at <paramref name="utcNow"/> (one past its end has not been resumed yet).</summary>
    public bool IsPaused(DateTime utcNow) => _config.PausedUntilUtc is { } until && until > utcNow;

    /// <summary>
    /// Stops recording until <paramref name="untilUtc"/> (<see cref="DateTime.MaxValue"/>: until <see cref="Resume"/>).
    /// Saved, so a restart in the middle does not start recording early. Sending carries on, as with any pause.
    /// </summary>
    public void Pause(DateTime untilUtc)
    {
        _log.Information("Pausing until {Until}", untilUtc == DateTime.MaxValue ? "resumed" : untilUtc.ToString("O"));
        _config.PausedUntilUtc = untilUtc;
        _config.Save();
        Stop();
    }

    public void Resume()
    {
        _config.PausedUntilUtc = null;
        _config.Save();
        Start();
    }

    /// <summary>Resumes when a timed pause has run out. Called from the tray's timer; true when it resumed.</summary>
    public bool ResumeIfPauseOver(DateTime utcNow)
    {
        if (_config.PausedUntilUtc is not { } until || until > utcNow) return false;

        _log.Information("Pause over — resuming");
        Resume();
        return true;
    }

    /// <summary>
    /// Persists the minute in progress. Called on exit, suspend and session end — the moments after
    /// which the in-memory part of a minute would otherwise be gone.
    /// </summary>
    public void Flush()
    {
        ActivityWindow? window;
        lock (_lock) window = _recorder.Flush();
        if (window is null) return;

        Queue(window);
        _log.Information("Flushed partial window {WindowStart} ({Count} entries)", window.WindowStart, window.Entries.Count);
    }

    /// <summary>To the outbox, and into today's total: queued time counts, whether or not it has been sent yet.</summary>
    private void Queue(ActivityWindow window)
    {
        _outbox.Save(window);
        _today.Add(window.WindowStart, window.Entries.Sum(e => e.ActiveSeconds));
    }

    /// <summary>
    /// Makes the next send pass fetch the mapping coverage again — after drift nudges are turned on, say. A server
    /// found to have no coverage route isn't asked again until the next sign-in.
    /// </summary>
    public void RefreshCoverage()
    {
        _coverageCheckedUtc = DateTime.MinValue;
        RequestSend();
    }

    private void ForgetServerAnswers()
    {
        lock (_lock) _lastResolved = null;
        _coverage = null;
        _coverageCheckedUtc = DateTime.MinValue;
        _coverageNotSupported = false;
    }

    /// <summary>Wakes the send loop now instead of at its next interval — after signing in, for example.</summary>
    public void RequestSend()
    {
        try
        {
            if (_sendSignal.CurrentCount == 0) _sendSignal.Release();
        }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    public TrackerStatus GetStatus()
    {
        var pending = _outbox.Count; // disk read — kept outside the lock the poll loop takes
        var today = _today.On(DateTime.UtcNow);
        var coverage = _config.DriftNudgesEnabled ? _coverage : null;
        lock (_lock)
            return new TrackerStatus(IsRunning, _isLocked, pending, _lastSentUtc, _lastSendResult, _lastPollError, _config.PausedUntilUtc,
                today, _lastResolved, coverage);
    }

    // ── Polling ──────────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = PollInterval;
        var maxCreditedGap = MaxCreditedGapFor(interval);
        lock (_lock) _recorder.MaxCreditedGap = maxCreditedGap;

        // A PeriodicTimer ticks on a fixed cadence however long a poll takes, and the time credited is
        // measured, not assumed — see ActivityRecorder.
        using var timer = new PeriodicTimer(interval);
        var clock = Stopwatch.StartNew();
        var lastTick = TimeSpan.Zero;

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = clock.Elapsed;
                var elapsed = now - lastTick;
                lastTick = now;

                try
                {
                    var isLocked = _isLocked;
                    var poll = isLocked ? PollResult.Locked : Observe();
                    var nowUtc = DateTime.UtcNow;

                    List<ActivityWindow> closed;
                    lock (_lock) closed = _recorder.Record(nowUtc, elapsed, poll.Observations);

                    // Catches its own failures: a nudge going wrong must not show up as a poll error.
                    _nudges?.OnPoll(nowUtc, maxCreditedGap, isLocked, poll.IsPresent, poll.Foreground);

                    foreach (var window in closed)
                    {
                        Queue(window);
                        _log.Information(
                            "Finalized window {WindowStart}: {Count} entries ({Active}s active)",
                            window.WindowStart, window.Entries.Count, window.Entries.Sum(e => e.ActiveSeconds));
                    }

                    if (closed.Count > 0) RequestSend();
                    _lastPollError = null;
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Poll failed");
                    _lastPollError = ex.Message;
                }
            }
        }
        catch (OperationCanceledException) { }

        _log.Debug("Poll loop exited");
    }

    /// <summary>
    /// What one poll saw. <see cref="IsPresent"/> is the user being there at all — input, or sound in the
    /// foreground — which an ignored foreground window doesn't change; <see cref="Foreground"/> is null then.
    /// </summary>
    private sealed record PollResult(List<Observation> Observations, bool IsPresent, ActivitySample? Foreground)
    {
        public static PollResult Locked { get; } = new([], false, null);
    }

    private PollResult Observe()
    {
        var idleSeconds = Win32.GetIdleSeconds();
        var monitors = MonitorHelper.GetMonitors();
        var audio = AudioSessionHelper.GetActiveAudioInfo();
        var foregroundHwnd = Win32.GetForegroundWindow();

        // ── Active (foreground) window ────────────────────────────────────────
        var active = CaptureWindow(foregroundHwnd, out var activePid);
        var isActivePlayingSound = active is not null && IsPlayingAudio(audio, activePid, active.ExecutablePath);

        if (idleSeconds >= _config.IdleThresholdSeconds)
        {
            // No input — but a foreground window playing sound is a film, a talk or a call, and the user
            // is still there. That time used to be dropped with every other idle minute.
            return active is not null && IsPassivelyPresent(idleSeconds, isActivePlayingSound, _config.MaxPassiveIdleMinutes)
                ? new PollResult(
                [
                    new Observation(active,
                        MonitorHelper.GetWindowMonitorIndex(foregroundHwnd, monitors),
                        IsBackground: false,
                        IsPlayingSound: true,
                        IsFullscreen: MonitorHelper.IsWindowFullscreen(foregroundHwnd, monitors))
                ], IsPresent: true, active)
                : new PollResult([], IsPresent: false, null);
        }

        var observations = new List<Observation>();
        var trackedKeys = new HashSet<string>();

        if (active is not null)
        {
            observations.Add(new Observation(active,
                MonitorHelper.GetWindowMonitorIndex(foregroundHwnd, monitors),
                IsBackground: false,
                IsPlayingSound: isActivePlayingSound,
                IsFullscreen: MonitorHelper.IsWindowFullscreen(foregroundHwnd, monitors)));
            trackedKeys.Add(ProcessKey(active));
        }

        // ── Background windows (other monitors, by visible area) ──────────────
        var backgroundWindows = MonitorHelper.GetBackgroundWindows(
            foregroundHwnd, monitors, _config.MinBackgroundWindowPercent, _config.MaxBackgroundWindowsPerMonitor);

        foreach (var bg in backgroundWindows)
        {
            var sample = ToSample(bg.Process, bg.WindowTitle);
            if (sample is null || !trackedKeys.Add(ProcessKey(sample))) continue;

            observations.Add(new Observation(sample, bg.MonitorIndex,
                IsBackground: true,
                IsPlayingSound: IsPlayingAudio(audio, bg.Pid, sample.ExecutablePath),
                IsFullscreen: false));
        }

        // ── Audio-playing processes not yet tracked ───────────────────────────
        foreach (var pid in audio.Pids)
        {
            var process = ProcessInfoCache.Get(pid);
            if (process is null) continue;

            var mainWindow = GetMainWindow(pid);
            var sample = ToSample(process, mainWindow != nint.Zero ? Win32.GetWindowTitle(mainWindow) : null);
            if (sample is null || !trackedKeys.Add(ProcessKey(sample))) continue;

            observations.Add(new Observation(sample,
                MonitorHelper.GetWindowMonitorIndex(mainWindow, monitors),
                IsBackground: true,
                IsPlayingSound: true,
                IsFullscreen: false));
        }

        return new PollResult(observations, IsPresent: true, active);
    }

    /// <summary>
    /// Whether an idle user still counts as present: only while the foreground window is audibly playing,
    /// and only up to <paramref name="maxPassiveIdleMinutes"/> without input, so autoplay left running is
    /// not a night of "watching". Fullscreen alone is deliberately not enough — a paused fullscreen video or
    /// game with the user gone looks exactly like one being watched.
    /// </summary>
    internal static bool IsPassivelyPresent(int idleSeconds, bool isForegroundPlayingSound, int maxPassiveIdleMinutes) =>
        isForegroundPlayingSound && idleSeconds < maxPassiveIdleMinutes * 60;

    private ActivitySample? CaptureWindow(nint hWnd, out int pid)
    {
        pid = 0;
        if (hWnd == nint.Zero) return null;

        var process = ProcessInfoCache.GetForWindow(hWnd, out pid);
        if (process is null) return null;

        return ToSample(process, Win32.GetWindowTitle(hWnd));
    }

    /// <summary>
    /// The window as it may be recorded, or null when it may not be — an ignore rule, or a private / hide-title
    /// window under <see cref="PrivateWindowHandling.Drop"/>. The raw title goes no further than this.
    /// </summary>
    private ActivitySample? ToSample(ProcessInfo process, string? rawTitle)
    {
        var normalized = WindowTitleNormalizer.Normalize(process.ProcessName, process.ProductName, rawTitle);
        var title = _privacy.Apply(_config, process.ProcessName, process.ProductName, rawTitle, normalized);
        if (title is null) return null;

        return new ActivitySample
        {
            ProcessName = process.ProcessName,
            WindowTitle = title,
            ExecutablePath = process.ExecutablePath,
            ProductName = process.ProductName
        };
    }

    private static nint GetMainWindow(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainWindowHandle;
        }
        catch { return nint.Zero; }
    }

    private static string ProcessKey(ActivitySample sample) => $"{sample.ProcessName}|{sample.ExecutablePath}";

    private static bool IsPlayingAudio(ActiveAudioInfo audio, int pid, string? exePath) =>
        audio.Pids.Contains(pid) || (exePath is not null && audio.ExePaths.Contains(exePath));

    private static TimeSpan MaxCreditedGapFor(TimeSpan pollInterval) =>
        TimeSpan.FromTicks(Math.Max(pollInterval.Ticks * 3, TimeSpan.FromSeconds(10).Ticks));

    // ── Sending ──────────────────────────────────────────────────────────────

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendPendingAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.Error(ex, "Send loop error");
            }

            try
            {
                await RefreshCoverageAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.Error(ex, "Mapping coverage refresh failed");
            }

            try { await _sendSignal.WaitAsync(SendInterval, ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.Debug("Send loop exited");
    }

    /// <summary>
    /// Sends pending windows oldest first and stops at the first one that has to wait: if the server is
    /// unreachable or the session is gone, the rest would fail the same way, each after its own timeout.
    /// </summary>
    /// <remarks>
    /// Each window first loses the entries the user's server rules ignore (<see cref="AgentRules"/>), with rules
    /// fetched at most <see cref="AgentRulesRefreshInterval"/> ago — a backlog sent after a day offline is
    /// filtered with today's rules, not with the ones it was recorded under. A window nothing is left of is
    /// removed here: the heartbeat refuses an empty one.
    /// </remarks>
    private async Task SendPendingAsync(CancellationToken ct)
    {
        var pending = _outbox.Pending(DateTime.UtcNow, WindowsPerSendPass);
        if (pending.Count == 0) return;

        if (!await RefreshAgentRulesAsync(ct)) return;

        foreach (var item in pending)
        {
            var window = _agentRules.Filter(item.Window);
            if (window is null)
            {
                if (_outbox.Remove(item)) TakeBackUnstored(item.Window, null);
                _log.Debug("Window {WindowStart}: every entry is ignored by a server rule — not sent", item.Window.WindowStart);
                continue;
            }

            var outcome = await _apiClient.SendActivityWindowAsync(window, ct);
            lock (_lock) _lastSendResult = outcome.Result;

            switch (outcome.Result)
            {
                case SendResult.Sent:
                    // Today's figures count a window once, when it is settled: one rewritten in flight goes again as a
                    // whole, and its first answer covers only part of it.
                    if (_outbox.Remove(item))
                    {
                        TakeBackUnstored(item.Window, window);
                        if (outcome.Response is { } settled) _today.AddResolved(window.WindowStart, settled);
                    }
                    lock (_lock) _lastSentUtc = DateTime.UtcNow;
                    _log.Debug("Sent window {WindowStart} ({Count} entries, {Dropped} ignored by a server rule)",
                        window.WindowStart, window.Entries.Count, item.Window.Entries.Count - window.Entries.Count);
                    if (outcome.Response is { } response)
                        OnResolved(window.WindowStart, response);
                    break;

                case SendResult.Rejected:
                    // Logged with the server's reason by ApiClient. Retrying an identical batch cannot help.
                    if (_outbox.Remove(item)) TakeBackUnstored(item.Window, null);
                    break;

                default:
                    return;
            }
        }
    }

    /// <summary>
    /// Takes what never got stored out of today's total: the entries a server rule dropped (<paramref name="sent"/> is
    /// what was left), or all of a window nothing was sent of or the server refused (<paramref name="sent"/> null).
    /// Only once the window is settled — one rewritten in flight goes again as a whole and is settled then.
    /// </summary>
    private void TakeBackUnstored(ActivityWindow queued, ActivityWindow? sent) =>
        _today.Add(queued.WindowStart, (sent?.Entries.Sum(e => e.ActiveSeconds) ?? 0) - queued.Entries.Sum(e => e.ActiveSeconds));

    /// <summary>Today's busiest activities on this PC, for the tray's breakdown.</summary>
    public IReadOnlyList<ActivityTotal> GetTodayTop(int count) => _today.Top(DateTime.UtcNow, count);

    /// <summary>
    /// What the server said about a stored minute: the newest one is the tray's "now", and every one goes to the drift
    /// nudge, which skips a backlog minute itself.
    /// </summary>
    private void OnResolved(DateTime windowStartUtc, HeartbeatResponse response)
    {
        var minute = new ResolvedMinute(MinuteVerdict.AsUtc(windowStartUtc), response);
        lock (_lock)
            if (_lastResolved is null || minute.WindowStartUtc >= _lastResolved.WindowStartUtc)
                _lastResolved = minute;

        _nudges?.OnMinuteResolved(DateTime.UtcNow, MinuteVerdict.From(windowStartUtc, response));
    }

    /// <summary>
    /// Fetches the mapping coverage behind the "map these apps to enable drift nudges" prompt, hourly and only while
    /// drift nudges are on: it is a week of the user's ledger resolved against their rules, not something to ask for
    /// every pass.
    /// </summary>
    private async Task RefreshCoverageAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (!_config.DriftNudgesEnabled || _coverageNotSupported || !_apiClient.HasSession) return;
        if (now - _coverageCheckedUtc < CoverageRefreshInterval) return;

        var fetch = await _apiClient.FetchCoverageAsync(ct);
        switch (fetch.Status)
        {
            case CoverageFetch.Fetched:
                _coverage = fetch.Coverage;
                _coverageCheckedUtc = now;
                _log.Information("Mapping coverage: {Uncovered} busy unmapped apps", fetch.Coverage?.Uncovered.Count ?? 0);
                break;
            case CoverageFetch.NotSupported:
                _coverageNotSupported = true;
                _log.Information("The server has no mapping-coverage route — no mapping prompt");
                break;
            default:
                // Try again in a few minutes rather than the full hour.
                _coverageCheckedUtc = now - CoverageRefreshInterval + TimeSpan.FromMinutes(5);
                break;
        }
    }

    /// <summary>
    /// Brings the server rules up to date when they are due. False when nothing is known yet and the download
    /// failed: the pass then waits rather than upload what a rule might cover. A server without the route counts
    /// as known-empty — it still filters on the heartbeat, and waiting for it would hold the outbox for good.
    /// Once a set is held, a failed download keeps using it and tries again on the next pass.
    /// </summary>
    private async Task<bool> RefreshAgentRulesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (!_agentRules.IsDue(now, AgentRulesRefreshInterval)) return true;

        var fetch = await _apiClient.FetchAgentRulesAsync(_agentRules.ETag, ct);
        switch (fetch.Status)
        {
            case AgentRulesFetch.Updated:
                _agentRules.Replace(fetch.Rules ?? [], fetch.ETag, now);
                _log.Information("Server rules updated: {Count} active", _agentRules.Count);
                return true;

            case AgentRulesFetch.Unchanged:
                _agentRules.MarkChecked(now);
                return true;

            case AgentRulesFetch.NotSupported:
                if (!_agentRules.IsKnown)
                    _log.Information("The server has no agent-rules route — sending unfiltered; it applies its rules on arrival");
                _agentRules.Replace([], null, now);
                return true;

            default:
                return _agentRules.IsKnown;
        }
    }

    // ── System events ────────────────────────────────────────────────────────

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                _log.Information("Session locked — not recording");
                _isLocked = true;
                break;
            case SessionSwitchReason.SessionUnlock:
                _log.Information("Session unlocked");
                _isLocked = false;
                break;
            case SessionSwitchReason.SessionLogoff:
                Flush();
                break;
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _log.Information("Suspending — flushing the minute in progress");
            Flush();
        }
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        _log.Information("Session ending ({Reason}) — flushing the minute in progress", e.Reason);
        Flush();
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _apiClient.SignedOut -= _agentRules.Clear;
        _apiClient.SignedOut -= ForgetServerAnswers;

        // Let an in-flight poll finish before flushing, so nothing it records lands after the flush.
        _pollCts?.Cancel();
        try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        Flush();

        _lifetimeCts.Cancel();
        try { _sendTask.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }

        _pollCts?.Dispose();
        _lifetimeCts.Dispose();
        _sendSignal.Dispose();
    }
}
