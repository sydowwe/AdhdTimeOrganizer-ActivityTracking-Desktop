namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// When the poll loop's next poll is due, in <see cref="System.Diagnostics.Stopwatch"/> timestamps. Pure: the loop passes
/// the time in.
/// </summary>
/// <remarks>
/// <b>Taking a due poll moves the schedule on, before the poll runs.</b> It used to move on only after a poll that
/// returned, so a poll that threw every time left the next poll in the past: the loop polled again at once, logged
/// "Poll failed" on every pass and held a core for as long as the failure lasted. A failed poll now waits its interval
/// like any other.
/// </remarks>
internal sealed class PollSchedule(long firstAt, long intervalTicks)
{
    /// <summary>The timestamp the next poll is due at.</summary>
    public long Next { get; private set; } = firstAt;

    /// <summary>
    /// True when a poll is due at <paramref name="now"/>, and the next is then scheduled whatever the poll does. After a
    /// sleep or a hang the next one is an interval from now, not a burst catching up.
    /// </summary>
    public bool TakeDue(long now)
    {
        if (now < Next) return false;

        Next += intervalTicks;
        if (Next <= now) Next = now + intervalTicks;
        return true;
    }
}
