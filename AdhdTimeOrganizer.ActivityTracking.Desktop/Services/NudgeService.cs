using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Native;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// Hyperfocus and break nudges: feeds each poll to <see cref="HyperfocusPolicy"/> and shows what it asks
/// for. Client-side only — nothing here reaches the server.
/// </summary>
/// <remarks>
/// <para>
/// Fed by the tracker's poll loop, so pausing tracking pauses nudges too: a paused tracker isn't watching,
/// and the policy counts the paused time as a break if it was long enough. The break timer has its own
/// timer, so the "break's over" nudge comes even while paused.
/// </para>
/// <para>
/// A call with no keyboard input is not a break: while an app holds the microphone the user counts as
/// present here. That is for nudges only — what the tracker records and sends doesn't change.
/// </para>
/// <para>
/// Threads: polls arrive on the poll loop's thread, the break timer on the thread pool, button presses on
/// the thread pool and are posted to the UI thread (they can change and save the config, which the Settings
/// form edits there). Everything that touches the policy holds <see cref="_lock"/>. No WinForms objects here.
/// </para>
/// </remarks>
public sealed class NudgeService : IDisposable
{
    private static readonly TimeSpan MicrophoneCheckEvery = TimeSpan.FromSeconds(10);

    private readonly AppConfig _config;
    private readonly SynchronizationContext? _uiContext;
    private readonly NudgeNotifier _notifier;
    private readonly ILogger _log = Log.ForContext<NudgeService>();

    private readonly Lock _lock = new();
    private readonly HyperfocusPolicy _policy = new(TimeSpan.FromSeconds(10));
    private readonly System.Threading.Timer _breakTimer;

    private DateTime _microphoneCheckedUtc = DateTime.MinValue;
    private bool _isMicrophoneInUse;
    private string? _quietReason;

    /// <param name="uiContext">Where button presses are handled; the tray's.</param>
    public NudgeService(AppConfig config, SynchronizationContext? uiContext)
    {
        _config = config;
        _uiContext = uiContext;
        _breakTimer = new System.Threading.Timer(_ => Evaluate(DateTime.UtcNow));
        _notifier = new NudgeNotifier(OnActivated);
    }

    /// <summary>
    /// One poll, from the poll loop. <paramref name="isPresent"/> is the tracker's own verdict (input, or
    /// sound in the foreground); <paramref name="foreground"/> is null when away or on an ignored window.
    /// </summary>
    public void OnPoll(DateTime nowUtc, TimeSpan maxObservedGap, bool isLocked, bool isPresent, ActivitySample? foreground)
    {
        try
        {
            var settings = NudgeSettings.From(_config);
            lock (_lock)
            {
                var present = isPresent || (!isLocked && IsMicrophoneInUse(nowUtc));
                var app = present ? foreground : null;

                _policy.MaxObservedGap = maxObservedGap;
                _policy.Record(nowUtc, present,
                    app is null ? null : $"{app.ProcessName}|{app.ExecutablePath}",
                    app is null ? null : app.ProductName ?? app.ProcessName,
                    settings);
            }

            Evaluate(nowUtc);
        }
        catch (Exception ex)
        {
            // A failed nudge must never cost a tracked minute: the caller's catch would report a poll error.
            _log.Error(ex, "Nudge evaluation failed");
        }
    }

    public NudgeStatus GetStatus()
    {
        lock (_lock) return _policy.GetStatus(DateTime.UtcNow);
    }

    /// <summary>Shows a nudge of <paramref name="kind"/> right away, with working buttons. For trying toasts by hand.</summary>
    public void ShowTest(NudgeKind kind)
    {
        Nudge nudge;
        lock (_lock) nudge = _policy.IssueForTest(kind, DateTime.UtcNow);
        _notifier.Show(nudge);
    }

    private void Evaluate(DateTime nowUtc)
    {
        Nudge? nudge;
        IReadOnlyList<NudgeKind> withdrawn;
        DateTime? breakEndsUtc;

        var settings = NudgeSettings.From(_config);
        lock (_lock)
        {
            nudge = _policy.TakeDue(nowUtc, settings, () => IsQuiet(nowUtc));
            withdrawn = _policy.TakeWithdrawn();
            breakEndsUtc = _policy.GetStatus(nowUtc).BreakEndsUtc;
        }

        foreach (var kind in withdrawn) _notifier.Remove(kind);

        if (nudge is not null)
        {
            _log.Information("Nudge {Kind} after {Minutes} min", nudge.Kind, (int)nudge.Length.TotalMinutes);
            _notifier.Show(nudge);
        }

        ScheduleBreakTimer(breakEndsUtc, nowUtc);
    }

    private void ScheduleBreakTimer(DateTime? breakEndsUtc, DateTime nowUtc)
    {
        try
        {
            if (breakEndsUtc is { } ends)
            {
                var due = ends - nowUtc;
                _breakTimer.Change(due > TimeSpan.Zero ? due + TimeSpan.FromMilliseconds(100) : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _breakTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
        catch (ObjectDisposedException) { /* exiting */ }
    }

    /// <summary>Called under <see cref="_lock"/>, and only when a stretch nudge is due.</summary>
    private bool IsQuiet(DateTime nowUtc)
    {
        var reason = _config.QuietWhenFullscreen && Win32.IsShellHoldingNotifications() ? "full screen or presenting"
            : _config.QuietWhenMicrophoneInUse && IsMicrophoneInUse(nowUtc) ? "microphone in use"
            : null;

        if (reason is not null && reason != _quietReason)
            _log.Information("Holding a nudge back: {Reason}", reason);
        _quietReason = reason;

        return reason is not null;
    }

    /// <summary>A registry walk, so cached for a few seconds. Called under <see cref="_lock"/>.</summary>
    private bool IsMicrophoneInUse(DateTime nowUtc)
    {
        if (nowUtc - _microphoneCheckedUtc >= MicrophoneCheckEvery || nowUtc < _microphoneCheckedUtc)
        {
            _isMicrophoneInUse = MicrophoneUsage.IsInUse();
            _microphoneCheckedUtc = nowUtc;
        }
        return _isMicrophoneInUse;
    }

    private void OnActivated(NudgeActivation activation)
    {
        if (_uiContext is null) Handle(activation);
        else _uiContext.Post(_ => Handle(activation), null);
    }

    private void Handle(NudgeActivation activation)
    {
        try
        {
            if (activation.Response == NudgeResponse.TurnOff && _config.HyperfocusNudgesEnabled)
            {
                _config.HyperfocusNudgesEnabled = false;
                _config.Save();
                _log.Information("Hyperfocus nudges turned off from a nudge");
            }

            var nowUtc = DateTime.UtcNow;
            bool isCurrent;
            lock (_lock) isCurrent = _policy.Respond(activation.NudgeId, activation.Response, nowUtc, NudgeSettings.From(_config));

            if (isCurrent) _log.Information("Nudge answered: {Response}", activation.Response);
            else _log.Debug("Ignored {Response} on nudge {NudgeId}: no longer current", activation.Response, activation.NudgeId);

            Evaluate(nowUtc); // withdraws toasts and (re)arms the break timer
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Handling a nudge response failed");
        }
    }

    public void Dispose()
    {
        _breakTimer.Dispose();
        _notifier.Dispose();
    }
}
