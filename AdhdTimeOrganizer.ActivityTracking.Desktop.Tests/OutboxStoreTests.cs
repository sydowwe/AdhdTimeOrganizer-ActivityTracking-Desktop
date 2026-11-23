using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public sealed class OutboxStoreTests : IDisposable
{
    private static readonly DateTime NineAm = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "outbox-tests-" + Guid.NewGuid().ToString("N"));
    private readonly OutboxStore _store;

    public OutboxStoreTests() => _store = new OutboxStore(_directory, TimeSpan.FromDays(30));

    /// <summary>A window written by one instance is there for the next — exit and reboot lose nothing.</summary>
    [Fact]
    public void Save_SurvivesANewInstance_AndPendingIsOldestFirst()
    {
        _store.Save(Window(NineAm.AddMinutes(1), Entry("b", 20)));
        _store.Save(Window(NineAm, Entry("a", 10)));

        var pending = new OutboxStore(_directory, TimeSpan.FromDays(30)).Pending(NineAm.AddMinutes(5), max: 10);

        Assert.Equal([NineAm, NineAm.AddMinutes(1)], pending.Select(p => p.Window.WindowStart));
        Assert.Equal(10, pending[0].Window.Entries.Single().ActiveSeconds);
    }

    /// <summary>The upload-health notice measures a stall by this, so it has to survive a restart like the files do.</summary>
    [Fact]
    public void Summary_NamesTheOldestWindow_FromTheFileNamesAlone()
    {
        Assert.Equal(new OutboxSummary(0, null), _store.Summary());

        _store.Save(Window(NineAm.AddMinutes(7), Entry("b", 20)));
        _store.Save(Window(NineAm, Entry("a", 10)));
        File.WriteAllText(Path.Combine(_directory, "not-a-window.json"), "{}");

        var summary = new OutboxStore(_directory, TimeSpan.FromDays(30)).Summary();

        Assert.Equal(3, summary.Count);
        Assert.Equal(NineAm, summary.OldestStartUtc);
        Assert.Equal(DateTimeKind.Utc, summary.OldestStartUtc!.Value.Kind);
    }

    [Fact]
    public void Remove_DeletesASentWindow()
    {
        _store.Save(Window(NineAm, Entry("a", 10)));
        _store.Remove(_store.Pending(NineAm, max: 10).Single());

        Assert.Equal(0, _store.Count);
    }

    /// <summary>
    /// A minute flushed early that then kept going is merged into one window: the server keys a window's
    /// rows on (process, title) and would skip a second copy's seconds.
    /// </summary>
    [Fact]
    public void Save_MergesASecondPartOfTheSameMinute()
    {
        _store.Save(Window(NineAm, Entry("a", 10), Entry("b", 5)));
        _store.Save(Window(NineAm, Entry("a", 15)));

        var entries = _store.Pending(NineAm, max: 10).Single().Window.Entries.ToDictionary(e => e.ProcessName);

        Assert.Equal(25, entries["a"].ActiveSeconds);
        Assert.Equal(5, entries["b"].ActiveSeconds);
    }

    /// <summary>
    /// The measured fields merge like the seconds: switches add up, a call in either part is a call. An entry from an
    /// outbox file an older tracker wrote has no count, and stays unknown only if both parts are.
    /// </summary>
    [Fact]
    public void Save_MergesTheSwitchCountAndCallFlagOfTheSameMinute()
    {
        _store.Save(Window(NineAm, Entry("a", 10, switchCount: 2), Entry("legacy", 5, switchCount: null)));
        _store.Save(Window(NineAm, Entry("a", 15, switchCount: 1, isInCall: true), Entry("legacy", 5, switchCount: null)));

        var entries = _store.Pending(NineAm, max: 10).Single().Window.Entries.ToDictionary(e => e.ProcessName);

        Assert.Equal(3, entries["a"].SwitchCount);
        Assert.True(entries["a"].IsInCall);
        Assert.Null(entries["legacy"].SwitchCount);
    }

    /// <summary>A window rewritten while in flight stays, so the merged-in part is sent too.</summary>
    [Fact]
    public void Remove_KeepsAWindowThatWasRewrittenWhileInFlight()
    {
        _store.Save(Window(NineAm, Entry("a", 10)));
        var inFlight = _store.Pending(NineAm, max: 10).Single();

        File.SetLastWriteTimeUtc(inFlight.Path, inFlight.LastWriteUtc.AddSeconds(1));
        _store.Remove(inFlight);

        Assert.Equal(1, _store.Count);
    }

    /// <summary>A file that is not a window is kept as .bad, out of every pass and the count; an expired window is deleted.</summary>
    [Fact]
    public void Pending_SetsAsideUnreadableFiles_AndDeletesExpiredWindows()
    {
        _store.Save(Window(NineAm.AddDays(-31), Entry("old", 10)));
        _store.Save(Window(NineAm, Entry("a", 10)));
        var damaged = Path.Combine(_directory, "20260615T0901.json");
        File.WriteAllText(damaged, "{ not json");

        var pending = _store.Pending(NineAm, max: 10);

        Assert.Equal(NineAm, Assert.Single(pending).Window.WindowStart);
        Assert.Equal(1, _store.Count);
        Assert.False(File.Exists(damaged));
        Assert.Equal("{ not json", File.ReadAllText(damaged + ".bad"));
        Assert.Equal(new OutboxSummary(1, NineAm), _store.Summary());
    }

    /// <summary>Locked for a moment — a scanner, a backup — is not damaged: it stays, and goes on a later pass.</summary>
    [Fact]
    public void Pending_LeavesAFileThatCannotBeOpened_ForTheNextPass()
    {
        _store.Save(Window(NineAm, Entry("a", 10)));
        _store.Save(Window(NineAm.AddMinutes(1), Entry("b", 10)));
        var locked = Directory.GetFiles(_directory, "*.json").Order(StringComparer.Ordinal).First();

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var pending = _store.Pending(NineAm.AddMinutes(5), max: 10);

            Assert.Equal(NineAm.AddMinutes(1), Assert.Single(pending).Window.WindowStart);
        }

        Assert.False(File.Exists(locked + ".bad"));
        Assert.Equal([NineAm, NineAm.AddMinutes(1)], _store.Pending(NineAm.AddMinutes(5), max: 10).Select(p => p.Window.WindowStart));
    }

    /// <summary>The minute's earlier copy was damaged: set aside, not merged into or lost silently, and the new copy is written.</summary>
    [Fact]
    public void Save_OverADamagedFile_SetsItAsideFirst()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "20260615T0900.json");
        File.WriteAllText(path, "");

        _store.Save(Window(NineAm, Entry("a", 10)));

        Assert.Equal(0, new FileInfo(path + ".bad").Length);
        Assert.Equal(10, Assert.Single(_store.Pending(NineAm, max: 10)).Window.Entries.Single().ActiveSeconds);
    }

    [Fact]
    public void Pending_DeletesBadFiles_OnlyOnceTheyAreOlderThanTheRetention()
    {
        Directory.CreateDirectory(_directory);
        var old = Path.Combine(_directory, "20260501T0900.json.bad");
        var recent = Path.Combine(_directory, "20260610T0900.json.bad");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        File.SetLastWriteTimeUtc(old, NineAm.AddDays(-31));
        File.SetLastWriteTimeUtc(recent, NineAm.AddDays(-5));

        _store.Pending(NineAm, max: 10);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Pending_ReturnsAtMostMax()
    {
        for (var i = 0; i < 5; i++)
            _store.Save(Window(NineAm.AddMinutes(i), Entry("a", 10)));

        Assert.Equal(2, _store.Pending(NineAm.AddMinutes(10), max: 2).Count);
    }

    private static ActivityWindow Window(DateTime start, params ActivityEntry[] entries) =>
        new() { WindowStart = start, Entries = entries.ToList() };

    private static ActivityEntry Entry(string process, int active, int? switchCount = 0, bool isInCall = false) => new()
    {
        ProcessName = process,
        WindowTitle = "t",
        ExecutablePath = null,
        ProductName = null,
        ActiveSeconds = active,
        BackgroundSeconds = 0,
        ActiveMonitor = 0,
        IsPlayingSound = false,
        IsFullscreen = false,
        SwitchCount = switchCount,
        IsInCall = isInCall
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
