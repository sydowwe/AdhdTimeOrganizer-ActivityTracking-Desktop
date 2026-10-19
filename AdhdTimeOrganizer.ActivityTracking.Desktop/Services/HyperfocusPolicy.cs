using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

public enum NudgeKind
{
    /// <summary>At the computer too long without a break.</summary>
    Stretch,

    /// <summary>In one app too long without a break from it.</summary>
    AppStretch,

    /// <summary>The break the user started from a nudge is over.</summary>
    BreakOver,

    /// <summary>Off the planned block for too long (<see cref="DriftPolicy"/>).</summary>
    Drift,

    /// <summary>A planned block is starting: start it now? (<see cref="StartRampPolicy"/>).</summary>
    StartRamp,

    /// <summary>
    /// Not a nudge: uploads are stuck, the session ended or the server refused windows (<see cref="UploadHealthPolicy"/>).
    /// It shares the toast plumbing and has a switch of its own.
    /// </summary>
    UploadHealth,

    /// <summary>
    /// Not a nudge either: the evening invite to map today's unmapped time (<see cref="UnmappedReviewPolicy"/>). It shares
    /// the toast plumbing, has no policy state to answer to, and has a switch of its own.
    /// </summary>
    UnmappedReview
}

public enum NudgeResponse
{
    TakeBreak,
    Snooze,
    NotNow,
    TurnOff,
    Dismiss,
    /// <summary>Drift only: open the planner to change the plan.</summary>
    SwitchPlan,
    /// <summary>Upload health only: open the sign-in form.</summary>
    SignIn,
    /// <summary>Upload health only: open the log folder.</summary>
    OpenLogs,
    /// <summary>Evening review only: open today's unmapped time in the web app.</summary>
    Review,
    /// <summary>Evening review only: close the invite. The evening is already handled; nothing more is remembered.</summary>
    NotToday,
    /// <summary>Start ramp only: mark the block started and open its timer in the web app.</summary>
    StartNow,
    /// <summary>Start ramp only: ask once more in two minutes.</summary>
    Later,
    /// <summary>Start ramp only: not this block — and no drift nudge in it either.</summary>
    NotThisOne
}

/// <summary>What a drift nudge says about the plan. <see cref="PlannedName"/> is user-authored: never log it.</summary>
public sealed record DriftDetails(string PlannedName, DateTime PlannedEndsUtc);

/// <summary>
/// A nudge to show. <see cref="Id"/> ties the toast's buttons back to it; ids are per kind's policy, so a press is
/// routed by <see cref="Kind"/> first. For a drift nudge <see cref="Length"/> is the off-plan time and
/// <see cref="AppName"/> what it went to. An upload-health notice carries <see cref="Upload"/> instead, the evening
/// invite <see cref="Review"/>, and a start ramp <see cref="StartRamp"/> (its <see cref="Length"/> is the block's).
/// </summary>
public sealed record Nudge(
    int Id, NudgeKind Kind, TimeSpan Length, string? AppName, DriftDetails? Drift = null, UploadNotice? Upload = null,
    UnmappedInvite? Review = null, StartRampDetails? StartRamp = null);

public sealed record NudgeStatus(TimeSpan? StretchLength, DateTime? BreakEndsUtc);

public sealed record NudgeSettings(
    bool Enabled,
    TimeSpan StretchAfter,
    TimeSpan AppStretchAfter,
    TimeSpan BreakLength,
    TimeSpan BreakTimer)
{
    /// <summary>Read on every poll, so a change in Settings applies to the stretch already running.</summary>
    public static NudgeSettings From(AppConfig config) => new(
        config.HyperfocusNudgesEnabled,
        TimeSpan.FromMinutes(Math.Max(0, config.HyperfocusNudgeMinutes)),
        TimeSpan.FromMinutes(Math.Max(0, config.AppHyperfocusNudgeMinutes)),
        TimeSpan.FromMinutes(Math.Clamp(config.BreakMinutes, 1, 120)),
        TimeSpan.FromMinutes(Math.Clamp(config.BreakTimerMinutes, 1, 240)));
}

/// <summary>
/// Decides when to nudge someone who has been at the computer, or in one app, for too long. Pure — no
/// clock, no Win32, no toasts — like <see cref="ActivityRecorder"/>: the caller passes the time and what
/// the poll saw.
/// </summary>
/// <remarks>
/// <para>
/// A <b>stretch</b> is the wall time since the last break, and a break is being away for
/// <see cref="NudgeSettings.BreakLength"/>: no input, no sound in the foreground, no call. Shorter
/// absences — the kettle, the door — don't end it. An <b>app stretch</b> is foreground time in one app;
/// any interruption shorter than a break (another app, a short absence) is tolerated, a longer one ends it.
/// A window the tracker ignores is neither the app nor another one: it interrupts, but starts nothing.
/// </para>
/// <para>
/// A gap the poll loop didn't see — sleep, tracking paused, a hang — is unknown time. As long as a break, it
/// counts as one, so nobody is told "you've been at it for 9 hours" the morning after.
/// </para>
/// <para>
/// Not nagging: an ignored nudge comes back after <see cref="RepeatAfter"/>, "5 more minutes" after
/// <see cref="SnoozeFor"/>, "Not now" not again in that stretch, and a nudge of the other kind waits
/// <see cref="MinGapBetweenKinds"/> after the last one. A quiet condition (full screen, a call) holds a due
/// nudge until it ends. The break-over nudge ignores quiet conditions and the off switch: it's an alarm
/// the user set.
/// </para>
/// </remarks>
public sealed class HyperfocusPolicy(TimeSpan maxObservedGap)
{
    internal static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MinGapBetweenKinds = TimeSpan.FromMinutes(10);

    private readonly Dictionary<NudgeKind, int> _outstanding = new();
    private readonly HashSet<NudgeKind> _withdrawn = new();

    private DateTime? _lastPollUtc;
    private TimeSpan _awayFor;
    private DateTime? _stretchStartUtc;
    private DateTime? _stretchNudgeAtUtc; // null: StretchAfter from the start
    private AppStretch? _app;
    private DateTime? _breakStartUtc;
    private DateTime? _breakEndsUtc;
    private (DateTime AtUtc, NudgeKind Kind)? _lastNudge;
    private int _lastId;

    /// <summary>Settable because it follows the poll interval, which Settings can change.</summary>
    public TimeSpan MaxObservedGap { get; set; } = maxObservedGap;

    /// <summary>
    /// Folds in one poll. <paramref name="appKey"/> identifies the foreground app (null when away or on an
    /// ignored window); <paramref name="appName"/> is what a nudge calls it.
    /// </summary>
    public void Record(DateTime nowUtc, bool isPresent, string? appKey, string? appName, NudgeSettings settings)
    {
        var elapsed = _lastPollUtc is { } last && nowUtc > last ? nowUtc - last : TimeSpan.Zero;
        _lastPollUtc = nowUtc;

        if (elapsed > MaxObservedGap)
        {
            if (elapsed >= settings.BreakLength) EndStretch();
            elapsed = TimeSpan.Zero;
        }

        if (!isPresent)
        {
            _awayFor += elapsed;
            if (_awayFor >= settings.BreakLength) EndStretch();
            else InterruptApp(elapsed, settings);
            return;
        }

        _awayFor = TimeSpan.Zero;
        _stretchStartUtc ??= nowUtc;

        if (appKey is not null && _app?.Key == appKey)
        {
            _app.Foreground += elapsed;
            _app.InterruptedFor = TimeSpan.Zero;
            return;
        }

        InterruptApp(elapsed, settings);
        if (appKey is not null && _app is null)
            _app = new AppStretch(appKey, appName ?? appKey);
    }

    /// <summary>
    /// The nudge to show now, if any, marked as shown. <paramref name="isQuiet"/> is asked only when a
    /// stretch nudge is due — it reads the registry.
    /// </summary>
    public Nudge? TakeDue(DateTime nowUtc, NudgeSettings settings, Func<bool> isQuiet)
    {
        if (_breakEndsUtc is { } breakEnds)
        {
            if (nowUtc < breakEnds) return null;

            // The start stays: "5 more minutes" extends this break, and the next nudge counts all of it.
            var length = nowUtc - (_breakStartUtc ?? breakEnds);
            _breakEndsUtc = null;
            // Whatever the user does next is a new stretch, whether or not they really left.
            EndStretch();
            return Issue(NudgeKind.BreakOver, nowUtc, length, null);
        }

        if (!settings.Enabled) return null;

        var kind = IsStretchDue(nowUtc, settings) ? NudgeKind.Stretch
            : IsAppStretchDue(settings) ? NudgeKind.AppStretch
            : (NudgeKind?)null;
        if (kind is null) return null;

        if (_lastNudge is { } lastNudge && lastNudge.Kind != kind && nowUtc - lastNudge.AtUtc < MinGapBetweenKinds)
            return null;
        if (isQuiet()) return null;

        if (kind == NudgeKind.Stretch)
        {
            _stretchNudgeAtUtc = nowUtc + RepeatAfter;
            return Issue(NudgeKind.Stretch, nowUtc, nowUtc - _stretchStartUtc!.Value, _app?.Name);
        }

        var app = _app!;
        app.NudgeAt = app.Foreground + RepeatAfter;
        return Issue(NudgeKind.AppStretch, nowUtc, app.Foreground, app.Name);
    }

    /// <summary>
    /// Applies a button press. False when the nudge is no longer current — answered already, or its stretch
    /// has ended — so a toast clicked late in the notification centre changes nothing.
    /// </summary>
    public bool Respond(int nudgeId, NudgeResponse response, DateTime nowUtc, NudgeSettings settings)
    {
        NudgeKind? match = null;
        foreach (var (kind, id) in _outstanding)
            if (id == nudgeId) match = kind;
        if (match is not { } answered) return false;

        _outstanding.Remove(answered);

        switch (response, answered)
        {
            case (NudgeResponse.TakeBreak, not NudgeKind.BreakOver):
                _breakStartUtc = nowUtc;
                _breakEndsUtc = nowUtc + settings.BreakTimer;
                Withdraw(NudgeKind.Stretch);
                Withdraw(NudgeKind.AppStretch);
                break;

            case (NudgeResponse.Snooze, NudgeKind.Stretch):
                _stretchNudgeAtUtc = nowUtc + SnoozeFor;
                break;
            case (NudgeResponse.Snooze, NudgeKind.AppStretch) when _app is not null:
                _app.NudgeAt = _app.Foreground + SnoozeFor;
                break;
            case (NudgeResponse.Snooze, NudgeKind.BreakOver):
                _breakEndsUtc = nowUtc + SnoozeFor;
                break;

            case (NudgeResponse.NotNow, NudgeKind.Stretch):
                _stretchNudgeAtUtc = DateTime.MaxValue;
                break;
            case (NudgeResponse.NotNow, NudgeKind.AppStretch) when _app is not null:
                _app.NudgeAt = TimeSpan.MaxValue;
                break;
        }

        return true;
    }

    /// <summary>Kinds whose toast is out of date — its stretch ended — and should leave the notification centre.</summary>
    public IReadOnlyList<NudgeKind> TakeWithdrawn()
    {
        var withdrawn = _withdrawn.ToList();
        _withdrawn.Clear();
        return withdrawn;
    }

    public NudgeStatus GetStatus(DateTime nowUtc) =>
        new(_stretchStartUtc is { } start ? nowUtc - start : null, _breakEndsUtc);

    /// <summary>Shows a nudge of <paramref name="kind"/> (not <see cref="NudgeKind.Drift"/>) now, so its toast and buttons can be tried by hand.</summary>
    internal Nudge IssueForTest(NudgeKind kind, DateTime nowUtc)
    {
        switch (kind)
        {
            case NudgeKind.Stretch:
                return Issue(kind, nowUtc, _stretchStartUtc is { } start ? nowUtc - start : TimeSpan.FromMinutes(90), _app?.Name);
            case NudgeKind.AppStretch:
                return Issue(kind, nowUtc, _app?.Foreground ?? TimeSpan.FromMinutes(120), _app?.Name ?? "Test app");
            default:
                _breakStartUtc = nowUtc - TimeSpan.FromMinutes(10);
                return Issue(kind, nowUtc, TimeSpan.FromMinutes(10), null);
        }
    }

    private bool IsStretchDue(DateTime nowUtc, NudgeSettings settings) =>
        _stretchStartUtc is { } start
        && settings.StretchAfter > TimeSpan.Zero
        && nowUtc >= (_stretchNudgeAtUtc ?? start + settings.StretchAfter);

    private bool IsAppStretchDue(NudgeSettings settings) =>
        _app is { } app
        && settings.AppStretchAfter > TimeSpan.Zero
        && app.Foreground >= (app.NudgeAt ?? settings.AppStretchAfter);

    private Nudge Issue(NudgeKind kind, DateTime nowUtc, TimeSpan length, string? appName)
    {
        var nudge = new Nudge(++_lastId, kind, length, appName);
        _outstanding[kind] = nudge.Id;
        _lastNudge = (nowUtc, kind);
        return nudge;
    }

    private void InterruptApp(TimeSpan elapsed, NudgeSettings settings)
    {
        if (_app is null) return;
        _app.InterruptedFor += elapsed;
        if (_app.InterruptedFor >= settings.BreakLength) EndAppStretch();
    }

    private void EndStretch()
    {
        _stretchStartUtc = null;
        _stretchNudgeAtUtc = null;
        Withdraw(NudgeKind.Stretch);
        EndAppStretch();
    }

    private void EndAppStretch()
    {
        _app = null;
        Withdraw(NudgeKind.AppStretch);
    }

    private void Withdraw(NudgeKind kind)
    {
        if (_outstanding.Remove(kind)) _withdrawn.Add(kind);
    }

    private sealed class AppStretch(string key, string name)
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
        public TimeSpan Foreground { get; set; }
        public TimeSpan InterruptedFor { get; set; }

        /// <summary>Foreground time at which to nudge; null: AppStretchAfter.</summary>
        public TimeSpan? NudgeAt { get; set; }
    }
}
