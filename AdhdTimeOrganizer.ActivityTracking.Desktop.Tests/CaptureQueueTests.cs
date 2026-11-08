using System.Net;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The capture queue (desktop prompt 08): a parked thought survives a restart, goes oldest first, and leaves only when the
/// server has it. Every way it fails is a thought quietly lost, so each case reads the files back.
/// </summary>
/// <remarks>Everything lives in a temp folder; the user's real queue is never touched.</remarks>
public sealed class CaptureQueueTests : IDisposable
{
    private static readonly DateTime NineAm = new(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "capture-tests-" + Guid.NewGuid().ToString("N"));
    private readonly CaptureQueue _queue;

    public CaptureQueueTests() => _queue = new CaptureQueue(_directory);

    [Fact]
    public void AParkedThought_SurvivesANewInstance_AndPendingIsOldestFirst()
    {
        _queue.Add("Buy stamps", NineAm.AddSeconds(5));
        _queue.Add("  Email the landlord  ", NineAm);

        var pending = new CaptureQueue(_directory).Pending(max: 10);

        Assert.Equal(["Email the landlord", "Buy stamps"], pending.Select(p => p.Text));
        Assert.Equal(NineAm, pending[0].CreatedUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void Nothing_IsNotParked(string? typed)
    {
        Assert.Null(_queue.Add(typed, NineAm));
        Assert.Equal(new CaptureSummary(0, 0), _queue.Summary());
    }

    /// <summary>Longer than the server takes is a 400 the queue would keep for good, so it is cut first — never mid-character.</summary>
    [Fact]
    public void Prepare_CutsToTheServersLimit_WithoutSplittingAPair()
    {
        Assert.Equal(CaptureQueue.MaxTextLength, CaptureQueue.Prepare(new string('a', CaptureQueue.MaxTextLength + 50))!.Length);

        var straddling = new string('a', CaptureQueue.MaxTextLength - 1) + "😀" + "tail";
        var cut = CaptureQueue.Prepare(straddling)!;

        Assert.Equal(CaptureQueue.MaxTextLength - 1, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }

    [Fact]
    public void Remove_DeletesTheDeliveredOne_Only()
    {
        var first = _queue.Add("Email the landlord", NineAm)!;
        _queue.Add("Buy stamps", NineAm.AddSeconds(1));

        Assert.True(_queue.Remove(first));
        Assert.False(_queue.Remove(first));

        Assert.Equal(["Buy stamps"], _queue.Pending(10).Select(p => p.Text));
    }

    /// <summary>A refused thought is the user's: it stays on disk, out of every later send, and is counted apart.</summary>
    [Fact]
    public void ARefusedThought_IsKept_ButNeverSentAgain()
    {
        var refused = _queue.Add("Email the landlord", NineAm)!;
        _queue.Add("Buy stamps", NineAm.AddSeconds(1));

        _queue.MarkRefused(refused);

        var reopened = new CaptureQueue(_directory);
        Assert.Equal(["Buy stamps"], reopened.Pending(10).Select(p => p.Text));
        Assert.Equal(new CaptureSummary(1, 1), reopened.Summary());
        Assert.Equal(2, Directory.GetFiles(_directory, "*.json").Length);
    }

    /// <summary>The outbox deletes an unreadable file; this queue may not — it could be the only copy of a thought.</summary>
    [Fact]
    public void AnUnreadableFile_IsSkipped_NotDeleted()
    {
        _queue.Add("Email the landlord", NineAm);
        var broken = Path.Combine(_directory, "20261108T085959000-broken.json");
        File.WriteAllText(broken, "{ not json");

        Assert.Equal(["Email the landlord"], _queue.Pending(10).Select(p => p.Text));
        Assert.True(File.Exists(broken));
    }

    [Fact]
    public void Pending_IsBounded()
    {
        for (var i = 0; i < 5; i++) _queue.Add($"Thought {i}", NineAm.AddSeconds(i));

        Assert.Equal(["Thought 0", "Thought 1"], _queue.Pending(max: 2).Select(p => p.Text));
    }

    /// <summary>The phone's table: only a 2xx removes, a malformed text is kept as refused, and everything else waits.</summary>
    [Theory]
    [InlineData(HttpStatusCode.OK, CaptureSendResult.Delivered)]
    [InlineData(HttpStatusCode.BadRequest, CaptureSendResult.Refused)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, CaptureSendResult.Refused)]
    [InlineData(HttpStatusCode.UnprocessableEntity, CaptureSendResult.Refused)]
    [InlineData(HttpStatusCode.NotFound, CaptureSendResult.NotSupported)]
    [InlineData(HttpStatusCode.Conflict, CaptureSendResult.NoPriority)]
    [InlineData(HttpStatusCode.Unauthorized, CaptureSendResult.Later)]
    [InlineData(HttpStatusCode.Forbidden, CaptureSendResult.Later)]
    [InlineData(HttpStatusCode.TooManyRequests, CaptureSendResult.Later)]
    [InlineData(HttpStatusCode.InternalServerError, CaptureSendResult.Later)]
    [InlineData(HttpStatusCode.BadGateway, CaptureSendResult.Later)]
    public void Classify_FollowsThePhonesRules(HttpStatusCode status, CaptureSendResult expected) =>
        Assert.Equal(expected, CaptureQueue.Classify(status));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
