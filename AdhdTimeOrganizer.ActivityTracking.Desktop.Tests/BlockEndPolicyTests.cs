using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class BlockEndPolicyTests
{
    private static readonly BlockEndSettings On = new(Enabled: true);

    /// <summary>The block every case is about: Thesis, 11:00–11:45, started in the planner; Email follows at 11:45.</summary>
    private static readonly DateTime Eleven = new(2026, 11, 23, 11, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Ends = Eleven.AddMinutes(45);

    private static readonly PlannedBlock Thesis = Block(1, Eleven, 45) with { InProgress = true };

    private static readonly PlannedBlock Email = Block(2, Ends, 30, "Email");

    // ---- when it speaks ---------------------------------------------------

    /// <summary>Five minutes before the end, not before: "Thesis ends in 5 min — next: Email at 11:45".</summary>
    [Fact]
    public void WarnsFiveMinutesBeforeTheEnd_NamingTheNextBlock()
    {
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: Email);

        Assert.Null(run.Poll(Ends.AddMinutes(-5).AddSeconds(-10)));
        var nudge = run.Poll(Ends.AddMinutes(-5));

        Assert.NotNull(nudge);
        Assert.Equal(NudgeKind.BlockEnd, nudge.Kind);
        Assert.Equal(TimeSpan.FromMinutes(5), nudge.Length);
        Assert.Equal(new BlockEndDetails(1, "Thesis", Ends, TimeSpan.FromMinutes(5), BlockEndAfter.Next, 2, "Email", Ends),
            nudge.BlockEnd);
    }

    [Fact]
    public void WarnsOncePerBlock()
    {
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: Email);
        Assert.NotNull(run.Poll(Ends.AddMinutes(-5)));

        Assert.Null(run.Poll(Ends.AddMinutes(-5).AddSeconds(10)));
        run.Answer(Ends.AddMinutes(-5), covering: [Thesis], next: Email);
        Assert.Null(run.Poll(Ends.AddMinutes(-3)));
    }

    [Fact]
    public void SaysWhenNothingIsPlannedAfter()
    {
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: null);

        var details = run.Poll(Ends.AddMinutes(-5))!.BlockEnd!;

        Assert.Equal(BlockEndAfter.Nothing, details.After);
        Assert.Null(details.NextName);
    }

    /// <summary>The answer's <c>next</c> starts inside Thesis: it isn't what comes after, so the toast doesn't say it is.</summary>
    [Fact]
    public void ANextBlockOverlappingThisOne_IsNotNamedAsWhatFollows()
    {
        var call = Block(3, Ends.AddMinutes(-3), 10, "Call");
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: call);

        var details = run.Poll(Ends.AddMinutes(-5))!.BlockEnd!;

        Assert.Equal(BlockEndAfter.Unknown, details.After);
        Assert.Null(details.NextName);
    }

    /// <summary>Only to someone at the computer: away at 11:40, it waits for them and says the minutes as they are.</summary>
    [Fact]
    public void WaitsForSomeoneAtTheComputer()
    {
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: Email);

        Assert.Null(run.Poll(Ends.AddMinutes(-5), isPresent: false));
        run.Answer(Ends.AddMinutes(-6), covering: [Thesis], next: Email);

        Assert.Equal(TimeSpan.FromMinutes(4), run.Poll(Ends.AddMinutes(-4))!.BlockEnd!.Left);
    }

    // ---- only a block the user is in --------------------------------------

    /// <summary>Not started in the planner and no minute on plan: a warning would read as "you wasted it".</summary>
    [Fact]
    public void ABlockNotStarted_GetsNoWarning()
    {
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis with { InProgress = false }], next: Email, onPlan: false);

        Assert.Null(run.Poll(Ends.AddMinutes(-5)));
        run.Answer(Ends.AddMinutes(-5), covering: [Thesis with { InProgress = false }], next: Email);
        Assert.Null(run.Poll(Ends.AddMinutes(-3)));
    }

    /// <summary>Not started in the planner, but worked on: a minute on plan in the last ten counts, one off-plan minute since doesn't undo it.</summary>
    [Fact]
    public void ABlockWorkedOnRecently_CountsAsIn()
    {
        var notStarted = Thesis with { InProgress = false };
        var run = new Run();
        run.Answer(Ends.AddMinutes(-12), covering: [notStarted], onPlan: true);
        run.Answer(Ends.AddMinutes(-7), covering: [notStarted], next: Email, onPlan: false);

        Assert.NotNull(run.Poll(Ends.AddMinutes(-5)));
    }

    [Fact]
    public void AnOnPlanMinuteLongAgo_DoesNotCount()
    {
        var notStarted = Thesis with { InProgress = false };
        var run = new Run();
        run.Answer(Ends.AddMinutes(-20), covering: [notStarted], onPlan: true);
        run.Answer(Ends.AddMinutes(-7), covering: [notStarted], next: Email, onPlan: false);

        Assert.Null(run.Poll(Ends.AddMinutes(-5)));
    }

    /// <summary>Getting on with it inside the five minutes still earns the warning, with the minutes left.</summary>
    [Fact]
    public void GettingOnWithItLate_StillWarns_WithWhatIsLeft()
    {
        var notStarted = Thesis with { InProgress = false };
        var run = new Run();
        run.Answer(Ends.AddMinutes(-6), covering: [notStarted], next: Email, onPlan: false);
        Assert.Null(run.Poll(Ends.AddMinutes(-5)));

        run.Answer(Ends.AddMinutes(-5), covering: [notStarted], next: Email, onPlan: true);

        Assert.Equal(TimeSpan.FromMinutes(3), run.Poll(Ends.AddMinutes(-3))!.BlockEnd!.Left);
    }

    // ---- which blocks -----------------------------------------------------

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    public void ABlockShorterThanTwentyMinutes_GetsNone(int minutes, bool warned)
    {
        var meds = Block(4, Ends.AddMinutes(-minutes), minutes, "Meds") with { InProgress = true };
        var run = new Run();
        run.Answer(Ends.AddMinutes(-6), covering: [meds]);

        Assert.Equal(warned, run.Poll(Ends.AddMinutes(-5)) is not null);
    }

    [Fact]
    public void WhichBlocks_FollowsItsOwnSetting()
    {
        Nudge? Warned(PlannedBlock block, RampBlocks blocks)
        {
            var run = new Run { Settings = new BlockEndSettings(Enabled: true, blocks) };
            run.Answer(Ends.AddMinutes(-6), covering: [block]);
            return run.Poll(Ends.AddMinutes(-5));
        }

        Assert.Null(Warned(Thesis with { IsOptional = true }, RampBlocks.NotOptional));
        Assert.NotNull(Warned(Thesis with { IsOptional = true }, RampBlocks.All));
        Assert.Null(Warned(Thesis, RampBlocks.Critical));
        Assert.NotNull(Warned(Thesis with { IsCritical = true }, RampBlocks.Critical));
    }

    /// <summary>On by default with its own choice, All but Optional; a config.json from before the setting reads the same.</summary>
    [Fact]
    public void Settings_DefaultOn_AllButOptional_AndSurviveTheConfigFile()
    {
        var old = JsonSerializer.Deserialize<AppConfig>("{}")!;
        Assert.Equal((true, RampBlocks.NotOptional), (old.BlockEndEnabled, old.BlockEndBlocks));

        var saved = JsonSerializer.Serialize(new AppConfig { BlockEndEnabled = false, BlockEndBlocks = RampBlocks.Critical });
        var read = JsonSerializer.Deserialize<AppConfig>(saved)!;
        Assert.Equal((false, RampBlocks.Critical), (read.BlockEndEnabled, read.BlockEndBlocks));
    }

    [Fact]
    public void Off_SaysNothing()
    {
        var run = new Run { Settings = new BlockEndSettings(Enabled: false) };
        run.Answer(Ends.AddMinutes(-6), covering: [Thesis]);

        Assert.Null(run.Poll(Ends.AddMinutes(-5)));
    }

    /// <summary>An answer older than three minutes may describe a plan moved since.</summary>
    [Fact]
    public void AStaleAnswer_SaysNothing()
    {
        var run = new Run();
        run.Answer(Ends.AddMinutes(-15), covering: [Thesis]);

        Assert.Null(run.Poll(Ends.AddMinutes(-5)));
    }

    // ---- quiet conditions -------------------------------------------------

    /// <summary>Held, not spent: it shows when the quiet ends, saying the minutes as they are then.</summary>
    [Fact]
    public void AQuietCondition_HoldsIt()
    {
        var run = new Run { IsQuiet = true };
        run.Answer(Ends.AddMinutes(-6), covering: [Thesis], next: Email);
        Assert.Null(run.Poll(Ends.AddMinutes(-5)));

        run.IsQuiet = false;
        run.Answer(Ends.AddMinutes(-4), covering: [Thesis], next: Email);

        Assert.Equal(TimeSpan.FromMinutes(2), run.Poll(Ends.AddMinutes(-2))!.BlockEnd!.Left);
    }

    /// <summary>Under a minute left when the quiet ends: a "1 min" warning is no warning, so it is let go.</summary>
    [Fact]
    public void HeldPastTheLastMinute_ItIsLetGo()
    {
        var run = new Run { IsQuiet = true };
        run.Answer(Ends.AddMinutes(-6), covering: [Thesis], next: Email);
        Assert.Null(run.Poll(Ends.AddMinutes(-5)));

        run.IsQuiet = false;
        run.Answer(Ends.AddMinutes(-2), covering: [Thesis], next: Email);

        Assert.Null(run.Poll(Ends.AddSeconds(-50)));
        Assert.Null(run.Poll(Ends.AddSeconds(-30)));
    }

    // ---- a block that changes ---------------------------------------------

    /// <summary>Extended in the planner after the warning: the toast leaves, and a new one comes before the new end.</summary>
    [Fact]
    public void ABlockExtended_MovesTheWarningToItsNewEnd()
    {
        var extended = Thesis with { EndsAtUtc = Ends.AddMinutes(15) };
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: Email);
        var first = run.Poll(Ends.AddMinutes(-5))!;
        Assert.False(run.Policy.TakeWithdrawn());

        run.Answer(Ends.AddMinutes(-4), covering: [extended]);
        Assert.True(run.Policy.TakeWithdrawn());
        Assert.False(run.Respond(first));

        Assert.Null(run.Poll(Ends.AddMinutes(-3)));
        run.Answer(Ends.AddMinutes(8), covering: [extended]);
        var second = run.Poll(Ends.AddMinutes(10));

        Assert.Equal((Ends.AddMinutes(15), TimeSpan.FromMinutes(5)), (second!.BlockEnd!.EndsUtc, second.BlockEnd.Left));
    }

    /// <summary>Extended before the warning: nothing at the old end's five minutes, the warning at the new one's.</summary>
    [Fact]
    public void ABlockExtendedBeforehand_IsWarnedAboutAtItsNewEnd()
    {
        var extended = Thesis with { EndsAtUtc = Ends.AddMinutes(15) };
        var run = new Run();
        run.Answer(Ends.AddMinutes(-6), covering: [extended]);

        Assert.Null(run.Poll(Ends.AddMinutes(-5)));
        run.Answer(Ends.AddMinutes(8), covering: [extended]);
        Assert.NotNull(run.Poll(Ends.AddMinutes(10)));
    }

    /// <summary>Cancelled, completed or moved away with the toast out: a minute inside it no longer lists it.</summary>
    [Fact]
    public void ABlockCancelled_TakesTheToastAway()
    {
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: Email);
        var nudge = run.Poll(Ends.AddMinutes(-5))!;

        run.Answer(Ends.AddMinutes(-4), covering: [], next: Email);

        Assert.True(run.Policy.TakeWithdrawn());
        Assert.False(run.Policy.TakeWithdrawn());
        Assert.False(run.Respond(nudge));
    }

    [Fact]
    public void ABlockCancelledBeforehand_GetsNoWarning()
    {
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: Email);
        run.Answer(Ends.AddMinutes(-6), covering: [], next: Email);

        Assert.Null(run.Poll(Ends.AddMinutes(-5)));
    }

    [Fact]
    public void TheToast_Leaves_WhenTheBlockEnds()
    {
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: Email);
        run.Poll(Ends.AddMinutes(-5));

        run.Poll(Ends.AddSeconds(-1));
        Assert.False(run.Policy.TakeWithdrawn());
        run.Poll(Ends);
        Assert.True(run.Policy.TakeWithdrawn());
    }

    [Fact]
    public void OK_AnswersTheToast_Once()
    {
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: Email);
        var nudge = run.Poll(Ends.AddMinutes(-5))!;

        Assert.True(run.Respond(nudge));
        Assert.False(run.Respond(nudge));
        run.Poll(Ends);
        Assert.False(run.Policy.TakeWithdrawn());
    }

    /// <summary>The toast tried by hand has no planner task behind it, so no answer lists it: it stays until its end.</summary>
    [Fact]
    public void TheTestToast_Stays_ThoughNoAnswerListsIt()
    {
        var run = new Run();
        run.Policy.IssueForTest(Ends.AddMinutes(-5));

        run.Answer(Ends.AddMinutes(-4), covering: []);

        Assert.False(run.Policy.TakeWithdrawn());
    }

    // ---- a restart --------------------------------------------------------

    /// <summary>Through JSON as <c>block-end.json</c> keeps it: no second warning after a restart, but one for a new end.</summary>
    [Fact]
    public void ARestart_DoesNotWarnTwice_ButStillWarnsAboutANewEnd()
    {
        var run = new Run();
        run.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: Email);
        Assert.NotNull(run.Poll(Ends.AddMinutes(-5)));

        var saved = JsonSerializer.Deserialize<BlockEndState>(JsonSerializer.Serialize(run.Policy.Snapshot()))!;
        var restarted = new Run();
        restarted.Policy.Restore(saved);

        restarted.Answer(Ends.AddMinutes(-5), covering: [Thesis], next: Email);
        Assert.Null(restarted.Poll(Ends.AddMinutes(-4)));

        var extended = Thesis with { EndsAtUtc = Ends.AddMinutes(30) };
        restarted.Answer(Ends.AddMinutes(23), covering: [extended]);
        Assert.NotNull(restarted.Poll(Ends.AddMinutes(25)));
    }

    // ---- the next block's start ramp --------------------------------------

    /// <summary>
    /// Email starts right at Thesis's end, and the heads-up named it: no "Email starts now" five minutes later. Moved on to it,
    /// the ramp stays quiet for good; not moved on three minutes in, the ramp asks.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AfterTheHeadsUp_TheNextBlocksRamp_AsksOnlyIfTheUserHasNotMovedOn(bool movedOn)
    {
        var both = new Both();
        both.Answer(Ends.AddMinutes(-7), covering: [Thesis], next: Email);
        var headsUp = both.BlockEnd.Poll(Ends.AddMinutes(-5))!;
        both.HandOver(headsUp, Ends.AddMinutes(-5));

        both.Answer(Ends.AddMinutes(-2), covering: [Thesis], next: Email);
        Assert.Null(both.RampPoll(Ends));
        Assert.Null(both.RampPoll(Ends.AddMinutes(2)));

        both.Answer(Ends, covering: [Email], onPlan: movedOn);
        var ramp = both.RampPoll(Ends.AddMinutes(3));

        if (movedOn)
        {
            Assert.Null(ramp);
            both.Answer(Ends.AddMinutes(2), covering: [Email], onPlan: false);
            Assert.Null(both.RampPoll(Ends.AddMinutes(4)));
        }
        else
        {
            Assert.Equal((2L, TimeSpan.FromMinutes(3)), (ramp!.StartRamp!.PlannerTaskId, ramp.StartRamp.LateBy));
        }
    }

    /// <summary>Without a heads-up — Thesis wasn't being worked on — Email's ramp asks at its start as always.</summary>
    [Fact]
    public void WithoutAHeadsUp_TheNextBlocksRamp_AsksAtItsStart()
    {
        var both = new Both();
        both.Answer(Ends.AddMinutes(-7), covering: [Thesis with { InProgress = false }], next: Email);
        Assert.Null(both.BlockEnd.Poll(Ends.AddMinutes(-5)));

        both.Answer(Ends.AddMinutes(-2), covering: [Thesis with { InProgress = false }], next: Email);

        Assert.Equal(2L, both.RampPoll(Ends)!.StartRamp!.PlannerTaskId);
    }

    /// <summary>Only a block following within ten minutes is handed over; one at 13:00 gets its ramp at 13:00.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(135, false)]
    public void HandsOver_OnlyToABlockFollowingWithinTenMinutes(int gapMinutes, bool handsOver)
    {
        var details = new BlockEndDetails(1, "Thesis", Ends, TimeSpan.FromMinutes(5), BlockEndAfter.Next, 2, "Email",
            Ends.AddMinutes(gapMinutes));

        Assert.Equal(handsOver ? 2L : (long?)null, BlockEndPolicy.HandsOverTo(details));
        Assert.Null(BlockEndPolicy.HandsOverTo(details with { After = BlockEndAfter.Unknown }));
        // The test toast's next block is not a planner task.
        Assert.Null(BlockEndPolicy.HandsOverTo(details with { NextPlannerTaskId = -2 }));
    }

    /// <summary>The ramp's half survives a restart between the heads-up and the next block's start, and a file without it reads.</summary>
    [Fact]
    public void TheHandOver_SurvivesARestart_AndAnOlderRampFileStillReads()
    {
        var ramp = new StartRampPolicy();
        Assert.True(ramp.Announce(2, Ends.AddMinutes(-5)));
        Assert.False(ramp.Announce(2, Ends.AddMinutes(-4)));

        var restarted = new StartRampPolicy();
        restarted.Restore(JsonSerializer.Deserialize<StartRampState>(JsonSerializer.Serialize(ramp.Snapshot()))!);
        restarted.Record(Ends.AddSeconds(45), new PlanView(Ends.AddMinutes(-1), [], Email, false, null));

        Assert.Null(restarted.TakeDue(Ends, new StartRampSettings(true), true, () => false, out _));

        var old = JsonSerializer.Deserialize<StartRampState>("""{"Settled":{},"AgainBlockId":null,"AgainAtUtc":null}""")!;
        new StartRampPolicy().Restore(old);
    }

    // ---- helpers ----------------------------------------------------------

    private static PlannedBlock Block(long id, DateTime startsUtc, int minutes, string name = "Thesis") => new()
    {
        PlannerTaskId = id,
        ActivityId = id * 10,
        ActivityName = name,
        StartsAtUtc = startsUtc,
        EndsAtUtc = startsUtc.AddMinutes(minutes)
    };

    private static PlanView Plan(DateTime windowStart, PlannedBlock[]? covering, PlannedBlock? next, bool? onPlan) =>
        new(windowStart, covering ?? [], next, TimerRunning: false, onPlan);

    /// <summary>The send loop's answers and the poll loop's questions, against one policy.</summary>
    private sealed class Run
    {
        public BlockEndPolicy Policy { get; } = new();
        public BlockEndSettings Settings { get; set; } = On;
        public bool IsQuiet { get; set; }

        /// <summary>The heartbeat's answer for the minute starting at <paramref name="windowStart"/>, received 105 s after it began.</summary>
        public void Answer(DateTime windowStart, PlannedBlock[]? covering = null, PlannedBlock? next = null, bool? onPlan = null) =>
            Policy.Record(windowStart.AddSeconds(105), Plan(windowStart, covering, next, onPlan));

        public Nudge? Poll(DateTime nowUtc, bool isPresent = true) => Policy.TakeDue(nowUtc, Settings, isPresent, () => IsQuiet, out _);

        public bool Respond(Nudge nudge) => Policy.Respond(nudge.Id);
    }

    /// <summary>The heads-up and the start ramp fed the same answers, the way <c>NudgeService</c> feeds them.</summary>
    private sealed class Both
    {
        public Run BlockEnd { get; } = new();
        private readonly StartRampPolicy _ramp = new();

        public void Answer(DateTime windowStart, PlannedBlock[]? covering = null, PlannedBlock? next = null, bool? onPlan = null)
        {
            BlockEnd.Answer(windowStart, covering, next, onPlan);
            _ramp.Record(windowStart.AddSeconds(105), Plan(windowStart, covering, next, onPlan));
        }

        public void HandOver(Nudge headsUp, DateTime nowUtc)
        {
            if (BlockEndPolicy.HandsOverTo(headsUp.BlockEnd!) is { } nextId) _ramp.Announce(nextId, nowUtc);
        }

        public Nudge? RampPoll(DateTime nowUtc) => _ramp.TakeDue(nowUtc, new StartRampSettings(true), true, () => false, out _);
    }
}
