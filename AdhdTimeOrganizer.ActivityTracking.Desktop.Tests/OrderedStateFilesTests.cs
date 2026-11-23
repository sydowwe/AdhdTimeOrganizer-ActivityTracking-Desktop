using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// NudgeService takes its drift / ramp / chime snapshots under its lock and writes them outside it: two threads can each
/// hold one, and the older must never be the one left on disk for the next start.
/// </summary>
public sealed class OrderedStateFilesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "state-files-tests-" + Guid.NewGuid().ToString("N"));

    private sealed record State(string Seen);

    private string PathOf(string name) => Path.Combine(_directory, name);

    [Fact]
    public void AnOlderSnapshotArrivingLast_IsNotWrittenOverTheNewer()
    {
        var files = new OrderedStateFiles();
        var path = PathOf("drift.json");

        Assert.True(files.Write(path, new State("newer"), takenAt: 2));
        Assert.False(files.Write(path, new State("older"), takenAt: 1));

        Assert.Equal("newer", JsonFile.TryRead<State>(path).Value!.Seen);
    }

    [Fact]
    public void SnapshotsInOrder_AreEachWritten()
    {
        var files = new OrderedStateFiles();
        var path = PathOf("drift.json");

        files.Write(path, new State("first"), takenAt: 1);
        files.Write(path, new State("second"), takenAt: 2);

        Assert.Equal("second", JsonFile.TryRead<State>(path).Value!.Seen);
    }

    /// <summary>One evaluation's number covers all its files; a later one that changed only the chime leaves drift's alone.</summary>
    [Fact]
    public void EachFileKeepsItsOwnOrder()
    {
        var files = new OrderedStateFiles();

        files.Write(PathOf("time-chime.json"), new State("chime 2"), takenAt: 2);
        Assert.True(files.Write(PathOf("drift.json"), new State("drift 1"), takenAt: 1));

        Assert.Equal("drift 1", JsonFile.TryRead<State>(PathOf("drift.json")).Value!.Seen);
    }

    [Fact]
    public void ManyThreads_LeaveTheNewestSnapshot()
    {
        var files = new OrderedStateFiles();
        var path = PathOf("drift.json");

        Parallel.For(1, 201, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => files.Write(path, new State(i.ToString()), i));

        Assert.Equal("200", JsonFile.TryRead<State>(path).Value!.Seen);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
