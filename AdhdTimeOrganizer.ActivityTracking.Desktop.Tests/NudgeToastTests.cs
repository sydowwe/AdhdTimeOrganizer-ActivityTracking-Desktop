using System.Xml.Linq;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class NudgeToastTests
{
    [Theory]
    [InlineData(45, "45 min")]
    [InlineData(90, "1 h 30 m")]
    [InlineData(120, "2 h")]
    public void FormatLength(int minutes, string expected) =>
        Assert.Equal(expected, NudgeToast.FormatLength(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Text_NamesTheApp_ForAnAppStretch() =>
        Assert.Equal("2 h straight in Rider",
            NudgeToast.Text(new Nudge(1, NudgeKind.AppStretch, TimeSpan.FromMinutes(120), "Rider")).Title);

    [Fact]
    public void Text_SaysWhatWasPlannedUntilWhen_AndWhereTheTimeWent()
    {
        var endsUtc = new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc);
        var (title, body) = NudgeToast.Text(new Nudge(1, NudgeKind.Drift, TimeSpan.FromMinutes(10), "YouTube",
            new DriftDetails("Thesis", endsUtc)));

        Assert.Equal($"Planned: Thesis (until {TimeZoneInfo.ConvertTimeFromUtc(endsUtc, TimeZoneInfo.Local):HH:mm})", title);
        Assert.Equal("Last 10 min: YouTube.", body);
    }

    [Fact]
    public void Parse_ReadsTheButtonPressed_AndIgnoresAClickOnTheBody()
    {
        Assert.Equal(new NudgeActivation(NudgeKind.Stretch, 7, NudgeResponse.TakeBreak),
            NudgeToast.Parse(new Dictionary<string, string> { ["kind"] = "Stretch", ["nudge"] = "7", ["action"] = "TakeBreak" }));
        Assert.Null(NudgeToast.Parse(new Dictionary<string, string> { ["kind"] = "Stretch", ["nudge"] = "7" }));
        Assert.Null(NudgeToast.Parse(new Dictionary<string, string> { ["kind"] = "Stretch", ["nudge"] = "x", ["action"] = "Snooze" }));
        // Ids are counted per policy, so a press without its kind can't be routed.
        Assert.Null(NudgeToast.Parse(new Dictionary<string, string> { ["nudge"] = "7", ["action"] = "Snooze" }));
    }

    /// <summary>
    /// Builds real toasts through the Windows App SDK runtime in this process, which proves the self-contained
    /// runtime loads. Every kind must build at all — the SDK doesn't escape button labels, and an apostrophe
    /// in one made <c>BuildNotification</c> throw — and every button must carry what
    /// <see cref="NudgeToast.Parse"/> reads back.
    /// </summary>
    [Theory]
    [InlineData(NudgeKind.Stretch, new[] { NudgeResponse.TakeBreak, NudgeResponse.Snooze, NudgeResponse.NotNow, NudgeResponse.TurnOff })]
    [InlineData(NudgeKind.AppStretch, new[] { NudgeResponse.TakeBreak, NudgeResponse.Snooze, NudgeResponse.NotNow, NudgeResponse.TurnOff })]
    [InlineData(NudgeKind.BreakOver, new[] { NudgeResponse.Dismiss, NudgeResponse.Snooze })]
    [InlineData(NudgeKind.Drift, new[] { NudgeResponse.Dismiss, NudgeResponse.Capture, NudgeResponse.SwitchPlan, NudgeResponse.Snooze, NudgeResponse.TurnOff })]
    [InlineData(NudgeKind.TimeChime, new[] { NudgeResponse.StopForToday })]
    public void Build_EveryKind_EveryButtonCarriesTheNudgeAndItsAnswer(NudgeKind kind, NudgeResponse[] responses)
    {
        // User-authored names in both drift fields: the planned activity and what the time went to.
        var nudge = new Nudge(42, kind, TimeSpan.FromMinutes(90), "O'Reilly <Reader>",
            kind == NudgeKind.Drift ? new DriftDetails("Thesis & \"notes\"", DateTime.UtcNow) : null);
        var notification = NudgeToast.Build(nudge);
        var toast = XDocument.Parse(notification.Payload).Root!;

        Assert.Equal(kind.ToString(), notification.Tag);
        Assert.Equal(NudgeToast.Group, notification.Group);
        Assert.Equal(NudgeToast.Text(nudge).Title, toast.Descendants("text").First().Value);
        Assert.Equal(
            responses.Select(r => new NudgeActivation(kind, 42, r)),
            toast.Descendants("action").Select(a => NudgeToast.Parse(Arguments((string)a.Attribute("arguments")!))));
    }

    /// <summary>What Windows hands back as <c>AppNotificationActivatedEventArgs.Arguments</c>: "a=1;b=2", in any order.</summary>
    private static Dictionary<string, string> Arguments(string encoded) =>
        encoded.Split(';').Select(pair => pair.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);

    /// <summary>
    /// The upload notice is a toast kind of its own, so it gets the same proof as the nudges: it builds (the SDK throws
    /// on an unescaped label), it says the cause and what to do, and each button reads back as the answer it carries.
    /// </summary>
    [Theory]
    [InlineData(UploadProblem.SignedOut, NudgeResponse.SignIn)]
    [InlineData(UploadProblem.Stalled, NudgeResponse.OpenLogs)]
    [InlineData(UploadProblem.Dropped, NudgeResponse.OpenLogs)]
    public void Build_UploadHealth_OffersTheFixForItsProblem_AndItsOwnSwitch(UploadProblem problem, NudgeResponse fix)
    {
        var notice = new UploadNotice(problem, 90, TimeSpan.FromHours(26), 4);
        var nudge = new Nudge(1, NudgeKind.UploadHealth, notice.StalledFor, null, Upload: notice);
        var notification = NudgeToast.Build(nudge);
        var toast = XDocument.Parse(notification.Payload).Root!;

        Assert.Equal("UploadHealth", notification.Tag);
        Assert.Equal(NudgeToast.Group, notification.Group);
        Assert.Equal(NudgeToast.Text(nudge).Title, toast.Descendants("text").First().Value);
        Assert.Equal(
            new[] { fix, NudgeResponse.TurnOff }.Select(r => new NudgeActivation(NudgeKind.UploadHealth, 1, r)),
            toast.Descendants("action").Select(a => NudgeToast.Parse(Arguments((string)a.Attribute("arguments")!))));
        Assert.Equal("Turn off these notices", (string?)toast.Descendants("action").Last().Attribute("content"));
    }

    [Fact]
    public void Text_UploadHealth_SaysWhatIsWrong_InCountsAndDurations()
    {
        Assert.Equal(("Activity Tracker is signed out",
                "1 h 30 m of tracked time is waiting to be sent. Tracking carries on; sign in to upload it."),
            NudgeToast.Text(Upload(UploadProblem.SignedOut)));
        Assert.Equal("Nothing has been uploaded for 26 h", NudgeToast.Text(Upload(UploadProblem.Stalled)).Title);
        Assert.Equal("4 min of tracked time was refused", NudgeToast.Text(Upload(UploadProblem.Dropped)).Title);
        Assert.Contains("update it", NudgeToast.Text(Upload(UploadProblem.Dropped)).Body);
    }

    /// <summary>Review L2: "Check the connection" only when there was no answer; a 403 is the account, not the Wi-Fi.</summary>
    [Theory]
    [InlineData(null, "Check the connection")]
    [InlineData(403, "The server refuses this account (403): check its access in the web app.")]
    [InlineData(503, "The server answered 503")]
    public void Text_Stalled_SaysWhyFromTheServersAnswer(int? code, string expected)
    {
        var body = NudgeToast.Text(new Nudge(1, NudgeKind.UploadHealth, TimeSpan.Zero, null,
            Upload: new UploadNotice(UploadProblem.Stalled, 90, TimeSpan.FromHours(26), 0, code))).Body;

        Assert.StartsWith("1 h 30 m of tracked time is waiting on this PC. ", body);
        Assert.Contains(expected, body);
        if (code == 403) Assert.DoesNotContain("connection", body);
    }

    /// <summary>
    /// The recording notice (prompt 25) is a kind of its own, so it never replaces an upload notice: it builds, and its buttons
    /// read back as Open log folder and the upload notices' switch.
    /// </summary>
    [Theory]
    [InlineData(RecordingProblem.PollFailing)]
    [InlineData(RecordingProblem.PollStopped)]
    [InlineData(RecordingProblem.NothingStored)]
    public void Build_RecordingHealth_OpensTheLog_AndHasTheUploadNoticesSwitch(RecordingProblem problem)
    {
        var nudge = Recording(problem, 12);
        var notification = NudgeToast.Build(nudge);
        var toast = XDocument.Parse(notification.Payload).Root!;

        Assert.Equal("RecordingHealth", notification.Tag);
        Assert.Equal(NudgeToast.Group, notification.Group);
        Assert.Equal("long", (string?)toast.Attribute("duration"));
        Assert.Equal(NudgeToast.Text(nudge).Title, toast.Descendants("text").First().Value);
        Assert.Equal(
            new[] { NudgeResponse.OpenLogs, NudgeResponse.TurnOff }.Select(r => new NudgeActivation(NudgeKind.RecordingHealth, 1, r)),
            toast.Descendants("action").Select(a => NudgeToast.Parse(Arguments((string)a.Attribute("arguments")!))));
        Assert.Equal(["Open log folder", "Turn off these notices"], toast.Descendants("action").Select(a => (string)a.Attribute("content")!));
    }

    /// <summary>Durations only, and what to do: the log folder, a restart.</summary>
    [Fact]
    public void Text_RecordingHealth_SaysWhatIsBroken_InDurations()
    {
        Assert.Equal(("Activity Tracker can’t record this PC",
                "Reading the screen has failed for 12 min, so nothing is being recorded. The log folder says why; restarting the tracker may help."),
            NudgeToast.Text(Recording(RecordingProblem.PollFailing, 12)));
        Assert.Equal(("Activity Tracker has stopped recording",
                "It hasn’t looked at the screen for 6 min, though you are at the computer. Restart the tracker; the log folder says what happened."),
            NudgeToast.Text(Recording(RecordingProblem.PollStopped, 6)));
        Assert.Equal(("Activity Tracker isn’t storing anything",
                "34 min at the computer and not one minute stored. The log folder says why; restarting the tracker may help."),
            NudgeToast.Text(Recording(RecordingProblem.NothingStored, 34)));
    }

    private static Nudge Recording(RecordingProblem problem, int minutes) =>
        new(1, NudgeKind.RecordingHealth, TimeSpan.FromMinutes(minutes), null,
            Recording: new RecordingNotice(problem, TimeSpan.FromMinutes(minutes)));

    private static Nudge Upload(UploadProblem problem) =>
        new(1, NudgeKind.UploadHealth, TimeSpan.Zero, null,
            Upload: new UploadNotice(problem, 90, TimeSpan.FromHours(26), 4));

    /// <summary>
    /// The start ramp: a reminder that stays until answered, Start now · In 2 minutes · Not this one — and the repeat
    /// "In 2 minutes" asked for has no third chance. A user-authored name with XML in it still builds.
    /// </summary>
    [Theory]
    [InlineData(true, new[] { NudgeResponse.StartNow, NudgeResponse.Later, NudgeResponse.NotThisOne })]
    [InlineData(false, new[] { NudgeResponse.StartNow, NudgeResponse.NotThisOne })]
    public void Build_StartRamp_IsAReminder_WithItsButtons(bool canDefer, NudgeResponse[] responses)
    {
        var startsUtc = new DateTime(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc);
        var nudge = Ramp(new StartRampDetails(7, "O'Reilly & <\"notes\">", startsUtc, startsUtc.AddMinutes(45), TimeSpan.Zero, canDefer));
        var notification = NudgeToast.Build(nudge);
        var toast = XDocument.Parse(notification.Payload).Root!;

        Assert.Equal("StartRamp", notification.Tag);
        Assert.Equal("reminder", (string?)toast.Attribute("scenario"));
        Assert.Equal(NudgeToast.Text(nudge).Title, toast.Descendants("text").First().Value);
        Assert.Equal(
            responses.Select(r => new NudgeActivation(NudgeKind.StartRamp, 5, r)),
            toast.Descendants("action").Select(a => NudgeToast.Parse(Arguments((string)a.Attribute("arguments")!))));
    }

    [Fact]
    public void Text_StartRamp_OnTime_Late_AndTheRepeat()
    {
        var startsUtc = new DateTime(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc);
        var endsUtc = startsUtc.AddMinutes(45);
        var until = TimeZoneInfo.ConvertTimeFromUtc(endsUtc, TimeZoneInfo.Local).ToString("HH:mm");

        Assert.Equal(($"Thesis starts now (until {until})", "45 min planned. Start the timer?"),
            NudgeToast.Text(Ramp(new StartRampDetails(1, "Thesis", startsUtc, endsUtc, TimeSpan.FromSeconds(40), true))));
        Assert.Equal(($"Thesis started 20 min ago (until {until})", "25 min left. Start now?"),
            NudgeToast.Text(Ramp(new StartRampDetails(1, "Thesis", startsUtc, endsUtc, TimeSpan.FromMinutes(20), true))));
        Assert.Equal(($"Thesis started 2 min ago (until {until})", "Ready now? One click starts the timer."),
            NudgeToast.Text(Ramp(new StartRampDetails(1, "Thesis", startsUtc, endsUtc, TimeSpan.FromMinutes(2), false))));
    }

    private static Nudge Ramp(StartRampDetails details) =>
        new(5, NudgeKind.StartRamp, details.EndsUtc - details.StartsUtc, null, StartRamp: details);

    /// <summary>
    /// The block-end heads-up is information: silent, not a reminder, one OK that reads back as Dismiss, and gone from the
    /// notification centre at the block's end. A user-authored name with XML in it still builds.
    /// </summary>
    [Fact]
    public void Build_BlockEnd_IsSilent_WithOneOK_AndExpiresAtTheBlocksEnd()
    {
        var endsUtc = DateTime.UtcNow.AddMinutes(5);
        var nudge = new Nudge(4, NudgeKind.BlockEnd, TimeSpan.FromMinutes(5), null, BlockEnd: new BlockEndDetails(
            7, "O'Reilly & <\"notes\">", endsUtc, TimeSpan.FromMinutes(5), BlockEndAfter.Next, 8, "Email & <co>", endsUtc));
        var notification = NudgeToast.Build(nudge);
        var toast = XDocument.Parse(notification.Payload).Root!;

        Assert.Equal("BlockEnd", notification.Tag);
        Assert.Equal(NudgeToast.Group, notification.Group);
        Assert.Equal(NudgeToast.Text(nudge).Title, toast.Descendants("text").First().Value);
        Assert.Equal("true", (string?)toast.Element("audio")?.Attribute("silent"));
        Assert.Null(toast.Attribute("scenario"));
        Assert.Null(toast.Attribute("duration"));
        Assert.Equal(new DateTimeOffset(endsUtc), notification.Expiration);
        Assert.Equal([new NudgeActivation(NudgeKind.BlockEnd, 4, NudgeResponse.Dismiss)],
            toast.Descendants("action").Select(a => NudgeToast.Parse(Arguments((string)a.Attribute("arguments")!))));
        Assert.Equal("OK", (string?)toast.Descendants("action").Single().Attribute("content"));
    }

    [Fact]
    public void Text_BlockEnd_NamesWhatComesNext_OrNothing_OrDoesNotGuess()
    {
        var endsUtc = new DateTime(2026, 11, 23, 11, 30, 0, DateTimeKind.Utc);
        var at = TimeZoneInfo.ConvertTimeFromUtc(endsUtc, TimeZoneInfo.Local).ToString("HH:mm");
        Nudge HeadsUp(BlockEndAfter after, double minutesLeft = 5) => new(1, NudgeKind.BlockEnd, TimeSpan.FromMinutes(minutesLeft), null,
            BlockEnd: after == BlockEndAfter.Next
                ? new BlockEndDetails(1, "Thesis", endsUtc, TimeSpan.FromMinutes(minutesLeft), after, 2, "Email", endsUtc)
                : new BlockEndDetails(1, "Thesis", endsUtc, TimeSpan.FromMinutes(minutesLeft), after));

        Assert.Equal(("Thesis ends in 5 min", $"Next: Email at {at}."), NudgeToast.Text(HeadsUp(BlockEndAfter.Next)));
        Assert.Equal(("Thesis ends in 3 min", "Nothing planned after."), NudgeToast.Text(HeadsUp(BlockEndAfter.Nothing, 2.9)));
        Assert.Equal(("Thesis ends in 5 min", "Time to wrap up the thought."), NudgeToast.Text(HeadsUp(BlockEndAfter.Unknown)));
    }

    /// <summary>
    /// A time check, not a nudge: it makes no sound, slides away on the short duration, and leaves the notification centre
    /// soon after rather than piling up "15 min", "30 min", "45 min" there.
    /// </summary>
    [Fact]
    public void Build_TimeChime_IsSilent_Short_AndExpires()
    {
        var nudge = new Nudge(2, NudgeKind.TimeChime, TimeSpan.FromMinutes(45), "YouTube");
        var before = DateTimeOffset.Now;
        var notification = NudgeToast.Build(nudge);
        var toast = XDocument.Parse(notification.Payload).Root!;

        Assert.Equal(("45 min on YouTube", "A time check, nothing more."), NudgeToast.Text(nudge));
        Assert.Equal("true", (string?)toast.Element("audio")?.Attribute("silent"));
        Assert.Null(toast.Attribute("scenario"));
        Assert.Null(toast.Attribute("duration"));
        Assert.InRange(notification.Expiration, before + ChimeSchedule.ToastLifetime, DateTimeOffset.Now + ChimeSchedule.ToastLifetime);
        Assert.Equal("Stop for today", (string?)toast.Descendants("action").Single().Attribute("content"));
    }

    /// <summary>
    /// The pause reminder is a kind of its own: it builds, says how long, and its two buttons read back as Resume and Keep
    /// paused. Long on screen, but not a reminder-scenario toast: it comes again in six hours if ignored.
    /// </summary>
    [Fact]
    public void Build_PauseReminder_SaysHowLong_WithResumeAndKeepPaused()
    {
        var nudge = new Nudge(1, NudgeKind.PauseReminder, TimeSpan.FromHours(4.6), null);
        var notification = NudgeToast.Build(nudge);
        var toast = XDocument.Parse(notification.Payload).Root!;

        Assert.Equal("PauseReminder", notification.Tag);
        Assert.Equal(NudgeToast.Group, notification.Group);
        Assert.Equal(("Tracking has been paused for 4 h", "Nothing on this PC is recorded until you resume."), NudgeToast.Text(nudge));
        Assert.Equal("long", (string?)toast.Attribute("duration"));
        Assert.Null(toast.Attribute("scenario"));
        Assert.Equal(
            new[] { NudgeResponse.Resume, NudgeResponse.KeepPaused }.Select(r => new NudgeActivation(NudgeKind.PauseReminder, 1, r)),
            toast.Descendants("action").Select(a => NudgeToast.Parse(Arguments((string)a.Attribute("arguments")!))));
        Assert.Equal(["Resume", "Keep paused"], toast.Descendants("action").Select(a => (string)a.Attribute("content")!));
    }

    [Theory]
    [InlineData(3.2, "3 h")]
    [InlineData(47.9, "47 h")]
    [InlineData(50, "2 days")]
    [InlineData(80, "3 days")]
    public void FormatPause_WholeUnits_RoundedDown(double hours, string expected) =>
        Assert.Equal(expected, NudgeToast.FormatPause(TimeSpan.FromHours(hours)));

    /// <summary>
    /// The away question (prompt 23): one button per answer — each reading back with its index — then Something else; an
    /// ordinary toast that leaves the notification centre when a press would no longer count. Activity names are the user's
    /// and land in button labels, which the SDK doesn't escape: markup and an apostrophe must still build and read back as
    /// written, and a long name is cut.
    /// </summary>
    [Fact]
    public void Build_AwayReturn_OneButtonPerAnswer_ThenSomethingElse()
    {
        var toUtc = DateTime.UtcNow.AddMinutes(-1);
        var longName = new string('x', 50);
        var nudge = new Nudge(6, NudgeKind.AwayReturn, TimeSpan.FromMinutes(47), null, Away: new AwayDetails(toUtc.AddMinutes(-47), toUtc,
            [new AwayAnswer(70, "O'Reilly & <\"co\">", true), new AwayAnswer(21, longName, false)]));
        var notification = NudgeToast.Build(nudge);
        var toast = XDocument.Parse(notification.Payload).Root!;

        Assert.Equal("AwayReturn", notification.Tag);
        Assert.Equal(NudgeToast.Group, notification.Group);
        Assert.Null(toast.Attribute("scenario"));
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(toUtc + AwayReturnPolicy.AnswerFor, DateTimeKind.Utc)), notification.Expiration);
        Assert.Equal(NudgeToast.Text(nudge).Title, toast.Descendants("text").First().Value);
        Assert.Equal(
            [
                new NudgeActivation(NudgeKind.AwayReturn, 6, NudgeResponse.AwayAnswer, 0),
                new NudgeActivation(NudgeKind.AwayReturn, 6, NudgeResponse.AwayAnswer, 1),
                new NudgeActivation(NudgeKind.AwayReturn, 6, NudgeResponse.SomethingElse)
            ],
            toast.Descendants("action").Select(a => NudgeToast.Parse(Arguments((string)a.Attribute("arguments")!))));
        Assert.Equal(["O’Reilly & <\"co\">", new string('x', NudgeToast.MaxAnswerLabel - 1) + "…", "Something else"],
            toast.Descendants("action").Select(a => (string)a.Attribute("content")!));
    }

    [Fact]
    public void Text_AwayReturn_SaysHowLongAndWhen_AndThePlannedBlock()
    {
        var fromUtc = new DateTime(2026, 11, 24, 10, 2, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddMinutes(47);
        var span = $"{TimeZoneInfo.ConvertTimeFromUtc(fromUtc, TimeZoneInfo.Local):HH:mm}–{TimeZoneInfo.ConvertTimeFromUtc(toUtc, TimeZoneInfo.Local):HH:mm}";
        Nudge Away(params AwayAnswer[] answers) =>
            new(1, NudgeKind.AwayReturn, toUtc - fromUtc, null, Away: new AwayDetails(fromUtc, toUtc, answers));

        Assert.Equal(("Away 47 min — what was it?", $"{span} · planned then: Thesis. One click files it in your history."),
            NudgeToast.Text(Away(new AwayAnswer(7, "Thesis", true), new AwayAnswer(21, "Rest", false))));
        Assert.Equal(("Away 47 min — what was it?", $"{span}. One click files it in your history."),
            NudgeToast.Text(Away(new AwayAnswer(21, "Rest", false))));
    }

    /// <summary>What the server knew about the absence: the phone's time from a minute up, with its busiest apps when it named any.</summary>
    [Fact]
    public void Text_AwayReturn_SaysThePhonesTime()
    {
        var fromUtc = new DateTime(2026, 11, 24, 10, 2, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddMinutes(47);
        var span = $"{TimeZoneInfo.ConvertTimeFromUtc(fromUtc, TimeZoneInfo.Local):HH:mm}–{TimeZoneInfo.ConvertTimeFromUtc(toUtc, TimeZoneInfo.Local):HH:mm}";
        Nudge Away(AwayKnown known, params AwayAnswer[] answers) =>
            new(1, NudgeKind.AwayReturn, toUtc - fromUtc, null, Away: new AwayDetails(fromUtc, toUtc, answers, known));

        Assert.Equal($"{span} · 21 min on the phone: YouTube, Instagram · planned then: Thesis. One click files it in your history.",
            NudgeToast.Text(Away(new AwayKnown
            {
                PhoneSeconds = 21 * 60, PhoneApps = [new AwayPhoneApp("YouTube", 900), new AwayPhoneApp("Instagram", 300)]
            }, new AwayAnswer(7, "Thesis", true))).Body);
        Assert.Equal($"{span} · 3 min on the phone. One click files it in your history.",
            NudgeToast.Text(Away(new AwayKnown { PhoneSeconds = 200 })).Body);
        Assert.Equal($"{span}. One click files it in your history.",
            NudgeToast.Text(Away(new AwayKnown { PhoneSeconds = 59, RecordedSeconds = 600 })).Body);
    }

    [Fact]
    public void Parse_ReadsTheAwayAnswersIndex()
    {
        Assert.Equal(new NudgeActivation(NudgeKind.AwayReturn, 3, NudgeResponse.AwayAnswer, 1), NudgeToast.Parse(
            new Dictionary<string, string> { ["kind"] = "AwayReturn", ["nudge"] = "3", ["action"] = "AwayAnswer", ["choice"] = "1" }));
        Assert.Null(NudgeToast.Parse(
            new Dictionary<string, string> { ["kind"] = "AwayReturn", ["nudge"] = "3", ["action"] = "AwayAnswer", ["choice"] = "-1" })!.Choice);
    }

    [Fact]
    public void Build_BreakOver_IsAReminder_ThatStaysUntilAnswered()
    {
        var toast = XDocument.Parse(NudgeToast.Build(new Nudge(3, NudgeKind.BreakOver, TimeSpan.FromMinutes(10), null)).Payload).Root!;

        Assert.Equal("reminder", (string?)toast.Attribute("scenario"));
    }
}
