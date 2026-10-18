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

    [Fact]
    public void Pending_DeletesUnreadableAndExpiredFiles()
    {
        _store.Save(Window(NineAm.AddDays(-31), Entry("old", 10)));
        _store.Save(Window(NineAm, Entry("a", 10)));
        File.WriteAllText(Path.Combine(_directory, "20260615T0901.json"), "{ not json");

        var pending = _store.Pending(NineAm, max: 10);

        Assert.Equal(NineAm, Assert.Single(pending).Window.WindowStart);
        Assert.Equal(1, _store.Count);
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

    private static ActivityEntry Entry(string process, int active) => new()
    {
        ProcessName = process,
        WindowTitle = "t",
        ExecutablePath = null,
        ProductName = null,
        ActiveSeconds = active,
        BackgroundSeconds = 0,
        ActiveMonitor = 0,
        IsPlayingSound = false,
        IsFullscreen = false
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
