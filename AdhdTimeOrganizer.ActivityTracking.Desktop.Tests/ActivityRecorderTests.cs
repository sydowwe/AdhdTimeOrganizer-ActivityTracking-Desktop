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

        var next = recorder.Flush()!;
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

        Assert.Null(recorder.Flush());
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

        Assert.Equal(2, Assert.Single(recorder.Flush()!.Entries).ActiveMonitor);
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

        var titles = recorder.Flush()!.Entries.ToDictionary(e => e.ProcessName, e => e.WindowTitle);
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

        var entry = Assert.Single(recorder.Flush()!.Entries);
        Assert.Equal(2, entry.ActiveSeconds);
        Assert.Equal(4, entry.BackgroundSeconds);
        Assert.True(entry.IsPlayingSound);
        Assert.Equal(0, entry.ActiveMonitor);
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
