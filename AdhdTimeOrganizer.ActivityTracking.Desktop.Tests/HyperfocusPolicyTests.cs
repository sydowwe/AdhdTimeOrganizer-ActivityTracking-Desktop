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

    /// <summary>A break spent at the desk: "5 more minutes" moves its end, the server is told, and the next toast counts all of it.</summary>
    [Fact]
    public void FiveMoreMinutesOfBreak_ExtendsIt_AndTheNextNudgeCountsAllOfIt()
    {
        var run = new Run();
        run.For(Minutes(90));
        run.Respond(run.Shown[^1], NudgeResponse.TakeBreak);
        var start = run.Now;
        run.For(Minutes(10));
        run.Respond(run.Shown[^1], NudgeResponse.Snooze);
        Assert.Equal(new BreakReport(start, run.Now + Minutes(5), null), run.Reports[^1]);

        run.For(Minutes(5));

        var breakOver = run.Shown[^1];
        Assert.Equal(NudgeKind.BreakOver, breakOver.Kind);
        Assert.Equal(Minutes(15), breakOver.Length);
    }

    // ---- prompt 10: the break speaks where the user is ---------------------------------------------------------------

    /// <summary>
    /// Phone in use at the end — from the desk's side, away at the end: the toast is held (the server hands the break to the
    /// phone), and coming back ends the break without it. Only the start and the end are reported.
    /// </summary>
    [Fact]
    public void AwayAtTheEnd_HoldsTheToast_AndTheReturnEndsTheBreakWithoutIt()
    {
        var run = new Run();
        run.For(Minutes(90));
        run.Respond(run.Shown[^1], NudgeResponse.TakeBreak);
        var start = run.Now;
        Assert.Equal([new BreakReport(start, start + Minutes(10), null)], run.Reports);

        run.For(Minutes(30), present: false);
        Assert.Equal(NudgeKind.Stretch, Assert.Single(run.Shown).Kind);
        Assert.Null(run.Policy.GetStatus(run.Now).BreakEndsUtc); // held past its end: no countdown left to show

        run.For(Minutes(1));
        var back = start + Minutes(30) + Run.Step;
        Assert.Equal(new BreakReport(start, start + Minutes(10), back), run.Reports[^1]);
        Assert.Equal(NudgeKind.Stretch, Assert.Single(run.Shown).Kind);
        Assert.Equal(Minutes(1) - Run.Step, run.Policy.GetStatus(run.Now).StretchLength); // a new stretch from the return
    }

    /// <summary>Back at the desk before the end: the break is over, so nobody is told it is — no toast at the end.</summary>
    [Fact]
    public void BackAtTheDeskBeforeTheEnd_EndsTheBreakSilently()
    {
        var run = new Run();
        run.For(Minutes(90));
        run.Respond(run.Shown[^1], NudgeResponse.TakeBreak);
        var start = run.Now;

        run.For(Minutes(1));           // still at the desk after the press: not a return
        run.For(Minutes(3), present: false);
        run.For(Minutes(20));

        Assert.Equal(NudgeKind.Stretch, Assert.Single(run.Shown).Kind);
        Assert.Equal(new BreakReport(start, start + Minutes(10), start + Minutes(4) + Run.Step), run.Reports[^1]);
        Assert.Equal(2, run.Reports.Count);
    }

    /// <summary>
    /// At the desk at the end (the phone face down, or nowhere): the desk speaks, as it always did, and "I'm back" ends the
    /// break on the server too.
    /// </summary>
    [Fact]
    public void AtTheDeskAtTheEnd_TheDeskSpeaks_AndImBackEndsTheBreak()
    {
        var run = new Run();
        run.For(Minutes(90));
        run.Respond(run.Shown[^1], NudgeResponse.TakeBreak);
        var start = run.Now;

        run.For(Minutes(10));
        Assert.Equal(NudgeKind.BreakOver, run.Shown[^1].Kind);
        Assert.Single(run.Reports); // the toast itself is not an end: "5 more minutes" may still follow

        Assert.True(run.Respond(run.Shown[^1], NudgeResponse.Dismiss));
        Assert.Equal(new BreakReport(start, start + Minutes(10), run.Now), run.Reports[^1]);
    }

    /// <summary>A sleep or a pause during the break is not being at the desk: the first poll after it ends the break.</summary>
    [Fact]
    public void AnUnobservedGapDuringTheBreak_CountsAsAway()
    {
        var run = new Run();
        run.For(Minutes(90));
        run.Respond(run.Shown[^1], NudgeResponse.TakeBreak);

        run.Skip(Minutes(40));
        run.Poll();

        Assert.Equal(run.Now, run.Reports[^1].EndedUtc);
        Assert.DoesNotContain(run.Shown, n => n.Kind == NudgeKind.BreakOver);
    }

    /// <summary>
    /// A toast shown and left unanswered: the stretch began at the toast, so a later trip to the kettle ends the break (and
    /// takes the stale toast away) without starting the stretch over.
    /// </summary>
    [Fact]
    public void AfterAnUnansweredToast_AShortAbsenceEndsTheBreak_ButNotTheStretch()
    {
        var run = new Run();
        run.For(Minutes(90));
        run.Respond(run.Shown[^1], NudgeResponse.TakeBreak);
        run.For(Minutes(10));
        Assert.Equal(NudgeKind.BreakOver, run.Shown[^1].Kind);
        run.Policy.TakeWithdrawn();

        run.For(Minutes(30));
        run.For(Minutes(2), present: false);
        run.For(Minutes(1));

        Assert.NotNull(run.Reports[^1].EndedUtc);
        Assert.Contains(NudgeKind.BreakOver, run.Policy.TakeWithdrawn());
        Assert.Equal(Minutes(33) - Run.Step, run.Policy.GetStatus(run.Now).StretchLength); // from the poll after the toast
    }

    /// <summary>Only a break the user started is reported: one the poll loop detects is in the server's ledger already.</summary>
    [Fact]
    public void ADetectedBreak_IsNotReported()
    {
        var run = new Run();
        run.For(Minutes(60));
        run.For(Minutes(10), present: false);
        run.For(Minutes(10));

        Assert.Empty(run.Reports);
    }

    /// <summary>The tray's test toast makes believe a break ran so its buttons work; the server never hears of it.</summary>
    [Fact]
    public void TheTestToastsBreak_IsNeverReported()
    {
        var run = new Run();
        var test = run.Policy.IssueForTest(NudgeKind.BreakOver, run.Now);

        Assert.True(run.Respond(test, NudgeResponse.Snooze));
        run.For(Minutes(5));
        Assert.True(run.Respond(run.Shown[^1], NudgeResponse.Dismiss));

        Assert.Empty(run.Reports);
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
        public List<BreakReport> Reports { get; } = [];

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
            TakeReport();
        }

        public bool Respond(Nudge nudge, NudgeResponse response)
        {
            var current = Policy.Respond(nudge.Id, response, Now, Settings);
            TakeReport();
            return current;
        }

        private void TakeReport()
        {
            if (Policy.TakeBreakReport() is { } report) Reports.Add(report);
        }
    }
}
