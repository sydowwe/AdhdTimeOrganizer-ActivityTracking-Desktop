using System.Collections.Concurrent;
using System.Diagnostics;
using DesktopActivityTracker.Models;
using DesktopActivityTracker.Native;
using Serilog;

namespace DesktopActivityTracker.Services;

/// <summary>
/// Core service: polls foreground window, aggregates into 1-minute windows, sends to backend.
/// </summary>
public sealed class ActivityTrackerService : IDisposable
{
    private readonly AppConfig _config;
    private readonly ApiClient _apiClient;
    private readonly ConcurrentQueue<ActivityWindow> _retryQueue = new();
    private readonly ILogger _log = Log.ForContext<ActivityTrackerService>();

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private Task? _sendTask;

    // key = "ProcessName|ExecutablePath", value = (sample, totalSeconds, monitorIndexSum, monitorSampleCount)
    private readonly Dictionary<string, (ActivitySample Sample, int Seconds, int MonitorTotal, int MonitorCount)> _currentMinute = new();
    private DateTime _currentWindowStart;
    private int _currentIdleTotal;
    private readonly object _lock = new();

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

        _log.Information("Starting tracker (poll interval: {PollMs}ms, idle threshold: {IdleS}s)",
            _config.PollIntervalMs, _config.IdleThresholdSeconds);

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

    /// <summary>
    /// Polls the foreground window at the configured interval.
    /// </summary>
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
                    // If we've crossed into a new minute, finalize the previous window
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
                        var sample = CaptureCurrentSample();
                        if (sample is not null)
                        {
                            var monitor = MonitorHelper.GetActiveMonitorIndex();
                            var key = $"{sample.ProcessName}|{sample.ExecutablePath}";

                            if (_currentMinute.TryGetValue(key, out var existing))
                            {
                                _currentMinute[key] = (sample, existing.Seconds + pollSeconds,
                                    existing.MonitorTotal + monitor, existing.MonitorCount + 1);
                            }
                            else
                            {
                                _currentMinute[key] = (sample, pollSeconds, monitor, 1);
                            }
                        }
                    }
                }

                await Task.Delay(_config.PollIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Poll loop error");
                StatusChanged?.Invoke($"Poll error: {ex.Message}");
                await Task.Delay(5000, ct);
            }
        }

        _log.Debug("Poll loop exited");
    }

    /// <summary>
    /// Periodically checks for windows to send and retries failed ones.
    /// </summary>
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

                    var sent = await _apiClient.SendActivityWindowAsync(window);
                    if (sent)
                    {
                        _log.Debug("Sent activity window {WindowStart} ({Count} entries)",
                            window.WindowStart, window.Entries.Count);
                    }
                    else
                    {
                        _log.Warning("Failed to send activity window {WindowStart} — re-queuing", window.WindowStart);
                        _retryQueue.Enqueue(window);
                    }
                }

                await Task.Delay(10_000, ct); // Check every 10s
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Send loop error");
                await Task.Delay(30_000, ct);
            }
        }

        _log.Debug("Send loop exited");
    }

    /// <summary>
    /// Aggregates the current minute's data and queues it for sending.
    /// Must be called within lock(_lock).
    /// </summary>
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
                .OrderByDescending(x => x.Seconds)
                .Select(x => new ActivityEntry
                {
                    ProcessName = x.Sample.ProcessName,
                    WindowTitle = x.Sample.WindowTitle,
                    ExecutablePath = x.Sample.ExecutablePath,
                    Seconds = x.Seconds,
                    ActiveMonitor = x.MonitorCount > 0
                        ? (int)Math.Round((double)x.MonitorTotal / x.MonitorCount)
                        : 0
                })
                .ToList()
        };

        _log.Information("Finalized window {WindowStart}: {Count} processes, {IdleSeconds}s idle",
            window.WindowStart, window.Entries.Count, window.IdleSeconds);

        _retryQueue.Enqueue(window);
        ResetCurrentWindow();
    }

    private void ResetCurrentWindow()
    {
        _currentMinute.Clear();
        _currentIdleTotal = 0;
    }

    private static ActivitySample? CaptureCurrentSample()
    {
        try
        {
            var title = Win32.GetForegroundWindowTitle();
            var processId = Win32.GetForegroundProcessId();

            if (title is null || processId is null) return null;

            var process = Process.GetProcessById((int)processId.Value);

            return new ActivitySample
            {
                ProcessName = process.ProcessName,
                WindowTitle = title,
                ExecutablePath = GetProcessPath(process)
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
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
}
