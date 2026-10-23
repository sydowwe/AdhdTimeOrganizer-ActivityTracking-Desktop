using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class HeartbeatResponseTests
{
    /// <summary>The server's answer as it is on the wire (camelCase, enums as names, instants with a Z).</summary>
    [Fact]
    public void ReadsTheServersAnswer()
    {
        var response = ApiClient.ReadHeartbeatResponse("""
            {
              "processedCount": 3,
              "resolution": [
                { "target": "Activity", "activityId": 12, "name": "Thesis", "activeSeconds": 40, "onPlan": true },
                { "target": null, "activityId": null, "name": null, "activeSeconds": 20, "onPlan": null }
              ],
              "planned": [
                { "plannerTaskId": 7, "activityId": 12, "activityName": "Thesis", "startsAt": "2026-10-18T09:00:00Z", "endsAt": "2026-10-18T09:30:00Z" }
              ]
            }
            """);

        Assert.NotNull(response);
        Assert.Equal(3, response.ProcessedCount);
        Assert.Equal([40, 20], response.Resolution.Select(r => r.ActiveSeconds));
        Assert.True(response.Resolution[0].OnPlan);
        Assert.True(response.Resolution[1].IsUnmapped);
        var block = Assert.Single(response.Planned);
        Assert.Equal(new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc), block.EndsAtUtc);
        Assert.Equal(DateTimeKind.Utc, block.EndsAtUtc.Kind);
    }

    /// <summary>A server from before 2026-10-18 answers with the stored count alone; the window is stored, there's just no resolution.</summary>
    [Theory]
    [InlineData("2")]
    [InlineData("")]
    [InlineData("not json")]
    public void AnOldServersCount_OrAnythingUnreadable_IsNoResolution(string body) =>
        Assert.Null(ApiClient.ReadHeartbeatResponse(body));

    /// <summary>A target added on the server later must not cost the whole answer.</summary>
    [Fact]
    public void AnUnknownTarget_StillReads() =>
        Assert.Equal("SomethingNew", ApiClient.ReadHeartbeatResponse(
            """{ "processedCount": 1, "resolution": [ { "target": "SomethingNew", "activeSeconds": 60 } ], "planned": [] }""")!
            .Resolution[0].Target);
}

public class TrayGlanceTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 9, 1, 5, DateTimeKind.Utc);

    [Fact]
    public void SaysTodaysTotal_AndWhatTheLastMinuteMappedTo() =>
        Assert.Equal("Today on this PC: 5 h 12 m · now: Thesis",
            TrayApplicationContext.DescribeGlance(Status(TimeSpan.FromMinutes(312), Minute(Now.AddSeconds(-65), Slice("Thesis", 50))), Now));

    /// <summary>A stale "now" is worse than none: paused, idle, offline or an old server leave just the total.</summary>
    [Fact]
    public void LeavesNowOut_WhenTheLastMinuteIsOld_OrThereIsNone()
    {
        Assert.Equal("Today on this PC: 45 min",
            TrayApplicationContext.DescribeGlance(Status(TimeSpan.FromMinutes(45), Minute(Now.AddMinutes(-10), Slice("Thesis", 50))), Now));
        Assert.Equal("Today on this PC: 0 min", TrayApplicationContext.DescribeGlance(Status(TimeSpan.Zero, null), Now));
    }

    [Fact]
    public void Now_SkipsIgnoredTime_AndSaysWhenTheRestIsNotMapped()
    {
        var minute = Minute(Now.AddSeconds(-65),
            new ResolvedTime { Target = "Ignored", ActiveSeconds = 40 },
            new ResolvedTime { Target = null, ActiveSeconds = 20 });

        Assert.Equal("not mapped", TrayApplicationContext.DescribeNow(minute, Now));
    }

    [Theory]
    [InlineData(new[] { "Slack" }, "Map Slack to enable drift nudges")]
    [InlineData(new[] { "Slack", "Google Chrome" }, "Map Slack and Google Chrome to enable drift nudges")]
    [InlineData(new[] { "Slack", "Google Chrome", "Microsoft Teams" }, "Map Slack, Google Chrome and Microsoft Teams to enable drift nudges")]
    public void MappingPrompt_NamesTheApps(string[] products, string expected) =>
        Assert.Equal(expected, TrayApplicationContext.DescribeMappingPrompt(new TrackerCoverage
        {
            Uncovered = [.. products.Select(p => new UncoveredApp { ProcessName = p.ToLowerInvariant(), ProductName = p, ActiveSeconds = 3600 })]
        }));

    [Fact]
    public void MappingPrompt_FallsBackToTheProcessName() =>
        Assert.Equal("Map rider64 to enable drift nudges", TrayApplicationContext.DescribeMappingPrompt(new TrackerCoverage
        {
            Uncovered = [new UncoveredApp { ProcessName = "rider64", ProductName = "", ActiveSeconds = 3600 }]
        }));

    [Fact]
    public void WebAppUri_JoinsThePathOntoTheConfiguredUrl()
    {
        var config = new AppConfig { WebAppUrl = "https://example.com/" };

        Assert.Equal("https://example.com/day-planner", config.WebAppUri(NudgeService.PlannerPath).AbsoluteUri);
        Assert.Equal("https://example.com/activity-tracking/desktop/settings/distinctEntries?coverage=Uncovered",
            config.WebAppUri(TrayApplicationContext.UncoveredEntriesPath).AbsoluteUri);
    }

    private static TrackerStatus Status(TimeSpan today, ResolvedMinute? last) =>
        new(true, false, 0, null, null, null, TodayOnThisPc: today, LastResolved: last);

    private static ResolvedMinute Minute(DateTime windowStartUtc, params ResolvedTime[] slices) =>
        new(windowStartUtc, new HeartbeatResponse { Resolution = [.. slices] });

    private static ResolvedTime Slice(string name, int seconds) =>
        new() { Target = "Activity", Name = name, ActiveSeconds = seconds };
}

public sealed class TodayTotalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"today-{Guid.NewGuid():N}.json");

    private static readonly TimeZoneInfo Bratislava = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bratislava");

    [Fact]
    public void AddsUpTheDay_AndKeepsItAcrossARestart()
    {
        var nine = new DateTime(2026, 10, 18, 7, 0, 0, DateTimeKind.Utc); // 09:00 in Bratislava
        var total = new TodayTotal(_path, Bratislava);
        total.Add(nine, 60);
        total.Add(nine.AddMinutes(1), 45);
        total.Add(nine.AddMinutes(2), -15); // a server rule took some back

        Assert.Equal(TimeSpan.FromSeconds(90), new TodayTotal(_path, Bratislava).On(nine.AddHours(1)));
    }

    /// <summary>"Today" is the user's day: 23:30 UTC is already tomorrow in Bratislava.</summary>
    [Fact]
    public void ADayIsTheLocalOne_AndANewDayStartsAgain()
    {
        var lateEvening = new DateTime(2026, 10, 18, 21, 0, 0, DateTimeKind.Utc); // 23:00 local
        var afterMidnight = new DateTime(2026, 10, 18, 22, 30, 0, DateTimeKind.Utc); // 00:30 local, the 19th
        var total = new TodayTotal(_path, Bratislava);

        total.Add(lateEvening, 60);
        total.Add(afterMidnight, 30);
        total.Add(lateEvening.AddMinutes(1), 60); // a flush of the day before, after midnight: not today's

        Assert.Equal(TimeSpan.FromSeconds(30), total.On(afterMidnight));
        Assert.Equal(TimeSpan.Zero, total.On(afterMidnight.AddDays(1)));
    }

    /// <summary>
    /// One line per activity (by id, so a rename during the day doesn't split it), one per role · category label, one
    /// for unmapped time; ignored time is not tracked time. Kept across a restart like the total.
    /// </summary>
    [Fact]
    public void TheBreakdown_GroupsByActivity_AndLeavesIgnoredTimeOut()
    {
        var at = new DateTime(2026, 10, 18, 7, 0, 0, DateTimeKind.Utc);
        var total = new TodayTotal(_path, Bratislava);

        total.AddResolved(at, Answer(
            new ResolvedTime { Target = "Activity", ActivityId = 12, Name = "Thesis", ActiveSeconds = 30 },
            new ResolvedTime { Target = "RoleOrCategory", Name = "Leisure · Video", ActiveSeconds = 10 },
            new ResolvedTime { Target = null, ActiveSeconds = 15 },
            new ResolvedTime { Target = "Ignored", ActiveSeconds = 5 }));
        total.AddResolved(at.AddMinutes(1), Answer(
            new ResolvedTime { Target = "Activity", ActivityId = 12, Name = "Thesis (writing)", ActiveSeconds = 60 }));

        Assert.Equal(
            new[] { new ActivityTotal("activity:12", "Thesis (writing)", 90), new ActivityTotal("unmapped", "Not mapped", 15),
                new ActivityTotal("label:Leisure · Video", "Leisure · Video", 10) },
            new TodayTotal(_path, Bratislava).Top(at, 5));
        Assert.Equal(2, new TodayTotal(_path, Bratislava).Top(at, 2).Count);
    }

    /// <summary>A today.json written before the breakdown existed still reads, with its total.</summary>
    [Fact]
    public void AFileFromBeforeTheBreakdown_StillReads()
    {
        File.WriteAllText(_path, """{"day":"2026-10-18","activeSeconds":120}""");
        var total = new TodayTotal(_path, Bratislava);
        var at = new DateTime(2026, 10, 18, 7, 0, 0, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.FromSeconds(120), total.On(at));
        Assert.Empty(total.Top(at, 5));
    }

    [Fact]
    public void TheTraysBreakdown_ListsTheTop_OrSaysThereIsNothingYet()
    {
        Assert.Equal(new[] { "Thesis — 1 h 30 m", "Not mapped — 15 min" },
            TrayApplicationContext.DescribeTodayBreakdown([new ActivityTotal("a", "Thesis", 5400), new ActivityTotal("u", "Not mapped", 900)]));
        Assert.Equal(new[] { "Nothing sent yet today" }, TrayApplicationContext.DescribeTodayBreakdown([]));
    }

    private static HeartbeatResponse Answer(params ResolvedTime[] slices) => new() { Resolution = [.. slices] };

    [Fact]
    public void NeverGoesBelowZero()
    {
        var at = new DateTime(2026, 10, 18, 7, 0, 0, DateTimeKind.Utc);
        var total = new TodayTotal(_path, Bratislava);
        total.Add(at, 30);
        total.Add(at, -60);

        Assert.Equal(TimeSpan.Zero, total.On(at));
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
