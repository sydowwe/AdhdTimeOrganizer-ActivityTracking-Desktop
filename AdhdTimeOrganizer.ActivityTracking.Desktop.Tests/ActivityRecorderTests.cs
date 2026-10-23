using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class ActivityRecorderTests
{
    private static readonly DateTime NineAm = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan TwoSeconds = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The time credited is what elapsed, not the configured interval. With the old fixed credit a poll
    /// loop that ran 27 times in a minute reported 54 seconds; here 24 polls 2.5 s apart report 60.
    /// </summary>
    [Fact]
    public void Record_CreditsMeasuredTime_NotPollCount()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10));
        var editor = Active("code", "Project");

        recorder.Record(NineAm, TimeSpan.Zero, []);
        for (var i = 1; i <= 24; i++)
            recorder.Record(NineAm.AddSeconds(i * 2.5), TimeSpan.FromSeconds(2.5), [editor]);

        var window = Assert.Single(recorder.Record(NineAm.AddSeconds(61), TimeSpan.FromSeconds(1), [editor]));

        Assert.Equal(NineAm, window.WindowStart);
        Assert.Equal(60, Assert.Single(window.Entries).ActiveSeconds);
    }

    /// <summary>
    /// A poll span that straddles the minute is split at it: the closing window gets only its own part,
    /// so it can never exceed the server's 60-second rule, and the rest opens the next window.
    /// </summary>
    [Fact]
    public void Record_SplitsASpanAtTheMinuteBoundary()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10));
        var editor = Active("code", "Project");

        recorder.Record(NineAm.AddSeconds(55), TimeSpan.Zero, []);
        recorder.Record(NineAm.AddSeconds(59), TimeSpan.FromSeconds(4), [editor]);

        var closed = recorder.Record(NineAm.AddSeconds(63), TimeSpan.FromSeconds(4), [editor]);

        var window = Assert.Single(closed);
        Assert.Equal(5, Assert.Single(window.Entries).ActiveSeconds);

        var next = Assert.Single(recorder.Flush());
        Assert.Equal(NineAm.AddMinutes(1), next.WindowStart);
        Assert.Equal(3, Assert.Single(next.Entries).ActiveSeconds);
    }

    /// <summary>
    /// A gap longer than the cap — sleep, a hang — credits nothing; the poll after it only knows what is
    /// on screen now. The window that was open before the gap is still closed and kept.
    /// </summary>
    [Fact]
    public void Record_CreditsNothingForAGapLongerThanTheCap_ButClosesTheOpenWindow()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10));
        var editor = Active("code", "Project");

        recorder.Record(NineAm, TimeSpan.Zero, []);
        recorder.Record(NineAm.AddSeconds(20), TwoSeconds, [editor]);

        var closed = recorder.Record(NineAm.AddHours(2), TimeSpan.FromHours(2), [editor]);

        var window = Assert.Single(closed);
        Assert.Equal(NineAm, window.WindowStart);
        Assert.Equal(2, Assert.Single(window.Entries).ActiveSeconds);

        Assert.Empty(recorder.Flush());
    }

    /// <summary>
    /// The reported monitor is where the window spent the most time. The average of the indices put a
    /// window seen on monitors 0 and 2 on monitor 1, where it never was.
    /// </summary>
    [Fact]
    public void Flush_ReportsTheMonitorWithTheMostTime_NotTheAverageIndex()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10));
        var sample = Sample("code", "Project");

        recorder.Record(NineAm, TimeSpan.Zero, []);
        recorder.Record(NineAm.AddSeconds(2), TwoSeconds, [new Observation(sample, 0, false, false, false)]);
        recorder.Record(NineAm.AddSeconds(4), TwoSeconds, [new Observation(sample, 2, false, false, false)]);
        recorder.Record(NineAm.AddSeconds(6), TwoSeconds, [new Observation(sample, 2, false, false, false)]);

        Assert.Equal(2, Assert.Single(Assert.Single(recorder.Flush()).Entries).ActiveMonitor);
    }

    /// <summary>
    /// A window with no title is named after its app. An empty title used to be a 400 that took the whole
    /// minute with it; the server now accepts one, but "Spotify" says more in a dashboard than nothing.
    /// </summary>
    [Fact]
    public void Flush_NamesAnUntitledWindowAfterItsProduct_ThenItsProcess()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10));

        recorder.Record(NineAm, TimeSpan.Zero, []);
        recorder.Record(NineAm.AddSeconds(2), TwoSeconds,
        [
            new Observation(Sample("chrome", "") with { ProductName = "Google Chrome" }, 0, true, true, false),
            new Observation(Sample("helper", ""), 0, true, true, false)
        ]);

        var titles = Assert.Single(recorder.Flush()).Entries.ToDictionary(e => e.ProcessName, e => e.WindowTitle);
        Assert.Equal("Google Chrome", titles["chrome"]);
        Assert.Equal("helper", titles["helper"]);
    }

    [Fact]
    public void Flush_KeepsActiveAndBackgroundTimeApart()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10));
        var sample = Sample("spotify", "Playlist");

        recorder.Record(NineAm, TimeSpan.Zero, []);
        recorder.Record(NineAm.AddSeconds(2), TwoSeconds, [new Observation(sample, 0, false, true, false)]);
        recorder.Record(NineAm.AddSeconds(4), TwoSeconds, [new Observation(sample, 1, true, true, false)]);
        recorder.Record(NineAm.AddSeconds(6), TwoSeconds, [new Observation(sample, 1, true, false, false)]);

        var entry = Assert.Single(Assert.Single(recorder.Flush()).Entries);
        Assert.Equal(2, entry.ActiveSeconds);
        Assert.Equal(4, entry.BackgroundSeconds);
        Assert.True(entry.IsPlayingSound);
        Assert.Equal(0, entry.ActiveMonitor);
    }

    // ── Switches and calls ───────────────────────────────────────────────────

    /// <summary>
    /// A switch is counted for the span it begins, once — even when that span runs over the minute boundary. The next
    /// minute's share of the same span is the window staying in front, not coming to it again.
    /// </summary>
    [Fact]
    public void Record_CountsASwitchOnce_InTheMinuteItHappenedIn()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10), holdBack: TimeSpan.FromSeconds(30));
        var sample = Sample("code", "Project");

        recorder.Record(NineAm.AddSeconds(55), TimeSpan.Zero, []);
        recorder.Record(NineAm.AddSeconds(63), TimeSpan.FromSeconds(8), [Active("code", "Project") with { IsSwitch = true }]);
        recorder.Record(NineAm.AddSeconds(65), TwoSeconds, [new Observation(sample, 0, false, false, false)]);

        var windows = recorder.Flush();

        Assert.Equal([1, 0], windows.Select(w => Assert.Single(w.Entries).SwitchCount));
        Assert.Equal([5, 5], windows.Select(w => Assert.Single(w.Entries).ActiveSeconds));
    }

    /// <summary>
    /// Every entry from this tracker carries a measured count, zero included: on the server a null means "an tracker that
    /// did not measure", and a background window that never came to the front measured zero switches.
    /// </summary>
    [Fact]
    public void Flush_ReportsAMeasuredZero_AndTheCallFlag()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10));

        recorder.Record(NineAm, TimeSpan.Zero, []);
        recorder.Record(NineAm.AddSeconds(2), TwoSeconds,
        [
            Active("code", "Project"),
            new Observation(Sample("teams", "Standup"), 1, IsBackground: true, IsPlayingSound: false, IsFullscreen: false, IsInCall: true)
        ]);

        var entries = Assert.Single(recorder.Flush()).Entries.ToDictionary(e => e.ProcessName);
        Assert.Equal(0, entries["code"].SwitchCount);
        Assert.False(entries["code"].IsInCall);
        Assert.Equal(0, entries["teams"].SwitchCount);
        Assert.True(entries["teams"].IsInCall);
        Assert.Equal(2, entries["teams"].BackgroundSeconds);
    }

    // ── Hold-back and idle backdating ────────────────────────────────────────

    /// <summary>
    /// A minute closes only once the hold-back has passed its end — before that, its last seconds could still be taken
    /// back, and a window that has been queued (or sent) can't be.
    /// </summary>
    [Fact]
    public void Record_ClosesAMinuteOnlyOnceTheHoldBackHasPassedItsEnd()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10), holdBack: TimeSpan.FromSeconds(40));
        var editor = Active("code", "Project");

        recorder.Record(NineAm.AddSeconds(50), TimeSpan.Zero, []);
        for (var second = 55; second <= 100; second += 5)
            Assert.Empty(recorder.Record(NineAm.AddSeconds(second), TimeSpan.FromSeconds(5), [editor]));

        var window = Assert.Single(recorder.Record(NineAm.AddSeconds(105), TimeSpan.FromSeconds(5), [editor]));
        Assert.Equal(NineAm, window.WindowStart);
        Assert.Equal(10, Assert.Single(window.Entries).ActiveSeconds);
    }

    /// <summary>
    /// The minute boundary case idle backdating has to get right: the user's last input was at 9:00:55, idleness is
    /// noticed at 9:01:27, and the time taken back runs across the boundary. The 9:00 window loses its last five
    /// seconds and 9:01 loses everything — and neither has been closed yet, so nothing already queued is touched.
    /// </summary>
    [Fact]
    public void Retract_TakesBackTimeAcrossTheMinuteBoundary_BeforeEitherWindowCloses()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(15), holdBack: TimeSpan.FromSeconds(45));
        var editor = Active("code", "Project");

        recorder.Record(NineAm.AddSeconds(27), TimeSpan.Zero, []);
        for (var second = 31; second <= 87; second += 4)
            Assert.Empty(recorder.Record(NineAm.AddSeconds(second), TimeSpan.FromSeconds(4), [editor]));

        var retracted = recorder.Retract(NineAm.AddSeconds(55));

        Assert.Equal(TimeSpan.FromSeconds(32), retracted);
        var window = Assert.Single(recorder.Flush());
        Assert.Equal(NineAm, window.WindowStart);
        Assert.Equal(28, Assert.Single(window.Entries).ActiveSeconds);
    }

    /// <summary>Time older than the hold-back is committed: it can no longer be taken back, and saying so is the answer.</summary>
    [Fact]
    public void Retract_CannotReachPastTheHoldBack()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10), holdBack: TimeSpan.FromSeconds(10));
        var editor = Active("code", "Project");

        recorder.Record(NineAm, TimeSpan.Zero, []);
        for (var second = 5; second <= 30; second += 5)
            recorder.Record(NineAm.AddSeconds(second), TimeSpan.FromSeconds(5), [editor]);

        Assert.Equal(TimeSpan.FromSeconds(10), recorder.Retract(NineAm));
        Assert.Equal(20, Assert.Single(Assert.Single(recorder.Flush()).Entries).ActiveSeconds);
    }

    /// <summary>
    /// With time held back across the boundary, an early flush (exit, suspend) has two windows to persist — the one
    /// whose minute is over and the one in progress.
    /// </summary>
    [Fact]
    public void Flush_PersistsBothWindowsWhenTheHoldBackSpansTheBoundary()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10), holdBack: TimeSpan.FromSeconds(30));
        var editor = Active("code", "Project");

        recorder.Record(NineAm.AddSeconds(50), TimeSpan.Zero, []);
        recorder.Record(NineAm.AddSeconds(58), TimeSpan.FromSeconds(8), [editor]);
        recorder.Record(NineAm.AddSeconds(66), TimeSpan.FromSeconds(8), [editor]);

        var windows = recorder.Flush();

        Assert.Equal([NineAm, NineAm.AddMinutes(1)], windows.Select(w => w.WindowStart));
        Assert.Equal([10, 6], windows.Select(w => Assert.Single(w.Entries).ActiveSeconds));
    }

    /// <summary>
    /// A wall clock stepped backwards credits nothing for the stretch it repeats: crediting it would count those seconds
    /// twice, and the minute they fall in may already be closed.
    /// </summary>
    [Fact]
    public void Record_CreditsNothingTwice_WhenTheWallClockStepsBack()
    {
        var recorder = new ActivityRecorder(TimeSpan.FromSeconds(10));
        var editor = Active("code", "Project");

        recorder.Record(NineAm.AddSeconds(20), TimeSpan.Zero, []);
        recorder.Record(NineAm.AddSeconds(30), TimeSpan.FromSeconds(10), [editor]);
        recorder.Record(NineAm.AddSeconds(27), TimeSpan.FromSeconds(5), [editor]);
        recorder.Record(NineAm.AddSeconds(35), TimeSpan.FromSeconds(8), [editor]);

        Assert.Equal(15, Assert.Single(Assert.Single(recorder.Flush()).Entries).ActiveSeconds);
    }

    private static Observation Active(string process, string title) =>
        new(Sample(process, title), 0, IsBackground: false, IsPlayingSound: false, IsFullscreen: false);

    private static ActivitySample Sample(string process, string title) => new()
    {
        ProcessName = process,
        WindowTitle = title,
        ExecutablePath = $@"C:\apps\{process}.exe"
    };
}
