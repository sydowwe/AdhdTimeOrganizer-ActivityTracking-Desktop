using System.Diagnostics;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Native;
using Microsoft.Win32;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

public sealed record TrackerStatus(
    bool IsRunning,
    bool IsLocked,
    int PendingWindows,
    DateTime? LastSentUtc,
    SendResult? LastSendResult,
    string? LastPollError);

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
    private const int WindowsPerSendPass = 50;

    private readonly AppConfig _config;
    private readonly ApiClient _apiClient;
    private readonly OutboxStore _outbox;
    private readonly ILogger _log = Log.ForContext<ActivityTrackerService>();

    private readonly Lock _lock = new();
    private readonly ActivityRecorder _recorder;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _sendSignal = new(0, 1);
    private readonly Task _sendTask;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    private volatile bool _isLocked;
    private volatile string? _lastPollError;
    private DateTime? _lastSentUtc;
    private SendResult? _lastSendResult;

    public bool IsRunning => _pollCts is { IsCancellationRequested: false };

    public ActivityTrackerService(AppConfig config, ApiClient apiClient)
        : this(config, apiClient, new OutboxStore(OutboxStore.DefaultDirectory, OutboxRetention))
    {
    }

    public ActivityTrackerService(AppConfig config, ApiClient apiClient, OutboxStore outbox)
    {
        _config = config;
        _apiClient = apiClient;
        _outbox = outbox;
        _recorder = new ActivityRecorder(MaxCreditedGapFor(PollInterval));

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

    /// <summary>
    /// Persists the minute in progress. Called on exit, suspend and session end — the moments after
    /// which the in-memory part of a minute would otherwise be gone.
    /// </summary>
    public void Flush()
    {
        ActivityWindow? window;
        lock (_lock) window = _recorder.Flush();
        if (window is null) return;

        _outbox.Save(window);
        _log.Information("Flushed partial window {WindowStart} ({Count} entries)", window.WindowStart, window.Entries.Count);
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
        lock (_lock)
            return new TrackerStatus(IsRunning, _isLocked, pending, _lastSentUtc, _lastSendResult, _lastPollError);
    }

    // ── Polling ──────────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = PollInterval;
        lock (_lock) _recorder.MaxCreditedGap = MaxCreditedGapFor(interval);

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
                    var observations = _isLocked ? [] : Observe();

                    List<ActivityWindow> closed;
                    lock (_lock) closed = _recorder.Record(DateTime.UtcNow, elapsed, observations);

                    foreach (var window in closed)
                    {
                        _outbox.Save(window);
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

    private List<Observation> Observe()
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
                ?
                [
                    new Observation(active,
                        MonitorHelper.GetWindowMonitorIndex(foregroundHwnd, monitors),
                        IsBackground: false,
                        IsPlayingSound: true,
                        IsFullscreen: MonitorHelper.IsWindowFullscreen(foregroundHwnd, monitors))
                ]
                : [];
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
            if (ShouldIgnore(sample) || !trackedKeys.Add(ProcessKey(sample))) continue;

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
            if (ShouldIgnore(sample) || !trackedKeys.Add(ProcessKey(sample))) continue;

            observations.Add(new Observation(sample,
                MonitorHelper.GetWindowMonitorIndex(mainWindow, monitors),
                IsBackground: true,
                IsPlayingSound: true,
                IsFullscreen: false));
        }

        return observations;
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

        var sample = ToSample(process, Win32.GetWindowTitle(hWnd));
        return ShouldIgnore(sample) ? null : sample;
    }

    private static ActivitySample ToSample(ProcessInfo process, string? rawTitle) => new()
    {
        ProcessName = process.ProcessName,
        WindowTitle = WindowTitleNormalizer.Normalize(process.ProcessName, process.ProductName, rawTitle),
        ExecutablePath = process.ExecutablePath,
        ProductName = process.ProductName
    };

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

    private bool ShouldIgnore(ActivitySample sample)
    {
        foreach (var rule in _config.IgnoreRules)
        {
            if (!sample.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (rule.WindowTitle is null) return true;
            if (rule.WindowTitle == "" && string.IsNullOrWhiteSpace(sample.WindowTitle)) return true;
            if (sample.WindowTitle.Equals(rule.WindowTitle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

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

            try { await _sendSignal.WaitAsync(SendInterval, ct); }
            catch (OperationCanceledException) { break; }
        }

        _log.Debug("Send loop exited");
    }

    /// <summary>
    /// Sends pending windows oldest first and stops at the first one that has to wait: if the server is
    /// unreachable or the session is gone, the rest would fail the same way, each after its own timeout.
    /// </summary>
    private async Task SendPendingAsync(CancellationToken ct)
    {
        foreach (var item in _outbox.Pending(DateTime.UtcNow, WindowsPerSendPass))
        {
            var result = await _apiClient.SendActivityWindowAsync(item.Window, ct);
            lock (_lock) _lastSendResult = result;

            switch (result)
            {
                case SendResult.Sent:
                    _outbox.Remove(item);
                    lock (_lock) _lastSentUtc = DateTime.UtcNow;
                    _log.Debug("Sent window {WindowStart} ({Count} entries)", item.Window.WindowStart, item.Window.Entries.Count);
                    break;

                case SendResult.Rejected:
                    // Logged with the server's reason by ApiClient. Retrying an identical batch cannot help.
                    _outbox.Remove(item);
                    break;

                default:
                    return;
            }
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
