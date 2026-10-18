using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// Turns credited spans of time into minute windows. Pure — no clock, no Win32 — so the accounting rules are testable.
/// </summary>
/// <remarks>
/// <para>
/// Each call credits the <b>measured</b> time since the previous one, not the configured interval. The
/// loop used to wait the full interval <em>after</em> doing its work, so a 2 s interval ran ~27 polls a
/// minute and credited 54 s — a steady 5-10 % undercount. The caller passes the observations that held
/// <em>during</em> the span: since the foreground is tracked from events, a span ends exactly where the
/// window in front changed.
/// </para>
/// <para>
/// A span that crosses a minute boundary is split at it, so a window never holds time from the next
/// minute and its active total stays within the server's 60-second rule without clamping.
/// </para>
/// <para>
/// A gap longer than <see cref="MaxCreditedGap"/> — sleep, a hung machine, the loop paused — credits
/// nothing: the call after it only knows what is on screen <em>now</em>, not what was during the gap.
/// </para>
/// <para>
/// <b>Credited time stays retractable for <see cref="HoldBack"/></b> before it is committed to its minute, and a
/// minute closes only once that much time has passed its end. That is what lets <see cref="Retract"/> take back the
/// seconds before idleness was noticed — they were credited while the idle threshold was still running — without
/// ever reaching into a window that has been queued, or sent. The cost is that each window closes
/// <see cref="HoldBack"/> after its minute instead of on it.
/// </para>
/// </remarks>
public sealed class ActivityRecorder(TimeSpan maxCreditedGap, TimeSpan holdBack = default)
{
    private readonly Dictionary<string, Accumulator> _entries = new();
    private readonly List<Segment> _pending = new();
    private DateTime? _windowStart;
    private DateTime? _lastEnd;

    /// <summary>Settable because it follows the poll interval, which Settings can change mid-minute.</summary>
    public TimeSpan MaxCreditedGap { get; set; } = maxCreditedGap;

    /// <summary>How long credited time can still be retracted. Settable because it follows the idle threshold.</summary>
    public TimeSpan HoldBack { get; set; } = holdBack;

    /// <summary>
    /// Credits <paramref name="observations"/> with the <paramref name="elapsed"/> time ending at
    /// <paramref name="nowUtc"/>, and returns every window that this call closed.
    /// </summary>
    public List<ActivityWindow> Record(DateTime nowUtc, TimeSpan elapsed, IReadOnlyList<Observation> observations)
    {
        var credited = elapsed < TimeSpan.Zero || elapsed > MaxCreditedGap ? TimeSpan.Zero : elapsed;
        var start = nowUtc - credited;

        // A wall clock stepped backwards credits nothing until it is past the last span again, rather than credit
        // the same stretch twice or reopen a window already closed.
        if (_lastEnd is { } lastEnd && start < lastEnd) start = lastEnd;

        var isFirstSegment = true;
        while (start < nowUtc)
        {
            var minuteEnd = MinuteStart(start).AddMinutes(1);
            var end = minuteEnd < nowUtc ? minuteEnd : nowUtc;

            // A switch happened where the span begins, so it belongs to the first minute only.
            _pending.Add(new Segment(start, end, observations, CountsSwitches: isFirstSegment));
            isFirstSegment = false;
            start = end;
        }

        if (_lastEnd is null || nowUtc > _lastEnd) _lastEnd = nowUtc;

        return Commit(nowUtc - HoldBack);
    }

    /// <summary>
    /// Takes back whatever was credited after <paramref name="fromUtc"/> and not committed yet — the seconds before
    /// idleness was detected, when nobody was there and the threshold simply had not run out. Returns how much.
    /// </summary>
    public TimeSpan Retract(DateTime fromUtc)
    {
        var retracted = TimeSpan.Zero;

        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var segment = _pending[i];
            if (segment.End <= fromUtc) break;

            if (segment.Start >= fromUtc)
            {
                retracted += segment.End - segment.Start;
                _pending.RemoveAt(i);
            }
            else
            {
                retracted += segment.End - fromUtc;
                _pending[i] = segment with { End = fromUtc };
            }
        }

        return retracted;
    }

    /// <summary>
    /// Commits everything and closes the open window early — on exit, suspend or session end — so it can be persisted.
    /// Up to two windows when the held-back time reaches into the previous minute. Later calls in the same minute start
    /// a fresh accumulation under the same window start.
    /// </summary>
    public List<ActivityWindow> Flush()
    {
        var closed = Commit(DateTime.MaxValue);
        if (Close() is { } window) closed.Add(window);
        return closed;
    }

    /// <summary>The key one entry of a window is accumulated under — and what "the same window" means for a switch.</summary>
    public static string EntryKey(ActivitySample sample) =>
        $"{sample.ProcessName}|{sample.ExecutablePath}|{sample.WindowTitle}";

    /// <summary>
    /// Commits every pending segment that ended by <paramref name="horizon"/>, closing a window when a segment lands
    /// in a later minute, then closes the open window once the horizon is past its end: nothing can land in it any more.
    /// </summary>
    private List<ActivityWindow> Commit(DateTime horizon)
    {
        var closed = new List<ActivityWindow>();

        var committed = 0;
        for (; committed < _pending.Count && _pending[committed].End <= horizon; committed++)
        {
            var segment = _pending[committed];
            var minute = MinuteStart(segment.Start);

            if (_windowStart is { } open && minute > open && Close() is { } window)
                closed.Add(window);

            if (_windowStart is null || minute > _windowStart) _windowStart = minute;
            Credit(segment);
        }
        _pending.RemoveRange(0, committed);

        if (horizon != DateTime.MaxValue && _windowStart is { } last && horizon > last.AddMinutes(1) && Close() is { } finished)
            closed.Add(finished);

        return closed;
    }

    private void Credit(Segment segment)
    {
        var milliseconds = (segment.End - segment.Start).TotalMilliseconds;

        foreach (var observation in segment.Observations)
        {
            var key = EntryKey(observation.Sample);
            if (!_entries.TryGetValue(key, out var accumulator))
            {
                accumulator = new Accumulator(observation.Sample);
                _entries[key] = accumulator;
            }

            accumulator.Add(observation, milliseconds, segment.CountsSwitches && observation.IsSwitch);
        }
    }

    private ActivityWindow? Close()
    {
        var windowStart = _windowStart;
        _windowStart = null;

        if (windowStart is null || _entries.Count == 0)
        {
            _entries.Clear();
            return null;
        }

        var entries = WindowLimits.Apply(_entries.Values.Select(a => a.ToEntry()));
        _entries.Clear();

        return entries.Count == 0 ? null : new ActivityWindow { WindowStart = windowStart.Value, Entries = entries };
    }

    private static DateTime MinuteStart(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    /// <summary>A credited stretch inside one minute, not yet committed to its window.</summary>
    private sealed record Segment(DateTime Start, DateTime End, IReadOnlyList<Observation> Observations, bool CountsSwitches);

    // ── Accumulator ──────────────────────────────────────────────────────────

    private sealed class Accumulator(ActivitySample sample)
    {
        private readonly Dictionary<int, double> _activeMsByMonitor = new();
        private readonly Dictionary<int, double> _backgroundMsByMonitor = new();
        private double _activeMs;
        private double _backgroundMs;
        private int _switches;
        private bool _wasPlayingSound;
        private bool _wasFullscreen;
        private bool _wasInCall;

        public void Add(Observation observation, double milliseconds, bool countSwitch)
        {
            if (observation.IsPlayingSound) _wasPlayingSound = true;
            if (observation.IsFullscreen) _wasFullscreen = true;
            if (observation.IsInCall) _wasInCall = true;
            if (countSwitch) _switches++;

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
            IsFullscreen = _wasFullscreen,
            SwitchCount = _switches,
            IsInCall = _wasInCall
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
