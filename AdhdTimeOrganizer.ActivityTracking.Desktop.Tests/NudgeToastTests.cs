using System.Xml.Linq;
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
    [InlineData(NudgeKind.Drift, new[] { NudgeResponse.Dismiss, NudgeResponse.SwitchPlan, NudgeResponse.Snooze, NudgeResponse.TurnOff })]
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

    [Fact]
    public void Build_BreakOver_IsAReminder_ThatStaysUntilAnswered()
    {
        var toast = XDocument.Parse(NudgeToast.Build(new Nudge(3, NudgeKind.BreakOver, TimeSpan.FromMinutes(10), null)).Payload).Root!;

        Assert.Equal("reminder", (string?)toast.Attribute("scenario"));
    }
}
