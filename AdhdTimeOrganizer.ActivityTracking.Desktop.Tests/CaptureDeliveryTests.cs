using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The send loop's capture tick, against a scripted send: what each answer does to the files, and when the queue waits.
/// The wire itself — route, body, a real token — is <see cref="SendLoopTests"/>'s.
/// </summary>
public sealed class CaptureDeliveryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "capture-delivery-" + Guid.NewGuid().ToString("N"));
    private readonly CaptureQueue _queue;
    private readonly List<string> _sent = [];
    private readonly Queue<CaptureSendResult> _answers = new();

    public CaptureDeliveryTests() => _queue = new CaptureQueue(_directory);

    [Fact]
    public async Task Delivered_Thoughts_LeaveTheQueue_OldestFirst()
    {
        Park("Email the landlord", "Buy stamps");
        var delivery = Delivery(CaptureSendResult.Delivered, CaptureSendResult.Delivered);

        await delivery.TickAsync(Now, CancellationToken.None);

        Assert.Equal(["Email the landlord", "Buy stamps"], _sent);
        Assert.Equal(new CaptureStatus(0, 0, null), delivery.Status());
    }

    /// <summary>Offline or signed out is true of the next one too: the pass stops, and nothing leaves the disk.</summary>
    [Theory]
    [InlineData(CaptureSendResult.Later)]
    [InlineData(CaptureSendResult.SignedOut)]
    public async Task AThoughtThatMustWait_StopsThePass_AndStays(CaptureSendResult answer)
    {
        Park("Email the landlord", "Buy stamps");
        var delivery = Delivery(answer);

        await delivery.TickAsync(Now, CancellationToken.None);

        Assert.Equal(["Email the landlord"], _sent);
        Assert.Equal(new CaptureStatus(2, 0, null), delivery.Status());
    }

    [Fact]
    public async Task ARefusedThought_IsSetAside_AndTheNextOneStillGoes()
    {
        Park("Email the landlord", "Buy stamps");
        var delivery = Delivery(CaptureSendResult.Refused, CaptureSendResult.Delivered);

        await delivery.TickAsync(Now, CancellationToken.None);

        Assert.Equal(["Email the landlord", "Buy stamps"], _sent);
        Assert.Equal(new CaptureStatus(0, 1, null), delivery.Status());
    }

    /// <summary>
    /// An old server (404) or an account with no task priority (409) is true of every thought: the queue waits
    /// <see cref="CaptureQueue.HoldFor"/> instead of asking every ten seconds, says why, and a new capture asks at once.
    /// </summary>
    [Theory]
    [InlineData(CaptureSendResult.NotSupported)]
    [InlineData(CaptureSendResult.NoPriority)]
    public async Task AServerThatCantTakeThem_HoldsTheQueue_UntilANewCaptureOrTheHoldIsOver(CaptureSendResult answer)
    {
        Park("Email the landlord");
        var delivery = Delivery(answer, CaptureSendResult.Delivered);

        await delivery.TickAsync(Now, CancellationToken.None);
        await delivery.TickAsync(Now.AddMinutes(10), CancellationToken.None);

        Assert.Single(_sent);
        Assert.Equal(new CaptureStatus(1, 0, answer), delivery.Status());

        await delivery.TickAsync(Now + CaptureQueue.HoldFor, CancellationToken.None);

        Assert.Equal(2, _sent.Count);
        Assert.Equal(new CaptureStatus(0, 0, null), delivery.Status());
    }

    [Fact]
    public async Task ANewCapture_EndsTheHold()
    {
        Park("Email the landlord");
        var delivery = Delivery(CaptureSendResult.NotSupported, CaptureSendResult.Delivered, CaptureSendResult.Delivered);
        await delivery.TickAsync(Now, CancellationToken.None);

        Park("Buy stamps");
        delivery.Wake();
        await delivery.TickAsync(Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(["Email the landlord", "Email the landlord", "Buy stamps"], _sent);
        Assert.Equal(new CaptureStatus(0, 0, null), delivery.Status());
    }

    /// <summary>A second apart, across calls too: the queue's order is the order they were parked in.</summary>
    private void Park(params string[] texts)
    {
        foreach (var text in texts)
        {
            _parkedAt = _parkedAt.AddSeconds(1);
            _queue.Add(text, _parkedAt);
        }
    }

    private DateTime _parkedAt = Now.AddMinutes(-5);

    private CaptureDelivery Delivery(params CaptureSendResult[] answers)
    {
        foreach (var answer in answers) _answers.Enqueue(answer);
        return new CaptureDelivery(_queue, (text, _) =>
        {
            _sent.Add(text);
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : CaptureSendResult.Later);
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
