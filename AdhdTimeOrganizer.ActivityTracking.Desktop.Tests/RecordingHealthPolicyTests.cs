using System.Net;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// "Is recording itself working?" (prompt 25): the poll failing for T, the poll loop silent, recordable time with nothing
/// stored for U — each once per episode, withdrawn when it clears, shown only to someone at the computer, off with the upload
/// notices' switch.
/// </summary>
public class RecordingHealthPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>A healthy tracker: running, polled a moment ago, a minute stored recently, someone typing.</summary>
    private static RecordingSnapshot Healthy(DateTime nowUtc) =>
        new(IsRunning: true, IsPaused: false, IsLocked: false, IdleFor: TimeSpan.FromSeconds(2),
            PollFailingSinceUtc: null, LastPollUtc: nowUtc.AddSeconds(-3), UnrecordedFor: TimeSpan.FromSeconds(70));

    private static RecordingSnapshot Failing(DateTime nowUtc, DateTime since) =>
        Healthy(nowUtc) with { PollFailingSinceUtc = since, LastPollUtc = since.AddSeconds(-5) };

    private static RecordingSnapshot Silent(DateTime nowUtc, DateTime lastPoll) => Healthy(nowUtc) with { LastPollUtc = lastPoll };

    private static RecordingSnapshot Unstored(DateTime nowUtc, int minutes) =>
        Healthy(nowUtc) with { UnrecordedFor = TimeSpan.FromMinutes(minutes) };

    [Fact]
    public void AHealthyTracker_SaysNothing()
    {
        var policy = new RecordingHealthPolicy();

        Assert.Equal(new RecordingHealthChange(null, false), policy.Evaluate(Now, Healthy(Now), enabled: true));
    }

    // ---- the poll failing (T) -----------------------------------------------

    [Fact]
    public void ThePollFailing_IsSaid_OnceItHasFailedForT()
    {
        var policy = new RecordingHealthPolicy();
        var since = Now;

        Assert.Null(policy.Evaluate(Now.AddMinutes(9), Failing(Now.AddMinutes(9), since), true).Show);
        var shown = policy.Evaluate(Now.AddMinutes(10), Failing(Now.AddMinutes(10), since), true).Show;

        Assert.Equal(new RecordingNotice(RecordingProblem.PollFailing, TimeSpan.FromMinutes(10)), shown);
        Assert.Equal(TimeSpan.FromMinutes(10), RecordingHealthPolicy.FailingAfter);
    }

    /// <summary>A poll that completes ends the episode: the toast leaves, and a new run of failures starts the count again.</summary>
    [Fact]
    public void APollThatCompletes_WithdrawsIt_AndANewRunCountsAfresh()
    {
        var policy = new RecordingHealthPolicy();
        policy.Evaluate(Now.AddMinutes(10), Failing(Now.AddMinutes(10), Now), true);

        Assert.Equal(new RecordingHealthChange(null, true), policy.Evaluate(Now.AddMinutes(11), Healthy(Now.AddMinutes(11)), true));

        var again = Now.AddMinutes(12);
        Assert.Null(policy.Evaluate(again.AddMinutes(5), Failing(again.AddMinutes(5), again), true).Show);
        Assert.NotNull(policy.Evaluate(again.AddMinutes(10), Failing(again.AddMinutes(10), again), true).Show);
    }

    // ---- the poll loop silent ---------------------------------------------

    /// <summary>The dead loop CLAUDE.md records: no poll, no error. Said after the silence plus a grace, not before.</summary>
    [Fact]
    public void ASilentPollLoop_IsSaid_AfterTheSilenceAndAGrace()
    {
        var policy = new RecordingHealthPolicy();
        var lastPoll = Now;
        var silentAt = lastPoll + RecordingHealthPolicy.SilentAfter;

        Assert.Null(policy.Evaluate(silentAt, Silent(silentAt, lastPoll), true).Show);
        Assert.Null(policy.Evaluate(silentAt.AddSeconds(30), Silent(silentAt.AddSeconds(30), lastPoll), true).Show);
        var shown = policy.Evaluate(silentAt + RecordingHealthPolicy.SilentGrace, Silent(silentAt + RecordingHealthPolicy.SilentGrace, lastPoll), true).Show;

        Assert.Equal(RecordingProblem.PollStopped, shown!.Problem);
        Assert.Equal(TimeSpan.FromMinutes(6), shown.For);
    }

    /// <summary>
    /// A PC waking from a night's sleep: the tray's first tick finds the last poll hours old, and the loop's next pass comes
    /// seconds later. The grace is what keeps that quiet.
    /// </summary>
    [Fact]
    public void WakingFromSleep_IsNotASilence()
    {
        var policy = new RecordingHealthPolicy();
        var woke = Now.AddHours(8);

        Assert.Null(policy.Evaluate(woke, Silent(woke, Now), true).Show);
        Assert.Null(policy.Evaluate(woke.AddSeconds(4), Healthy(woke.AddSeconds(4)), true).Show);
        Assert.Null(policy.Evaluate(woke.AddMinutes(2), Healthy(woke.AddMinutes(2)), true).Show);
    }

    /// <summary>A failing loop is not also a silent one: one notice, the one that has a cause.</summary>
    [Fact]
    public void AFailingLoop_IsNotCalledSilent()
    {
        var policy = new RecordingHealthPolicy();

        for (var minute = 0; minute < 10; minute++)
            Assert.Null(policy.Evaluate(Now.AddMinutes(minute), Failing(Now.AddMinutes(minute), Now), true).Show);

        Assert.Equal(RecordingProblem.PollFailing, policy.Evaluate(Now.AddMinutes(10), Failing(Now.AddMinutes(10), Now), true).Show!.Problem);
    }

    // ---- nothing stored (U) -----------------------------------------------

    [Fact]
    public void RecordableTime_WithNothingStoredForU_IsSaid()
    {
        var policy = new RecordingHealthPolicy();

        Assert.Null(policy.Evaluate(Now, Unstored(Now, 29), true).Show);
        var shown = policy.Evaluate(Now.AddMinutes(1), Unstored(Now.AddMinutes(1), 30), true).Show;

        Assert.Equal(new RecordingNotice(RecordingProblem.NothingStored, TimeSpan.FromMinutes(30)), shown);
        Assert.Equal(TimeSpan.FromMinutes(30), RecordingHealthPolicy.UnrecordedAfter);
    }

    /// <summary>
    /// Decided 2026-11-23: a user whose every window their own local rules ignore has nothing recordable, so the count never
    /// grows — however long they sit there, typing, with the loop polling fine. (The count itself: <see cref="RecordingCountTests"/>.)
    /// </summary>
    [Fact]
    public void EveryWindowIgnored_ByTheUsersOwnRules_IsNotAProblem()
    {
        var policy = new RecordingHealthPolicy();

        for (var minute = 0; minute <= 120; minute += 5)
            Assert.Null(policy.Evaluate(Now.AddMinutes(minute), Healthy(Now.AddMinutes(minute)) with { UnrecordedFor = TimeSpan.Zero }, true).Show);
    }

    // ---- who is told, and how often -----------------------------------------

    /// <summary>Paused or stopped on purpose, nothing is wrong; nothing is said, even with every counter past its limit.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PausedOrStopped_SaysNothing(bool isPaused, bool isStopped)
    {
        var policy = new RecordingHealthPolicy();
        var snapshot = Failing(Now.AddHours(1), Now) with
        {
            IsPaused = isPaused, IsRunning = !isStopped, UnrecordedFor = TimeSpan.FromHours(1), LastPollUtc = Now
        };

        Assert.Null(policy.Evaluate(Now.AddHours(1), snapshot, true).Show);
        Assert.Null(policy.Evaluate(Now.AddHours(2), snapshot, true).Show);
    }

    /// <summary>A toast to an empty desk is buried: held until input comes, by the idle time the tray reads itself.</summary>
    [Fact]
    public void Nobody_AtTheComputer_HoldsIt_UntilTheyAreBack()
    {
        var policy = new RecordingHealthPolicy();
        var at = Now.AddMinutes(15);

        Assert.Null(policy.Evaluate(at, Failing(at, Now) with { IdleFor = TimeSpan.FromMinutes(5) }, true).Show);
        Assert.Null(policy.Evaluate(at, Failing(at, Now) with { IsLocked = true }, true).Show);
        Assert.NotNull(policy.Evaluate(at.AddMinutes(1), Failing(at.AddMinutes(1), Now), true).Show);
    }

    [Fact]
    public void OncePerEpisode_ThenAtMostDaily()
    {
        var policy = new RecordingHealthPolicy();
        Assert.NotNull(policy.Evaluate(Now, Unstored(Now, 30), true).Show);

        Assert.Null(policy.Evaluate(Now.AddMinutes(1), Unstored(Now.AddMinutes(1), 31), true).Show);
        Assert.Null(policy.Evaluate(Now.AddHours(23), Unstored(Now.AddHours(23), 60 * 23), true).Show);
        Assert.NotNull(policy.Evaluate(Now + RecordingHealthPolicy.RepeatEvery, Unstored(Now.AddHours(24), 60 * 24), true).Show);
    }

    /// <summary>The upload notices' switch: off shows nothing and takes away what is out; on again, the problem is said again.</summary>
    [Fact]
    public void TheSwitch_Off_WithdrawsIt_AndShowsNothing()
    {
        var policy = new RecordingHealthPolicy();
        policy.Evaluate(Now, Unstored(Now, 30), true);

        Assert.Equal(new RecordingHealthChange(null, true), policy.Evaluate(Now.AddMinutes(1), Unstored(Now.AddMinutes(1), 31), false));
        Assert.Equal(new RecordingHealthChange(null, false), policy.Evaluate(Now.AddMinutes(2), Unstored(Now.AddMinutes(2), 32), false));

        Assert.NotNull(policy.Evaluate(Now.AddMinutes(3), Unstored(Now.AddMinutes(3), 33), true).Show);
    }

    /// <summary>The state is in memory: after a restart a loop still broken is said again, once its own counters get there.</summary>
    [Fact]
    public void ARestart_StillBroken_SaysItAgain()
    {
        new RecordingHealthPolicy().Evaluate(Now, Unstored(Now, 30), true);

        var restarted = new RecordingHealthPolicy();

        Assert.NotNull(restarted.Evaluate(Now.AddMinutes(1), Unstored(Now.AddMinutes(1), 31), true).Show);
    }
}

/// <summary>
/// What the tracker reports for <see cref="RecordingHealthPolicy"/>: recordable time since the last stored window, and when
/// the loop last polled. Signed out, with a server that answers nothing, so the outbox only fills.
/// </summary>
public sealed class RecordingCountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"recording-count-{Guid.NewGuid():N}");
    private readonly AppConfig _config;
    private readonly ApiClient _api;
    private readonly ActivityTrackerService _tracker;

    private static readonly ActivitySample Code = new() { ProcessName = "code", WindowTitle = "notes.md", ExecutablePath = @"C:\apps\code.exe" };

    public RecordingCountTests()
    {
        _config = new AppConfig { FilePath = Path.Combine(_dir, "config.json"), ApiBaseUrl = "https://server.test" };
        _api = new ApiClient(_config, new NoServer());
        _tracker = new ActivityTrackerService(_config, _api, new OutboxStore(Path.Combine(_dir, "outbox"), TimeSpan.FromDays(30)),
            new TodayTotal(Path.Combine(_dir, "today.json"), TimeZoneInfo.Utc));
    }

    [Fact]
    public void RecordableTime_Counts_UntilAWindowIsStored()
    {
        var start = MinuteStart();
        _tracker.Credit(start.AddSeconds(5), TimeSpan.FromSeconds(5), [new Observation(Code, 0, false, false, false)]);
        _tracker.Credit(start.AddSeconds(10), TimeSpan.FromSeconds(5), [new Observation(Code, 0, false, false, false)]);

        Assert.Equal(TimeSpan.FromSeconds(10), _tracker.GetStatus().UnrecordedFor);

        _tracker.Flush(); // the minute goes to the outbox

        Assert.Equal(TimeSpan.Zero, _tracker.GetStatus().UnrecordedFor);
    }

    /// <summary>
    /// Decided 2026-11-23: a span with nothing the user's own rules let through (an ignored app, a dropped private window — the
    /// poll hands an empty list) is not recordable time, so working only in ignored apps never reaches U. Nor is a gap too long
    /// to credit (a sleep).
    /// </summary>
    [Fact]
    public void IgnoredWindows_AndGapsTooLongToCredit_DoNotCount()
    {
        var start = MinuteStart();
        _tracker.Credit(start.AddSeconds(5), TimeSpan.FromSeconds(5), []);
        _tracker.Credit(start.AddHours(2), TimeSpan.FromHours(2), [new Observation(Code, 0, false, false, false)]);

        Assert.Equal(TimeSpan.Zero, _tracker.GetStatus().UnrecordedFor);
    }

    /// <summary>A tracker never started (signed out at first run, say) is not a silent one.</summary>
    [Fact]
    public void NeverStarted_HasNoLastPoll_AndNoFailure()
    {
        Assert.Null(_tracker.GetStatus().LastPollUtc);
        Assert.Null(_tracker.GetStatus().PollFailingSinceUtc);
    }

    private static DateTime MinuteStart()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _api.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class NoServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
