using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public sealed class DiagnosticsReportTests : IDisposable
{
    private const string Profile = @"C:\Users\someone";
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DiagnosticsEnvironment Env = new("1.0.0", "Microsoft Windows 10.0.26200 (build 26200)", "OS X64, process X64, .NET 10.0.3");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"diagnostics-{Guid.NewGuid():N}");

    /// <summary>
    /// A config full of the things that must not leave the machine — the token, a title rule, a path rule — and a poll
    /// error and log lines carrying an e-mail and the profile folder.
    /// </summary>
    private AppConfig Config()
    {
        var config = new AppConfig { FilePath = Path.Combine(_dir, "config.json"), ApiBaseUrl = "https://server.test", RefreshToken = "refresh-token-SECRET" };
        config.LocalRules =
        [
            new LocalRule { ProcessName = "keepass-secret-process", WindowTitle = "Salary negotiation notes" },
            new LocalRule { Action = LocalRuleAction.HideTitle, WindowTitle = @"D:\private\taxes.xlsx" }
        ];
        return config;
    }

    private static TrackerStatus Status() => new(
        IsRunning: true, IsLocked: false, PendingWindows: 12, LastSentUtc: Now.AddMinutes(-3), LastSendResult: SendResult.RetryLater,
        LastPollError: "Could not read window 'Salary negotiation notes'", OldestPendingUtc: Now.AddMinutes(-12), RejectedWindows: 2);

    private static string Format(AppConfig config, params string[] log) =>
        DiagnosticsReport.Format(Env, config, Status(), serverRules: 7, keepAlive: null, log, Now, Profile);

    /// <summary>The point of the whole thing: it goes into a bug report, so none of the user's own words may be in it.</summary>
    [Fact]
    public void TheReport_HoldsNoToken_NoRulePattern_NoTitle_NoEmail_NoProfilePath()
    {
        var report = Format(Config(),
            @"2026-10-19 11:59:00.000 [ERR] Could not read C:\Users\someone\AppData\Local\x\config.json for jakub@example.com",
            "2026-10-19 11:59:01.000 [WRN] mail me at a.b+c@mail.example.org or at c:/users/someone/x");

        Assert.DoesNotContain("refresh-token-SECRET", report);
        Assert.DoesNotContain("keepass-secret-process", report);
        Assert.DoesNotContain("Salary negotiation notes", report); // a rule's title, and the poll error's message
        Assert.DoesNotContain("taxes.xlsx", report);
        Assert.DoesNotContain("example.com", report);
        Assert.DoesNotContain("example.org", report);
        Assert.DoesNotContain("someone", report);
        Assert.Contains("[email]", report);
        Assert.Contains(@"%USERPROFILE%\AppData\Local\x\config.json", report);
        Assert.Contains("%USERPROFILE%/x", report);
    }

    [Fact]
    public void TheReport_SaysWhereTheTrackerStands_AndCountsTheRules()
    {
        var report = Format(Config());

        Assert.Contains("App version: 1.0.0", report);
        Assert.Contains("Windows: Microsoft Windows 10.0.26200 (build 26200)", report);
        Assert.Contains("Signed in: yes", report);
        Assert.Contains("Tracking: running", report);
        Assert.Contains("Outbox: 12 min waiting, oldest from 2026-10-19 11:48 UTC", report);
        Assert.Contains("Last send: RetryLater, last stored 2026-10-19 11:57:00 UTC", report);
        Assert.Contains("Dropped by the server since start: 2 min", report);
        Assert.Contains("Poll error: yes, see the log", report);
        Assert.Contains("Server: https://server.test", report);
        Assert.Contains("Rules: 2 local, 7 from the server", report);
        Assert.Contains("Upload notices: on", report);
        Assert.Contains("Capture hotkey: Win+Alt+N", report);
        Assert.Contains("Pause keep-alive: none sent since start", report);
    }

    [Fact]
    public void TheCaptureHotkey_SwitchedOff_SaysOff()
    {
        var config = Config();
        config.CaptureHotkey = "";

        Assert.Contains("Capture hotkey: off", Format(config));
    }

    /// <summary>
    /// The one thing a "went silent while paused" report needs: did the keep-alive go out, and did the server take it. A
    /// refusal says why, because it is the old server and not the tracker.
    /// </summary>
    [Theory]
    [InlineData(SendResult.Sent, "Pause keep-alive: Sent at 2026-10-19 10:00 UTC")]
    [InlineData(SendResult.RetryLater, "Pause keep-alive: RetryLater at 2026-10-19 10:00 UTC")]
    [InlineData(SendResult.Rejected, "Pause keep-alive: Rejected at 2026-10-19 10:00 UTC (the server predates empty heartbeats)")]
    public void TheReport_SaysWhatTheLastKeepAliveGot(SendResult result, string expected)
    {
        var report = DiagnosticsReport.Format(Env, Config(), Status(), serverRules: 7,
            new KeepAliveAnswer(result, Now.AddHours(-2)), [], Now, Profile);

        Assert.Contains(expected, report);
    }

    [Fact]
    public void TheReport_SaysSo_WhenNothingIsKnownYet()
    {
        var config = Config();
        config.RefreshToken = null;
        config.PausedUntilUtc = DateTime.MaxValue;

        var report = DiagnosticsReport.Format(Env, config, new TrackerStatus(false, false, 0, null, null, null, DateTime.MaxValue),
            serverRules: null, keepAlive: null, [], Now, Profile);

        Assert.Contains("Signed in: no", report);
        Assert.Contains("Tracking: paused until resumed", report);
        Assert.Contains("Outbox: 0 min waiting", report);
        Assert.Contains("Last send: none yet, nothing stored since start", report);
        Assert.Contains("from the server", report);
        Assert.Contains("not downloaded yet", report);
    }

    // ---- the log tail ----------------------------------------------------

    /// <summary>The logger holds today's file open for writing; reading it must not need the file to be free.</summary>
    [Fact]
    public void Tail_ReadsTheFileTheLoggerHasOpen_AndTakesTheNewestLinesOldestFirst()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(Path.Combine(_dir, "log-20261018.txt"), ["old 1", "old 2", "old 3"]);

        using var logger = new FileStream(Path.Combine(_dir, "log-20261019.txt"), FileMode.Create, FileAccess.Write, FileShare.Read);
        using (var writer = new StreamWriter(logger, leaveOpen: true))
        {
            writer.WriteLine("today 1");
            writer.WriteLine("today 2");
        }

        Assert.Equal(["old 3", "today 1", "today 2"], DiagnosticsLog.Tail(_dir, 3));
        Assert.Equal(["old 1", "old 2", "old 3", "today 1", "today 2"], DiagnosticsLog.Tail(_dir, 200));
    }

    [Fact]
    public void Tail_OfNoLogFolder_IsEmpty() =>
        Assert.Empty(DiagnosticsLog.Tail(Path.Combine(_dir, "missing"), 200));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
