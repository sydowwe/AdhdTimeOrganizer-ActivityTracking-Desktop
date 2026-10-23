using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// Each case is a batch the server's validator would refuse — and a refused batch is a lost minute.
/// </summary>
public class WindowLimitsTests
{
    [Fact]
    public void Apply_CapsEachEntryAtSixtySecondsOfActivePlusBackground()
    {
        var entry = Assert.Single(WindowLimits.Apply([Entry("a", active: 31, background: 30)]));

        Assert.Equal(31, entry.ActiveSeconds);
        Assert.Equal(29, entry.BackgroundSeconds);
    }

    [Fact]
    public void Apply_TrimsTheActiveTotalToSixty_FromTheBusiestEntries()
    {
        var entries = WindowLimits.Apply([Entry("a", active: 31), Entry("b", active: 30)]);

        Assert.Equal(60, entries.Sum(e => e.ActiveSeconds));
        Assert.Equal(30, entries.Single(e => e.ProcessName == "a").ActiveSeconds);
        Assert.Equal(30, entries.Single(e => e.ProcessName == "b").ActiveSeconds);
    }

    [Fact]
    public void Apply_DropsEmptyEntries_AndKeepsTheBusiestTwoHundred()
    {
        var input = Enumerable.Range(0, 250)
            .Select(i => Entry($"p{i}", active: 0, background: i % 60))
            .ToList();

        var entries = WindowLimits.Apply(input);

        Assert.Equal(WindowLimits.MaxEntriesPerWindow, entries.Count);
        Assert.DoesNotContain(entries, e => e.ActiveSeconds == 0 && e.BackgroundSeconds == 0);
        // 1..59 seconds, four or five of each: the busiest 200 are exactly the four of each of 10..59.
        Assert.Equal(10, entries.Min(e => e.BackgroundSeconds));
    }

    [Fact]
    public void Apply_FitsProcessNameAndTitleIntoTheIndexByteLimit()
    {
        var title = new string('\u65e5', 900); // 2700 bytes in UTF-8

        var entry = Assert.Single(WindowLimits.Apply([Entry("a", active: 10, title: title)]));

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(entry.ProcessName + entry.WindowTitle)
                    <= WindowLimits.MaxIndexedEntryTextBytes);
        Assert.StartsWith(entry.WindowTitle!, title);
    }

    [Fact]
    public void Apply_DropsAnOverlongPath_AndTruncatesAnOverlongProductName()
    {
        var entry = Assert.Single(WindowLimits.Apply(
        [
            new ActivityEntry
            {
                ProcessName = "a",
                WindowTitle = "t",
                ExecutablePath = new string('p', WindowLimits.MaxExecutablePathLength + 1),
                ProductName = new string('n', 300),
                ActiveSeconds = 10,
                BackgroundSeconds = 0,
                ActiveMonitor = 0,
                IsPlayingSound = false,
                IsFullscreen = false
            }
        ]));

        Assert.Equal("", entry.ExecutablePath);
        Assert.Equal(WindowLimits.MaxProductNameLength, entry.ProductName!.Length);
    }

    /// <summary>
    /// Nothing leaves the tracker null or blank where a server before 2026-10-18 would refuse it: a null
    /// product name or path was a 500 there (NOT NULL insert), a blank title a 400.
    /// </summary>
    [Fact]
    public void Apply_SendsEmptyStringsForMissingProductNameAndPath()
    {
        var entry = Assert.Single(WindowLimits.Apply([Entry("helper", active: 10)]));

        Assert.Equal("", entry.ProductName);
        Assert.Equal(@"C:\apps\helper.exe", entry.ExecutablePath);

        var noPath = Assert.Single(WindowLimits.Apply(
        [
            new ActivityEntry
            {
                ProcessName = "elevated", WindowTitle = "Admin", ExecutablePath = null, ProductName = null,
                ActiveSeconds = 10, BackgroundSeconds = 0, ActiveMonitor = 0, IsPlayingSound = false, IsFullscreen = false
            }
        ]));
        Assert.Equal("", noPath.ExecutablePath);
        Assert.Equal("", noPath.ProductName);
    }

    /// <summary>
    /// Two windows fighting over focus can flip hundreds of times a minute. The server refuses past its bound, and a
    /// refused heartbeat is a lost minute — so the count is clamped to it, and an unknown one stays unknown.
    /// </summary>
    [Fact]
    public void Apply_ClampsTheSwitchCountToTheServersBound()
    {
        var entries = WindowLimits.Apply(
        [
            Entry("fighting", active: 30, switchCount: 500),
            Entry("calm", active: 20, switchCount: 3, isInCall: true),
            Entry("unknown", active: 5, switchCount: null)
        ]).ToDictionary(e => e.ProcessName);

        Assert.Equal(WindowLimits.MaxSwitchesPerEntry, entries["fighting"].SwitchCount);
        Assert.Equal(3, entries["calm"].SwitchCount);
        Assert.True(entries["calm"].IsInCall);
        Assert.Null(entries["unknown"].SwitchCount);
    }

    private static ActivityEntry Entry(
        string process, int active, int background = 0, string title = "t", int? switchCount = 0, bool isInCall = false) => new()
    {
        ProcessName = process,
        WindowTitle = title,
        ExecutablePath = $@"C:\apps\{process}.exe",
        ProductName = null,
        ActiveSeconds = active,
        BackgroundSeconds = background,
        ActiveMonitor = 0,
        IsPlayingSound = false,
        IsFullscreen = false,
        SwitchCount = switchCount,
        IsInCall = isInCall
    };
}
