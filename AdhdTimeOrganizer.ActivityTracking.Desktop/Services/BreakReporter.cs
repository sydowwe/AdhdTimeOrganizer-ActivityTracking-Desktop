using System.Net;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>What one break report's send came to.</summary>
public enum BreakSendResult
{
    /// <summary>The server weighed it (2xx).</summary>
    Delivered,

    /// <summary>Offline, a 5xx, a throttle, signed out: kept, tried again after <see cref="BreakReporter.RetryAfter"/>.</summary>
    Later,

    /// <summary>404 (a server from before the route) or a refusal: dropped. The phone just doesn't hear of this break.</summary>
    Dropped
}

/// <summary>
/// Hands the server the break the user started (prompt 10), so the phone can say "break's over" when it is the one in use.
/// Only the <b>latest</b> report is kept — each is the whole break — and in memory only: a break is minutes long, and one
/// lost to a restart costs the phone's nudge, never a tracked minute. It never touches the heartbeat or the outbox, so a
/// server without the route costs nothing but one 404 per break.
/// </summary>
public sealed class BreakReporter : IDisposable
{
    /// <summary>How long a report that couldn't go waits before the next try.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A report is worth sending until two hours past the break's planned end: the server stops showing a break to the phone
    /// then (its <c>DesktopBreakRule.ShownFor</c>), so a later one would change nothing anyone reads.
    /// </summary>
    public static readonly TimeSpan WorthSendingFor = TimeSpan.FromHours(2);

    private readonly ILogger _log = Log.ForContext<BreakReporter>();
    private readonly Func<BreakReport, CancellationToken, Task<BreakSendResult>> _send;
    private readonly Func<DateTime> _utcNow;
    private readonly Lock _lock = new();
    private readonly System.Threading.Timer _retry;
    private readonly CancellationTokenSource _stop = new();

    private BreakReport? _pending;
    private bool _sending;

    /// <param name="send">One attempt; never throws but for cancellation (<c>ApiClient.SendBreakAsync</c>).</param>
    /// <param name="utcNow">The clock; tests pin it.</param>
    public BreakReporter(Func<BreakReport, CancellationToken, Task<BreakSendResult>> send, Func<DateTime>? utcNow = null)
    {
        _send = send;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _retry = new System.Threading.Timer(_ => _ = PumpAsync());
    }

    /// <summary>What a break report's status code means for it.</summary>
    public static BreakSendResult Classify(HttpStatusCode statusCode) => (int)statusCode switch
    {
        >= 200 and < 300 => BreakSendResult.Delivered,
        401 or 408 or 425 or 429 or >= 500 => BreakSendResult.Later,
        _ => BreakSendResult.Dropped
    };

    public static bool IsWorthSending(BreakReport report, DateTime utcNow) => utcNow < report.PlannedEndUtc + WorthSendingFor;

    /// <summary>The break changed: this report replaces any not yet sent, and goes now. Fire and forget.</summary>
    public void Report(BreakReport report)
    {
        lock (_lock) _pending = report;
        _ = PumpAsync();
    }

    /// <summary>Whether a report is still waiting to go.</summary>
    internal bool HasPending
    {
        get { lock (_lock) return _pending is not null; }
    }

    /// <summary>Sends what is pending, one at a time, until nothing is or it has to wait. Internal so tests drive the retry.</summary>
    internal async Task PumpAsync()
    {
        lock (_lock)
        {
            if (_sending) return; // the running pass picks the newer report up
            _sending = true;
        }

        // Every way out clears _sending under the same lock that decided to leave, so a report arriving in between is
        // either seen by this pass or starts its own.
        while (true)
        {
            BreakReport next;
            lock (_lock)
            {
                if (_pending is not null && !IsWorthSending(_pending, _utcNow()))
                {
                    _log.Information("Break report dropped: past the time the phone could use it");
                    _pending = null;
                }
                if (_pending is null)
                {
                    _sending = false;
                    return;
                }
                next = _pending;
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
                _log.Warning(ex, "Break report failed — will retry");
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
                if (result == BreakSendResult.Dropped) _log.Information("Break report not taken by the server");
                if (ReferenceEquals(_pending, next)) _pending = null; // a newer one may have come meanwhile
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
