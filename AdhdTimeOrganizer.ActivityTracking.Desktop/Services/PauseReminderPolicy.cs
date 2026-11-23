namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <param name="PausedUntilUtc">The saved pause's end; <see cref="DateTime.MaxValue"/> for "until I resume", null when not paused.</param>
/// <param name="PausedSinceUtc">When recording stopped; null from a config written before it was saved.</param>
/// <param name="IdleFor">Since the last keyboard or mouse input on this machine.</param>
public sealed record PauseSnapshot(DateTime? PausedUntilUtc, DateTime? PausedSinceUtc, TimeSpan IdleFor);

/// <param name="Show">How long the pause has lasted, when a reminder is due now.</param>
/// <param name="Withdraw">The reminder still out is out of date: the pause ended or became a timed one, or the switch went off.</param>
public sealed record PauseReminderChange(TimeSpan? Show, bool Withdraw);

/// <summary>
/// Decides when to say "Tracking has been paused for 4 h". Pure — no clock, no Win32, no toasts — like
/// <see cref="UploadHealthPolicy"/>: the tray's timer passes the time and a snapshot, so it runs while the poll loop is stopped.
/// </summary>
/// <remarks>
/// <para>
/// Only a pause "until I resume" is reminded about: a timed one ends by itself. The first reminder comes once the pause is
/// <see cref="FirstAfter"/> old, then at most every <see cref="RepeatEvery"/>. Each waits for someone at the keyboard (input
/// within <see cref="PresentWithin"/>): a reminder to an empty desk — or a sleeping user — is buried in the notification
/// centre by morning. That is also why there is no night window.
/// </para>
/// <para>
/// The pause's start is saved with it (<c>AppConfig.PausedSinceUtc</c>), so its age outlives a restart. A config from before
/// that field has none, and the pause is counted from the first time this policy sees it. The reminder's own clock is in
/// memory; the tray's start-up balloon ("paused — double-click to resume") counts as a reminder (<see cref="Announced"/>), so
/// a restart doesn't add a toast to it.
/// </para>
/// </remarks>
public sealed class PauseReminderPolicy
{
    internal static readonly TimeSpan FirstAfter = TimeSpan.FromHours(3);
    internal static readonly TimeSpan RepeatEvery = TimeSpan.FromHours(6);
    internal static readonly TimeSpan PresentWithin = TimeSpan.FromMinutes(1);

    private DateTime? _since;     // the start of the open-ended pause being watched; null when there is none
    private DateTime? _remindedUtc; // the last reminder about it (or the start-up balloon)
    private bool _isOut;

    public PauseReminderChange Evaluate(DateTime nowUtc, PauseSnapshot snapshot, bool enabled)
    {
        var since = Track(nowUtc, snapshot);
        var withdraw = _isOut && (since is null || !enabled);
        if (withdraw) _isOut = false;

        if (since is not { } start || !enabled || snapshot.IdleFor >= PresentWithin)
            return new PauseReminderChange(null, withdraw);

        var due = _remindedUtc is { } reminded ? nowUtc - reminded >= RepeatEvery : nowUtc - start >= FirstAfter;
        if (!due) return new PauseReminderChange(null, withdraw);

        _remindedUtc = nowUtc;
        _isOut = true;
        return new PauseReminderChange(nowUtc - start, false);
    }

    /// <summary>The tray said the tracker is paused (the start-up balloon): the next reminder is <see cref="RepeatEvery"/> away.</summary>
    public void Announced(DateTime nowUtc, PauseSnapshot snapshot)
    {
        if (Track(nowUtc, snapshot) is not null) _remindedUtc = nowUtc;
    }

    /// <summary>The reminder was answered and has left the screen; the next one still keeps its distance.</summary>
    public void Answered() => _isOut = false;

    /// <summary>The start of the open-ended pause in force, or null. A new pause starts its reminders afresh.</summary>
    private DateTime? Track(DateTime nowUtc, PauseSnapshot snapshot)
    {
        if (snapshot.PausedUntilUtc != DateTime.MaxValue)
        {
            _since = null;
            _remindedUtc = null;
            return null;
        }

        var since = snapshot.PausedSinceUtc ?? _since ?? nowUtc;
        if (_since != since) _remindedUtc = null;
        _since = since;
        return since;
    }
}
