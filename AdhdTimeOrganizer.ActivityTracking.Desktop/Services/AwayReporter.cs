using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// Hands the server each answered absence (prompt 23, <c>POST /api/tracker/desktop/away</c>). The moment of return is often the
/// moment a woken laptop is still finding its Wi-Fi, so an answer that can't go now waits, in memory, and is tried again
/// every <see cref="RetryAfter"/>, oldest first. In memory only: an answer is one click, and one lost to a restart costs a
/// History row the web app can still add, never a tracked minute. It never touches the heartbeat or the outbox.
/// </summary>
/// <remarks>
/// What each answer's status code means is <see cref="BreakReporter.Classify"/>'s rule, unchanged: a 2xx is delivered (a
/// retry of a delivered answer adds nothing on the server); offline, 401, 408, 425, 429 and 5xx wait; a 404 (a server from
/// before the route), 400 (refused as implausible) or 403 drops it.
/// </remarks>
public sealed class AwayReporter : IDisposable
{
    /// <summary>How long an answer that couldn't go waits before the next try.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>An answer is let go a day after the absence ended; the server refuses one two days old anyway.</summary>
    public static readonly TimeSpan WorthSendingFor = TimeSpan.FromDays(1);

    private readonly ILogger _log = Log.ForContext<AwayReporter>();
    private readonly Func<AwayAnswered, CancellationToken, Task<BreakSendResult>> _send;
    private readonly Func<DateTime> _utcNow;
    private readonly Lock _lock = new();
    private readonly System.Threading.Timer _retry;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<AwayAnswered> _pending = [];

    private bool _sending;

    /// <param name="send">One attempt; never throws but for cancellation (<c>ApiClient.SendAwayAsync</c>).</param>
    /// <param name="utcNow">The clock; tests pin it.</param>
    public AwayReporter(Func<AwayAnswered, CancellationToken, Task<BreakSendResult>> send, Func<DateTime>? utcNow = null)
    {
        _send = send;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _retry = new System.Threading.Timer(_ => _ = PumpAsync());
    }

    public static bool IsWorthSending(AwayAnswered answered, DateTime utcNow) => utcNow < answered.ToUtc + WorthSendingFor;

    /// <summary>An absence was answered: it goes now, after any still waiting. Fire and forget.</summary>
    public void Report(AwayAnswered answered)
    {
        lock (_lock) _pending.Add(answered);
        _ = PumpAsync();
    }

    /// <summary>How many answers are still waiting to go.</summary>
    internal int PendingCount
    {
        get { lock (_lock) return _pending.Count; }
    }

    /// <summary>Sends what is pending, oldest first, until nothing is or it has to wait. Internal so tests drive the retry.</summary>
    internal async Task PumpAsync()
    {
        lock (_lock)
        {
            if (_sending) return; // the running pass picks a new answer up
            _sending = true;
        }

        while (true)
        {
            AwayAnswered next;
            lock (_lock)
            {
                var stale = _pending.RemoveAll(a => !IsWorthSending(a, _utcNow()));
                if (stale > 0) _log.Information("{Count} away answer(s) dropped: too old to send", stale);
                if (_pending.Count == 0)
                {
                    _sending = false;
                    return;
                }
                next = _pending[0];
            }

            BreakSendResult result;
            try
            {
                result = await _send(next, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                lock (_lock) _sending = false;
                return;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Away answer failed — will retry");
                result = BreakSendResult.Later;
            }

            lock (_lock)
            {
                if (result == BreakSendResult.Later)
                {
                    ScheduleRetry();
                    _sending = false;
                    return;
                }
                if (result == BreakSendResult.Dropped) _log.Information("Away answer not taken by the server");
                _pending.Remove(next);
            }
        }
    }

    private void ScheduleRetry()
    {
        try
        {
            _retry.Change(RetryAfter, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) { /* exiting */ }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _retry.Dispose();
        _stop.Dispose();
    }
}
