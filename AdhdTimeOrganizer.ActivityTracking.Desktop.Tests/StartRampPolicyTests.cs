using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class StartRampPolicyTests
{
    private static readonly StartRampSettings On = new(Enabled: true);

    /// <summary>The block every case is about: Thesis, 11:00–11:45.</summary>
    private static readonly DateTime Eleven = new(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc);

    private static readonly PlannedBlock Thesis = Block(1, Eleven, 45);

    // ---- when it speaks ---------------------------------------------------

    /// <summary>The answer for 10:58 names Thesis as next: nothing at 10:59, the toast at 11:00 sharp.</summary>
    [Fact]
    public void OffersTheNextBlock_AtItsStart_NotBefore()
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-2), next: Thesis);

        Assert.Null(run.Poll(Eleven.AddSeconds(-10)));
        var nudge = run.Poll(Eleven);

        Assert.NotNull(nudge);
        Assert.Equal(NudgeKind.StartRamp, nudge.Kind);
        Assert.Equal(TimeSpan.FromMinutes(45), nudge.Length);
        Assert.Equal(new StartRampDetails(1, "Thesis", Eleven, Eleven.AddMinutes(45), TimeSpan.Zero, CanDefer: true), nudge.StartRamp);
    }

    [Fact]
    public void AsksOncePerBlock()
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-2), next: Thesis);
        Assert.NotNull(run.Poll(Eleven));

        Assert.Null(run.Poll(Eleven.AddSeconds(10)));
        run.Answer(Eleven, covering: [Thesis]);
        Assert.Null(run.Poll(Eleven.AddMinutes(2)));
    }

    /// <summary>Only to someone at the computer: away at 11:00, the toast waits for them.</summary>
    [Fact]
    public void WaitsForSomeoneAtTheComputer()
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-1), next: Thesis);

        Assert.Null(run.Poll(Eleven, isPresent: false));
        var nudge = run.Poll(Eleven.AddMinutes(1));

        Assert.Equal(TimeSpan.FromMinutes(1), nudge!.StartRamp!.LateBy);
    }

    /// <summary>Sitting down at 11:20, from the blocks covering the first minute sent: "Thesis started 21 min ago".</summary>
    [Fact]
    public void SittingDownLate_OffersTheBlockStillRunning()
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(20), covering: [Thesis]);

        var nudge = run.Poll(Eleven.AddMinutes(21));

        Assert.Equal(TimeSpan.FromMinutes(21), nudge!.StartRamp!.LateBy);
    }

    /// <summary>Late, and less than ten minutes left: asking only makes someone feel behind. And it doesn't ask later either.</summary>
    [Fact]
    public void SittingDownLate_WithLittleLeft_SaysNothing()
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(35), covering: [Thesis]);
        Assert.Null(run.Poll(Eleven.AddMinutes(36)));

        run.Answer(Eleven.AddMinutes(36), covering: [Thesis]);
        Assert.Null(run.Poll(Eleven.AddMinutes(37)));
    }

    /// <summary>A short block is still offered at its start — the ten minutes only apply once it is under way.</summary>
    [Fact]
    public void AShortBlock_IsOfferedAtItsStart()
    {
        var meds = Block(2, Eleven, 5, "Meds");
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-1), next: meds);

        Assert.Equal("Meds", run.Poll(Eleven.AddSeconds(30))!.StartRamp!.PlannedName);
    }

    // ---- when it doesn't --------------------------------------------------

    [Fact]
    public void AStartedBlock_IsNotOffered()
    {
        var run = new Run();
        run.Answer(Eleven, covering: [Thesis with { InProgress = true }]);

        Assert.Null(run.Poll(Eleven.AddMinutes(1)));
    }

    /// <summary>Already on it — the minute resolved on plan — so there is nothing to start.</summary>
    [Fact]
    public void AnOnPlanMinute_MeansItIsUnderWay()
    {
        var run = new Run();
        run.Answer(Eleven, covering: [Thesis], onPlan: true);
        Assert.Null(run.Poll(Eleven.AddMinutes(1)));

        run.Answer(Eleven.AddMinutes(1), covering: [Thesis], onPlan: false);
        Assert.Null(run.Poll(Eleven.AddMinutes(2)));
    }

    /// <summary>Timing something already (a countdown, a pomodoro, a stopwatch): its end is the moment to move on to the plan.</summary>
    [Fact]
    public void ARunningTimer_HoldsIt_UntilTheTimerStops()
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-1), next: Thesis, timerRunning: true);
        Assert.Null(run.Poll(Eleven));

        run.Answer(Eleven.AddMinutes(4), covering: [Thesis], timerRunning: true);
        Assert.Null(run.Poll(Eleven.AddMinutes(5)));

        run.Answer(Eleven.AddMinutes(5), covering: [Thesis], timerRunning: false);
        Assert.Equal(TimeSpan.FromMinutes(6), run.Poll(Eleven.AddMinutes(6))!.StartRamp!.LateBy);
    }

    [Fact]
    public void AQuietCondition_HoldsIt_AndDoesNotSpendIt()
    {
        var run = new Run { IsQuiet = true };
        run.Answer(Eleven.AddMinutes(-1), next: Thesis);
        Assert.Null(run.Poll(Eleven));

        run.IsQuiet = false;
        Assert.NotNull(run.Poll(Eleven.AddMinutes(1)));
    }

    /// <summary>An answer older than three minutes may describe a plan moved since; a backlog minute says nothing about now.</summary>
    [Fact]
    public void AStaleAnswer_OrABacklogMinute_SaysNothing()
    {
        var stale = new Run();
        stale.Answer(Eleven.AddMinutes(-10), next: Thesis);
        Assert.Null(stale.Poll(Eleven));

        var backlog = new Run();
        backlog.Answer(Eleven.AddMinutes(-30), next: Thesis, receivedAt: Eleven);
        Assert.Null(backlog.Poll(Eleven));
    }

    /// <summary>A server from before the ramp can't say whether a block was started: offering would be a guess.</summary>
    [Fact]
    public void AnOlderServer_SaysNothing()
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-1), covering: [], next: Thesis, timerRunning: null);

        Assert.Null(run.Poll(Eleven));
    }

    [Fact]
    public void Off_SaysNothing()
    {
        var run = new Run { Settings = new StartRampSettings(Enabled: false) };
        run.Answer(Eleven.AddMinutes(-1), next: Thesis);

        Assert.Null(run.Poll(Eleven));
    }

    // ---- the answers ------------------------------------------------------

    /// <summary>"In 2 minutes" asks exactly once more, and the repeat has no "In 2 minutes" of its own.</summary>
    [Fact]
    public void InTwoMinutes_AsksOnceMore_WithoutTheButton()
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-1), next: Thesis);
        var first = run.Poll(Eleven)!;

        Assert.Equal(Thesis, run.Respond(first, NudgeResponse.Later, Eleven.AddSeconds(20)));
        run.Answer(Eleven, covering: [Thesis]);
        Assert.Null(run.Poll(Eleven.AddMinutes(2)));

        var again = run.Poll(Eleven.AddMinutes(2).AddSeconds(30));
        Assert.NotNull(again);
        Assert.False(again.StartRamp!.CanDefer);

        run.Respond(again, NudgeResponse.Later, Eleven.AddMinutes(3));
        run.Answer(Eleven.AddMinutes(4), covering: [Thesis]);
        Assert.Null(run.Poll(Eleven.AddMinutes(6)));
    }

    [Fact]
    public void InTwoMinutes_StandsDown_WhenTheBlockGetsStartedMeanwhile()
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-1), next: Thesis);
        run.Respond(run.Poll(Eleven)!, NudgeResponse.Later, Eleven);

        run.Answer(Eleven.AddMinutes(1), covering: [Thesis with { InProgress = true }]);

        Assert.Null(run.Poll(Eleven.AddMinutes(3)));
    }

    [Theory]
    [InlineData(NudgeResponse.StartNow)]
    [InlineData(NudgeResponse.NotThisOne)]
    public void StartNow_AndNotThisOne_SettleTheBlock_AndASecondPressIsNotCurrent(NudgeResponse response)
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-1), next: Thesis);
        var nudge = run.Poll(Eleven)!;

        Assert.Equal(Thesis, run.Respond(nudge, response, Eleven));
        Assert.Null(run.Respond(nudge, response, Eleven));

        run.Answer(Eleven, covering: [Thesis]);
        Assert.Null(run.Poll(Eleven.AddMinutes(2)));
    }

    // ---- the toast leaving ------------------------------------------------

    [Fact]
    public void TheToast_Leaves_WhenTheBlockIsStarted_AndALatePressIsNotCurrent()
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-1), next: Thesis);
        var nudge = run.Poll(Eleven)!;
        Assert.False(run.Policy.TakeWithdrawn());

        run.Answer(Eleven, covering: [Thesis with { InProgress = true }]);

        Assert.True(run.Policy.TakeWithdrawn());
        Assert.False(run.Policy.TakeWithdrawn());
        Assert.Null(run.Respond(nudge, NudgeResponse.StartNow, Eleven.AddMinutes(2)));
    }

    [Fact]
    public void TheToast_Leaves_WhenTheBlockEnds()
    {
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-1), next: Thesis);
        run.Poll(Eleven);

        run.Poll(Eleven.AddMinutes(45));

        Assert.True(run.Policy.TakeWithdrawn());
    }

    /// <summary>A second block starting while the first one's toast is ignored replaces it: the old press is no longer current.</summary>
    [Fact]
    public void TheNextBlock_ReplacesAnIgnoredToast()
    {
        var email = Block(2, Eleven.AddMinutes(45), 30, "Email");
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-1), next: Thesis);
        var thesis = run.Poll(Eleven)!;

        run.Answer(Eleven.AddMinutes(43), covering: [Thesis], next: email);
        var next = run.Poll(Eleven.AddMinutes(45));

        Assert.Equal("Email", next!.StartRamp!.PlannedName);
        Assert.Null(run.Respond(thesis, NudgeResponse.StartNow, Eleven.AddMinutes(45)));
    }

    // ---- a restart --------------------------------------------------------

    /// <summary>The saved state, through JSON as <c>start-ramp.json</c> keeps it: a restart neither asks again nor forgets "In 2 minutes".</summary>
    [Fact]
    public void ARestart_DoesNotAskAgain_AndStillAsksTheOneMore()
    {
        var email = Block(2, Eleven.AddMinutes(10), 30, "Email");
        var run = new Run();
        run.Answer(Eleven.AddMinutes(-1), next: Thesis);
        run.Poll(Eleven);
        run.Answer(Eleven.AddMinutes(8), covering: [Thesis], next: email);
        run.Respond(run.Poll(Eleven.AddMinutes(10))!, NudgeResponse.Later, Eleven.AddMinutes(10));

        var saved = JsonSerializer.Deserialize<StartRampState>(JsonSerializer.Serialize(run.Policy.Snapshot()))!;
        var restarted = new Run();
        restarted.Policy.Restore(saved);

        restarted.Answer(Eleven.AddMinutes(10), covering: [Thesis, email]);
        Assert.Null(restarted.Poll(Eleven.AddMinutes(11)));
        var again = restarted.Poll(Eleven.AddMinutes(12));
        Assert.Equal("Email", again!.StartRamp!.PlannedName);
        Assert.False(again.StartRamp.CanDefer);
    }

    // ---- PlanView ---------------------------------------------------------

    [Fact]
    public void PlanView_ReadsTheHeartbeatsAnswer()
    {
        var response = new HeartbeatResponse
        {
            Planned = [Thesis],
            Next = Block(2, Eleven.AddHours(1), 30, "Email"),
            TimerRunning = true,
            Resolution = [new ResolvedTime { Target = "Activity", Name = "Thesis", ActiveSeconds = 40, OnPlan = true }]
        };

        var plan = PlanView.From(Eleven, response);

        Assert.Equal((Eleven, 1, 2L, (bool?)true, (bool?)true),
            (plan.WindowStartUtc, plan.Covering.Count, plan.Next!.PlannerTaskId, plan.TimerRunning, plan.OnPlan));
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

    /// <summary>The send loop's answers and the poll loop's questions, against one policy.</summary>
    private sealed class Run
    {
        public StartRampPolicy Policy { get; } = new();
        public StartRampSettings Settings { get; set; } = On;
        public bool IsQuiet { get; set; }

        /// <summary>The heartbeat's answer for the minute starting at <paramref name="windowStart"/>, received 105 s after it began unless said otherwise.</summary>
        public void Answer(DateTime windowStart, PlannedBlock[]? covering = null, PlannedBlock? next = null, bool? timerRunning = false,
            bool? onPlan = null, DateTime? receivedAt = null) =>
            Policy.Record(receivedAt ?? windowStart.AddSeconds(105), new PlanView(windowStart, covering ?? [], next, timerRunning, onPlan));

        public Nudge? Poll(DateTime nowUtc, bool isPresent = true) => Policy.TakeDue(nowUtc, Settings, isPresent, () => IsQuiet, out _);

        public PlannedBlock? Respond(Nudge nudge, NudgeResponse response, DateTime nowUtc) => Policy.Respond(nudge.Id, response, nowUtc, out _);
    }
}
