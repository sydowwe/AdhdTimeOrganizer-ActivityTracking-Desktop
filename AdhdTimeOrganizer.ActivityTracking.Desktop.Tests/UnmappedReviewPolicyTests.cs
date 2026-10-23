using System.Xml.Linq;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// When the evening invite is due and what it says. Pure: the clock and the server's answer are arguments, so every
/// case is a sentence about the user's evening.
/// </summary>
public class UnmappedReviewPolicyTests : IDisposable
{
    private static readonly DateTime Moment = new(2026, 10, 19, 20, 30, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"unmapped-review-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(-1, null, ReviewTiming.NotYet)]
    [InlineData(0, null, ReviewTiming.Due)]
    [InlineData(179, null, ReviewTiming.Due)]
    [InlineData(181, null, ReviewTiming.Missed)]
    public void Timing_FollowsTheEveningMoment(int minutesAfter, int? handledMinutesAfter, ReviewTiming expected)
    {
        DateTime? handled = handledMinutesAfter is { } h ? Moment.AddMinutes(h) : null;

        Assert.Equal(expected, UnmappedReviewPolicy.Timing(Moment.AddMinutes(minutesAfter), Moment, handled));
    }

    /// <summary>An evening is looked at once: invited, or found empty, either way a second pass or a restart stays silent.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 0)]
    [InlineData(200, 0)]
    public void Timing_AnEveningAlreadyHandled_IsNeverDueAgain(int minutesAfter, int handledMinutesAfter)
    {
        Assert.Equal(ReviewTiming.Handled,
            UnmappedReviewPolicy.Timing(Moment.AddMinutes(minutesAfter), Moment, Moment.AddMinutes(handledMinutesAfter)));
    }

    /// <summary>Yesterday's handled evening says nothing about tonight's.</summary>
    [Fact]
    public void Timing_YesterdaysHandledEvening_IsNotTonights()
    {
        Assert.Equal(ReviewTiming.Due, UnmappedReviewPolicy.Timing(Moment.AddMinutes(1), Moment, Moment.AddDays(-1)));
        Assert.Equal(ReviewTiming.NotYet, UnmappedReviewPolicy.Timing(Moment.AddHours(-1), Moment, Moment.AddDays(-1)));
    }

    /// <summary>The server's "don't ask" is an empty list; the tracker adds no threshold of its own.</summary>
    [Fact]
    public void Invite_WithNoAppsNamed_IsNoInvite()
    {
        Assert.Null(UnmappedReviewPolicy.Invite(new UnmappedToday { UnmappedSeconds = 14 * 60, TrackedSeconds = 3600 }));
    }

    [Fact]
    public void Invite_NamesTheTopTwo_AndCountsTheRest()
    {
        var invite = UnmappedReviewPolicy.Invite(new UnmappedToday
        {
            UnmappedSeconds = 130 * 60,
            Apps =
            [
                App("discord", "Discord", 50, false),
                App("chrome", "Google Chrome", 30, true),
                App("notepad", "Notepad", 20, false),
                App("calc", "", 12, false)
            ]
        });

        Assert.NotNull(invite);
        Assert.Equal(TimeSpan.FromMinutes(130), invite.Unmapped);
        Assert.Equal(new[] { ("Discord", 50, false), ("Google Chrome", 30, true) },
            invite.Apps.Select(a => (a.Name, (int)a.Length.TotalMinutes, a.IsBrowser)));
        Assert.Equal(2, invite.More);
    }

    /// <summary>A product the tracker could not read is named by its process, never left blank in a toast.</summary>
    [Fact]
    public void Invite_AnAppWithoutAProductName_IsNamedByItsProcess()
    {
        var invite = UnmappedReviewPolicy.Invite(new UnmappedToday { UnmappedSeconds = 20 * 60, Apps = [App("calc", "", 20, false)] });

        Assert.Equal("calc", Assert.Single(invite!.Apps).Name);
    }

    [Fact]
    public void Text_SaysHowMuchAndWhich_AndThatABrowserIsMappedByPage()
    {
        var invite = UnmappedReviewPolicy.Invite(new UnmappedToday
        {
            UnmappedSeconds = 130 * 60,
            Apps = [App("discord", "Discord", 50, false), App("chrome", "Google Chrome", 30, true), App("notepad", "Notepad", 20, false)]
        })!;

        Assert.Equal(("2 h 10 m on this PC today isn’t mapped", "Discord 50 min, Google Chrome 30 min (by page) and 1 more. Map them?"),
            UnmappedReviewPolicy.Text(invite));
    }

    [Fact]
    public void Text_WithEveryAppNamed_HasNoTail()
    {
        var invite = UnmappedReviewPolicy.Invite(new UnmappedToday { UnmappedSeconds = 50 * 60, Apps = [App("discord", "Discord", 50, false)] })!;

        Assert.Equal("Discord 50 min. Map them?", UnmappedReviewPolicy.Text(invite).Body);
    }

    /// <summary>The toast builds with the SDK's builder, which throws on a button label with an ASCII apostrophe.</summary>
    [Fact]
    public void Build_TheInvite_OffersReviewAndNotToday()
    {
        var invite = UnmappedReviewPolicy.Invite(new UnmappedToday { UnmappedSeconds = 50 * 60, Apps = [App("discord", "Discord", 50, false)] })!;
        var nudge = new Nudge(1, NudgeKind.UnmappedReview, invite.Unmapped, null, Review: invite);

        var notification = NudgeToast.Build(nudge);
        var toast = XDocument.Parse(notification.Payload).Root!;

        Assert.Equal("UnmappedReview", notification.Tag);
        Assert.Equal(UnmappedReviewPolicy.Text(invite).Title, toast.Descendants("text").First().Value);
        Assert.Equal(
            new[] { NudgeResponse.Review, NudgeResponse.NotToday }.Select(r => new NudgeActivation(NudgeKind.UnmappedReview, 1, r)),
            toast.Descendants("action").Select(a => NudgeToast.Parse(ArgumentsOf((string)a.Attribute("arguments")!))));
    }

    [Fact]
    public void Store_KeepsTheHandledEvening_AcrossARestart()
    {
        var path = Path.Combine(_dir, "unmapped-review.json");

        new UnmappedReviewStore(path).MarkHandled(Moment);

        Assert.Equal(Moment, new UnmappedReviewStore(path).HandledMomentUtc);
    }

    [Fact]
    public void Store_AnUnreadableFile_IsNothingHandled()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "unmapped-review.json");
        File.WriteAllText(path, "not json");

        Assert.Null(new UnmappedReviewStore(path).HandledMomentUtc);
    }

    [Fact]
    public void Store_WithoutAPath_TouchesNoFile()
    {
        var store = new UnmappedReviewStore();

        store.MarkHandled(Moment);

        Assert.Equal(Moment, store.HandledMomentUtc);
        Assert.False(Directory.Exists(_dir));
    }

    private static UnmappedApp App(string process, string product, int minutes, bool isBrowser) =>
        new() { ProcessName = process, ProductName = product, ActiveSeconds = minutes * 60, IsBrowser = isBrowser };

    /// <summary>The way Windows hands a button's <c>key=value;key=value</c> arguments back.</summary>
    private static Dictionary<string, string> ArgumentsOf(string arguments) =>
        arguments.Split(';').Select(pair => pair.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
