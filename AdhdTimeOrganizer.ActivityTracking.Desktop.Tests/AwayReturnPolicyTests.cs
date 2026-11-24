using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// "Away 47 min — what was it?" (prompt 23): which absences are asked about — short, long, a lock, a sleep, a break the user
/// started, a pause, a restart — what the toast offers, and what a press comes to.
/// </summary>
public class AwayReturnPolicyTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The poll loop's credited gap: polls a minute apart here are all seen.</summary>
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(2);

    private static readonly AwaySettings On = new(true, []);

    [Fact]
    public void AShortAbsence_IsNotAsked()
    {
        var policy = new AwayReturnPolicy();

        Away(policy, minutes: 14);

        Assert.Null(TakeDue(policy, T0.AddMinutes(14)));
    }

    /// <summary>The absence runs from the last input — the instant idle backdating took back from — to the first poll back.</summary>
    [Fact]
    public void ALongAbsence_IsAsked_FromTheLastInputToTheReturn()
    {
        var policy = new AwayReturnPolicy();

        Away(policy, minutes: 47);

        var nudge = TakeDue(policy, T0.AddMinutes(47));
        Assert.NotNull(nudge);
        Assert.Equal(NudgeKind.AwayReturn, nudge.Kind);
        Assert.Equal((T0, T0.AddMinutes(47)), (nudge.Away!.FromUtc, nudge.Away.ToUtc));
        Assert.Equal(TimeSpan.FromMinutes(47), nudge.Length);
        Assert.Null(TakeDue(policy, T0.AddMinutes(48))); // once
    }

    [Theory]
    [InlineData(4 * 60, true)]
    [InlineData(4 * 60 + 1, false)]
    public void PastTheCeiling_ItIsANight_NotAnAbsence(int minutes, bool asked)
    {
        var policy = new AwayReturnPolicy();

        Away(policy, minutes);

        Assert.Equal(asked, TakeDue(policy, T0.AddMinutes(minutes)) is not null);
    }

    /// <summary>A lock takes nothing back, so the absence starts at the last poll that found the user there.</summary>
    [Fact]
    public void ALock_IsAnAbsence_FromTheLastPresentPoll()
    {
        var policy = new AwayReturnPolicy();
        policy.Record(T0, isPresent: true, null, false, Gap);
        for (var m = 1; m < 30; m++) policy.Record(T0.AddMinutes(m), isPresent: false, null, false, Gap);
        policy.Record(T0.AddMinutes(30), isPresent: true, null, false, Gap);

        var nudge = TakeDue(policy, T0.AddMinutes(30));
        Assert.Equal((T0, T0.AddMinutes(30)), (nudge!.Away!.FromUtc, nudge.Away.ToUtc));
    }

    [Fact]
    public void ALockOvernight_IsNotAsked()
    {
        var policy = new AwayReturnPolicy();
        policy.Record(T0, isPresent: true, null, false, Gap);
        for (var m = 1; m < 9 * 60; m++) policy.Record(T0.AddMinutes(m), isPresent: false, null, false, Gap);
        policy.Record(T0.AddHours(9), isPresent: true, null, false, Gap);

        Assert.Null(TakeDue(policy, T0.AddHours(9)));
    }

    /// <summary>A sleep is a gap the poll loop didn't see: an absence from the poll before it, up to the ceiling.</summary>
    [Theory]
    [InlineData(40, true)]
    [InlineData(5 * 60, false)]
    public void ASleep_IsAnAbsence_UpToTheCeiling(int minutes, bool asked)
    {
        var policy = new AwayReturnPolicy();
        policy.Record(T0, isPresent: true, null, false, Gap);
        policy.Record(T0.AddMinutes(minutes), isPresent: true, null, false, Gap);

        var nudge = TakeDue(policy, T0.AddMinutes(minutes));
        Assert.Equal(asked, nudge is not null);
        if (asked) Assert.Equal(T0, nudge!.Away!.FromUtc);
    }

    /// <summary>"Taking a break" said what the time was: the absence it covers is not asked about, even past the timer.</summary>
    [Fact]
    public void ABreakTheUserStarted_IsNotAsked()
    {
        var policy = new AwayReturnPolicy();

        Away(policy, minutes: 40, inStartedBreak: true);

        Assert.Null(TakeDue(policy, T0.AddMinutes(40)));
    }

    /// <summary>...and a later absence, after the break ended, is asked about again.</summary>
    [Fact]
    public void AfterTheStartedBreak_TheNextAbsenceIsAsked()
    {
        var policy = new AwayReturnPolicy();
        Away(policy, minutes: 40, inStartedBreak: true);

        Away(policy, minutes: 30, start: T0.AddMinutes(41));

        Assert.Equal(T0.AddMinutes(41), TakeDue(policy, T0.AddMinutes(71))?.Away?.FromUtc);
    }

    /// <summary>A pause is the user choosing not to be watched: the gap it leaves is never asked about.</summary>
    [Fact]
    public void APause_IsNotAsked()
    {
        var policy = new AwayReturnPolicy();
        policy.Record(T0, isPresent: true, null, false, Gap);

        policy.Forget(); // paused; the poll loop stops
        policy.Record(T0.AddMinutes(50), isPresent: true, null, false, Gap); // resumed

        Assert.Null(TakeDue(policy, T0.AddMinutes(50)));
    }

    /// <summary>A pause taken while away: the absence before it is forgotten with it.</summary>
    [Fact]
    public void APauseDuringAnAbsence_IsNotAsked()
    {
        var policy = new AwayReturnPolicy();
        policy.Record(T0, isPresent: true, null, false, Gap);
        policy.Record(T0.AddMinutes(1), isPresent: false, T0, false, Gap);

        policy.Forget();
        policy.Record(T0.AddMinutes(30), isPresent: true, null, false, Gap);

        Assert.Null(TakeDue(policy, T0.AddMinutes(30)));
    }

    /// <summary>A restart knows nothing before it: the first polls only start watching, away or not.</summary>
    [Fact]
    public void ARestart_KnowsNoAbsence()
    {
        var policy = new AwayReturnPolicy();
        for (var m = 0; m < 40; m++) policy.Record(T0.AddMinutes(m), isPresent: false, null, false, Gap);
        policy.Record(T0.AddMinutes(40), isPresent: true, null, false, Gap);

        Assert.Null(TakeDue(policy, T0.AddMinutes(40)));
    }

    [Fact]
    public void WhileAwayOrQuiet_TheQuestionIsHeld_ThenLetGoAfterHalfAnHour()
    {
        var policy = new AwayReturnPolicy();
        Away(policy, minutes: 47);
        var returned = T0.AddMinutes(47);

        Assert.Null(policy.TakeDue(returned, On, isPresent: false, () => false));
        Assert.Null(policy.TakeDue(returned.AddMinutes(5), On, isPresent: true, () => true));
        Assert.NotNull(policy.TakeDue(returned.AddMinutes(10), On, isPresent: true, () => false));

        Away(policy, minutes: 47, start: T0.AddHours(2));
        Assert.Null(policy.TakeDue(T0.AddHours(2).AddMinutes(47) + AwayReturnPolicy.HoldFor + TimeSpan.FromSeconds(1), On, true, () => false));
    }

    [Fact]
    public void SwitchedOff_NothingIsAsked_AndAToastStillOutLeaves()
    {
        var policy = new AwayReturnPolicy();
        Away(policy, minutes: 47);
        Assert.NotNull(TakeDue(policy, T0.AddMinutes(47)));

        Away(policy, minutes: 30, start: T0.AddHours(1));
        Assert.Null(policy.TakeDue(T0.AddHours(1).AddMinutes(30), On with { Enabled = false }, true, () => false));
        Assert.True(policy.TakeWithdrawn());
        Assert.Null(TakeDue(policy, T0.AddHours(1).AddMinutes(31)));
    }

    /// <summary>
    /// The planned block is offered when it covered at least half of the absence — from the last answer's <c>planned</c> or its
    /// <c>next</c> — first, then the Settings answers, each activity once and at most two of them.
    /// </summary>
    [Fact]
    public void TheAnswers_ThePlannedBlockFirst_ThenTheChosen_EachOnce()
    {
        var policy = new AwayReturnPolicy();
        var meeting = Block(7, 70, "Team meeting", T0.AddMinutes(10), 60);
        policy.RecordPlan(new PlanView(T0.AddMinutes(-1), [], meeting, false, null));
        Away(policy, minutes: 47);

        var nudge = policy.TakeDue(T0.AddMinutes(47), new AwaySettings(true,
            [new AwayChoice(70, "Team meeting"), new AwayChoice(21, "Rest"), new AwayChoice(22, "Errands")]), true, () => false);

        Assert.Equal(
            [new AwayAnswer(70, "Team meeting", true), new AwayAnswer(21, "Rest", false)],
            nudge!.Away!.Answers);
    }

    [Fact]
    public void ABlockCoveringLessThanHalf_IsNotOffered()
    {
        var policy = new AwayReturnPolicy();
        policy.RecordPlan(new PlanView(T0.AddMinutes(-1), [Block(7, 70, "Thesis", T0.AddMinutes(-60), 80)], null, false, true));
        Away(policy, minutes: 47); // Thesis ends 20 min in: under half

        Assert.Empty(TakeDue(policy, T0.AddMinutes(47))!.Away!.Answers);
    }

    [Fact]
    public void APress_NamesTheAnswer_OrSomethingElse_OnceAndOnlyWhileCurrent()
    {
        var policy = new AwayReturnPolicy();
        Away(policy, minutes: 47);
        var nudge = policy.TakeDue(T0.AddMinutes(47), new AwaySettings(true, [new AwayChoice(21, "Rest")]), true, () => false)!;

        Assert.Equal(new AwayOutcome(T0, T0.AddMinutes(47), new AwayAnswer(21, "Rest", false)),
            policy.Respond(nudge.Id, NudgeResponse.AwayAnswer, 0, T0.AddMinutes(48)));
        Assert.Null(policy.Respond(nudge.Id, NudgeResponse.AwayAnswer, 0, T0.AddMinutes(48))); // answered already

        Away(policy, minutes: 30, start: T0.AddHours(1));
        var second = TakeDue(policy, T0.AddHours(1).AddMinutes(30))!;
        Assert.Equal(new AwayOutcome(T0.AddHours(1), T0.AddHours(1).AddMinutes(30), null),
            policy.Respond(second.Id, NudgeResponse.SomethingElse, null, T0.AddHours(1).AddMinutes(31)));
    }

    [Fact]
    public void APressAfterTwoHours_OrOnAMissingAnswer_SendsNothing()
    {
        var policy = new AwayReturnPolicy();
        Away(policy, minutes: 47);
        var nudge = TakeDue(policy, T0.AddMinutes(47))!;
        Assert.Null(policy.Respond(nudge.Id, NudgeResponse.AwayAnswer, 0, T0.AddMinutes(47) + AwayReturnPolicy.AnswerFor + TimeSpan.FromMinutes(1)));

        Away(policy, minutes: 30, start: T0.AddHours(4));
        var second = TakeDue(policy, T0.AddHours(4).AddMinutes(30))!;
        Assert.Null(policy.Respond(second.Id, NudgeResponse.AwayAnswer, 3, T0.AddHours(4).AddMinutes(31)));
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>
    /// Present at <paramref name="start"/>, gone from then (the first away poll a minute later says the last input was at
    /// <paramref name="start"/>), back <paramref name="minutes"/> after it — polls a minute apart.
    /// </summary>
    private static void Away(AwayReturnPolicy policy, int minutes, DateTime? start = null, bool inStartedBreak = false)
    {
        var from = start ?? T0;
        policy.Record(from, isPresent: true, null, false, Gap);
        policy.Record(from.AddMinutes(1), isPresent: false, from, inStartedBreak, Gap);
        for (var m = 2; m < minutes; m++) policy.Record(from.AddMinutes(m), isPresent: false, null, inStartedBreak, Gap);
        policy.Record(from.AddMinutes(minutes), isPresent: true, null, inStartedBreak, Gap);
    }

    private static Nudge? TakeDue(AwayReturnPolicy policy, DateTime nowUtc) => policy.TakeDue(nowUtc, On, isPresent: true, () => false);

    private static PlannedBlock Block(long id, long activityId, string name, DateTime startsUtc, int minutes) => new()
    {
        PlannerTaskId = id, ActivityId = activityId, ActivityName = name, StartsAtUtc = startsUtc, EndsAtUtc = startsUtc.AddMinutes(minutes)
    };
}
