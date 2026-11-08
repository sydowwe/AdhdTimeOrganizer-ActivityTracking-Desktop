using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>What the time chime is set to, read from <see cref="AppConfig"/> on every minute and every evaluation.</summary>
/// <param name="Choices">Ticked (true) or unticked (false) by hand, by the today breakdown's key.</param>
/// <param name="DriftWatches">Drift nudges are on: inside a planned block they speak, and the chime stays quiet.</param>
public sealed record ChimeSettings(
    bool Enabled, TimeSpan Interval, bool FollowDistractions, IReadOnlyDictionary<string, bool> Choices, bool DriftWatches)
{
    /// <summary>The phone's choices and default (android decision 11-2).</summary>
    internal static readonly int[] IntervalChoices = [5, 10, 15, 20, 30];

    internal const int DefaultMinutes = 15;

    public static ChimeSettings From(AppConfig config) => new(
        config.TimeChimeEnabled,
        TimeSpan.FromMinutes(IntervalChoices.Contains(config.TimeChimeMinutes) ? config.TimeChimeMinutes : DefaultMinutes),
        config.TimeChimeDistractions,
        config.TimeChimeChoices.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Last().Chime),
        config.DriftNudgesEnabled);

    /// <summary>Whether time on this slice of a minute is chimed: a choice by hand wins, else the server's distraction flag.</summary>
    public bool Chimes(ResolvedTime slice) =>
        !slice.IsIgnored && !slice.IsUnmapped && Chimes(TodayTotal.KeyOf(slice).Key, slice.IsDistraction);

    /// <summary>The same, for a line of the today breakdown (<paramref name="key"/>) the server last flagged or not.</summary>
    public bool Chimes(string key, bool isDistraction) =>
        key != TodayTotal.UnmappedKey
        && (Choices.TryGetValue(key, out var chime) ? chime : FollowDistractions && isDistraction);
}

/// <summary>One line the chime can be set on, in Settings. <see cref="Name"/> is user-authored.</summary>
public sealed record ChimeCandidate(string Key, string Name, bool IsDistraction);

/// <summary>The tray's and Settings' half of the choice: which lines to offer, and how a tick is kept.</summary>
public static class ChimeChoices
{
    /// <summary>
    /// What Settings offers: today's lines busiest first, then the known distractions, then anything chosen by hand
    /// before — each once, by key, under its freshest name. Unmapped time is never offered: there is nothing to name.
    /// </summary>
    public static List<ChimeCandidate> Candidates(
        IEnumerable<ActivityTotal> today, IReadOnlyDictionary<string, string> distractions, IEnumerable<ChimeChoice> choices) =>
        today.Select(t => (t.Key, t.Name))
            .Concat(distractions.OrderBy(d => d.Value, StringComparer.CurrentCulture).Select(d => (d.Key, Name: d.Value)))
            .Concat(choices.Select(c => (c.Key, c.Name)))
            .Where(line => line.Key != TodayTotal.UnmappedKey)
            .DistinctBy(line => line.Key)
            .Select(line => new ChimeCandidate(line.Key, line.Name, distractions.ContainsKey(line.Key)))
            .ToList();

    /// <summary>
    /// The choices with <paramref name="key"/> set to <paramref name="chime"/>. A choice that only repeats the default (a
    /// distraction while distractions are followed, anything else off) is dropped rather than kept, so a flag the user
    /// later sets on the server still decides for every line they never touched. A new list: the config's is never mutated.
    /// </summary>
    public static List<ChimeChoice> With(
        IEnumerable<ChimeChoice> choices, string key, string name, bool chime, bool isDistraction, bool followDistractions)
    {
        var rest = choices.Where(c => c.Key != key).ToList();
        if (chime != (followDistractions && isDistraction)) rest.Add(new ChimeChoice(key, name, chime));
        return rest;
    }
}

/// <summary>
/// What <see cref="ChimeSchedule"/> knows, saved across restarts (<c>time-chime.json</c>), so a restart neither repeats a
/// chime nor forgets "Stop for today". <see cref="Name"/> and the distractions' names are user-authored; the file is the
/// user's own.
/// </summary>
/// <param name="Distractions">The breakdown keys the server last called a distraction, with their names: the tray and
/// Settings show them as chimed before the user ticks anything.</param>
public sealed record ChimeState(
    DateTime? LastMinuteUtc,
    DateTime? LastChosenUtc,
    int StretchMinutes,
    int SaidMinutes,
    string? Name,
    bool InWatchedBlock,
    DateOnly? StoppedOn,
    Dictionary<string, string>? Distractions);

/// <summary>
/// Decides when to say "15 min on YouTube": a quiet, short-lived toast at each interval of continuous time in a chosen
/// activity, so time spent outside a plan stays felt. Pure, like <see cref="DriftPolicy"/> — the caller passes the time and
/// each sent minute's <see cref="MinuteVerdict"/>, and the local zone only decides when "today" ends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chosen</b> is the server's answer, never a process list: YouTube in Chrome chimes and docs in Chrome don't. A minute
/// counts when chosen activities hold at least half of its tracked (not ignored) time; the chosen activities count as one
/// stretch (android 11-5), named by the busiest of them in the latest minute.
/// </para>
/// <para>
/// <b>The stretch.</b> Counted in minutes, a minute at a time from the send loop. A gap of up to <see cref="ResetGap"/>
/// (another activity, idle, locked, a minute nothing chosen held) pauses it; a longer one starts it over (android 11-7).
/// A minute older than <see cref="FreshFor"/> when it arrives (an outbox backlog) says nothing about now and is skipped.
/// </para>
/// <para>
/// <b>When it speaks.</b> At each multiple of the interval, the same interval every time (no escalation, android 11-3).
/// Each mark is passed once: a mark due under a quiet condition is skipped, not held — a late "15 min" is wrong by the time
/// it is said — and so is one inside a planned block while drift nudges are on, since the drift nudge speaks there (one
/// voice per block). A changed interval chimes at its next mark past the one already said, never repeating it.
/// </para>
/// <para>
/// <b>"Stop for today"</b> silences it until local midnight; the stretch still counts, so the first chime after midnight
/// says the true length.
/// </para>
/// </remarks>
public sealed class ChimeSchedule(TimeZoneInfo zone)
{
    internal static readonly TimeSpan FreshFor = DriftPolicy.FreshFor;
    internal static readonly TimeSpan ResetGap = TimeSpan.FromMinutes(3);

    /// <summary>How long a chime stays in the notification centre: a "15 min" from an hour ago is noise.</summary>
    internal static readonly TimeSpan ToastLifetime = TimeSpan.FromMinutes(2);

    /// <summary>When the chosen minutes have no name (a role · category the server could not name).</summary>
    internal const string Unnamed = "this";

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, string> _distractions = new();

    private DateTime? _lastMinuteUtc;
    private DateTime? _lastChosenUtc;
    private int _stretchMinutes;
    private int _saidMinutes;
    private string? _name;
    private bool _inWatchedBlock;
    private DateOnly? _stoppedOn;
    private int _lastId;

    public ChimeState Snapshot() => new(
        _lastMinuteUtc, _lastChosenUtc, _stretchMinutes, _saidMinutes, _name, _inWatchedBlock, _stoppedOn,
        new Dictionary<string, string>(_distractions));

    public void Restore(ChimeState state)
    {
        _lastMinuteUtc = state.LastMinuteUtc;
        _lastChosenUtc = state.LastChosenUtc;
        _stretchMinutes = state.StretchMinutes;
        _saidMinutes = state.SaidMinutes;
        _name = state.Name;
        _inWatchedBlock = state.InWatchedBlock;
        _stoppedOn = state.StoppedOn;
        _distractions.Clear();
        foreach (var (key, name) in state.Distractions ?? new Dictionary<string, string>()) _distractions[key] = name;
    }

    /// <summary>The breakdown keys the server last called a distraction, with their names.</summary>
    public IReadOnlyDictionary<string, string> Distractions => new Dictionary<string, string>(_distractions);

    /// <summary>The current stretch, for tests and the log: minutes counted, and whether it is still going at <paramref name="nowUtc"/>.</summary>
    internal (int Minutes, bool Current) Stretch(DateTime nowUtc) => (_stretchMinutes, IsCurrent(nowUtc));

    /// <summary>Folds in one sent minute.</summary>
    public void Record(DateTime nowUtc, MinuteVerdict minute, ChimeSettings settings)
    {
        var slices = minute.Slices ?? [];
        // The flags are true of the activity whenever the minute was, so even a backlog minute keeps them current.
        LearnDistractions(slices);

        if (minute.WindowStartUtc < nowUtc - FreshFor) return;
        // The send loop goes oldest first; a repeat of a minute (sent again after a flush) is already counted.
        if (_lastMinuteUtc is { } last && minute.WindowStartUtc <= last) return;
        _lastMinuteUtc = minute.WindowStartUtc;

        var tracked = slices.Where(s => !s.IsIgnored && s.ActiveSeconds > 0).ToList();
        var chosen = tracked.Where(settings.Chimes).ToList();
        var chosenSeconds = chosen.Sum(s => s.ActiveSeconds);
        if (chosenSeconds == 0 || chosenSeconds * 2 < tracked.Sum(s => s.ActiveSeconds)) return;

        if (_lastChosenUtc is not { } lastChosen || minute.WindowStartUtc - (lastChosen + OneMinute) > ResetGap)
        {
            _stretchMinutes = 0;
            _saidMinutes = 0;
        }

        _stretchMinutes++;
        _lastChosenUtc = minute.WindowStartUtc;
        _name = chosen.MaxBy(s => s.ActiveSeconds)!.Name ?? Unnamed;
        _inWatchedBlock = settings.DriftWatches && minute.Block is not null;
    }

    /// <summary>
    /// The chime to show now, if any. <paramref name="isQuiet"/> is asked only when a mark is due; a mark is passed either
    /// way, so the state changes whenever this returns a chime or skips one — the caller saves after both.
    /// </summary>
    public Nudge? TakeDue(DateTime nowUtc, ChimeSettings settings, Func<bool> isQuiet, out bool changed)
    {
        changed = false;
        if (!settings.Enabled || !IsCurrent(nowUtc) || _stoppedOn == DayOf(nowUtc)) return null;

        var interval = Math.Max(1, (int)settings.Interval.TotalMinutes);
        var mark = _stretchMinutes / interval * interval;
        if (mark == 0 || mark <= _saidMinutes) return null;

        _saidMinutes = mark;
        changed = true;
        if (_inWatchedBlock || isQuiet()) return null;

        return Issue(mark, _name ?? Unnamed);
    }

    /// <summary>"Stop for today", from any chime still on screen: quiet until local midnight.</summary>
    public void StopForToday(DateTime nowUtc) => _stoppedOn = DayOf(nowUtc);

    /// <summary>Shows a chime now, so its toast and button can be tried by hand.</summary>
    internal Nudge IssueForTest() => Issue(Math.Max(_stretchMinutes, 15), _name ?? "YouTube");

    private Nudge Issue(int minutes, string name) =>
        new(++_lastId, NudgeKind.TimeChime, TimeSpan.FromMinutes(minutes), name);

    /// <summary>The last chosen minute ended no more than <see cref="ResetGap"/> ago.</summary>
    private bool IsCurrent(DateTime nowUtc) =>
        _lastChosenUtc is { } last && nowUtc - (last + OneMinute) <= ResetGap;

    private void LearnDistractions(IEnumerable<ResolvedTime> slices)
    {
        foreach (var slice in slices.Where(s => !s.IsIgnored && !s.IsUnmapped))
        {
            var (key, name) = TodayTotal.KeyOf(slice);
            if (slice.IsDistraction) _distractions[key] = name;
            else _distractions.Remove(key);
        }
    }

    private DateOnly DayOf(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(MinuteVerdict.AsUtc(utc), zone));
}
