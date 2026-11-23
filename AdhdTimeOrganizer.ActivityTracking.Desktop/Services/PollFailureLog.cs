namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>What to write for one failed poll-loop pass.</summary>
internal enum PollLogEntry
{
    /// <summary>Nothing: the same failure, already in the log, and the next summary not due.</summary>
    None,

    /// <summary>The exception in full, with its stack: the first of a run, or a failure unlike the last.</summary>
    Full,

    /// <summary>One line: the same failure is still happening, with the count and how long.</summary>
    StillFailing
}

/// <summary>
/// Keeps a poll that fails on every pass from filling the log. Pure — the loop passes the time and the exception's message.
/// </summary>
/// <remarks>
/// A poll failing every 5 s used to log the exception with its stack each time — some 17,000 entries a day, and each log
/// file the diagnostics tail reads would be nothing else. Now: the first failure in full; the same message again only as a
/// line every <see cref="SummaryEvery"/> ("still failing, 120 times in 10 min"); a different message in full; and one line
/// when a poll completes again. A run is the same run as <c>TrackerStatus.PollFailingSinceUtc</c>'s: only a completed
/// poll ends it. One per poll-loop session, touched by that loop's thread only.
/// </remarks>
internal sealed class PollFailureLog
{
    internal static readonly TimeSpan SummaryEvery = TimeSpan.FromMinutes(10);

    private string? _message;
    private DateTime _runStartUtc;
    private DateTime _lastWrittenUtc;
    private int _failures;

    /// <summary>A pass failed with <paramref name="message"/>. <c>Failures</c> and <c>For</c> are the run's so far.</summary>
    public (PollLogEntry Entry, int Failures, TimeSpan For) Failed(DateTime nowUtc, string message)
    {
        if (_failures == 0) _runStartUtc = nowUtc;
        _failures++;

        PollLogEntry entry;
        if (!string.Equals(message, _message, StringComparison.Ordinal))
        {
            _message = message;
            entry = PollLogEntry.Full;
        }
        else
        {
            // A clock set back must not silence the run for good.
            entry = nowUtc - _lastWrittenUtc >= SummaryEvery || nowUtc < _lastWrittenUtc ? PollLogEntry.StillFailing : PollLogEntry.None;
        }

        if (entry != PollLogEntry.None) _lastWrittenUtc = nowUtc;
        return (entry, _failures, nowUtc - _runStartUtc);
    }

    /// <summary>A poll completed. The run that ended — to say so once — or null when nothing was failing.</summary>
    public (int Failures, TimeSpan For)? Recovered(DateTime nowUtc)
    {
        if (_failures == 0) return null;

        var ended = (_failures, nowUtc - _runStartUtc);
        _failures = 0;
        _message = null;
        return ended;
    }
}
