using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// What one heartbeat answer says about the plan, for the start ramp: the blocks covering the minute, the next one today,
/// whether a timer runs, and whether the minute was on plan. <see cref="TimerRunning"/> null is a server from before the
/// ramp, which also can't say whether a block was started.
/// </summary>
public sealed record PlanView(
    DateTime WindowStartUtc, IReadOnlyList<PlannedBlock> Covering, PlannedBlock? Next, bool? TimerRunning, bool? OnPlan)
{
    public static PlanView From(DateTime windowStartUtc, HeartbeatResponse response) => new(
        MinuteVerdict.AsUtc(windowStartUtc), response.Planned, response.Next, response.TimerRunning,
        MinuteVerdict.From(windowStartUtc, response).OnPlan);
}

/// <summary>What a start-ramp toast says. <see cref="PlannedName"/> is user-authored: never log it.</summary>
/// <param name="LateBy">How long ago the block started when the toast was issued; under a minute is "starts now".</param>
/// <param name="CanDefer">False on the one repeat "In 2 minutes" asks for: it has no third chance.</param>
public sealed record StartRampDetails(
    long PlannerTaskId, string PlannedName, DateTime StartsUtc, DateTime EndsUtc, TimeSpan LateBy, bool CanDefer);

public sealed record StartRampSettings(bool Enabled, RampBlocks Blocks = RampBlocks.NotOptional)
{
    public static StartRampSettings From(AppConfig config) => new(config.StartRampEnabled, config.StartRampBlocks);

    /// <summary>
    /// Whether the user wants to be asked about this block. A server from before the flags reads every block as neither, so
    /// under <see cref="RampBlocks.Critical"/> it says nothing rather than guess.
    /// </summary>
    public bool Admits(PlannedBlock block) => Blocks switch
    {
        RampBlocks.All => true,
        RampBlocks.Critical => block.IsCritical,
        _ => !block.IsOptional
    };
}

/// <summary>
/// What <see cref="StartRampPolicy"/> knows, saved across restarts (<c>start-ramp.json</c>), so a restart neither asks
/// again about a block it already offered nor forgets an "In 2 minutes".
/// </summary>
/// <param name="Settled">Blocks the ramp is done with, by planner task id, each with its end for pruning.</param>
public sealed record StartRampState(Dictionary<long, DateTime> Settled, long? AgainBlockId, DateTime? AgainAtUtc);

/// <summary>
/// Decides when to offer a planned block at its start: "Thesis starts now (until 11:30). Start the timer?" Pure, like
/// <see cref="DriftPolicy"/> — the caller passes the time, each heartbeat answer (<see cref="PlanView"/>) and whether the
/// user is at the computer.
/// </summary>
/// <remarks>
/// <para>
/// <b>When.</b> At a block's start, from the answer's <c>next</c>, while the user is at the computer — or, sitting down
/// late, from the blocks covering the minute: "Thesis started 20 min ago". Late only while <see cref="MinLeftWhenLate"/>
/// is left; asking about a block nearly over only makes someone feel behind. The plan is trusted for
/// <see cref="FreshFor"/> after its minute, as the drift nudge trusts a minute: an older answer may describe a plan since
/// moved.
/// </para>
/// <para>
/// <b>Which blocks</b> — <see cref="RampBlocks"/>, from the planner's importance; All but Optional by default. A block left
/// out isn't settled, so changing the setting mid-block takes effect.
/// </para>
/// <para>
/// <b>Skipped</b> when the block is already under way: started in the planner (the web app's timer marks it), or the
/// minute was on plan. <b>Held</b> under the quiet conditions, and while a countdown, pomodoro or stopwatch runs: the user
/// is timing something already, and Start now would land on a dialog offering to throw it away. Stopping it is the moment
/// to move on to the plan.
/// </para>
/// <para>
/// <b>Not nagging.</b> One toast per block. <b>In 2 minutes</b> asks exactly once more, without that button, so it can't
/// become a snooze loop; <b>Start now</b> and <b>Not this one</b> settle the block, and so does ignoring the toast. A
/// toast still out leaves when the block is started, the user gets on with it, the block ends, or the block is no longer
/// on the plan (cancelled or moved, here or on the phone).
/// </para>
/// </remarks>
public sealed class StartRampPolicy
{
    internal static readonly TimeSpan FreshFor = DriftPolicy.FreshFor;
    internal static readonly TimeSpan OnTimeFor = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan MinLeftWhenLate = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(2);

    /// <summary>A settled block is remembered this long past its end: a block extended later keeps its id.</summary>
    private static readonly TimeSpan RememberFor = TimeSpan.FromDays(1);

    private readonly Dictionary<long, DateTime> _settled = new();

    private PlanView? _plan;
    private (long Id, DateTime AtUtc)? _again;
    private (int Id, PlannedBlock Block, bool CanDefer)? _outstanding;
    private bool _withdrawn;
    private int _lastId;

    public StartRampState Snapshot() => new(new Dictionary<long, DateTime>(_settled), _again?.Id, _again?.AtUtc);

    public void Restore(StartRampState state)
    {
        _settled.Clear();
        foreach (var (id, endsUtc) in state.Settled) _settled[id] = endsUtc;
        _again = state is { AgainBlockId: { } id2, AgainAtUtc: { } at } ? (id2, at) : null;
    }

    /// <summary>Folds in one heartbeat answer. A backlog minute's is skipped: it says nothing about now.</summary>
    public void Record(DateTime nowUtc, PlanView plan)
    {
        if (plan.WindowStartUtc < nowUtc - FreshFor) return;
        if (_plan is { } known && plan.WindowStartUtc < known.WindowStartUtc) return;
        _plan = plan;

        FollowTheToastOut(plan);
    }

    /// <summary>
    /// The toast still out, against a new answer. A minute at or past the block's start must list the block among those
    /// covering it, or the block has left the plan; an earlier minute can't say (it would be <c>next</c> at best, and another
    /// block may come first). A block still listed may have been extended, and the toast then stays to its new end. The test
    /// toast's block is not a planner task, so no answer lists it.
    /// </summary>
    private void FollowTheToastOut(PlanView plan)
    {
        if (_outstanding is not { } outstanding) return;

        var id = outstanding.Block.PlannerTaskId;
        if (IsUnderWay(id, plan))
        {
            Withdraw();
            return;
        }

        if (plan.Covering.FirstOrDefault(b => b.PlannerTaskId == id) is { } listed)
            _outstanding = (outstanding.Id, listed, outstanding.CanDefer);
        else if (id > 0 && plan.WindowStartUtc >= MinuteVerdict.AsUtc(outstanding.Block.StartsAtUtc))
            Withdraw();
    }

    /// <summary>
    /// The toast to show now, if any. <paramref name="isPresent"/>: someone is at the computer. <paramref name="isQuiet"/>
    /// is asked only when one is due. True from <paramref name="changed"/> when the saved state should be written.
    /// </summary>
    public Nudge? TakeDue(DateTime nowUtc, StartRampSettings settings, bool isPresent, Func<bool> isQuiet, out bool changed)
    {
        changed = Prune(nowUtc);
        if (_outstanding is { Block: var shown } && nowUtc >= MinuteVerdict.AsUtc(shown.EndsAtUtc))
            Withdraw();

        if (!settings.Enabled || !isPresent) return null;
        if (_plan is not { TimerRunning: { } timerRunning } plan || nowUtc - plan.WindowStartUtc > FreshFor) return null;

        foreach (var block in Started(plan, nowUtc))
        {
            var id = block.PlannerTaskId;
            if (!settings.Admits(block) || _settled.ContainsKey(id)) continue;

            var isAgain = _again?.Id == id;
            if (isAgain && nowUtc < _again!.Value.AtUtc) continue;

            var startsUtc = MinuteVerdict.AsUtc(block.StartsAtUtc);
            var endsUtc = MinuteVerdict.AsUtc(block.EndsAtUtc);
            var lateBy = nowUtc - startsUtc;
            if (block.InProgress || IsUnderWay(id, plan) || (lateBy > OnTimeFor && endsUtc - nowUtc < MinLeftWhenLate))
            {
                Settle(block);
                changed = true;
                continue;
            }

            if (timerRunning || isQuiet()) return null;

            Settle(block);
            changed = true;
            var nudge = new Nudge(++_lastId, NudgeKind.StartRamp, endsUtc - startsUtc, null,
                StartRamp: new StartRampDetails(id, block.ActivityName, startsUtc, endsUtc, lateBy, CanDefer: !isAgain));
            _outstanding = (nudge.Id, block, !isAgain);
            _withdrawn = false;
            return nudge;
        }

        return null;
    }

    /// <summary>
    /// Applies a button press: the block it was about, or null when the toast is no longer current — answered already,
    /// replaced by another block's, or withdrawn.
    /// </summary>
    public PlannedBlock? Respond(int nudgeId, NudgeResponse response, DateTime nowUtc, out bool changed)
    {
        changed = false;
        if (_outstanding is not { } outstanding || outstanding.Id != nudgeId) return null;
        _outstanding = null;

        if (response == NudgeResponse.Later && outstanding.CanDefer)
        {
            _again = (outstanding.Block.PlannerTaskId, nowUtc + AskAgainAfter);
            _settled.Remove(outstanding.Block.PlannerTaskId);
            changed = true;
        }
        return outstanding.Block;
    }

    /// <summary>True once when the toast still out is out of date and should leave the notification centre.</summary>
    public bool TakeWithdrawn()
    {
        var withdrawn = _withdrawn;
        _withdrawn = false;
        return withdrawn;
    }

    /// <summary>Shows a ramp toast now, so its text and buttons can be tried by hand.</summary>
    internal Nudge IssueForTest(DateTime nowUtc)
    {
        var block = new PlannedBlock
        {
            PlannerTaskId = -1, ActivityName = "Test plan", StartsAtUtc = nowUtc, EndsAtUtc = nowUtc.AddMinutes(45)
        };
        var nudge = new Nudge(++_lastId, NudgeKind.StartRamp, TimeSpan.FromMinutes(45), null,
            StartRamp: new StartRampDetails(-1, block.ActivityName, nowUtc, block.EndsAtUtc, TimeSpan.Zero, CanDefer: true));
        _outstanding = (nudge.Id, block, true);
        return nudge;
    }

    /// <summary>The blocks of the answer whose start has passed and whose end has not, earliest start first.</summary>
    private static IEnumerable<PlannedBlock> Started(PlanView plan, DateTime nowUtc) =>
        plan.Covering
            .Concat(plan.Next is { } next ? [next] : [])
            .DistinctBy(b => b.PlannerTaskId)
            .Where(b => MinuteVerdict.AsUtc(b.StartsAtUtc) <= nowUtc && nowUtc < MinuteVerdict.AsUtc(b.EndsAtUtc))
            .OrderBy(b => b.StartsAtUtc);

    /// <summary>Started in the planner, or the minute was spent on the plan while the block covered it.</summary>
    private static bool IsUnderWay(long plannerTaskId, PlanView plan) =>
        plan.Covering.Any(b => b.PlannerTaskId == plannerTaskId && (b.InProgress || plan.OnPlan == true));

    private void Settle(PlannedBlock block)
    {
        _settled[block.PlannerTaskId] = MinuteVerdict.AsUtc(block.EndsAtUtc);
        if (_again?.Id == block.PlannerTaskId) _again = null;
    }

    private bool Prune(DateTime nowUtc)
    {
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
