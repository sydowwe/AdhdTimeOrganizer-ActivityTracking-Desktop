using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// What one sent minute says about the plan, read off the heartbeat's answer. The server judges each slice of the
/// minute (<see cref="ResolvedTime.OnPlan"/>); this only weighs them.
/// </summary>
/// <param name="Block">The planned block the minute fell in; the first when two overlap. Null when nothing was planned.</param>
/// <param name="OnPlan">
/// True when at least as much of the minute fit the plan as didn't, false when more went elsewhere, null when none of it
/// could be judged — unmapped or ignored windows, or nothing planned. Unmapped time never tips it either way.
/// </param>
/// <param name="OffPlanName">What most of the off-plan time mapped to — "YouTube" in "Last 10 min: YouTube".</param>
public sealed record MinuteVerdict(DateTime WindowStartUtc, PlannedBlock? Block, bool? OnPlan, string? OffPlanName)
{
    public static MinuteVerdict From(DateTime windowStartUtc, HeartbeatResponse response)
    {
        var onPlan = response.Resolution.Where(r => r.OnPlan == true).Sum(r => r.ActiveSeconds);
        var offPlan = response.Resolution.Where(r => r.OnPlan == false).ToList();
        var offPlanSeconds = offPlan.Sum(r => r.ActiveSeconds);

        return new MinuteVerdict(
            AsUtc(windowStartUtc),
            response.Planned.FirstOrDefault(),
            onPlan + offPlanSeconds == 0 ? null : onPlan >= offPlanSeconds,
            offPlan.MaxBy(r => r.ActiveSeconds)?.Name);
    }

    internal static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}

/// <summary>
/// What <see cref="DriftPolicy"/> knows, saved across restarts: without it a restart in the middle of a block forgets the
/// nudge already given there and gives it again, and forgets a streak and a snooze. The outstanding toast is not part of
/// it — every toast is removed on exit. <see cref="OffPlanNames"/> are user-authored; the file is the user's own.
/// </summary>
/// <param name="QuietBlockId">The block the start ramp's "Not this one" was pressed on; absent from older files.</param>
public sealed record DriftState(
    PlannedBlock? Block,
    DateTime? LastMinuteUtc,
    DateTime? LastOffPlanUtc,
    int OffPlanMinutes,
    Dictionary<string, int> OffPlanNames,
    bool DoneForBlock,
    DateTime? SnoozedUntilUtc,
    long? QuietBlockId = null);

public sealed record DriftSettings(bool Enabled, TimeSpan After)
{
    /// <summary>Never sooner than ten minutes: a few minutes on something else is a glance, not a drift.</summary>
    internal const int MinMinutes = 10;

    public static DriftSettings From(AppConfig config) => new(
        config.DriftNudgesEnabled,
        TimeSpan.FromMinutes(Math.Clamp(config.DriftNudgeMinutes, MinMinutes, 120)));
}

/// <summary>
/// Decides when to tell someone they slid off their plan: "Planned: Thesis (until 11:30). Last 10 min: YouTube." Pure,
/// like <see cref="HyperfocusPolicy"/> — the caller passes the time and each minute's <see cref="MinuteVerdict"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The streak.</b> Off-plan minutes inside one planned block, counted until an on-plan minute resets them. Unmapped
/// and ignored minutes neither count nor reset — the server can't say what they were. A gap of more than
/// <see cref="MaxGap"/> between two off-plan minutes (away, locked, a run of unmapped windows) starts the count again,
/// so "Last 10 min" is never stitched across a lunch break. A new block starts everything again.
/// </para>
/// <para>
/// <b>Only now counts.</b> A minute sent from the outbox long after it happened (offline, signed out) says nothing about
/// what the user is doing now, so one older than <see cref="FreshFor"/> is skipped.
/// </para>
/// <para>
/// <b>Not nagging.</b> At most one nudge per planned block. "Snooze" asks for exactly one more after
/// <see cref="SnoozeFor"/>, if the user is still off plan then; "Back to it", "Switch plan", "Turn off" and ignoring the
/// toast all end it for the block. A quiet condition holds a due nudge until it ends, as long as the streak is still
/// current. An on-plan minute or the block ending withdraws a toast still in the notification centre.
/// </para>
/// <para>
/// <b>"Not this one" on the start ramp</b> (<see cref="QuietBlock"/>) ends it for that block too: the user said they are
/// not doing it, and "you slid off it" would be a reproach for a decision.
/// </para>
/// </remarks>
public sealed class DriftPolicy
{
    internal static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, int> _offPlanNames = new();

    private PlannedBlock? _block;
    private DateTime? _lastMinuteUtc;
    private DateTime? _lastOffPlanUtc;
    private int _offPlanMinutes;
    private bool _doneForBlock;
    private DateTime? _snoozedUntilUtc;
    private long? _quietBlockId;
    private int? _outstandingId;
    private bool _withdrawn;
    private int _lastId;

    public DriftState Snapshot() => new(
        _block, _lastMinuteUtc, _lastOffPlanUtc, _offPlanMinutes, new Dictionary<string, int>(_offPlanNames), _doneForBlock, _snoozedUntilUtc,
        _quietBlockId);

    /// <summary>Picks up where a saved <paramref name="state"/> left off. A state from another day ends at the next minute's new block.</summary>
    public void Restore(DriftState state)
    {
        _block = state.Block;
        _lastMinuteUtc = state.LastMinuteUtc;
        _lastOffPlanUtc = state.LastOffPlanUtc;
        _offPlanMinutes = state.OffPlanMinutes;
        _offPlanNames.Clear();
        foreach (var (name, minutes) in state.OffPlanNames) _offPlanNames[name] = minutes;
        _doneForBlock = state.DoneForBlock;
        _snoozedUntilUtc = state.SnoozedUntilUtc;
        _quietBlockId = state.QuietBlockId;
    }

    /// <summary>No drift nudge in <paramref name="plannerTaskId"/>, now or once it begins; a toast out for it leaves.</summary>
    public void QuietBlock(long plannerTaskId)
    {
        _quietBlockId = plannerTaskId;
        if (_block?.PlannerTaskId == plannerTaskId) Withdraw();
    }

    /// <summary>Folds in one sent minute.</summary>
    public void Record(DateTime nowUtc, MinuteVerdict minute)
    {
        if (minute.WindowStartUtc < nowUtc - FreshFor) return;
        // The send loop goes oldest first; a repeat of a minute (sent again after a flush) is already counted.
        if (_lastMinuteUtc is { } last && minute.WindowStartUtc <= last) return;
        _lastMinuteUtc = minute.WindowStartUtc;

        if (minute.Block?.PlannerTaskId != _block?.PlannerTaskId)
            StartBlock();
        _block = minute.Block; // a block moved or extended keeps its id; take its latest end
        if (_block is null) return;

        switch (minute.OnPlan)
        {
            case true:
                ResetStreak();
                Withdraw();
                break;

            case false:
                if (_lastOffPlanUtc is { } lastOff && minute.WindowStartUtc - lastOff > MaxGap)
                    ResetStreak();
                _offPlanMinutes++;
                _lastOffPlanUtc = minute.WindowStartUtc;
                var name = minute.OffPlanName ?? "something else";
                _offPlanNames[name] = _offPlanNames.GetValueOrDefault(name) + 1;
                break;
        }
    }

    /// <summary>
    /// The nudge to show now, if any, marked as shown. <paramref name="isQuiet"/> is asked only when one is due.
    /// </summary>
    public Nudge? TakeDue(DateTime nowUtc, DriftSettings settings, Func<bool> isQuiet)
    {
        if (!settings.Enabled || _block is null || _doneForBlock || _block.PlannerTaskId == _quietBlockId) return null;
        if (_snoozedUntilUtc is { } until && nowUtc < until) return null;
        if (_offPlanMinutes < settings.After.TotalMinutes) return null;
        if (!IsStreakCurrent(nowUtc)) return null;
        if (isQuiet()) return null;

        _doneForBlock = true;
        _snoozedUntilUtc = null;
        return Issue(_block, _offPlanMinutes, _offPlanNames.MaxBy(n => n.Value).Key);
    }

    /// <summary>
    /// Applies a button press. False when the nudge is no longer current — answered already, or withdrawn because the
    /// block ended or the user got back to it.
    /// </summary>
    public bool Respond(int nudgeId, NudgeResponse response, DateTime nowUtc)
    {
        if (_outstandingId != nudgeId) return false;
        _outstandingId = null;

        if (response == NudgeResponse.Snooze)
        {
            _snoozedUntilUtc = nowUtc + SnoozeFor;
            _doneForBlock = false;
        }
        else
        {
            // Back to it, Switch plan, Turn off: the block is settled either way.
            ResetStreak();
        }
        return true;
    }

    /// <summary>True once when the toast still out is out of date and should leave the notification centre.</summary>
    public bool TakeWithdrawn()
    {
        var withdrawn = _withdrawn;
        _withdrawn = false;
        return withdrawn;
    }

    /// <summary>Shows a drift nudge now, so its toast and buttons can be tried by hand.</summary>
    internal Nudge IssueForTest(DateTime nowUtc) =>
        Issue(_block ?? new PlannedBlock
            {
                PlannerTaskId = -1,
                ActivityName = "Test plan",
                StartsAtUtc = nowUtc.AddMinutes(-30),
                EndsAtUtc = nowUtc.AddMinutes(30)
            },
            Math.Max(_offPlanMinutes, 10),
            _offPlanNames.Count > 0 ? _offPlanNames.MaxBy(n => n.Value).Key : "YouTube");

    /// <summary>The last off-plan minute ended no more than <see cref="MaxGap"/> ago.</summary>
    private bool IsStreakCurrent(DateTime nowUtc) =>
        _lastOffPlanUtc is { } lastOff && nowUtc - (lastOff + TimeSpan.FromMinutes(1)) <= MaxGap;

    private Nudge Issue(PlannedBlock block, int minutes, string offPlanName)
    {
        var nudge = new Nudge(++_lastId, NudgeKind.Drift, TimeSpan.FromMinutes(minutes), offPlanName,
            new DriftDetails(block.ActivityName, MinuteVerdict.AsUtc(block.EndsAtUtc)));
        _outstandingId = nudge.Id;
        return nudge;
    }

    private void StartBlock()
    {
        ResetStreak();
        Withdraw();
        _doneForBlock = false;
        _snoozedUntilUtc = null;
    }

    private void ResetStreak()
    {
        _offPlanMinutes = 0;
        _lastOffPlanUtc = null;
        _offPlanNames.Clear();
    }

    private void Withdraw()
    {
        if (_outstandingId is null) return;
        _outstandingId = null;
        _withdrawn = true;
    }
}
