using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class HyperfocusPolicyTests
{
    private static readonly NudgeSettings Defaults = new(
        Enabled: true,
        StretchAfter: TimeSpan.FromMinutes(90),
        AppStretchAfter: TimeSpan.FromMinutes(120),
        BreakLength: TimeSpan.FromMinutes(5),
        BreakTimer: TimeSpan.FromMinutes(10));

    private static readonly NudgeSettings AppOnly = Defaults with { StretchAfter = TimeSpan.Zero };

    [Fact]
    public void Nudges_AfterNinetyMinutesWithoutABreak_NotBefore()
    {
        var run = new Run();

        run.For(Minutes(89));
        Assert.Empty(run.Shown);

        run.For(Minutes(1));
        var nudge = Assert.Single(run.Shown);
        Assert.Equal(NudgeKind.Stretch, nudge.Kind);
        Assert.Equal(Minutes(90), nudge.Length);
    }

    /// <summary>The kettle doesn't end a stretch; five minutes away does.</summary>
    [Fact]
    public void AShortAbsence_DoesNotEndTheStretch_ABreakDoes()
    {
        var shortAbsence = new Run();
        shortAbsence.For(Minutes(60));
        shortAbsence.For(Minutes(4), present: false);
        shortAbsence.For(Minutes(26));
        Assert.Equal(NudgeKind.Stretch, Assert.Single(shortAbsence.Shown).Kind);

        var realBreak = new Run();
        realBreak.For(Minutes(60));
        realBreak.For(Minutes(5), present: false);
        realBreak.For(Minutes(60));
        Assert.Empty(realBreak.Shown);
        // The new stretch starts at the first poll that sees the user back.
        Assert.Equal(Minutes(60) - Run.Step, realBreak.Policy.GetStatus(realBreak.Now).StretchLength);
    }

    /// <summary>Sleep or a paused tracker: nobody knows what happened, so a gap as long as a break is one.</summary>
    [Fact]
    public void AnUnobservedGap_AsLongAsABreak_CountsAsOne()
    {
        var asleep = new Run();
        asleep.For(Minutes(80));
        asleep.Skip(TimeSpan.FromHours(8));
        asleep.For(Minutes(20));
        Assert.Empty(asleep.Shown);

        var hiccup = new Run();
        hiccup.For(Minutes(80));
        hiccup.Skip(Minutes(2));
        hiccup.For(Minutes(8));
        Assert.Single(hiccup.Shown);
    }

    [Fact]
    public void AQuietCondition_HoldsTheNudge_UntilItEnds_AndIsOnlyCheckedOnceANudgeIsDue()
    {
        var run = new Run();

        run.For(Minutes(89));
        Assert.Equal(0, run.QuietChecks); // the microphone check reads the registry; not on every poll

        run.IsQuiet = true;
        run.For(Minutes(40));
        Assert.Empty(run.Shown);
        Assert.True(run.QuietChecks > 0);

        run.IsQuiet = false;
        run.Poll();
        Assert.Equal(Minutes(129), Assert.Single(run.Shown).Length);
    }

    [Fact]
    public void AnIgnoredNudge_ComesBackAfterHalfAnHour()
    {
        var run = new Run();
        run.For(Minutes(90));

        run.For(Minutes(29));
        Assert.Single(run.Shown);

        run.For(Minutes(1));
        Assert.Equal(2, run.Shown.Count(n => n.Kind == NudgeKind.Stretch));
    }

    [Fact]
    public void FiveMoreMinutes_ComesBackAfterFiveMinutes()
    {
        var run = new Run();
        run.For(Minutes(90));
        Assert.True(run.Respond(run.Shown[^1], NudgeResponse.Snooze));

        run.For(Minutes(4));
        Assert.Single(run.Shown);

        run.For(Minutes(1));
        Assert.Equal(2, run.Shown.Count);
        Assert.Equal(Minutes(95), run.Shown[^1].Length);
    }

    [Fact]
    public void NotNow_IsNotAskedAgainInThatStretch_ButIsInTheNext()
    {
        var run = new Run(Defaults with { AppStretchAfter = TimeSpan.Zero });
        run.For(Minutes(90));
        run.Respond(run.Shown[^1], NudgeResponse.NotNow);

        run.For(Minutes(180));
        Assert.Single(run.Shown);

        run.For(Minutes(5), present: false);
        run.For(Minutes(90) + Run.Step); // the new stretch starts at the first poll that sees the user back
        Assert.Equal(2, run.Shown.Count);
    }

    /// <summary>
    /// "Taking a break" silences stretch nudges until the break timer ends, then says so — even in a quiet
    /// condition, because the user asked for that one. What comes after is a new stretch.
    /// </summary>
    [Fact]
    public void TakingABreak_EndsWithABreakOverNudge_EvenWhenQuiet_AndStartsANewStretch()
    {
        var run = new Run();
        run.For(Minutes(90));
        run.Respond(run.Shown[^1], NudgeResponse.TakeBreak);
        Assert.Equal(run.Now + Minutes(10), run.Policy.GetStatus(run.Now).BreakEndsUtc);

        run.IsQuiet = true;
        run.For(Minutes(9));
        Assert.Single(run.Shown);

        run.For(Minutes(1));
        var breakOver = run.Shown[^1];
        Assert.Equal(NudgeKind.BreakOver, breakOver.Kind);
        Assert.Equal(Minutes(10), breakOver.Length);
        Assert.Null(run.Policy.GetStatus(run.Now).BreakEndsUtc);

        run.IsQuiet = false;
        run.For(Minutes(89));
        Assert.Equal(2, run.Shown.Count);
    }

    [Fact]
    public void FiveMoreMinutesOfBreak_ExtendsIt_AndTheNextNudgeCountsAllOfIt()
    {
        var run = new Run();
        run.For(Minutes(90));
        run.Respond(run.Shown[^1], NudgeResponse.TakeBreak);
        run.For(Minutes(10), present: false);
        run.Respond(run.Shown[^1], NudgeResponse.Snooze);

        run.For(Minutes(5), present: false);

        var breakOver = run.Shown[^1];
        Assert.Equal(NudgeKind.BreakOver, breakOver.Kind);
        Assert.Equal(Minutes(15), breakOver.Length);
    }

    /// <summary>Alt-tab to chat and back doesn't end "2 h straight in Rider"; five minutes elsewhere does.</summary>
    [Fact]
    public void AnAppStretch_SurvivesShortSwitches_AndEndsAfterABreakFromTheApp()
    {
        var shortSwitch = new Run(AppOnly);
        shortSwitch.For(Minutes(60), "rider");
        shortSwitch.For(Minutes(3), "chrome");
        shortSwitch.For(Minutes(60), "rider");
        var nudge = Assert.Single(shortSwitch.Shown);
        Assert.Equal(NudgeKind.AppStretch, nudge.Kind);
        Assert.Equal("Rider", nudge.AppName);
        Assert.Equal(Minutes(120), nudge.Length); // foreground time, not the three minutes in chrome

        var realSwitch = new Run(AppOnly);
        realSwitch.For(Minutes(60), "rider");
        realSwitch.For(Minutes(5), "chrome");
        realSwitch.For(Minutes(60), "rider");
        Assert.Empty(realSwitch.Shown);
    }

    /// <summary>An ignored window (the desktop, the Start menu) isn't the app, but isn't a new one either.</summary>
    [Fact]
    public void AnIgnoredWindow_InterruptsTheAppStretch_WithoutStartingOne()
    {
        var run = new Run(AppOnly);
        run.For(Minutes(60), "rider");
        run.For(Minutes(5), app: null);
        run.For(Minutes(60), "rider");

        Assert.Empty(run.Shown);
        Assert.Equal(Minutes(125), run.Policy.GetStatus(run.Now).StretchLength); // still at the computer throughout
    }

    [Fact]
    public void ANudgeOfTheOtherKind_WaitsTenMinutesAfterTheLastOne()
    {
        var run = new Run(Defaults with { AppStretchAfter = Minutes(95) });
        run.For(Minutes(90));
        Assert.Equal(NudgeKind.Stretch, Assert.Single(run.Shown).Kind);

        run.For(Minutes(9));
        Assert.Single(run.Shown);

        run.For(Minutes(1));
        Assert.Equal(NudgeKind.AppStretch, run.Shown[^1].Kind);
    }

    /// <summary>A toast clicked late in the notification centre, after its stretch ended, changes nothing.</summary>
    [Fact]
    public void AResponseToANudgeThatIsNoLongerCurrent_ChangesNothing_AndItsToastIsWithdrawn()
    {
        var run = new Run();
        run.For(Minutes(90));
        var stale = run.Shown[^1];
        run.Policy.TakeWithdrawn();

        run.For(Minutes(5), present: false);

        Assert.Contains(NudgeKind.Stretch, run.Policy.TakeWithdrawn());
        Assert.False(run.Respond(stale, NudgeResponse.TakeBreak));
        Assert.Null(run.Policy.GetStatus(run.Now).BreakEndsUtc);
    }

    [Fact]
    public void ANudgeAnsweredOnce_IgnoresASecondAnswer()
    {
        var run = new Run();
        run.For(Minutes(90));
        var nudge = run.Shown[^1];

        Assert.True(run.Respond(nudge, NudgeResponse.Snooze));
        Assert.False(run.Respond(nudge, NudgeResponse.TakeBreak));
    }

    [Fact]
    public void TurnedOff_ShowsNoStretchNudges_ButABreakAlreadyRunningStillEnds()
    {
        var run = new Run();
        run.For(Minutes(90));
        run.Respond(run.Shown[^1], NudgeResponse.TakeBreak);

        run.Settings = Defaults with { Enabled = false };
        run.For(Minutes(200));

        Assert.Equal([NudgeKind.Stretch, NudgeKind.BreakOver], run.Shown.Select(n => n.Kind));
    }

    [Fact]
    public void AChangedThreshold_AppliesToTheStretchAlreadyRunning()
    {
        var run = new Run();
        run.For(Minutes(50));
        Assert.Empty(run.Shown);

        run.Settings = Defaults with { StretchAfter = Minutes(45) };
        run.Poll();

        Assert.Single(run.Shown);
    }

    private static TimeSpan Minutes(int minutes) => TimeSpan.FromMinutes(minutes);

    /// <summary>A poll loop: one poll every <see cref="Step"/>, starting with a first poll at nine.</summary>
    private sealed class Run
    {
        public static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

        public HyperfocusPolicy Policy { get; } = new(maxObservedGap: TimeSpan.FromSeconds(30));
        public NudgeSettings Settings { get; set; }
        public DateTime Now { get; private set; } = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        public bool IsQuiet { get; set; }
        public int QuietChecks { get; private set; }
        public List<Nudge> Shown { get; } = [];

        private string? _app = "rider";
        private bool _present = true;

        public Run(NudgeSettings? settings = null)
        {
            Settings = settings ?? Defaults;
            Poll();
        }

        /// <summary>Polls for <paramref name="duration"/> with the user in <paramref name="app"/>, or away.</summary>
        public void For(TimeSpan duration, string? app = "rider", bool present = true)
        {
            _app = app;
            _present = present;
            var end = Now + duration;
            while (Now < end)
            {
                Now += Step;
                Poll();
            }
        }

        /// <summary>Time passing with no poll at all.</summary>
        public void Skip(TimeSpan gap) => Now += gap;

        public void Poll()
        {
            var app = _present ? _app : null;
            Policy.Record(Now, _present, app, app is null ? null : char.ToUpperInvariant(app[0]) + app[1..], Settings);

            var nudge = Policy.TakeDue(Now, Settings, () =>
            {
                QuietChecks++;
                return IsQuiet;
            });
            if (nudge is not null) Shown.Add(nudge);
        }

        public bool Respond(Nudge nudge, NudgeResponse response) => Policy.Respond(nudge.Id, response, Now, Settings);
    }
}
