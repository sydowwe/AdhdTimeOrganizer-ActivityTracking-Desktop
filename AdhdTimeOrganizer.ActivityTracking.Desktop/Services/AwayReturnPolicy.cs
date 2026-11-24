using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>One of the activities the user set up in Settings as an away answer ("Break → Rest"). The name is user-authored.</summary>
public sealed record AwayChoice(long ActivityId, string Name);

/// <summary>A button on the away toast: an activity, and whether it is the planned block's. The name is user-authored.</summary>
public sealed record AwayAnswer(long ActivityId, string Name, bool IsPlanned);

/// <summary>What the away toast asks about: the span the user was away, and the activities it offers.</summary>
public sealed record AwayDetails(DateTime FromUtc, DateTime ToUtc, IReadOnlyList<AwayAnswer> Answers);

/// <summary>An absence answered with an activity: what <see cref="AwayReporter"/> sends.</summary>
public sealed record AwayAnswered(DateTime FromUtc, DateTime ToUtc, long ActivityId);

/// <summary>What a press on the away toast came to: the span, and the activity chosen — null for "Something else".</summary>
public sealed record AwayOutcome(DateTime FromUtc, DateTime ToUtc, AwayAnswer? Answer);

public sealed record AwaySettings(bool Enabled, IReadOnlyList<AwayChoice> Choices)
{
    public static AwaySettings From(AppConfig config) => new(config.AwayReturnEnabled, config.AwayAnswers);
}

/// <summary>
/// "Away 47 min — what was it?" (prompt 23). The tracker sees the computer and nothing else: a meeting in another room, lunch,
/// a call, a walk leave a hole in the day, and by the evening nobody remembers what filled it. The one moment the user still
/// knows is when they sit back down — so then, one toast, a few answers, one click. Pure, like <see cref="HyperfocusPolicy"/>:
/// the caller passes the time, what each poll saw and the heartbeat's plan.
/// </summary>
/// <remarks>
/// <para>
/// <b>An absence</b> starts at the last input before it — the instant idle backdating takes back from — or, for a lock or a
/// gap the poll loop didn't see (sleep, a hang), at the last poll that found the user present. It ends at the first poll that
/// finds them back. One of <see cref="MinAway"/> to <see cref="MaxAway"/> is asked about; past that it was a night, not an
/// absence. A call at the computer is presence (the caller counts the microphone), so it is never asked about.
/// </para>
/// <para>
/// <b>Not asked</b>: about a break the user started from a nudge ("Taking a break" — they said what it was), across a pause
/// (<see cref="Forget"/>: the user chose not to be watched), or after a restart (nothing known before it): the first poll after
/// either only starts watching.
/// </para>
/// <para>
/// <b>Answers.</b> The planned block that covered at least half of the absence, from the last heartbeat answer before it (its
/// <c>planned</c> and <c>next</c>), then the up to two activities from Settings, without repeating the block's. The toast
/// adds "Something else" (the web app's manual log, pre-filled) and closing it is "skip". A press answers within
/// <see cref="AnswerFor"/> of the return; the toast expires then.
/// </para>
/// <para>
/// <b>When.</b> At the return, to someone at the computer, held under the quiet conditions — it is a question, not a reproach,
/// and keeps no gap to the other families. One not shown within <see cref="HoldFor"/> of the return is let go: the moment
/// that made it easy to answer has passed. A newer absence replaces a question still held.
/// </para>
/// </remarks>
public sealed class AwayReturnPolicy
{
    internal static readonly TimeSpan MinAway = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan MaxAway = TimeSpan.FromHours(4);
    internal static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan AnswerFor = TimeSpan.FromHours(2);

    /// <summary>At most this many of the Settings answers; with the block and "Something else" a toast has four buttons.</summary>
    internal const int MaxChoices = 2;

    private DateTime? _lastPollUtc;
    private DateTime? _lastPresentUtc; // null until a poll since the start (or a pause) found someone there
    private DateTime? _awayFromUtc;
    private bool _chosenBreak;
    private (DateTime FromUtc, DateTime ToUtc)? _pending;
    private (int Id, AwayDetails Details)? _outstanding;
    private bool _withdrawn;
    private PlanView? _plan;
    private int _lastId;

    /// <summary>
    /// Folds in one poll. <paramref name="awayFromUtc"/> is the instant idle backdating took back from, on the first poll that
    /// found the user gone (null otherwise, and for a lock). <paramref name="inStartedBreak"/>: a break the user started from a
    /// nudge is open — read before the hyperfocus policy sees this poll, which ends it on the return.
    /// </summary>
    public void Record(DateTime nowUtc, bool isPresent, DateTime? awayFromUtc, bool inStartedBreak, TimeSpan maxObservedGap)
    {
        var last = _lastPollUtc;
        _lastPollUtc = nowUtc;

        // Nothing known before this poll (a start, a pause), or nobody seen since: an absence can't be measured yet.
        if (last is not { } lastPoll || _lastPresentUtc is not { } lastPresent)
        {
            if (isPresent) _lastPresentUtc = nowUtc;
            return;
        }

        var unseenGap = nowUtc - lastPoll > maxObservedGap;
        if (inStartedBreak) _chosenBreak = true;

        if (!isPresent)
        {
            _awayFromUtc ??= unseenGap ? lastPoll : awayFromUtc ?? lastPresent;
            return;
        }

        DateTime? fromUtc = _awayFromUtc ?? (unseenGap ? lastPoll : null);
        var chosenBreak = _chosenBreak;
        _awayFromUtc = null;
        _chosenBreak = false;
        _lastPresentUtc = nowUtc;

        if (fromUtc is not { } from || chosenBreak) return;
        var length = nowUtc - from;
        if (length < MinAway || length > MaxAway) return;

        _pending = (from, nowUtc);
    }

    /// <summary>A heartbeat answer: the plan the absence is matched against. The newest minute's wins.</summary>
    public void RecordPlan(PlanView plan)
    {
        if (_plan is { } known && plan.WindowStartUtc < known.WindowStartUtc) return;
        _plan = plan;
    }

    /// <summary>
    /// Tracking was paused: what was being watched is forgotten, so the gap the pause leaves is never asked about. A question
    /// already waiting stays — it is about time before the pause.
    /// </summary>
    public void Forget()
    {
        _lastPollUtc = null;
        _lastPresentUtc = null;
        _awayFromUtc = null;
        _chosenBreak = false;
    }

    /// <summary>The toast to show now, if any. <paramref name="isQuiet"/> is asked only when one is due.</summary>
    public Nudge? TakeDue(DateTime nowUtc, AwaySettings settings, bool isPresent, Func<bool> isQuiet)
    {
        if (!settings.Enabled)
        {
            _pending = null;
            if (_outstanding is not null) Withdraw();
            return null;
        }

        if (_pending is not { } pending) return null;
        if (nowUtc - pending.ToUtc > HoldFor)
        {
            _pending = null;
            return null;
        }
        if (!isPresent || isQuiet()) return null;

        _pending = null;
        var details = new AwayDetails(pending.FromUtc, pending.ToUtc, Answers(pending.FromUtc, pending.ToUtc, settings.Choices));
        var nudge = new Nudge(++_lastId, NudgeKind.AwayReturn, pending.ToUtc - pending.FromUtc, null, Away: details);
        _outstanding = (nudge.Id, details);
        _withdrawn = false;
        return nudge;
    }

    /// <summary>
    /// A press. Null when the toast is no longer current — answered already, replaced, or older than <see cref="AnswerFor"/> — so
    /// a toast clicked late in the notification centre sends nothing. <paramref name="choice"/> indexes the toast's answers.
    /// </summary>
    public AwayOutcome? Respond(int nudgeId, NudgeResponse response, int? choice, DateTime nowUtc)
    {
        if (_outstanding is not { } outstanding || outstanding.Id != nudgeId) return null;
        _outstanding = null;

        var details = outstanding.Details;
        if (nowUtc - details.ToUtc > AnswerFor) return null;

        return response switch
        {
            NudgeResponse.AwayAnswer when choice is { } i && i >= 0 && i < details.Answers.Count =>
                new AwayOutcome(details.FromUtc, details.ToUtc, details.Answers[i]),
            NudgeResponse.SomethingElse => new AwayOutcome(details.FromUtc, details.ToUtc, null),
            _ => null
        };
    }

    /// <summary>True once when the toast still out should leave the notification centre (the switch went off).</summary>
    public bool TakeWithdrawn()
    {
        var withdrawn = _withdrawn;
        _withdrawn = false;
        return withdrawn;
    }

    /// <summary>Shows the toast now, so its text and buttons can be tried by hand. Its answers are not activities: nothing is sent.</summary>
    internal Nudge IssueForTest(DateTime nowUtc)
    {
        var details = new AwayDetails(nowUtc.AddMinutes(-47), nowUtc,
            [new AwayAnswer(-1, "Test plan", true), new AwayAnswer(-2, "Test answer", false)]);
        var nudge = new Nudge(++_lastId, NudgeKind.AwayReturn, TimeSpan.FromMinutes(47), null, Away: details);
        _outstanding = (nudge.Id, details);
        return nudge;
    }

    /// <summary>The planned block (when one covered at least half of the absence), then the Settings answers, each activity once.</summary>
    private List<AwayAnswer> Answers(DateTime fromUtc, DateTime toUtc, IReadOnlyList<AwayChoice> choices)
    {
        var answers = new List<AwayAnswer>(MaxChoices + 1);
        if (PlannedDuring(fromUtc, toUtc) is { } block)
            answers.Add(new AwayAnswer(block.ActivityId, block.ActivityName, true));

        foreach (var choice in choices.Where(c => c.ActivityId > 0).Take(MaxChoices))
            if (answers.All(a => a.ActivityId != choice.ActivityId))
                answers.Add(new AwayAnswer(choice.ActivityId, choice.Name, false));

        return answers;
    }

    /// <summary>
    /// Of the blocks the last answer knew — those covering its minute and the next one starting after it — the one that overlaps
    /// the absence most, if it covers at least half of it. Nothing is sent while the user is away, so no answer knows more.
    /// </summary>
    private PlannedBlock? PlannedDuring(DateTime fromUtc, DateTime toUtc)
    {
        if (_plan is not { } plan) return null;

        var half = TimeSpan.FromTicks((toUtc - fromUtc).Ticks / 2);
        return plan.Covering.Append(plan.Next)
            .OfType<PlannedBlock>()
            .Where(b => b.ActivityId > 0)
            .Select(b => (Block: b, Overlap: Overlap(b, fromUtc, toUtc)))
            .Where(x => x.Overlap >= half)
            .OrderByDescending(x => x.Overlap)
            .Select(x => x.Block)
            .FirstOrDefault();
    }

    private static TimeSpan Overlap(PlannedBlock block, DateTime fromUtc, DateTime toUtc)
    {
        var start = MinuteVerdict.AsUtc(block.StartsAtUtc);
        var end = MinuteVerdict.AsUtc(block.EndsAtUtc);
        var overlap = (end < toUtc ? end : toUtc) - (start > fromUtc ? start : fromUtc);
        return overlap > TimeSpan.Zero ? overlap : TimeSpan.Zero;
    }

    private void Withdraw()
    {
        _outstanding = null;
        _withdrawn = true;
    }
}
