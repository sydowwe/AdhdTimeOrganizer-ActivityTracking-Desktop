using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <param name="Pending">Thoughts on disk still to deliver.</param>
/// <param name="Refused">Thoughts the server refused; kept on disk, never sent again.</param>
/// <param name="Held">Why the queue is waiting longer than a pass, if it is: <see cref="CaptureSendResult.NotSupported"/> or <see cref="CaptureSendResult.NoPriority"/>.</param>
public sealed record CaptureStatus(int Pending, int Refused, CaptureSendResult? Held);

/// <summary>
/// The send loop's capture tick: delivers parked thoughts oldest first, each through <c>POST /api/tracker/capture</c>, and
/// stops at the first that has to wait — offline, a server error, signed out — since the rest would wait the same way.
/// Counts and ids in the log, never the text.
/// </summary>
/// <remarks>
/// A server from before the route (404) and an account with no task priority (409) are true of every thought, not of one,
/// so the whole queue is left for <see cref="CaptureQueue.HoldFor"/> rather than asking every ten seconds. A new capture
/// (<see cref="Wake"/>) asks at once anyway: the user may have just deployed the server or added a priority.
/// </remarks>
public sealed class CaptureDelivery(CaptureQueue queue, Func<string, CancellationToken, Task<CaptureSendResult>> send)
{
    private const int PerPass = 20;

    private readonly ILogger _log = Log.ForContext<CaptureDelivery>();

    // Written on the send loop; Wake comes from the UI thread and the status read from the tray's timer.
    private readonly Lock _gate = new();
    private DateTime _heldUntilUtc = DateTime.MinValue;
    private CaptureSendResult? _held;

    /// <summary>A thought was just parked: the next pass sends, held or not.</summary>
    public void Wake()
    {
        lock (_gate) _heldUntilUtc = DateTime.MinValue;
    }

    public CaptureStatus Status()
    {
        var summary = queue.Summary();
        lock (_gate) return new CaptureStatus(summary.Pending, summary.Refused, summary.Pending > 0 ? _held : null);
    }

    public async Task TickAsync(DateTime nowUtc, CancellationToken ct)
    {
        lock (_gate)
            if (nowUtc < _heldUntilUtc) return;

        var pending = queue.Pending(PerPass);
        if (pending.Count == 0)
        {
            Hold(null, DateTime.MinValue);
            return;
        }

        foreach (var item in pending)
        {
            var result = await send(item.Text, ct);
            switch (result)
            {
                case CaptureSendResult.Delivered:
                    queue.Remove(item);
                    Hold(null, DateTime.MinValue);
                    _log.Information("Capture {CaptureId} delivered", item.Id);
                    continue;

                case CaptureSendResult.Refused:
                    queue.MarkRefused(item);
                    _log.Warning("Capture {CaptureId} refused by the server — kept on disk, not sent again", item.Id);
                    continue;

                case CaptureSendResult.NotSupported:
                case CaptureSendResult.NoPriority:
                    Hold(result, nowUtc + CaptureQueue.HoldFor);
                    _log.Information("Captures held for {Minutes} min: {Reason}", CaptureQueue.HoldFor.TotalMinutes, result);
                    return;

                default:
                    return;
            }
        }
    }

    private void Hold(CaptureSendResult? reason, DateTime untilUtc)
    {
        lock (_gate)
        {
            _held = reason;
            _heldUntilUtc = untilUtc;
        }
    }
}
