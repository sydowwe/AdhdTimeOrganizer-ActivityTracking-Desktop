using System.Collections.Concurrent;
using System.Diagnostics;
using DesktopActivityTracker.Models;
using DesktopActivityTracker.Native;
using Serilog;

namespace DesktopActivityTracker.Services;

public sealed class ActivityTrackerService : IDisposable
{
    private readonly AppConfig _config;
    private readonly ApiClient _apiClient;
    private readonly ConcurrentQueue<ActivityWindow> _retryQueue = new();
    private readonly ILogger _log = Log.ForContext<ActivityTrackerService>();

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private Task? _sendTask;

    private readonly Dictionary<string, ProcessAccumulator> _currentMinute = new();
    private DateTime _currentWindowStart;
    private int _currentIdleTotal;
    private readonly object _lock = new();

    // Cached per exe path — populated on first encounter, kept for the app lifetime
    private static readonly Dictionary<string, string?> _productNameCache = new();

    public bool IsRunning => _cts is not null && !_cts.IsCancellationRequested;
    public event Action<string>? StatusChanged;

    public ActivityTrackerService(AppConfig config, ApiClient apiClient)
    {
        _config = config;
        _apiClient = apiClient;
    }

    public void Start()
    {
        if (IsRunning) return;

        _log.Information(
            "Starting tracker (poll: {PollMs}ms, idle threshold: {IdleS}s, " +
            "min bg window: {MinBg}%, max bg per monitor: {MaxBg})",
            _config.PollIntervalMs, _config.IdleThresholdSeconds,
            _config.MinBackgroundWindowPercent, _config.MaxBackgroundWindowsPerMonitor);

        _cts = new CancellationTokenSource();
        _currentWindowStart = GetCurrentMinuteStart();

        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        _sendTask = Task.Run(() => SendLoopAsync(_cts.Token));

        StatusChanged?.Invoke("Tracking active");
    }

    public void Stop()
    {
        _log.Information("Stopping tracker");
        _cts?.Cancel();
        StatusChanged?.Invoke("Tracking paused");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var idleSeconds = Win32.GetIdleSeconds();
                var currentMinuteStart = GetCurrentMinuteStart();

                lock (_lock)
                {
                    if (currentMinuteStart > _currentWindowStart)
                    {
                        FinalizeCurrentWindow();
                        _currentWindowStart = currentMinuteStart;
                    }

                    var pollSeconds = _config.PollIntervalMs / 1000;

                    if (idleSeconds >= _config.IdleThresholdSeconds)
                    {
                        _currentIdleTotal += pollSeconds;
                    }
                    else
                    {
                        // Gather audio info once per poll — used for all three sections below
                        var audio = AudioSessionHelper.GetActiveAudioInfo();

                        var foregroundHwnd = Win32.GetForegroundWindow();

                        // ── Active (foreground) window ────────────────────────────
                        var activeSample = CaptureWindowSample(foregroundHwnd, out var activePid);
                        if (activeSample is not null && !ShouldIgnore(activeSample))
                        {
                            var activeMonitor = MonitorHelper.GetActiveMonitorIndex();
                            Accumulate(activeSample, activeMonitor, pollSeconds,
                                isBackground: false,
                                isPlayingSound: IsPlayingAudio(audio, activePid, activeSample.ExecutablePath),
                                isFullscreen: MonitorHelper.IsWindowFullscreen(foregroundHwnd));
                        }
                        else
                        {
                            activeSample = null; // treat ignored active window as no active sample
                        }

                        var activeKey = activeSample is not null
                            ? $"{activeSample.ProcessName}|{activeSample.ExecutablePath}"
                            : null;

                        var trackedKeys = new HashSet<string>();
                        if (activeKey is not null) trackedKeys.Add(activeKey);

                        // ── Background windows (other monitors, by visible area) ──
                        var bgWindows = MonitorHelper.GetBackgroundWindows(
                            foregroundHwnd,
                            _config.MinBackgroundWindowPercent,
                            _config.MaxBackgroundWindowsPerMonitor);

                        foreach (var bg in bgWindows)
                        {
                            var bgSample = new ActivitySample
                            {
                                ProcessName = bg.ProcessName,
                                WindowTitle = bg.WindowTitle ?? string.Empty,
                                ExecutablePath = bg.ExecutablePath
                            };
                            if (ShouldIgnore(bgSample)) continue;

                            var key = $"{bg.ProcessName}|{bg.ExecutablePath}";
                            if (!trackedKeys.Add(key)) continue;

                            Accumulate(bgSample, bg.MonitorIndex, pollSeconds,
                                isBackground: true,
                                isPlayingSound: IsPlayingAudio(audio, bg.Pid, bg.ExecutablePath),
                                isFullscreen: false);
                        }

                        // ── Audio-playing processes not yet tracked ───────────────
                        foreach (var pid in audio.Pids)
                        {
                            var audioSample = CaptureProcessSample(pid, out var mainWindowHandle);
                            if (audioSample is null || ShouldIgnore(audioSample)) continue;

                            var key = $"{audioSample.ProcessName}|{audioSample.ExecutablePath}";
                            if (!trackedKeys.Add(key)) continue;

                            Accumulate(audioSample,
                                MonitorHelper.GetWindowMonitorIndex(mainWindowHandle),
                                pollSeconds,
                                isBackground: true,
                                isPlayingSound: true,
                                isFullscreen: false);
                        }
                    }
                }

                await Task.Delay(_config.PollIntervalMs, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error(ex, "Poll loop error");
                StatusChanged?.Invoke($"Poll error: {ex.Message}");
                await Task.Delay(5000, ct);
            }
        }

        _log.Debug("Poll loop exited");
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var retryCount = _retryQueue.Count;
                for (var i = 0; i < retryCount; i++)
                {
                    if (!_retryQueue.TryDequeue(out var window)) break;

                    if (await _apiClient.SendActivityWindowAsync(window))
                        _log.Debug("Sent window {WindowStart} ({Count} entries)", window.WindowStart, window.Entries.Count);
                    else
                    {
                        _log.Warning("Failed to send window {WindowStart} — re-queuing", window.WindowStart);
                        _retryQueue.Enqueue(window);
                    }
                }

                await Task.Delay(10_000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error(ex, "Send loop error");
                await Task.Delay(30_000, ct);
            }
        }

        _log.Debug("Send loop exited");
    }

    private void FinalizeCurrentWindow()
    {
        if (_currentMinute.Count == 0) return;

        if (_currentIdleTotal > _config.IdleThresholdSeconds)
        {
            _log.Debug("Skipping window {WindowStart} — idle {IdleSeconds}s exceeds threshold",
                _currentWindowStart, _currentIdleTotal);
            ResetCurrentWindow();
            return;
        }

        var window = new ActivityWindow
        {
            WindowStart = _currentWindowStart,
            IdleSeconds = _currentIdleTotal,
            Entries = _currentMinute.Values
                .OrderByDescending(x => x.ActiveSeconds)
                .ThenByDescending(x => x.BackgroundSeconds)
                .Select(x => new ActivityEntry
                {
                    ProcessName = x.Sample.ProcessName,
                    WindowTitle = x.Sample.WindowTitle,
                    ExecutablePath = x.Sample.ExecutablePath,
                    ActiveSeconds = x.ActiveSeconds,
                    BackgroundSeconds = x.BackgroundSeconds,
                    ActiveMonitor = x.ResolvedMonitor,
                    IsPlayingSound = x.WasPlayingSound,
                    IsFullscreen = x.WasFullscreen,
                    ProductName = GetProductName(x.Sample.ExecutablePath)
                })
                .ToList()
        };

        _log.Information(
            "Finalized window {WindowStart}: {Count} processes ({Active} active / {Bg} background), {IdleSeconds}s idle",
            window.WindowStart, window.Entries.Count,
            window.Entries.Count(e => e.ActiveSeconds > 0),
            window.Entries.Count(e => e.BackgroundSeconds > 0 && e.ActiveSeconds == 0),
            window.IdleSeconds);

        _retryQueue.Enqueue(window);
        ResetCurrentWindow();
    }

    private void ResetCurrentWindow()
    {
        _currentMinute.Clear();
        _currentIdleTotal = 0;
    }

    private void Accumulate(ActivitySample sample, int monitorIndex, int pollSeconds,
        bool isBackground, bool isPlayingSound, bool isFullscreen)
    {
        var key = $"{sample.ProcessName}|{sample.ExecutablePath}";

        if (!_currentMinute.TryGetValue(key, out var acc))
        {
            acc = new ProcessAccumulator { Sample = sample };
            _currentMinute[key] = acc;
        }
        else
        {
            acc.Sample = sample; // keep most recent window title
        }

        if (isPlayingSound) acc.WasPlayingSound = true;
        if (isFullscreen) acc.WasFullscreen = true;

        if (isBackground)
        {
            acc.BackgroundSeconds += pollSeconds;
            acc.BackgroundMonitorTotal += monitorIndex;
            acc.BackgroundMonitorCount++;
        }
        else
        {
            acc.ActiveSeconds += pollSeconds;
            acc.ActiveMonitorTotal += monitorIndex;
            acc.ActiveMonitorCount++;
        }
    }

    private static ActivitySample? CaptureWindowSample(nint hWnd, out int pid)
    {
        pid = 0;
        try
        {
            if (hWnd == nint.Zero) return null;
            Win32.GetWindowThreadProcessId(hWnd, out var processId);
            pid = (int)processId;
            if (pid == 0) return null;

            var process = Process.GetProcessById(pid);
            return new ActivitySample
            {
                ProcessName = process.ProcessName,
                WindowTitle = Win32.GetWindowTitle(hWnd) ?? string.Empty,
                ExecutablePath = GetProcessPath(process)
            };
        }
        catch { return null; }
    }

    private static ActivitySample? CaptureProcessSample(int pid, out nint mainWindowHandle)
    {
        mainWindowHandle = nint.Zero;
        try
        {
            var process = Process.GetProcessById(pid);
            mainWindowHandle = process.MainWindowHandle;
            var title = mainWindowHandle != nint.Zero
                ? Win32.GetWindowTitle(mainWindowHandle) ?? string.Empty
                : string.Empty;

            return new ActivitySample
            {
                ProcessName = process.ProcessName,
                WindowTitle = title,
                ExecutablePath = GetProcessPath(process)
            };
        }
        catch { return null; }
    }

    private static string? GetProcessPath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch { return null; }
    }

    private static string? GetProductName(string? executablePath)
    {
        if (executablePath is null) return null;
        if (_productNameCache.TryGetValue(executablePath, out var cached)) return cached;

        string? name = null;
        try { name = FileVersionInfo.GetVersionInfo(executablePath).ProductName; }
        catch { /* access denied or file gone */ }

        _productNameCache[executablePath] = name;
        return name;
    }

    private static bool IsPlayingAudio(ActiveAudioInfo audio, int pid, string? exePath)
    {
        if (audio.Pids.Contains(pid)) return true;
        if (exePath is not null && audio.ExePaths.Contains(exePath)) return true;
        return false;
    }

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

    private static DateTime GetCurrentMinuteStart()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    // ── Accumulator ──────────────────────────────────────────────────────────

    private sealed class ProcessAccumulator
    {
        public required ActivitySample Sample { get; set; }
        public int ActiveSeconds { get; set; }
        public int BackgroundSeconds { get; set; }
        public int ActiveMonitorTotal { get; set; }
        public int ActiveMonitorCount { get; set; }
        public int BackgroundMonitorTotal { get; set; }
        public int BackgroundMonitorCount { get; set; }
        public bool WasPlayingSound { get; set; }
        public bool WasFullscreen { get; set; }

        public int ResolvedMonitor => ActiveMonitorCount > 0
            ? (int)Math.Round((double)ActiveMonitorTotal / ActiveMonitorCount)
            : BackgroundMonitorCount > 0
                ? (int)Math.Round((double)BackgroundMonitorTotal / BackgroundMonitorCount)
                : 0;
    }
}
