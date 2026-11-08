using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// When a paused tracker tells the server it is still here. Pure: the send loop hands in the state and the clock.
/// </summary>
/// <remarks>
/// <para>
/// The server calls a tracker silent after 48 h without a report, and a pause reports nothing, so "Until I resume" used
/// to end in a "tracker went silent" push. A heartbeat with no entries moves the last-seen stamp and stores nothing.
/// <see cref="Every"/> matches the phone's empty sync, and leaves eight tries inside the 48 h.
/// </para>
/// <para>
/// <b>It is not a minute.</b> It never goes through the outbox and none of its outcomes reach the status: a refusal is
/// not a dropped window, and a keep-alive that got through is not a stored one. A server from before empty heartbeats
/// answers 400. That is an answer, so it waits the full interval like a success.
/// </para>
/// </remarks>
public sealed class PauseKeepAlive
{
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    /// <summary>After no answer: offline or a 5xx. Short, because the PC may only be on for an hour of the 48.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private DateTime _nextTryUtc = DateTime.MinValue;

    /// <param name="lastStoredUtc">When a window was last stored: a backlog sent during the pause already says the tracker is alive.</param>
    public bool IsDue(bool paused, bool hasSession, DateTime? lastStoredUtc, DateTime utcNow) =>
        paused && hasSession && utcNow >= _nextTryUtc && (lastStoredUtc is not { } stored || utcNow - stored >= Every);

    public void Answered(SendResult result, DateTime utcNow) =>
        _nextTryUtc = utcNow + (result is SendResult.Sent or SendResult.Rejected ? Every : RetryAfter);

    /// <summary>No entries, on a whole minute: the heartbeat refuses any other start.</summary>
    public static ActivityWindow Window(DateTime utcNow) => new()
    {
        WindowStart = new DateTime(utcNow.Ticks - utcNow.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc),
        Entries = []
    };
}
