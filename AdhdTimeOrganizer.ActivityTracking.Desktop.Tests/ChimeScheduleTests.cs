using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class ChimeScheduleTests
{
    private const string YouTube = "activity:7";
    private const string Reddit = "activity:8";

    private static readonly ResolvedTime YouTubeSlice = Slice(7, "YouTube");
    private static readonly ResolvedTime RedditSlice = Slice(8, "Reddit");
    private static readonly ResolvedTime DocsSlice = Slice(9, "Docs");

    [Fact]
    public void Chimes_AtEachIntervalOfContinuousTime_TheSameIntervalEveryTime()
    {
        var run = new Run(Settings(ticked: YouTube));

        run.Minutes(14, YouTubeSlice);
        Assert.Empty(run.Shown);

        run.Minutes(1, YouTubeSlice);
        var chime = Assert.Single(run.Shown);
        Assert.Equal(NudgeKind.TimeChime, chime.Kind);
        Assert.Equal(TimeSpan.FromMinutes(15), chime.Length);
        Assert.Equal("YouTube", chime.AppName);

        run.Minutes(15, YouTubeSlice);
        Assert.Equal(new[] { 15, 30 }, run.Shown.Select(n => (int)n.Length.TotalMinutes));
    }

    /// <summary>The server's answer decides, not the process: YouTube in Chrome chimes, docs in Chrome don't.</summary>
    [Fact]
    public void OnlyWhatWasChosen_Counts()
    {
        var run = new Run(Settings(ticked: YouTube));

        run.Minutes(30, DocsSlice);
        run.Minutes(30, Unmapped());

        Assert.Empty(run.Shown);
    }

    /// <summary>
    /// A gap of up to three minutes pauses the count (a reply in a chat window); a longer one — another activity, idle,
    /// locked, nothing sent — starts it over.
    /// </summary>
    [Fact]
    public void AGapOfThreeMinutes_Pauses_ALongerOne_StartsOver()
    {
        var paused = new Run(Settings(ticked: YouTube));
        paused.Minutes(10, YouTubeSlice);
        paused.Minutes(3, DocsSlice);
        paused.Minutes(5, YouTubeSlice);
        Assert.Equal(TimeSpan.FromMinutes(15), Assert.Single(paused.Shown).Length);

        var reset = new Run(Settings(ticked: YouTube));
        reset.Minutes(10, YouTubeSlice);
        reset.Minutes(4, DocsSlice);
        reset.Minutes(14, YouTubeSlice);
        Assert.Empty(reset.Shown);

        var away = new Run(Settings(ticked: YouTube));
        away.Minutes(10, YouTubeSlice);
        away.Skip(4); // idle or locked: no minute is sent
        away.Minutes(14, YouTubeSlice);
        Assert.Empty(away.Shown);
    }

    /// <summary>Hopping between Reddit and YouTube must not dodge the chime; it is named by what is in front.</summary>
    [Fact]
    public void TheChosenActivities_AreOneStretch_NamedByTheLatest()
    {
        var run = new Run(Settings(ticked: [YouTube, Reddit]));

        run.Minutes(8, YouTubeSlice);
        run.Minutes(7, RedditSlice);

        Assert.Equal("Reddit", Assert.Single(run.Shown).AppName);
    }

    /// <summary>A minute counts when chosen time holds at least half of it: a glance at docs doesn't break the stretch.</summary>
    [Fact]
    public void AMinute_CountsWhenChosenTimeHoldsAtLeastHalfOfIt()
    {
        var run = new Run(Settings(ticked: YouTube));

        run.Minutes(15, Slice(7, "YouTube", 30), Slice(9, "Docs", 30));
        Assert.Single(run.Shown);

        var mostlyDocs = new Run(Settings(ticked: YouTube));
        mostlyDocs.Minutes(15, Slice(7, "YouTube", 20), Slice(9, "Docs", 40));
        Assert.Empty(mostlyDocs.Shown);
    }

    /// <summary>A backlog minute (offline, signed out) says nothing about now.</summary>
    [Fact]
    public void BacklogMinutes_AreSkipped()
    {
        var schedule = new ChimeSchedule(TimeZoneInfo.Utc);
        var now = Start.AddHours(2);
        for (var i = 0; i < 30; i++)
            schedule.Record(now, Minute(Start.AddMinutes(i), [YouTubeSlice]), Settings(ticked: YouTube));

        Assert.Null(schedule.TakeDue(now, Settings(ticked: YouTube), () => false, out _));
        Assert.Equal(0, schedule.Stretch(now).Minutes);
    }

    [Fact]
    public void ARepeatedMinute_IsCountedOnce()
    {
        var schedule = new ChimeSchedule(TimeZoneInfo.Utc);
        var minute = Minute(Start, [YouTubeSlice]);

        schedule.Record(Start.AddMinutes(1), minute, Settings(ticked: YouTube));
        schedule.Record(Start.AddMinutes(1.5), minute, Settings(ticked: YouTube));

        Assert.Equal(1, schedule.Stretch(Start.AddMinutes(1.5)).Minutes);
    }

    /// <summary>
    /// A mark due during a call or in full screen is skipped, not held: "15 min" said at minute 22 is wrong. The next mark
    /// still comes.
    /// </summary>
    [Fact]
    public void QuietDuringACallOrFullScreen_SkipsTheMark_TheNextOneStillComes()
    {
        var run = new Run(Settings(ticked: YouTube)) { Quiet = true };

        run.Minutes(15, YouTubeSlice);
        Assert.Empty(run.Shown);

        run.Quiet = false;
        run.Minutes(14, YouTubeSlice);
        Assert.Empty(run.Shown);

        run.Minutes(1, YouTubeSlice);
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(run.Shown).Length);
    }

    /// <summary>
    /// Inside a planned block with drift nudges on, the drift nudge speaks and the chime stays quiet — one voice per block.
    /// The minutes still count, so the first chime after the block says the true length.
    /// </summary>
    [Fact]
    public void InsideABlock_WithDriftNudgesOn_TheChimeIsSilent_ButStillCounts()
    {
        var run = new Run(Settings(ticked: YouTube, driftWatches: true));

        run.Minutes(20, [YouTubeSlice], Block());
        Assert.Empty(run.Shown);

        run.Minutes(10, YouTubeSlice);
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(run.Shown).Length);
    }

    [Fact]
    public void InsideABlock_WithDriftNudgesOff_TheChimeSpeaks()
    {
        var run = new Run(Settings(ticked: YouTube, driftWatches: false));

        run.Minutes(15, [YouTubeSlice], Block());

        Assert.Single(run.Shown);
    }

    /// <summary>"Stop for today" lasts until local midnight; the stretch keeps counting through it.</summary>
    [Fact]
    public void StopForToday_LastsUntilLocalMidnight()
    {
        // 23:00 in a zone two hours ahead of UTC: local midnight is 22:00 UTC.
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
        var start = new DateTime(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc);
        var run = new Run(Settings(ticked: YouTube), zone, start);

        run.Minutes(15, YouTubeSlice);
        Assert.Single(run.Shown);
        run.Schedule.StopForToday(run.NowUtc);

        run.Minutes(44, YouTubeSlice); // to 21:59 UTC = 23:59 local
        Assert.Single(run.Shown);

        run.Minutes(1, YouTubeSlice); // 22:00 UTC = 00:00 local, 60 min in
        Assert.Equal(TimeSpan.FromMinutes(60), run.Shown[^1].Length);
    }

    [Fact]
    public void Off_SaysNothing()
    {
        var run = new Run(Settings(ticked: YouTube) with { Enabled = false });

        run.Minutes(30, YouTubeSlice);

        Assert.Empty(run.Shown);
    }

    /// <summary>A shorter interval mid-stretch chimes at its next mark past the one already said, never repeating it.</summary>
    [Fact]
    public void AChangedInterval_ChimesAtItsNextMark_NeverRepeating()
    {
        var run = new Run(Settings(ticked: YouTube, minutes: 30));

        run.Minutes(31, YouTubeSlice);
        Assert.Equal(new[] { 30 }, run.Shown.Select(n => (int)n.Length.TotalMinutes));

        run.Settings = Settings(ticked: YouTube, minutes: 10);
        run.Minutes(8, YouTubeSlice); // 39
        Assert.Single(run.Shown);
        run.Minutes(1, YouTubeSlice); // 40
        Assert.Equal(new[] { 30, 40 }, run.Shown.Select(n => (int)n.Length.TotalMinutes));
    }

    // ---- which lines chime -------------------------------------------------

    /// <summary>
    /// The server's distraction flag chooses, unless the user unticked it; a tick by hand chooses anything mapped; ignored
    /// and unmapped time never chime.
    /// </summary>
    [Fact]
    public void Chimes_DistractionsFollowTheFlag_HandChoicesWin()
    {
        var reddit = RedditSlice with { IsDistraction = true };
        var follow = Settings();
        Assert.True(follow.Chimes(reddit));
        Assert.False(follow.Chimes(YouTubeSlice));
        Assert.False((follow with { FollowDistractions = false }).Chimes(reddit));

        var unticked = Settings() with { Choices = new Dictionary<string, bool> { [Reddit] = false, [YouTube] = true } };
        Assert.False(unticked.Chimes(reddit));
        Assert.True(unticked.Chimes(YouTubeSlice));

        Assert.False(Settings(ticked: "unmapped").Chimes(Unmapped()));
        Assert.False(follow.Chimes(new ResolvedTime { Target = "Ignored", ActiveSeconds = 60, IsDistraction = true }));
    }

    /// <summary>A role · category has no id: it is keyed by its label, as the today breakdown keys it.</summary>
    [Fact]
    public void ARoleOrCategory_IsChosenByItsLabel()
    {
        var leisure = new ResolvedTime { Target = "RoleOrCategory", Name = "Leisure · Video", ActiveSeconds = 60 };
        var run = new Run(Settings(ticked: "label:Leisure · Video"));

        run.Minutes(15, leisure);

        Assert.Equal("Leisure · Video", Assert.Single(run.Shown).AppName);
    }

    /// <summary>The flags are learned from every minute, a backlog one too, and forgotten when the server stops saying so.</summary>
    [Fact]
    public void Distractions_AreLearnedFromTheServersFlag()
    {
        var schedule = new ChimeSchedule(TimeZoneInfo.Utc);
        var old = Start.AddHours(-1);

        schedule.Record(Start, Minute(old, [RedditSlice with { IsDistraction = true }, YouTubeSlice]), Settings());
        Assert.Equal(new Dictionary<string, string> { [Reddit] = "Reddit" }, schedule.Distractions);

        schedule.Record(Start, Minute(old.AddMinutes(1), [RedditSlice]), Settings());
        Assert.Empty(schedule.Distractions);
    }

    /// <summary>A restart neither repeats a chime nor forgets "Stop for today".</summary>
    [Fact]
    public void SnapshotAndRestore_KeepTheStretch_TheMarkSaid_AndTheStop()
    {
        var run = new Run(Settings(ticked: YouTube));
        run.Minutes(16, YouTubeSlice);
        Assert.Single(run.Shown);

        var restored = new ChimeSchedule(TimeZoneInfo.Utc);
        restored.Restore(run.Schedule.Snapshot());
        Assert.Null(restored.TakeDue(run.NowUtc, run.Settings, () => false, out _));
        Assert.Equal(16, restored.Stretch(run.NowUtc).Minutes);

        restored.StopForToday(run.NowUtc);
        var again = new ChimeSchedule(TimeZoneInfo.Utc);
        again.Restore(restored.Snapshot());
        var next = run.NowUtc;
        for (var i = 0; i < 14; i++)
        {
            again.Record(next.AddMinutes(1), Minute(next, [YouTubeSlice]), run.Settings);
            next = next.AddMinutes(1);
        }
        Assert.Null(again.TakeDue(next, run.Settings, () => false, out _));
    }

    // ---- the tray's and Settings' choices ----------------------------------

    [Fact]
    public void With_KeepsOnlyChoicesThatDifferFromTheDefault()
    {
        var ticked = ChimeChoices.With([], YouTube, "YouTube", chime: true, isDistraction: false, followDistractions: true);
        Assert.Equal(new[] { new ChimeChoice(YouTube, "YouTube", true) }, ticked);

        var unticked = ChimeChoices.With(ticked, Reddit, "Reddit", chime: false, isDistraction: true, followDistractions: true);
        Assert.Equal(new[] { new ChimeChoice(YouTube, "YouTube", true), new ChimeChoice(Reddit, "Reddit", false) }, unticked);

        // Back to the default: the choice goes, and the server's flag decides again.
        Assert.Equal(new[] { new ChimeChoice(YouTube, "YouTube", true) },
            ChimeChoices.With(unticked, Reddit, "Reddit", chime: true, isDistraction: true, followDistractions: true));
        Assert.Empty(ChimeChoices.With(ticked, YouTube, "YouTube", chime: false, isDistraction: false, followDistractions: true));
    }

    [Fact]
    public void Candidates_TodayFirst_ThenDistractions_ThenEarlierChoices_NeverUnmapped()
    {
        var candidates = ChimeChoices.Candidates(
            [new ActivityTotal(YouTube, "YouTube", 3000), new ActivityTotal("unmapped", "Not mapped", 2000), new ActivityTotal(Reddit, "Reddit (renamed)", 100)],
            new Dictionary<string, string> { [Reddit] = "Reddit", ["activity:20"] = "Instagram" },
            [new ChimeChoice("activity:30", "Twitch", true), new ChimeChoice(YouTube, "YouTube (old)", true)]);

        Assert.Equal(
            new[]
            {
                new ChimeCandidate(YouTube, "YouTube", false),
                new ChimeCandidate(Reddit, "Reddit (renamed)", true),
                new ChimeCandidate("activity:20", "Instagram", true),
                new ChimeCandidate("activity:30", "Twitch", false)
            },
            candidates);
    }

    [Fact]
    public void From_ReadsTheConfig_AndFallsBackToFifteen_ForAnIntervalNotOffered()
    {
        var config = new AppConfig
        {
            FilePath = Path.Combine(Path.GetTempPath(), $"chime-{Guid.NewGuid():N}.json"),
            TimeChimeEnabled = true,
            TimeChimeMinutes = 7,
            DriftNudgesEnabled = true,
            TimeChimeChoices = [new ChimeChoice(YouTube, "YouTube", true)]
        };

        var settings = ChimeSettings.From(config);

        Assert.True(settings.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(15), settings.Interval);
        Assert.True(settings.FollowDistractions);
        Assert.True(settings.DriftWatches);
        Assert.True(settings.Choices[YouTube]);
    }

    // ---- helpers ---------------------------------------------------------

    private static readonly DateTime Start = new(2026, 11, 8, 14, 0, 0, DateTimeKind.Utc);

    private static ChimeSettings Settings(string? ticked = null, int minutes = 15, bool driftWatches = false) =>
        Settings(ticked is null ? [] : [ticked], minutes, driftWatches);

    private static ChimeSettings Settings(string[] ticked, int minutes = 15, bool driftWatches = false) =>
        new(true, TimeSpan.FromMinutes(minutes), true, ticked.ToDictionary(k => k, _ => true), driftWatches);

    private static ResolvedTime Slice(long activityId, string name, int seconds = 60) =>
        new() { Target = "Activity", ActivityId = activityId, Name = name, ActiveSeconds = seconds };

    private static ResolvedTime Unmapped() => new() { ActiveSeconds = 60 };

    private static PlannedBlock Block() => new()
    {
        PlannerTaskId = 1, ActivityId = 1, ActivityName = "Thesis",
        StartsAtUtc = Start.AddHours(-1), EndsAtUtc = Start.AddHours(1)
    };

    private static MinuteVerdict Minute(DateTime startUtc, ResolvedTime[] slices, PlannedBlock? block = null) =>
        new(startUtc, block, null, null, slices);

    /// <summary>
    /// The send loop's rhythm: each minute arrives just after it ends, and the service asks for a due chime right after.
    /// </summary>
    private sealed class Run(ChimeSettings settings, TimeZoneInfo? zone = null, DateTime? start = null)
    {
        private DateTime _next = start ?? Start;

        public ChimeSchedule Schedule { get; } = new(zone ?? TimeZoneInfo.Utc);
        public ChimeSettings Settings { get; set; } = settings;
        public bool Quiet { get; set; }
        public List<Nudge> Shown { get; } = [];
        public DateTime NowUtc { get; private set; } = start ?? Start;

        public void Minutes(int count, params ResolvedTime[] slices) => Minutes(count, slices, null);

        public void Minutes(int count, ResolvedTime[] slices, PlannedBlock? block)
        {
            for (var i = 0; i < count; i++)
            {
                NowUtc = _next.AddMinutes(1).AddSeconds(30);
                Schedule.Record(NowUtc, Minute(_next, slices, block), Settings);
                if (Schedule.TakeDue(NowUtc, Settings, () => Quiet, out _) is { } chime) Shown.Add(chime);
                _next = _next.AddMinutes(1);
            }
        }

        /// <summary>Minutes in which nothing is sent: away, locked, paused.</summary>
        public void Skip(int count) => _next = _next.AddMinutes(count);
    }
}
