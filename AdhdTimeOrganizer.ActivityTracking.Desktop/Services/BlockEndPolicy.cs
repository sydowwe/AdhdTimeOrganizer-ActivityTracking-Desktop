using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// What a block-end heads-up says. <see cref="PlannedName"/> and <see cref="NextName"/> are user-authored: never log them.
/// </summary>
/// <param name="Left">How long the block had left when the toast was issued.</param>
/// <param name="After">What follows: the next block, nothing, or — the next one overlapping this — unknown.</param>
public sealed record BlockEndDetails(
    long PlannerTaskId, string PlannedName, DateTime EndsUtc, TimeSpan Left, BlockEndAfter After,
    long? NextPlannerTaskId = null, string? NextName = null, DateTime? NextStartsUtc = null);

/// <summary>What the heads-up can say about after the block.</summary>
public enum BlockEndAfter
{
    /// <summary>The answer's <c>next</c> starts at or after this block's end: "next: Email at 11:30".</summary>
    Next,

    /// <summary>The answer has no <c>next</c>: "nothing planned after".</summary>
    Nothing,

    /// <summary>The answer's <c>next</c> starts inside this block, so it isn't what comes after; the toast doesn't say.</summary>
    Unknown
}

public sealed record BlockEndSettings(bool Enabled, RampBlocks Blocks = RampBlocks.NotOptional)
{
    public static BlockEndSettings From(AppConfig config) => new(config.BlockEndEnabled, config.BlockEndBlocks);

    public bool Admits(PlannedBlock block) => StartRampSettings.Admits(Blocks, block);
}

/// <summary>
/// What <see cref="BlockEndPolicy"/> knows, saved across restarts (<c>block-end.json</c>), so a restart doesn't warn twice.
/// </summary>
/// <param name="Settled">Blocks warned about (or let go), by planner task id, each with the end it was settled for.</param>
public sealed record BlockEndState(Dictionary<long, DateTime> Settled);

/// <summary>
/// The start ramp's mirror image: a few minutes before a block the user is in ends, one quiet toast — "Thesis ends in
/// 5 min — next: Email at 11:30". A thought can then be finished rather than cut. Pure, like
/// <see cref="StartRampPolicy"/>: the caller passes the time, each heartbeat answer (<see cref="PlanView"/>) and whether
/// someone is at the computer.
/// </summary>
/// <remarks>
/// <para>
/// <b>When.</b> <see cref="Lead"/> before the end of a block from the answer's <c>planned</c>, while someone is at the
/// computer. Held under the quiet conditions, with the minutes left said as they are when it shows; let go once less than
/// <see cref="MinLeft"/> is left — a "1 min" warning is no warning. A block shorter than <see cref="MinBlockLength"/>
/// gets none. The plan is trusted for <see cref="FreshFor"/> after its minute, like the ramp's.
/// </para>
/// <para>
/// <b>Only a block the user is in</b>: started in the planner (<c>inProgress</c>), or a minute on plan while it covered
/// within <see cref="RecentFor"/>. A warning about a block the user wasn't doing would read as "you wasted it". Not held
/// by a running timer: the block's own countdown rings only at the end.
/// </para>
/// <para>
/// <b>Which blocks</b> — <see cref="BlockEndSettings.Blocks"/>, its own setting, All but Optional by default.
/// </para>
/// <para>
/// <b>Once per block end.</b> Settled by planner task id with the end it was for, so a block extended in the planner is
/// warned about again before its new end. The toast leaves at the block's end, and earlier when the block is extended or
/// shortened (a new warning follows for the new end), completed or cancelled (no minute inside it lists it any more).
/// </para>
/// <para>
/// <b>The next block's start ramp.</b> When the next block starts within <see cref="FollowsWithin"/> of this one's end, the
/// heads-up has named it, and the caller tells the ramp so (<see cref="StartRampPolicy.Announce"/>): the ramp then waits a
/// few minutes into that block and speaks only if the user hasn't moved on to it.
/// </para>
/// </remarks>
public sealed class BlockEndPolicy
{
    internal static readonly TimeSpan Lead = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MinLeft = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MinBlockLength = TimeSpan.FromMinutes(20);
    internal static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan FreshFor = DriftPolicy.FreshFor;

    /// <summary>A next block starting this soon after the end is the one the heads-up hands over to.</summary>
    internal static readonly TimeSpan FollowsWithin = HyperfocusPolicy.MinGapBetweenKinds;

    /// <summary>A settled block is remembered this long past its end, as the ramp's are.</summary>
    private static readonly TimeSpan RememberFor = TimeSpan.FromDays(1);

    private readonly Dictionary<long, DateTime> _settled = new();

    /// <summary>The last minute that was on plan while each block covered it. Not saved: the next such minute says it again.</summary>
    private readonly Dictionary<long, DateTime> _onPlanAt = new();

    private PlanView? _plan;
    private (int Id, PlannedBlock Block)? _outstanding;
    private bool _withdrawn;
    private int _lastId;

    public BlockEndState Snapshot() => new(new Dictionary<long, DateTime>(_settled));

    public void Restore(BlockEndState state)
    {
        _settled.Clear();
        foreach (var (id, endsUtc) in state.Settled) _settled[id] = endsUtc;
    }

    /// <summary>Folds in one heartbeat answer. A backlog minute's is skipped: it says nothing about now.</summary>
    public void Record(DateTime nowUtc, PlanView plan)
    {
        if (plan.WindowStartUtc < nowUtc - FreshFor) return;
        if (_plan is { } known && plan.WindowStartUtc < known.WindowStartUtc) return;
        _plan = plan;

        if (plan.OnPlan == true)
            foreach (var block in plan.Covering)
                _onPlanAt[block.PlannerTaskId] = plan.WindowStartUtc;

        FollowTheToastOut(plan);
    }

    /// <summary>
    /// The toast still out, against a new answer: a minute before the block's end that no longer lists it means the block
    /// was completed, cancelled or moved; one listing it with another end means it was extended or shortened. Either way
    /// the toast is out of date. The test toast's block is not a planner task, so no answer lists it.
    /// </summary>
    private void FollowTheToastOut(PlanView plan)
    {
        if (_outstanding is not { Block: var shown }) return;

        var id = shown.PlannerTaskId;
        var endsUtc = MinuteVerdict.AsUtc(shown.EndsAtUtc);
        if (plan.Covering.FirstOrDefault(b => b.PlannerTaskId == id) is { } listed)
        {
            if (MinuteVerdict.AsUtc(listed.EndsAtUtc) != endsUtc) Withdraw();
        }
        else if (id > 0 && plan.WindowStartUtc < endsUtc)
        {
            Withdraw();
        }
    }

    /// <summary>
    /// The toast to show now, if any. <paramref name="isQuiet"/> is asked only when one is due. True from
    /// <paramref name="changed"/> when the saved state should be written.
    /// </summary>
    public Nudge? TakeDue(DateTime nowUtc, BlockEndSettings settings, bool isPresent, Func<bool> isQuiet, out bool changed)
    {
        changed = Prune(nowUtc);
        if (_outstanding is { Block: var shown } && nowUtc >= MinuteVerdict.AsUtc(shown.EndsAtUtc))
            Withdraw();

        if (!settings.Enabled || !isPresent) return null;
        if (_plan is not { } plan || nowUtc - plan.WindowStartUtc > FreshFor) return null;

        foreach (var block in plan.Covering.OrderBy(b => b.EndsAtUtc))
        {
            var id = block.PlannerTaskId;
            var startsUtc = MinuteVerdict.AsUtc(block.StartsAtUtc);
            var endsUtc = MinuteVerdict.AsUtc(block.EndsAtUtc);
            if (_settled.TryGetValue(id, out var settledFor) && settledFor == endsUtc) continue;
            if (!settings.Admits(block) || endsUtc - startsUtc < MinBlockLength) continue;

            var left = endsUtc - nowUtc;
            if (left > Lead) continue;
            if (left < MinLeft)
            {
                _settled[id] = endsUtc;
                changed = true;
                continue;
            }

            if (!IsIn(block, nowUtc)) continue;
            if (isQuiet()) return null;

            _settled[id] = endsUtc;
            changed = true;
            var nudge = new Nudge(++_lastId, NudgeKind.BlockEnd, left, null, BlockEnd: Details(block, endsUtc, left, plan.Next));
            _outstanding = (nudge.Id, block);
            _withdrawn = false;
            return nudge;
        }

        return null;
    }

    /// <summary>
    /// The block a heads-up handed over to, for the ramp: the next block when it starts within <see cref="FollowsWithin"/>
    /// of the end, or null.
    /// </summary>
    public static long? HandsOverTo(BlockEndDetails details) =>
        details is { After: BlockEndAfter.Next, NextPlannerTaskId: > 0 and var nextId, NextStartsUtc: { } nextStartsUtc }
        && nextStartsUtc - details.EndsUtc <= FollowsWithin
            ? nextId
            : null;

    /// <summary>OK, or a press on a toast no longer current: either way nothing is left to decide.</summary>
    public bool Respond(int nudgeId)
    {
        if (_outstanding is not { } outstanding || outstanding.Id != nudgeId) return false;
        _outstanding = null;
        return true;
    }

    /// <summary>True once when the toast still out is out of date and should leave the notification centre.</summary>
    public bool TakeWithdrawn()
    {
        var withdrawn = _withdrawn;
        _withdrawn = false;
        return withdrawn;
    }

    /// <summary>Shows a heads-up now, so its text and button can be tried by hand.</summary>
    internal Nudge IssueForTest(DateTime nowUtc)
    {
        var block = new PlannedBlock
        {
            PlannerTaskId = -1, ActivityName = "Test plan", StartsAtUtc = nowUtc.AddMinutes(-40), EndsAtUtc = nowUtc.AddMinutes(5)
        };
        var next = new PlannedBlock
        {
            PlannerTaskId = -2, ActivityName = "Next plan", StartsAtUtc = block.EndsAtUtc, EndsAtUtc = block.EndsAtUtc.AddMinutes(30)
        };
        var nudge = new Nudge(++_lastId, NudgeKind.BlockEnd, Lead, null,
            BlockEnd: Details(block, block.EndsAtUtc, Lead, next));
        _outstanding = (nudge.Id, block);
        return nudge;
    }

    /// <summary>Started in the planner, or a minute on plan while it covered, recently.</summary>
    private bool IsIn(PlannedBlock block, DateTime nowUtc) =>
        block.InProgress
        || (_onPlanAt.TryGetValue(block.PlannerTaskId, out var at) && nowUtc - at <= RecentFor);

    private static BlockEndDetails Details(PlannedBlock block, DateTime endsUtc, TimeSpan left, PlannedBlock? next)
    {
        if (next is null)
            return new BlockEndDetails(block.PlannerTaskId, block.ActivityName, endsUtc, left, BlockEndAfter.Nothing);

        var nextStartsUtc = MinuteVerdict.AsUtc(next.StartsAtUtc);
        return nextStartsUtc < endsUtc
            ? new BlockEndDetails(block.PlannerTaskId, block.ActivityName, endsUtc, left, BlockEndAfter.Unknown)
            : new BlockEndDetails(block.PlannerTaskId, block.ActivityName, endsUtc, left, BlockEndAfter.Next,
                next.PlannerTaskId, next.ActivityName, nextStartsUtc);
    }

    private bool Prune(DateTime nowUtc)
    {
        foreach (var id in _onPlanAt.Where(o => nowUtc - o.Value > RememberFor).Select(o => o.Key).ToList())
            _onPlanAt.Remove(id);

        var stale = _settled.Where(s => s.Value + RememberFor < nowUtc).Select(s => s.Key).ToList();
        foreach (var id in stale) _settled.Remove(id);
        return stale.Count > 0;
    }

    private void Withdraw()
    {
        if (_outstanding is null) return;
        _outstanding = null;
        _withdrawn = true;
    }
}
