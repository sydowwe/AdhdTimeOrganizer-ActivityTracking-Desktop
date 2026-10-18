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

    [Fact]
    public void Build_BreakOver_IsAReminder_ThatStaysUntilAnswered()
    {
        var toast = XDocument.Parse(NudgeToast.Build(new Nudge(3, NudgeKind.BreakOver, TimeSpan.FromMinutes(10), null)).Payload).Root!;

        Assert.Equal("reminder", (string?)toast.Attribute("scenario"));
    }
}
