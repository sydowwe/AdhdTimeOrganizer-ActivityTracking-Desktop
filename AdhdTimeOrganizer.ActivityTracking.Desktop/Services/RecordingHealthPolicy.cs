namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

public enum RecordingProblem
{
    /// <summary>Every poll has failed for <see cref="RecordingHealthPolicy.FailingAfter"/>: nothing on screen is being read.</summary>
    PollFailing,

    /// <summary>
    /// The poll loop has not polled for <see cref="RecordingHealthPolicy.SilentAfter"/> though it should be running, and has
    /// not said why — the dead loop CLAUDE.md records, which once ended tracking without a word.
    /// </summary>
    PollStopped,

    /// <summary>The poll loop sees recordable windows, yet no minute has been stored for <see cref="RecordingHealthPolicy.UnrecordedAfter"/>.</summary>
    NothingStored
}

/// <param name="IsRunning">The poll loop should be running: started, not stopped.</param>
/// <param name="IsPaused">A pause is in force.</param>
/// <param name="IsLocked">The session is locked: a poll records nothing, rightly.</param>
/// <param name="IdleFor">
/// Time since the last keyboard or mouse input, read by the tray's own timer (<c>Win32.GetIdleTime</c>) — never through the poll
/// loop, whose health is the question.
/// </param>
/// <param name="PollFailingSinceUtc">The first failure of the current run of failed passes; null once a poll completes.</param>
/// <param name="LastPollUtc">The last poll that completed, or the loop's start before its first; null when it never started.</param>
/// <param name="UnrecordedFor">
/// Time the poll loop credited to a window it may record (anything a local ignore rule or a dropped private window leaves) since
/// the last window was queued. Steady state is about two minutes: the minute itself and its hold-back.
/// </param>
public sealed record RecordingSnapshot(
    bool IsRunning, bool IsPaused, bool IsLocked, TimeSpan IdleFor, DateTime? PollFailingSinceUtc, DateTime? LastPollUtc,
    TimeSpan UnrecordedFor);

/// <summary>What a notice says. Durations only.</summary>
/// <param name="For">How long it has lasted: failing for, silent for, or recordable time without a stored minute.</param>
public sealed record RecordingNotice(RecordingProblem Problem, TimeSpan For);

/// <param name="Show">A notice to show now; it replaces one still in the notification centre.</param>
/// <param name="Withdraw">The notice still out is out of date. Never set together with <paramref name="Show"/>.</param>
public sealed record RecordingHealthChange(RecordingNotice? Show, bool Withdraw);

/// <summary>
/// Decides when to say that <b>recording itself</b> is broken (prompt 25). <see cref="UploadHealthPolicy"/> watches the send side
/// only; a poll loop failing on every pass showed nothing but "Error:" in the tooltip. Pure — no clock, no Win32, no toasts: the
/// tray's timer passes the time and a snapshot, so it runs even when the poll loop does not.
/// </summary>
/// <remarks>
/// <para>
/// Three conditions, one notice at a time, in this order:
/// <list type="bullet">
/// <item><see cref="RecordingProblem.PollFailing"/> — the poll has thrown on every pass for <see cref="FailingAfter"/> (T).</item>
/// <item><see cref="RecordingProblem.PollStopped"/> — no poll has completed for <see cref="SilentAfter"/>, with no failure to
/// say why, and that has held for <see cref="SilentGrace"/>: a PC waking from sleep finds the last poll hours old until the loop's
/// next pass.</item>
/// <item><see cref="RecordingProblem.NothingStored"/> — <see cref="UnrecordedAfter"/> (U) of recordable time and not one window
/// queued. It counts only what the poll loop was allowed to record, so a user whose every window their own local rules ignore
/// never trips it (decided 2026-11-23); server rules drop windows after they are queued, so they do not matter here.</item>
/// </list>
/// T and U are the owner's (10 min and 30 min, decided 2026-11-23).
/// </para>
/// <para>
/// <b>Who is told.</b> Nothing while paused (the loop is stopped on purpose) or locked, and a notice is shown only to someone at
/// the computer — input within <see cref="PresentWithin"/>, as the pause reminder waits — since a toast to an empty desk is buried.
/// The conditions themselves keep counting while nobody is there.
/// </para>
/// <para>
/// <b>Not nagging</b>, the upload notices' rules: once per episode, again at most every <see cref="RepeatEvery"/> while it
/// lasts; a condition that clears withdraws its toast. The same switch (<c>UploadNoticesEnabled</c>): off shows nothing and
/// withdraws what is out. State in memory: after a restart a still-broken loop is noticed again from scratch.
/// </para>
/// </remarks>
public sealed class RecordingHealthPolicy
{
    internal static readonly TimeSpan FailingAfter = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan SilentAfter = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan SilentGrace = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan UnrecordedAfter = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan PresentWithin = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan RepeatEvery = TimeSpan.FromHours(24);

    private DateTime? _silentSince;
    private RecordingProblem? _shown;
    private DateTime _shownUtc;

    public RecordingHealthChange Evaluate(DateTime nowUtc, RecordingSnapshot snapshot, bool enabled)
    {
        var watching = snapshot.IsRunning && !snapshot.IsPaused;

        var failingFor = watching && snapshot.PollFailingSinceUtc is { } failingSince ? nowUtc - failingSince : TimeSpan.Zero;
        var failing = failingFor >= FailingAfter;

        // Silent: no poll, and no failure that says why. A locked session still polls, so the lock doesn't excuse silence —
        // but the grace restarts after it, like after a sleep.
        var silentFor = watching && snapshot.PollFailingSinceUtc is null && snapshot.LastPollUtc is { } lastPoll
            ? nowUtc - lastPoll
            : TimeSpan.Zero;
        if (silentFor >= SilentAfter && !snapshot.IsLocked) _silentSince ??= nowUtc;
        else _silentSince = null;
        var stopped = _silentSince is { } since && nowUtc - since >= SilentGrace;

        var nothingStored = watching && snapshot.UnrecordedFor >= UnrecordedAfter;

        RecordingProblem? current =
            failing ? RecordingProblem.PollFailing
            : stopped ? RecordingProblem.PollStopped
            : nothingStored ? RecordingProblem.NothingStored
            : null;

        var withdraw = _shown is { } shown && (!enabled || shown switch
        {
            RecordingProblem.PollFailing => !failing,
            RecordingProblem.PollStopped => !stopped,
            _ => !nothingStored
        });
        if (withdraw) _shown = null;

        var present = !snapshot.IsLocked && snapshot.IdleFor <= PresentWithin;
        RecordingNotice? show = null;
        if (enabled && present && current is { } problem && (_shown != problem || nowUtc - _shownUtc >= RepeatEvery))
        {
            _shown = problem;
            _shownUtc = nowUtc;
            show = new RecordingNotice(problem, problem switch
            {
                RecordingProblem.PollFailing => failingFor,
                RecordingProblem.PollStopped => silentFor,
                _ => snapshot.UnrecordedFor
            });
        }

        return new RecordingHealthChange(show, withdraw && show is null);
    }
}
