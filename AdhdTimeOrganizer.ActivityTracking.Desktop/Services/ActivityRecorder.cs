using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// Turns polls into minute windows. Pure — no clock, no Win32 — so the accounting rules are testable.
/// </summary>
/// <remarks>
/// <para>
/// Each poll credits the <b>measured</b> time since the previous one, not the configured interval. The
/// loop used to wait the full interval <em>after</em> doing its work, so a 2 s interval ran ~27 polls a
/// minute and credited 54 s — a steady 5-10 % undercount.
/// </para>
/// <para>
/// A span that crosses a minute boundary is split at it, so a window never holds time from the next
/// minute and its active total stays within the server's 60-second rule without clamping.
/// </para>
/// <para>
/// A gap longer than <see cref="MaxCreditedGap"/> — sleep, a hung machine, the loop paused — credits
/// nothing: the poll after it only knows what is on screen <em>now</em>, not what was during the gap.
/// </para>
/// </remarks>
public sealed class ActivityRecorder(TimeSpan maxCreditedGap)
{
    private readonly Dictionary<string, Accumulator> _entries = new();
    private DateTime? _windowStart;

    /// <summary>Settable because it follows the poll interval, which Settings can change mid-minute.</summary>
    public TimeSpan MaxCreditedGap { get; set; } = maxCreditedGap;

    /// <summary>
    /// Credits <paramref name="observations"/> with the <paramref name="elapsed"/> time ending at
    /// <paramref name="nowUtc"/>, and returns every window that this poll closed.
    /// </summary>
    public List<ActivityWindow> Record(DateTime nowUtc, TimeSpan elapsed, IReadOnlyList<Observation> observations)
    {
        var closed = new List<ActivityWindow>();

        var credited = elapsed < TimeSpan.Zero || elapsed > MaxCreditedGap ? TimeSpan.Zero : elapsed;
        var cursor = nowUtc - credited;

        while (true)
        {
            var minute = MinuteStart(cursor);

            if (_windowStart is { } open && minute > open)
            {
                if (Close() is { } window) closed.Add(window);
                _windowStart = minute;
            }
            else
            {
                // A wall clock stepped backwards stays in the open window instead of reopening a past one.
                _windowStart ??= minute;
                if (minute < _windowStart) minute = _windowStart.Value;
            }

            var segmentEnd = minute.AddMinutes(1) < nowUtc ? minute.AddMinutes(1) : nowUtc;
            var milliseconds = (segmentEnd - cursor).TotalMilliseconds;
            if (milliseconds > 0)
                Credit(observations, milliseconds);

            if (segmentEnd >= nowUtc) break;
            cursor = segmentEnd;
        }

        return closed;
    }

    /// <summary>
    /// Closes the open window early — on exit, suspend or session end — so it can be persisted. Later
    /// polls in the same minute start a fresh accumulation under the same window start.
    /// </summary>
    public ActivityWindow? Flush() => Close();

    private void Credit(IReadOnlyList<Observation> observations, double milliseconds)
    {
        foreach (var observation in observations)
        {
            var sample = observation.Sample;
            var key = $"{sample.ProcessName}|{sample.ExecutablePath}|{sample.WindowTitle}";

            if (!_entries.TryGetValue(key, out var accumulator))
            {
                accumulator = new Accumulator(sample);
                _entries[key] = accumulator;
            }

            accumulator.Add(observation, milliseconds);
        }
    }

    private ActivityWindow? Close()
    {
        if (_windowStart is null || _entries.Count == 0)
        {
            _entries.Clear();
            return null;
        }

        var entries = WindowLimits.Apply(_entries.Values.Select(a => a.ToEntry()));
        _entries.Clear();

        return entries.Count == 0 ? null : new ActivityWindow { WindowStart = _windowStart.Value, Entries = entries };
    }

    private static DateTime MinuteStart(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    // ── Accumulator ──────────────────────────────────────────────────────────

    private sealed class Accumulator(ActivitySample sample)
    {
        private readonly Dictionary<int, double> _activeMsByMonitor = new();
        private readonly Dictionary<int, double> _backgroundMsByMonitor = new();
        private double _activeMs;
        private double _backgroundMs;
        private bool _wasPlayingSound;
        private bool _wasFullscreen;

        public void Add(Observation observation, double milliseconds)
        {
            if (observation.IsPlayingSound) _wasPlayingSound = true;
            if (observation.IsFullscreen) _wasFullscreen = true;

            var byMonitor = observation.IsBackground ? _backgroundMsByMonitor : _activeMsByMonitor;
            byMonitor[observation.MonitorIndex] = byMonitor.GetValueOrDefault(observation.MonitorIndex) + milliseconds;

            if (observation.IsBackground) _backgroundMs += milliseconds;
            else _activeMs += milliseconds;
        }

        public ActivityEntry ToEntry() => new()
        {
            ProcessName = sample.ProcessName,
            // A window with no title (an audio-only process, a titleless popup) is named after its app,
            // which is what the user would recognise in a dashboard.
            WindowTitle = string.IsNullOrWhiteSpace(sample.WindowTitle)
                ? sample.ProductName ?? sample.ProcessName
                : sample.WindowTitle,
            ExecutablePath = sample.ExecutablePath,
            ProductName = sample.ProductName,
            ActiveSeconds = ToSeconds(_activeMs),
            BackgroundSeconds = ToSeconds(_backgroundMs),
            ActiveMonitor = ResolveMonitor(),
            IsPlayingSound = _wasPlayingSound,
            IsFullscreen = _wasFullscreen
        };

        /// <summary>
        /// The monitor the window spent the most time on. This used to be the average of the indices,
        /// so a window seen on monitors 0 and 2 was reported on monitor 1, where it never was.
        /// </summary>
        private int ResolveMonitor()
        {
            var source = _activeMsByMonitor.Count > 0 ? _activeMsByMonitor : _backgroundMsByMonitor;
            return source.Count == 0 ? 0 : source.MaxBy(kv => kv.Value).Key;
        }

        private static int ToSeconds(double milliseconds) =>
            (int)Math.Round(milliseconds / 1000, MidpointRounding.AwayFromZero);
    }
}
