using System.Net;
using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services.Matching;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The user's own rules and private windows, decided before anything reaches the outbox. Asserted on the title
/// that would be recorded — null is "not recorded at all".
/// </summary>
public class PrivacyFilterTests
{
    private static string? Apply(AppConfig config, string process, string? product, string? rawTitle) =>
        new PrivacyFilter().Apply(config, process, product, rawTitle,
            WindowTitleNormalizer.Normalize(process, product, rawTitle));

    private static AppConfig With(params LocalRule[] rules) => new() { LocalRules = [.. rules] };

    /// <summary>The shipped defaults are the old ignore rules, and must still mean what they meant.</summary>
    [Theory]
    [InlineData("explorer", "Program Manager", null)]
    [InlineData("explorer", "", null)]
    [InlineData("explorer", "Documents", "Documents")]
    [InlineData("SearchHost", "Search", null)]
    public void DefaultRules_KeepTheirMeaning(string process, string rawTitle, string? expected) =>
        Assert.Equal(expected, Apply(new AppConfig(), process, null, rawTitle));

    /// <summary>A config.json from before hide-title rules: same key, no action or match fields.</summary>
    [Fact]
    public void OldConfig_ReadsAsExactIgnoreRules()
    {
        var config = JsonSerializer.Deserialize<AppConfig>(
            """{ "IgnoreRules": [ { "processName": "explorer", "windowTitle": "" }, { "processName": "KeePass" } ] }""")!;

        Assert.Collection(config.LocalRules,
            r => Assert.Equal((LocalRuleAction.Ignore, "explorer", PatternMatchType.Exact, "", PatternMatchType.Exact),
                (r.Action, r.ProcessName, r.ProcessNameMatchType, r.WindowTitle, r.WindowTitleMatchType)),
            r => Assert.Equal((LocalRuleAction.Ignore, "KeePass", (string?)null), (r.Action, r.ProcessName, r.WindowTitle)));
    }

    [Fact]
    public void NewFields_AreWrittenAsNames()
    {
        var json = JsonSerializer.Serialize(With(new LocalRule
        {
            Action = LocalRuleAction.HideTitle,
            WindowTitle = "bank",
            WindowTitleMatchType = PatternMatchType.Contains
        }));

        Assert.Contains("\"action\":\"HideTitle\"", json);
        Assert.Contains("\"windowTitleMatchType\":\"Contains\"", json);
        Assert.Contains("\"PrivateWindowHandling\":\"KeepTimeAsAppName\"", json);
    }

    [Theory]
    [InlineData("Firefox", "Firefox - PRIVATE")]
    [InlineData(null, "firefox - PRIVATE")]   // no product name: the process stands in, never a blank title
    [InlineData("  ", "firefox - PRIVATE")]
    public void HideTitle_RecordsTheAppNameOnly(string? product, string expected)
    {
        var config = With(new LocalRule { Action = LocalRuleAction.HideTitle, ProcessName = "firefox" });

        Assert.Equal(expected, Apply(config, "firefox", product, "My bank — statement"));
    }

    [Fact]
    public void HideTitle_UnderDrop_RecordsNothing()
    {
        var config = With(new LocalRule { Action = LocalRuleAction.HideTitle, ProcessName = "firefox" });
        config.PrivateWindowHandling = PrivateWindowHandling.Drop;

        Assert.Null(Apply(config, "firefox", "Firefox", "My bank"));
    }

    [Fact]
    public void Ignore_WinsOverHideTitle()
    {
        var config = With(
            new LocalRule { Action = LocalRuleAction.HideTitle, ProcessName = "Signal" },
            new LocalRule { Action = LocalRuleAction.Ignore, ProcessName = "Signal", WindowTitle = "Note to self" });

        Assert.Null(Apply(config, "Signal", "Signal", "Note to self"));
        Assert.Equal("Signal - PRIVATE", Apply(config, "Signal", "Signal", "Mum"));
    }

    [Fact]
    public void ATitleRule_WithNoProcess_CoversEveryApp()
    {
        var config = With(new LocalRule { Action = LocalRuleAction.HideTitle, WindowTitle = "*bank*", WindowTitleMatchType = PatternMatchType.Wildcard });

        Assert.Equal("Google Chrome - PRIVATE", Apply(config, "chrome", "Google Chrome", "Tatra Banka - Internet banking - Google Chrome"));
        Assert.Equal("Rider - PRIVATE", Apply(config, "rider64", "Rider", "BankingApp – Program.cs"));
        Assert.Equal("Inbox", Apply(config, "outlook", "Outlook", "Inbox"));
    }

    /// <summary>
    /// The normalizer drops a browser's own suffix, and with it a localized private-browsing marker. A rule on that
    /// marker has to see the raw title, or a Slovak Firefox's private windows would go up with their titles.
    /// </summary>
    [Fact]
    public void ATitleRule_AlsoSeesWhatNormalizationStrips()
    {
        var config = With(new LocalRule
        {
            Action = LocalRuleAction.HideTitle,
            WindowTitle = "Súkromné prehliadanie",
            WindowTitleMatchType = PatternMatchType.Contains
        });

        Assert.Equal("Firefox - PRIVATE", Apply(config, "firefox", "Firefox", "Moja banka — Mozilla Firefox Súkromné prehliadanie"));
    }

    [Theory]
    [InlineData("firefox", "Firefox", "Moja banka — Mozilla Firefox Private Browsing", "Firefox - PRIVATE")]
    [InlineData("msedge", "Microsoft Edge", "Moja banka a 2 ďalšie stránky - [InPrivate] - Microsoft​ Edge", "Microsoft Edge - PRIVATE")]
    [InlineData("firefox", "Firefox", "Moja banka — Mozilla Firefox", "Moja banka")]
    public void PrivateWindows_LoseTheirTitle(string process, string product, string rawTitle, string expected) =>
        Assert.Equal(expected, Apply(new AppConfig(), process, product, rawTitle));

    [Fact]
    public void PrivateWindows_KeepTheirTitle_WhenTurnedOff()
    {
        var config = new AppConfig { HidePrivateWindows = false };

        Assert.Equal("Moja banka", Apply(config, "firefox", "Firefox", "Moja banka — Mozilla Firefox Private Browsing"));
    }

    [Fact]
    public void ARuleNamingNothing_MatchesNothing()
    {
        var rule = new LocalRule { Action = LocalRuleAction.Ignore, ProcessName = "" };

        Assert.False(PrivacyFilter.IsUsable(rule));
        Assert.Equal("Inbox", Apply(With(rule), "outlook", "Outlook", "Inbox"));
    }

    [Fact]
    public void ABadRegex_IsNoMatch_NotACrashInThePollLoop()
    {
        var config = With(new LocalRule { Action = LocalRuleAction.Ignore, WindowTitle = "[unterminated", WindowTitleMatchType = PatternMatchType.Regex });

        Assert.False(PatternMatcher.IsValid("[unterminated", PatternMatchType.Regex));
        Assert.Equal("Inbox", Apply(config, "outlook", "Outlook", "Inbox"));
    }
}

/// <summary>The server rules, applied to a window just before it is sent.</summary>
public class AgentRulesTests
{
    private static ActivityEntry Entry(string process, string? title, string? product = "Product") => new()
    {
        ProcessName = process,
        WindowTitle = title,
        ProductName = product,
        ActiveSeconds = 30,
        BackgroundSeconds = 0,
        ActiveMonitor = 0,
        IsPlayingSound = false,
        IsFullscreen = false
    };

    private static ActivityWindow Window(params ActivityEntry[] entries) =>
        new() { WindowStart = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc), Entries = [.. entries] };

    /// <summary>"Ignore anything titled *GitHub*, except Chrome — that one is Programming."</summary>
    private static AgentRules OverlappingRules()
    {
        var rules = new AgentRules();
        rules.Replace(
        [
            new AgentRule { Id = 1, WindowTitle = "*GitHub*", WindowTitleMatchType = PatternMatchType.Wildcard, IsIgnored = true },
            new AgentRule
            {
                Id = 2, ProcessName = "chrome", ProcessNameMatchType = PatternMatchType.Exact,
                WindowTitle = "*GitHub*", WindowTitleMatchType = PatternMatchType.Wildcard, IsIgnored = false
            }
        ], "\"v1\"", DateTime.UtcNow);
        return rules;
    }

    [Fact]
    public void Filter_DropsWhatAnIgnoringRuleWins_AndKeepsWhatANarrowerRuleClaims()
    {
        var filtered = OverlappingRules().Filter(Window(
            Entry("firefox", "sydowwe - GitHub"),
            Entry("chrome", "sydowwe - GitHub"),
            Entry("Code", "main.ts")));

        Assert.NotNull(filtered);
        Assert.Equal(new[] { "chrome", "Code" }, filtered.Entries.Select(e => e.ProcessName));
    }

    [Fact]
    public void Filter_ReturnsTheSameWindow_WhenNothingIsIgnored()
    {
        var window = Window(Entry("Code", "main.ts"));

        Assert.Same(window, OverlappingRules().Filter(window));
    }

    /// <summary>The heartbeat refuses an empty window, so one with nothing left is not sent at all.</summary>
    [Fact]
    public void Filter_ReturnsNull_WhenEveryEntryIsIgnored() =>
        Assert.Null(OverlappingRules().Filter(Window(Entry("firefox", "GitHub"))));

    /// <summary>The heartbeat stores a missing product as "" before matching, and "*" matches "" — not null.</summary>
    [Fact]
    public void Filter_ResolvesAMissingProductAsEmpty_LikeTheHeartbeat()
    {
        var rules = new AgentRules();
        rules.Replace([new AgentRule { Id = 1, ProductName = "*", ProductNameMatchType = PatternMatchType.Wildcard, IsIgnored = true }], null, DateTime.UtcNow);

        Assert.Null(rules.Filter(Window(Entry("elevated", "Admin", product: null))));
    }

    [Fact]
    public void Lifecycle_UnknownUntilDownloaded_DueAfterTheInterval_ForgottenOnClear()
    {
        var rules = new AgentRules();
        var now = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
        var interval = TimeSpan.FromMinutes(5);

        Assert.False(rules.IsKnown);
        Assert.True(rules.IsDue(now, interval));

        rules.Replace([new AgentRule { Id = 1, ProcessName = "x", ProcessNameMatchType = PatternMatchType.Exact, IsIgnored = true }], "\"v1\"", now);
        Assert.True(rules.IsKnown);
        Assert.False(rules.IsDue(now.AddMinutes(4), interval));
        Assert.True(rules.IsDue(now.AddMinutes(5), interval));

        rules.MarkChecked(now.AddMinutes(5));
        Assert.False(rules.IsDue(now.AddMinutes(9), interval));
        Assert.Equal("\"v1\"", rules.ETag);

        rules.Clear();
        Assert.False(rules.IsKnown);
        Assert.Null(rules.ETag);
        Assert.Equal(0, rules.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, AgentRulesFetch.Updated)]
    [InlineData(HttpStatusCode.NotModified, AgentRulesFetch.Unchanged)]
    // A server from before the route: it still filters on arrival, and waiting for it would hold the outbox forever.
    [InlineData(HttpStatusCode.NotFound, AgentRulesFetch.NotSupported)]
    [InlineData(HttpStatusCode.Forbidden, AgentRulesFetch.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, AgentRulesFetch.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, AgentRulesFetch.Unavailable)]
    public void ClassifyAgentRules(HttpStatusCode status, AgentRulesFetch expected) =>
        Assert.Equal(expected, ApiClient.ClassifyAgentRules(status));
}

public class PauseTests
{
    private static readonly TimeZoneInfo Bratislava = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");

    private static DateTime Utc(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void FifteenMinutesAndAnHour_AreFromNow()
    {
        Assert.Equal(Utc(18, 9, 15), TrayApplicationContext.PauseEnd(PauseChoice.FifteenMinutes, Utc(18, 9, 0), Bratislava));
        Assert.Equal(Utc(18, 10, 0), TrayApplicationContext.PauseEnd(PauseChoice.OneHour, Utc(18, 9, 0), Bratislava));
    }

    /// <summary>On the user's clock: 23:30 in Bratislava (CEST, UTC+2) pauses until 06:00 there, 04:00 UTC.</summary>
    [Fact]
    public void UntilTomorrowMorning_EndsAtTheNextSixOClock_OnTheUsersClock()
    {
        Assert.Equal(Utc(19, 4, 0), TrayApplicationContext.PauseEnd(PauseChoice.UntilTomorrowMorning, Utc(18, 21, 30), Bratislava));
        // Already past midnight there: the morning that is coming, not the one after.
        Assert.Equal(Utc(19, 4, 0), TrayApplicationContext.PauseEnd(PauseChoice.UntilTomorrowMorning, Utc(19, 0, 30), Bratislava));
    }

    [Fact]
    public void UntilResumed_HasNoEnd() =>
        Assert.Equal(DateTime.MaxValue, TrayApplicationContext.PauseEnd(PauseChoice.UntilResumed, Utc(18, 9, 0), Bratislava));

    [Fact]
    public void Status_SaysUntilWhen()
    {
        Assert.Equal("Paused", TrayApplicationContext.DescribePause(null));
        Assert.Equal("Paused", TrayApplicationContext.DescribePause(DateTime.MaxValue));

        var inAnHour = DateTime.UtcNow.AddHours(1);
        Assert.StartsWith("Paused until ", TrayApplicationContext.DescribeStatus(
            new TrackerStatus(false, false, 0, null, null, null, inAnHour), hasSession: true));
    }
}
