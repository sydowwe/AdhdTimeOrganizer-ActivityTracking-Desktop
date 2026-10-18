using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class DriftPolicyTests
{
    private static readonly DriftSettings Defaults = new(Enabled: true, After: TimeSpan.FromMinutes(10));

    private static readonly PlannedBlock Thesis = Block(1, "Thesis");

    [Fact]
    public void Nudges_AfterTenOffPlanMinutesInABlock_NotBefore()
    {
        var run = new Run();

        run.Minutes(9, onPlan: false, "YouTube");
        Assert.Empty(run.Shown);

        run.Minutes(1, onPlan: false, "YouTube");
        var nudge = Assert.Single(run.Shown);
        Assert.Equal(NudgeKind.Drift, nudge.Kind);
        Assert.Equal(TimeSpan.FromMinutes(10), nudge.Length);
        Assert.Equal("YouTube", nudge.AppName);
        Assert.Equal(new DriftDetails("Thesis", Thesis.EndsAtUtc), nudge.Drift);
    }

    /// <summary>Unmapped apps are neither on nor off the plan: they don't count, and they don't reset.</summary>
    [Fact]
    public void UnmappedMinutes_NeitherCountNorReset()
    {
        var run = new Run();

        run.Minutes(6, onPlan: false, "YouTube");
        run.Minutes(3, onPlan: null);
        run.Minutes(3, onPlan: false, "YouTube");
        Assert.Empty(run.Shown);

        run.Minutes(1, onPlan: false, "YouTube");
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(run.Shown).Length);
    }

    [Fact]
    public void AnOnPlanMinute_StartsTheCountAgain()
    {
        var run = new Run();

        run.Minutes(9, onPlan: false, "YouTube");
        run.Minutes(1, onPlan: true);
        run.Minutes(9, onPlan: false, "YouTube");

        Assert.Empty(run.Shown);
    }

    /// <summary>"Last 10 min" is never stitched across a lunch break.</summary>
    [Fact]
    public void AGapBetweenOffPlanMinutes_LongerThanFive_StartsTheCountAgain()
    {
        var run = new Run();

        run.Minutes(8, onPlan: false, "YouTube");
        run.Skip(TimeSpan.FromMinutes(6));
        run.Minutes(8, onPlan: false, "YouTube");
        Assert.Empty(run.Shown);

        run.Minutes(2, onPlan: false, "YouTube");
        Assert.Single(run.Shown);
    }

    [Fact]
    public void AtMostOneNudgePerBlock_AndANewBlockStartsAgain()
    {
        var run = new Run();

        run.Minutes(40, onPlan: false, "YouTube");
        Assert.Single(run.Shown);

        run.Block = Block(2, "Email");
        run.Minutes(10, onPlan: false, "YouTube");
        Assert.Equal(2, run.Shown.Count);
        Assert.Equal("Email", run.Shown[^1].Drift!.PlannedName);
    }

    [Fact]
    public void Snooze_AsksForOneMore_ThirtyMinutesLater_IfStillOffPlan()
    {
        var run = new Run();
        run.Minutes(10, onPlan: false, "YouTube");
        Assert.True(run.Respond(run.Shown[^1], NudgeResponse.Snooze));

        run.Minutes(29, onPlan: false, "YouTube");
        Assert.Single(run.Shown);

        run.Minutes(1, onPlan: false, "YouTube");
        Assert.Equal(2, run.Shown.Count);
        Assert.Equal(TimeSpan.FromMinutes(40), run.Shown[^1].Length);

        run.Minutes(60, onPlan: false, "YouTube");
        Assert.Equal(2, run.Shown.Count);
    }

    [Fact]
    public void Snooze_ThenBackOnPlan_NudgesNoMore_UntilTheUserDriftsAgain()
    {
        var run = new Run();
        run.Minutes(10, onPlan: false, "YouTube");
        run.Respond(run.Shown[^1], NudgeResponse.Snooze);

        run.Minutes(5, onPlan: true);
        run.Minutes(24, onPlan: false, "Reddit");
        Assert.Single(run.Shown); // snoozed for 30 minutes from the press

        run.Minutes(1, onPlan: false, "Reddit");
        Assert.Equal(2, run.Shown.Count);
        Assert.Equal("Reddit", run.Shown[^1].AppName);
        Assert.Equal(TimeSpan.FromMinutes(25), run.Shown[^1].Length); // counted from the return to the plan, not the first drift
    }

    [Theory]
    [InlineData(NudgeResponse.Dismiss)]
    [InlineData(NudgeResponse.SwitchPlan)]
    [InlineData(NudgeResponse.TurnOff)]
    public void BackToIt_SwitchPlan_OrTurnOff_SettleTheBlock(NudgeResponse response)
    {
        var run = new Run();
        run.Minutes(10, onPlan: false, "YouTube");
        Assert.True(run.Respond(run.Shown[^1], response));

        run.Minutes(60, onPlan: false, "YouTube");
        Assert.Single(run.Shown);
    }

    /// <summary>Minutes sent from the outbox after a stretch offline say nothing about now.</summary>
    [Fact]
    public void ABacklogOfMinutes_IsIgnored()
    {
        var run = new Run { SendDelay = TimeSpan.FromMinutes(20) };

        run.Minutes(30, onPlan: false, "YouTube");

        Assert.Empty(run.Shown);
    }

    [Fact]
    public void AQuietCondition_HoldsTheNudge_WhileTheDriftGoesOn()
    {
        var run = new Run { IsQuiet = true };
        run.Minutes(15, onPlan: false, "YouTube");
        Assert.Empty(run.Shown);

        run.IsQuiet = false;
        run.Minutes(1, onPlan: false, "YouTube");
        Assert.Equal(TimeSpan.FromMinutes(16), Assert.Single(run.Shown).Length);
    }

    [Fact]
    public void AQuietCondition_OutlastingTheDrift_DropsTheNudge()
    {
        var run = new Run { IsQuiet = true };
        run.Minutes(12, onPlan: false, "YouTube");
        run.Skip(TimeSpan.FromMinutes(10)); // the call ran on with nothing sent: away, or idle
        run.IsQuiet = false;
        run.Poll();

        Assert.Empty(run.Shown);
    }

    /// <summary>A toast still in the notification centre leaves it once the user is back on plan; a late press changes nothing.</summary>
    [Fact]
    public void BackOnPlan_WithdrawsTheToast_AndALatePressIsIgnored()
    {
        var run = new Run();
        run.Minutes(10, onPlan: false, "YouTube");
        var nudge = run.Shown[^1];
        Assert.False(run.Policy.TakeWithdrawn());

        run.Minutes(1, onPlan: true);
        Assert.True(run.Policy.TakeWithdrawn());
        Assert.False(run.Policy.TakeWithdrawn());
        Assert.False(run.Respond(nudge, NudgeResponse.Snooze));
    }

    [Fact]
    public void TheBlockEnding_WithdrawsTheToast()
    {
        var run = new Run();
        run.Minutes(10, onPlan: false, "YouTube");

        run.Block = null;
        run.Minutes(1, onPlan: null);

        Assert.True(run.Policy.TakeWithdrawn());
    }

    [Fact]
    public void NamesWhereMostOfTheOffPlanTimeWent()
    {
        var run = new Run();
        run.Minutes(3, onPlan: false, "Reddit");
        run.Minutes(7, onPlan: false, "YouTube");

        Assert.Equal("YouTube", Assert.Single(run.Shown).AppName);
    }

    [Fact]
    public void Off_OrNothingPlanned_NeverNudges()
    {
        var off = new Run { Settings = Defaults with { Enabled = false } };
        off.Minutes(30, onPlan: false, "YouTube");
        Assert.Empty(off.Shown);

        var unplanned = new Run { Block = null };
        unplanned.Minutes(30, onPlan: false, "YouTube");
        Assert.Empty(unplanned.Shown);
    }

    [Fact]
    public void Settings_NeverNudgeSoonerThanTenMinutes() =>
        Assert.Equal(TimeSpan.FromMinutes(10), DriftSettings.From(new AppConfig { DriftNudgeMinutes = 2 }).After);

    // ---- MinuteVerdict ---------------------------------------------------

    /// <summary>Seconds decide, and unmapped time doesn't tip it: 20 s on plan against 15 s off is on plan, whatever the 25 s unmapped were.</summary>
    [Fact]
    public void Verdict_WeighsTheJudgedSeconds_AndLeavesUnmappedTimeOut()
    {
        var verdict = MinuteVerdict.From(new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc), Response(
            Slice(25, null),
            Slice(20, true, "Thesis"),
            Slice(10, false, "YouTube"),
            Slice(5, false, "Reddit")));

        Assert.True(verdict.OnPlan);
        Assert.Equal("YouTube", verdict.OffPlanName);
        Assert.Equal(Thesis, verdict.Block);
    }

    [Fact]
    public void Verdict_IsOffPlan_WhenMoreWentElsewhere_AndNullWhenNothingCouldBeJudged()
    {
        var at = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

        Assert.False(MinuteVerdict.From(at, Response(Slice(10, true, "Thesis"), Slice(30, false, "YouTube"))).OnPlan);
        Assert.Null(MinuteVerdict.From(at, Response(Slice(60, null))).OnPlan);
        Assert.Null(MinuteVerdict.From(at, new HeartbeatResponse()).OnPlan);
    }

    // ---- helpers ---------------------------------------------------------

    private static PlannedBlock Block(long id, string name) => new()
    {
        PlannerTaskId = id,
        ActivityId = id * 10,
        ActivityName = name,
        StartsAtUtc = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc),
        EndsAtUtc = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc)
    };

    private static ResolvedTime Slice(int seconds, bool? onPlan, string? name = null) =>
        new() { Target = name is null ? null : "Activity", Name = name, ActiveSeconds = seconds, OnPlan = onPlan };

    private static HeartbeatResponse Response(params ResolvedTime[] slices) =>
        new() { Resolution = [.. slices], Planned = [Thesis] };

    /// <summary>
    /// The send loop: one minute window after another, each answered a little after it closed (or
    /// <see cref="SendDelay"/> after, for a backlog), and the policy asked after each.
    /// </summary>
    private sealed class Run
    {
        private DateTime _nextWindowStart = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

        public DriftPolicy Policy { get; } = new();
        public DriftSettings Settings { get; set; } = Defaults;
        public PlannedBlock? Block { get; set; } = Thesis;
        public TimeSpan SendDelay { get; set; } = TimeSpan.FromSeconds(65);
        public bool IsQuiet { get; set; }
        public DateTime Now { get; private set; } = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
        public List<Nudge> Shown { get; } = [];

        public void Minutes(int count, bool? onPlan, string? offPlanName = null)
        {
            for (var i = 0; i < count; i++)
            {
                var windowStart = _nextWindowStart;
                _nextWindowStart = windowStart.AddMinutes(1);
                Now = windowStart + SendDelay;

                Policy.Record(Now, new MinuteVerdict(windowStart, Block, Block is null ? null : onPlan, offPlanName));
                Poll();
            }
        }

        /// <summary>Time passing with no minute sent: away, locked, paused.</summary>
        public void Skip(TimeSpan gap)
        {
            _nextWindowStart += gap;
            Now += gap;
        }

        public void Poll()
        {
            if (Policy.TakeDue(Now, Settings, () => IsQuiet) is { } nudge) Shown.Add(nudge);
        }

        public bool Respond(Nudge nudge, NudgeResponse response) => Policy.Respond(nudge.Id, response, Now);
    }
}
