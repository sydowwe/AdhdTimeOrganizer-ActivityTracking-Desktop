using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// What one poll saw. <see cref="IsPresent"/> is the user being there at all — input, or sound in the foreground —
/// which an ignored foreground window doesn't change; <see cref="Foreground"/> is null then.
/// </summary>
/// <param name="IsPassive">Present only because the foreground window is playing sound: no input for the idle threshold.</param>
/// <param name="IdleTime">Time since the last keyboard or mouse input.</param>
public sealed record PollResult(
    List<Observation> Observations,
    bool IsPresent,
    ActivitySample? Foreground,
    bool IsPassive = false,
    bool IsLocked = false,
    TimeSpan IdleTime = default)
{
    public static PollResult Locked { get; } = new([], false, null, IsLocked: true);
}

/// <summary>
/// What is on screen as the tracker credits it: the observations that hold from one change to the next. Pure, like
/// <see cref="ActivityRecorder"/> — the poll loop feeds it polls and foreground changes, and credits each span with
/// <see cref="TakeForSpan"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A switch</b> is the window in front becoming a different entry — another window, or another title of the same
/// one (after normalization, so a ticking unread counter is not one). Coming back from being away, or from a locked
/// screen, to the window that was in front before is not a switch. A window that may not be recorded (an ignore rule,
/// a dropped private window) still counts as something in front: switching to it and back is a switch back.
/// </para>
/// <para>
/// <b>Idle backdating.</b> Idleness is only noticed once there has been no input for the whole threshold, and every
/// second of that was credited as if the user were there. The first poll that finds the user gone answers the instant
/// to take back from: the last input, or the last poll that found them present by sound, whichever is later — passive
/// presence was real presence and stays credited.
/// </para>
/// </remarks>
public sealed class ScreenState
{
    /// <summary>The key of "a window is in front, but not one that is recorded".</summary>
    private const string NothingRecordedInFront = "";

    private List<Observation> _observations = [];
    private string? _foregroundKey;
    private bool _wasPresent;
    private DateTime? _lastPassiveUtc;

    public IReadOnlyList<Observation> Observations => _observations;

    /// <summary>
    /// The observations for the span that just ended. A switch is counted for the span it begins only, so its flag is
    /// cleared here for every span after it.
    /// </summary>
    public IReadOnlyList<Observation> TakeForSpan()
    {
        var current = _observations;
        if (current.Any(o => o.IsSwitch))
            _observations = current.Select(o => o.IsSwitch ? o with { IsSwitch = false } : o).ToList();
        return current;
    }

    /// <summary>
    /// The window in front changed between polls, with the user there. <paramref name="foreground"/> is null when it
    /// may not be recorded. The background stays as the last poll saw it, less the process that just came to the front:
    /// a process is observed once at a time, which is what keeps an entry within 60 seconds a minute.
    /// </summary>
    public void ForegroundChanged(Observation? foreground)
    {
        var background = _observations
            .Where(o => o.IsBackground && (foreground is null || ProcessKey(o.Sample) != ProcessKey(foreground.Sample)));

        _observations = foreground is null ? [.. background] : [MarkSwitch(foreground), .. background];
        if (foreground is null) _foregroundKey = NothingRecordedInFront;
        _wasPresent = true;
    }

    /// <summary>
    /// Adopts a full poll. Returns the instant to take credited time back from when this is the first poll to find the
    /// user gone, else null.
    /// </summary>
    public DateTime? Polled(DateTime nowUtc, PollResult poll)
    {
        DateTime? retractFrom = null;
        if (_wasPresent && !poll.IsPresent && !poll.IsLocked)
        {
            var lastInput = nowUtc - poll.IdleTime;
            retractFrom = _lastPassiveUtc is { } passive && passive > lastInput ? passive : lastInput;
        }

        _wasPresent = poll.IsPresent;
        if (poll.IsPassive) _lastPassiveUtc = nowUtc;

        _observations = poll.Observations.Select(o => o.IsBackground ? o : MarkSwitch(o)).ToList();

        // Present with nothing recorded in front: an ignored or private window, or none at all. Away or locked keeps the
        // last key, so coming back to the same window is not a switch.
        if (poll.IsPresent && _observations.All(o => o.IsBackground))
            _foregroundKey = NothingRecordedInFront;

        return retractFrom;
    }

    private Observation MarkSwitch(Observation foreground)
    {
        var key = ActivityRecorder.EntryKey(foreground.Sample);
        var isSwitch = _foregroundKey is not null && key != _foregroundKey;
        _foregroundKey = key;
        return isSwitch ? foreground with { IsSwitch = true } : foreground;
    }

    internal static string ProcessKey(ActivitySample sample) => $"{sample.ProcessName}|{sample.ExecutablePath}";
}
