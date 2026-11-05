namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

public enum UploadProblem
{
    /// <summary>No session, and tracked time is waiting for one.</summary>
    SignedOut,

    /// <summary>Signed in, but the oldest unsent minute is a day old: offline, a failing server, rules never fetched.</summary>
    Stalled,

    /// <summary>The server refused windows for their content. Each one is a lost minute.</summary>
    Dropped
}

/// <param name="HasSession">A refresh token is saved.</param>
/// <param name="IsPaused">A pause is in force.</param>
/// <param name="PendingWindows">Minutes waiting in the outbox.</param>
/// <param name="OldestPendingUtc">When the oldest of them began; null with an empty outbox.</param>
/// <param name="RejectedWindows">Windows the server has refused since the tracker started.</param>
public sealed record UploadSnapshot(
    bool HasSession, bool IsPaused, int PendingWindows, DateTime? OldestPendingUtc, int RejectedWindows);

/// <summary>What a notice says. Numbers only — nothing the user wrote.</summary>
/// <param name="StalledFor">For <see cref="UploadProblem.Stalled"/>: how old the oldest unsent minute is.</param>
/// <param name="Dropped">For <see cref="UploadProblem.Dropped"/>: windows refused since the last notice.</param>
public sealed record UploadNotice(UploadProblem Problem, int PendingWindows, TimeSpan StalledFor, int Dropped);

/// <param name="Show">A notice to show now; it replaces one still in the notification centre.</param>
/// <param name="Withdraw">The notice still out is out of date. Never set together with <paramref name="Show"/>.</param>
public sealed record UploadHealthChange(UploadNotice? Show, bool Withdraw);

/// <summary>
/// Decides when to tell the user that what they track isn't arriving. Pure — no clock, no toasts — like
/// <see cref="DriftPolicy"/>: the caller passes the time and a snapshot of the tracker.
/// </summary>
/// <remarks>
/// <para>
/// Recording goes on whatever happens, so a stall shows only in the tray's tooltip, and the server's own
/// "tracker went silent" push needs 48 hours and a browser that is listening. Three conditions, at most one notice at
/// a time (a toast replaces the last one), in this order: <see cref="UploadProblem.SignedOut"/>, then
/// <see cref="UploadProblem.Stalled"/>, then <see cref="UploadProblem.Dropped"/>. A day signed out is also a day
/// stalled; one notice names the cause. A lower one that is held back by a higher one comes when that resolves.
/// </para>
/// <para>
/// <b>Signed out</b> counts once <see cref="SignedOutGrace"/> has passed with minutes waiting: signing out on purpose
/// and cancelling the form is not a problem to report the moment it happens, and a tracker that never recorded
/// anything has nothing to lose. <b>Stalled</b> is measured by the oldest unsent minute, not by the last success: the
/// outbox is on disk and survives a restart, "last sent" does not. It counts once that minute has stayed overdue for
/// <see cref="StallGrace"/>: a PC waking from a long sleep finds a minute a day old that nothing has tried to send yet,
/// and the send loop and the network need a moment. A drop notice waits through that grace too. <b>Dropped</b> is an
/// event, not a state, so it is counted in batches of <see cref="DroppedBatch"/> since the last notice and its toast is
/// not withdrawn when things look better; it leaves when the tracker exits.
/// </para>
/// <para>
/// <b>Not nagging.</b> A notice is shown once per episode and again at most every <see cref="RepeatEvery"/> while it
/// lasts; a condition that resolves withdraws its toast and starts a new episode if it returns. Nothing is shown while
/// paused or with the switch off (turning it off withdraws what is out). The state is in memory: a restart while still
/// stalled shows the notice once more.
/// </para>
/// </remarks>
public sealed class UploadHealthPolicy
{
    internal static readonly TimeSpan SignedOutGrace = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan StallAfter = TimeSpan.FromHours(24);
    internal static readonly TimeSpan StallGrace = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan RepeatEvery = TimeSpan.FromHours(24);
    internal const int DroppedBatch = 3;

    private DateTime? _signedOutSince;
    private DateTime? _overdueSince;
    private UploadProblem? _shown;
    private DateTime _shownUtc;
    private int _droppedAtLastNotice;

    public UploadHealthChange Evaluate(DateTime nowUtc, UploadSnapshot snapshot, bool enabled)
    {
        // The clocks run whatever the switches say, so a grace that began during a pause isn't restarted by it.
        if (snapshot.HasSession || snapshot.PendingWindows == 0) _signedOutSince = null;
        else _signedOutSince ??= nowUtc;

        var stalledFor = snapshot.OldestPendingUtc is { } oldest ? nowUtc - oldest : TimeSpan.Zero;
        var overdue = snapshot.HasSession && stalledFor >= StallAfter;
        if (overdue) _overdueSince ??= nowUtc;
        else _overdueSince = null;

        var signedOut = _signedOutSince is { } since && nowUtc - since >= SignedOutGrace;
        var stalled = _overdueSince is { } overdueSince && nowUtc - overdueSince >= StallGrace;
        var dropped = Math.Max(0, snapshot.RejectedWindows - _droppedAtLastNotice);

        UploadProblem? current =
            signedOut ? UploadProblem.SignedOut
            : stalled ? UploadProblem.Stalled
            : dropped >= DroppedBatch && !overdue ? UploadProblem.Dropped
            : null;

        var withdraw = _shown is { } shown && (!enabled || shown switch
        {
            UploadProblem.SignedOut => !signedOut,
            UploadProblem.Stalled => !stalled,
            _ => false
        });
        if (withdraw) _shown = null;

        UploadNotice? show = null;
        if (enabled && !snapshot.IsPaused && current is { } problem && (_shown != problem || nowUtc - _shownUtc >= RepeatEvery))
        {
            _shown = problem;
            _shownUtc = nowUtc;
            if (problem == UploadProblem.Dropped) _droppedAtLastNotice = snapshot.RejectedWindows;

            show = new UploadNotice(problem, snapshot.PendingWindows,
                problem == UploadProblem.Stalled ? stalledFor : TimeSpan.Zero,
                problem == UploadProblem.Dropped ? dropped : 0);
        }

        return new UploadHealthChange(show, withdraw && show is null);
    }
}
