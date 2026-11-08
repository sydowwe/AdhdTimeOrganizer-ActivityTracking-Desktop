using System.Collections.Concurrent;
using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The wiring around the two pure policies: minutes and polls reaching them, a button press reaching the right one,
/// what "Turn off" and "Switch plan" do, the gap between the two families, and the drift state outliving a restart.
/// </summary>
/// <remarks>
/// Nothing here touches the user's machine: the notifier is a recording fake (no Windows registration, no toast), the
/// browser is a list, the config and the drift state live in a temp folder, and the quiet conditions are off so the
/// real full-screen and microphone state can't decide a test.
/// </remarks>
public sealed class NudgeServiceTests : IDisposable
{
    private static readonly PlannedBlock Thesis = new()
    {
        PlannerTaskId = 1,
        ActivityId = 10,
        ActivityName = "Thesis",
        StartsAtUtc = DateTime.UtcNow.AddHours(-3),
        EndsAtUtc = DateTime.UtcNow.AddHours(3)
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nudge-service-{Guid.NewGuid():N}");
    private readonly List<NudgeService> _services = [];
    private readonly List<string> _folders = [];
    private readonly ConcurrentQueue<long> _startedBlocks = new(); // filled from the thread pool
    private readonly ConcurrentQueue<long> _skippedBlocks = new(); // filled from the thread pool
    private int _signIns;

    private DateTime _start = DateTime.UtcNow.AddHours(-2);
    private AppConfig _config = null!; // the latest Create()'s
    private readonly TimeZoneInfo _zone = NoonNow();

    [Fact]
    public void ResolvedMinutes_ReachTheDriftPolicy_AndItsNudgeIsShown()
    {
        var (service, notifier, _) = Create();

        OffPlan(service, 10);

        var nudge = Assert.Single(notifier.Shown);
        Assert.Equal(NudgeKind.Drift, nudge.Kind);
        Assert.Equal("Thesis", nudge.Drift!.PlannedName);
    }

    [Fact]
    public void TurnOff_OnADriftToast_TurnsOffDriftNudgesOnly_AndSavesIt()
    {
        var (service, notifier, _) = Create();
        OffPlan(service, 10);

        service.OnActivated(new NudgeActivation(NudgeKind.Drift, notifier.Shown[^1].Id, NudgeResponse.TurnOff));

        Assert.False(_config.DriftNudgesEnabled);
        Assert.True(_config.HyperfocusNudgesEnabled);
        var saved = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_config.FilePath))!;
        Assert.False(saved.DriftNudgesEnabled);
        Assert.True(saved.HyperfocusNudgesEnabled);
    }

    [Fact]
    public void TurnOff_OnAHyperfocusToast_LeavesDriftNudgesOn()
    {
        var (service, _, _) = Create();

        service.OnActivated(new NudgeActivation(NudgeKind.Stretch, 1, NudgeResponse.TurnOff));

        Assert.False(_config.HyperfocusNudgesEnabled);
        Assert.True(_config.DriftNudgesEnabled);
    }

    [Fact]
    public void SwitchPlan_OpensTodaysPlannerInTheWebApp()
    {
        var (service, notifier, opened) = Create();
        OffPlan(service, 10);

        service.OnActivated(new NudgeActivation(NudgeKind.Drift, notifier.Shown[^1].Id, NudgeResponse.SwitchPlan));

        Assert.Equal("https://app.test/day-planner", Assert.Single(opened).AbsoluteUri);
    }

    /// <summary>Back on plan, the toast still in the notification centre is out of date and leaves it.</summary>
    [Fact]
    public void BackOnPlan_WithdrawsTheDriftToast()
    {
        var (service, notifier, _) = Create();
        OffPlan(service, 10);

        Minutes(service, 1, onPlan: true);

        Assert.Equal([NudgeKind.Drift], notifier.Removed);
    }

    /// <summary>"At most one per block" holds across a restart in the middle of the block.</summary>
    [Fact]
    public void ARestart_RemembersTheNudgeAlreadyGivenInTheBlock()
    {
        var (first, firstNotifier, _) = Create();
        OffPlan(first, 10);
        Assert.Single(firstNotifier.Shown);
        first.Dispose();

        var (second, secondNotifier, _) = Create();
        OffPlan(second, 30);

        Assert.Empty(secondNotifier.Shown);
    }

    [Fact]
    public void ARestart_KeepsTheStreak()
    {
        var (first, firstNotifier, _) = Create();
        OffPlan(first, 6);
        first.Dispose();

        var (second, secondNotifier, _) = Create();
        OffPlan(second, 4);

        Assert.Empty(firstNotifier.Shown);
        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(secondNotifier.Shown).Length);
    }

    /// <summary>
    /// A drift nudge due right after a hyperfocus one waits out the gap between the families instead of landing a
    /// minute after it — and then still comes.
    /// </summary>
    [Fact]
    public void ADriftNudge_WaitsTenMinutesAfterAHyperfocusOne()
    {
        var (service, notifier, _) = Create();
        var t0 = _start;
        var app = new ActivitySample { ProcessName = "chrome", WindowTitle = "YouTube", ProductName = "Google Chrome" };

        // At the computer for 90 minutes: the stretch nudge at t0 + 90 min.
        for (var poll = t0; poll <= t0.AddMinutes(90); poll = poll.AddSeconds(10))
            service.OnPoll(poll, TimeSpan.FromSeconds(30), isLocked: false, isPresent: true, app);
        Assert.Equal(NudgeKind.Stretch, Assert.Single(notifier.Shown).Kind);

        // Off plan from t0 + 81 min: due at the 10th minute (answered at t0 + 91 min), held until t0 + 100 min.
        _start = t0.AddMinutes(81);
        OffPlan(service, 18);
        Assert.Single(notifier.Shown);

        OffPlan(service, 1);
        var drift = notifier.Shown[^1];
        Assert.Equal(NudgeKind.Drift, drift.Kind);
        Assert.Equal(TimeSpan.FromMinutes(19), drift.Length);
    }

    // ---- the start ramp ---------------------------------------------------

    /// <summary>
    /// The heartbeat's answer names the next block; at its start, with someone at the computer, the ramp's toast shows —
    /// and Start now both starts the block on the server and opens its timer in the web app.
    /// </summary>
    [Fact]
    public void TheStartRamp_ShowsAtTheBlocksStart_AndStartNow_StartsItAndOpensItsTimer()
    {
        var (service, notifier, opened) = Create();
        var startsUtc = NextMinute().AddMinutes(1);
        var email = Block(7, "Email", startsUtc, 30);

        Present(service, startsUtc.AddMinutes(-1));
        service.OnMinuteResolved(startsUtc.AddSeconds(-15), Verdict(startsUtc.AddMinutes(-2), null, null),
            new PlanView(startsUtc.AddMinutes(-2), [], email, false, null));
        Assert.Empty(notifier.Shown);

        Present(service, startsUtc);
        var ramp = Assert.Single(notifier.Shown);
        Assert.Equal(NudgeKind.StartRamp, ramp.Kind);
        Assert.Equal("Email", ramp.StartRamp!.PlannedName);

        service.OnActivated(new NudgeActivation(NudgeKind.StartRamp, ramp.Id, NudgeResponse.StartNow));

        Assert.Equal("https://app.test/?track=7", Assert.Single(opened).AbsoluteUri);
        Assert.True(SpinWait.SpinUntil(() => !_startedBlocks.IsEmpty, TimeSpan.FromSeconds(5)), "the block is started on the server");
        Assert.Equal(new[] { 7L }, _startedBlocks);
    }

    /// <summary>
    /// "Not this one" ends drift nudges in that block too: twenty-five minutes off plan in it — past the gap between the
    /// families — and no drift toast. On the server it skips the block, and starts nothing.
    /// </summary>
    [Fact]
    public void NotThisOne_OnTheRamp_AlsoQuietsDriftInThatBlock()
    {
        // Near the real clock: a press is handled at DateTime.UtcNow, and a poll two hours earlier would read as a
        // two-hour stretch to the hyperfocus policy.
        _start = NextMinute().AddMinutes(-2);
        var (service, notifier, opened) = Create();
        Present(service, _start.AddSeconds(30));
        Minutes(service, 1, onPlan: false, withPlan: true);
        var ramp = Assert.Single(notifier.Shown);

        service.OnActivated(new NudgeActivation(NudgeKind.StartRamp, ramp.Id, NudgeResponse.NotThisOne));
        OffPlan(service, 25);

        Assert.Equal([NudgeKind.StartRamp], notifier.Shown.Select(n => n.Kind));
        Assert.Empty(opened);
        Assert.Empty(_startedBlocks);
        Assert.True(SpinWait.SpinUntil(() => !_skippedBlocks.IsEmpty, TimeSpan.FromSeconds(5)), "the block is skipped on the server");
        Assert.Equal(new[] { ramp.StartRamp!.PlannerTaskId }, _skippedBlocks);
    }

    /// <summary>"In 2 minutes" is still a maybe, and a second press on an answered toast is not current: neither skips.</summary>
    [Fact]
    public void OnlyACurrentNotThisOne_SkipsTheBlock()
    {
        _start = NextMinute().AddMinutes(-2);
        var (service, notifier, _) = Create();
        Present(service, _start.AddSeconds(30));
        Minutes(service, 1, onPlan: false, withPlan: true);
        var ramp = Assert.Single(notifier.Shown);

        service.OnActivated(new NudgeActivation(NudgeKind.StartRamp, ramp.Id, NudgeResponse.Later));
        service.OnActivated(new NudgeActivation(NudgeKind.StartRamp, ramp.Id, NudgeResponse.NotThisOne));

        Assert.False(SpinWait.SpinUntil(() => !_skippedBlocks.IsEmpty, TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>The test toast's block is not a planner task: nothing reaches the server for it, whichever button.</summary>
    [Theory]
    [InlineData(NudgeResponse.StartNow)]
    [InlineData(NudgeResponse.NotThisOne)]
    public void TheTestToast_TellsTheServerNothing(NudgeResponse response)
    {
        var (service, notifier, _) = Create();
        service.ShowTest(NudgeKind.StartRamp);
        var ramp = Assert.Single(notifier.Shown);

        service.OnActivated(new NudgeActivation(NudgeKind.StartRamp, ramp.Id, response));

        Assert.False(SpinWait.SpinUntil(() => !_startedBlocks.IsEmpty || !_skippedBlocks.IsEmpty, TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>The ramp's state is kept beside drift.json, so a restart in the block doesn't offer it again.</summary>
    [Fact]
    public void ARestart_DoesNotOfferTheSameBlockAgain()
    {
        var (first, firstNotifier, _) = Create();
        Present(first, _start.AddSeconds(30));
        Minutes(first, 1, onPlan: false, withPlan: true);
        Assert.Equal(NudgeKind.StartRamp, Assert.Single(firstNotifier.Shown).Kind);
        first.Dispose();

        var (second, secondNotifier, _) = Create();
        Present(second, _start.AddSeconds(30));
        Minutes(second, 1, onPlan: false, withPlan: true);

        Assert.Empty(secondNotifier.Shown);
        Assert.True(File.Exists(Path.Combine(_dir, "start-ramp.json")));
    }

    [Fact]
    public void TheStartRamp_Off_ShowsNothing()
    {
        var (service, notifier, _) = Create();
        _config.StartRampEnabled = false;
        Present(service, _start.AddSeconds(30));

        Minutes(service, 1, onPlan: false, withPlan: true);

        Assert.Empty(notifier.Shown);
    }

    // ---- upload notices ---------------------------------------------------

    /// <summary>A stall reaches the notifier as a notice of its own kind, once, and leaves when the outbox drains.</summary>
    [Fact]
    public void AStall_IsShownOnce_AndWithdrawnWhenItResolves()
    {
        var (service, notifier, _) = Create();
        var firstSeen = DateTime.UtcNow;
        var now = firstSeen + UploadHealthPolicy.StallGrace;

        service.OnUploadStatus(firstSeen, Stalled(firstSeen));
        Assert.Empty(notifier.Shown);

        service.OnUploadStatus(now, Stalled(now));
        service.OnUploadStatus(now.AddSeconds(2), Stalled(now.AddSeconds(2)));

        var shown = Assert.Single(notifier.Shown);
        Assert.Equal(NudgeKind.UploadHealth, shown.Kind);
        Assert.Equal(UploadProblem.Stalled, shown.Upload!.Problem);

        service.OnUploadStatus(now.AddMinutes(1), new UploadSnapshot(true, false, 0, null, 0));

        Assert.Equal([NudgeKind.UploadHealth], notifier.Removed);
    }

    /// <summary>
    /// "Turn off" on an upload notice is its own switch: the nudges stay on, and what follows is silence. And the other
    /// way round — a nudge's "Turn off" must not silence the notice that tells you the tracker has stopped delivering.
    /// </summary>
    [Fact]
    public void TurnOff_OnAnUploadNotice_SwitchesOffOnlyThose_AndSavesIt()
    {
        var (service, notifier, _) = Create();
        var now = DateTime.UtcNow;
        service.OnUploadStatus(now, Stalled(now));
        service.OnUploadStatus(now + UploadHealthPolicy.StallGrace, Stalled(now + UploadHealthPolicy.StallGrace));
        Assert.Single(notifier.Shown);

        service.OnActivated(new NudgeActivation(NudgeKind.UploadHealth, 1, NudgeResponse.TurnOff));

        Assert.False(_config.UploadNoticesEnabled);
        Assert.True(_config.HyperfocusNudgesEnabled);
        Assert.True(_config.DriftNudgesEnabled);
        Assert.False(JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_config.FilePath))!.UploadNoticesEnabled);
        Assert.Contains(NudgeKind.UploadHealth, notifier.Removed);

        notifier.Shown.Clear();
        service.OnUploadStatus(now.AddDays(3), Stalled(now.AddDays(3)));
        Assert.Empty(notifier.Shown);
    }

    [Fact]
    public void TurnOff_OnANudge_LeavesUploadNoticesOn()
    {
        var (service, _, _) = Create();

        service.OnActivated(new NudgeActivation(NudgeKind.Stretch, 1, NudgeResponse.TurnOff));
        service.OnActivated(new NudgeActivation(NudgeKind.Drift, 1, NudgeResponse.TurnOff));

        Assert.True(_config.UploadNoticesEnabled);
    }

    [Fact]
    public void TheButtons_OpenTheSignInForm_AndTheLogFolder()
    {
        var (service, _, _) = Create();

        service.OnActivated(new NudgeActivation(NudgeKind.UploadHealth, 1, NudgeResponse.SignIn));
        service.OnActivated(new NudgeActivation(NudgeKind.UploadHealth, 1, NudgeResponse.OpenLogs));

        Assert.Equal(1, _signIns);
        Assert.Equal(Program.LogDirectory, Assert.Single(_folders));
    }

    /// <summary>
    /// The chime is fed the server's resolution with the drift verdict, outside any planned block. "Stop for today" removes
    /// it, is saved, and holds after a restart; nothing else is switched off.
    /// </summary>
    [Fact]
    public void TimeChime_ComesFromResolvedMinutes_AndStopForToday_OutlivesARestart()
    {
        var (service, notifier, _) = Create();
        _config.TimeChimeEnabled = true;
        _config.TimeChimeChoices = [new ChimeChoice("activity:7", "YouTube", true)];

        YouTubeMinutes(service, 15);

        var chime = Assert.Single(notifier.Shown);
        Assert.Equal((NudgeKind.TimeChime, "YouTube", TimeSpan.FromMinutes(15)), (chime.Kind, chime.AppName, chime.Length));

        service.OnActivated(new NudgeActivation(NudgeKind.TimeChime, chime.Id, NudgeResponse.StopForToday));
        Assert.Contains(NudgeKind.TimeChime, notifier.Removed);
        Assert.True(_config.TimeChimeEnabled);
        Assert.True(File.Exists(Path.Combine(_dir, "time-chime.json")));

        var (restarted, restartedNotifier, _) = Create();
        _config.TimeChimeEnabled = true;
        _config.TimeChimeChoices = [new ChimeChoice("activity:7", "YouTube", true)];
        YouTubeMinutes(restarted, 15);
        Assert.Empty(restartedNotifier.Shown);
    }

    [Fact]
    public void TimeChime_IsQuietInsideAPlannedBlock_WhileDriftNudgesAreOn()
    {
        var (service, notifier, _) = Create();
        _config.TimeChimeEnabled = true;
        _config.TimeChimeChoices = [new ChimeChoice("activity:7", "YouTube", true)];

        YouTubeMinutes(service, 15, Thesis);

        Assert.DoesNotContain(notifier.Shown, n => n.Kind == NudgeKind.TimeChime);
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>Minutes spent on the YouTube activity, outside any block unless one is given, answered as the send loop would.</summary>
    private void YouTubeMinutes(NudgeService service, int count, PlannedBlock? block = null)
    {
        ResolvedTime[] youTube = [new() { Target = "Activity", ActivityId = 7, Name = "YouTube", ActiveSeconds = 60, OnPlan = block is null ? null : false }];
        for (var i = 0; i < count; i++)
        {
            var windowStart = _start;
            _start = _start.AddMinutes(1);
            service.OnMinuteResolved(windowStart.AddSeconds(65),
                new MinuteVerdict(windowStart, block, block is null ? null : false, block is null ? null : "YouTube", youTube));
        }
    }

    /// <summary>
    /// A zone in which it is about noon now, so the two hours of minutes these tests replay never cross the chime's local
    /// midnight, whenever the suite runs.
    /// </summary>
    private static TimeZoneInfo NoonNow()
    {
        var offset = TimeSpan.FromMinutes(Math.Round((TimeSpan.FromHours(12) - DateTime.UtcNow.TimeOfDay).TotalMinutes));
        return TimeZoneInfo.CreateCustomTimeZone("noon-now", offset, "noon-now", "noon-now");
    }

    private (NudgeService Service, FakeNotifier Notifier, List<Uri> Opened) Create()
    {
        var config = _config = new AppConfig
        {
            FilePath = Path.Combine(_dir, "config.json"),
            DriftNudgesEnabled = true,
            QuietWhenFullscreen = false,
            QuietWhenMicrophoneInUse = false,
            WebAppUrl = "https://app.test"
        };
        var notifier = new FakeNotifier();
        var opened = new List<Uri>();
        var service = new NudgeService(config, uiContext: null, _ => notifier, opened.Add, Path.Combine(_dir, "drift.json"),
            signIn: () => _signIns++, openFolder: _folders.Add,
            startBlock: id =>
            {
                _startedBlocks.Enqueue(id);
                return Task.FromResult(true);
            },
            skipBlock: id =>
            {
                _skippedBlocks.Enqueue(id);
                return Task.FromResult(true);
            },
            zone: _zone);
        _services.Add(service);
        return (service, notifier, opened);
    }

    private static PlannedBlock Block(long id, string name, DateTime startsUtc, int minutes) => new()
    {
        PlannerTaskId = id, ActivityId = id * 10, ActivityName = name, StartsAtUtc = startsUtc, EndsAtUtc = startsUtc.AddMinutes(minutes)
    };

    private static DateTime NextMinute()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes(1);
    }

    private static MinuteVerdict Verdict(DateTime windowStart, PlannedBlock? block, bool? onPlan) =>
        new(windowStart, block, onPlan, onPlan == false ? "YouTube" : null);

    /// <summary>A poll that finds someone at the computer.</summary>
    private static void Present(NudgeService service, DateTime nowUtc) =>
        service.OnPoll(nowUtc, TimeSpan.FromSeconds(30), isLocked: false, isPresent: true,
            new ActivitySample { ProcessName = "code", WindowTitle = "Chapter 2", ProductName = "VS Code" });

    private static UploadSnapshot Stalled(DateTime nowUtc) =>
        new(HasSession: true, IsPaused: false, PendingWindows: 90, OldestPendingUtc: nowUtc.AddHours(-25), RejectedWindows: 0);

    private void OffPlan(NudgeService service, int minutes) => Minutes(service, minutes, onPlan: false);

    /// <summary>
    /// Minute windows from <see cref="_start"/> on, each answered 65 s after it began, as the send loop would; with
    /// <paramref name="withPlan"/>, the answer carries Thesis as the block covering it, not started, as a server with the
    /// start ramp sends it.
    /// </summary>
    private void Minutes(NudgeService service, int count, bool onPlan, bool withPlan = false)
    {
        for (var i = 0; i < count; i++)
        {
            var windowStart = _start;
            _start = _start.AddMinutes(1);
            service.OnMinuteResolved(windowStart.AddSeconds(65), new MinuteVerdict(windowStart, Thesis, onPlan, onPlan ? null : "YouTube"),
                withPlan ? new PlanView(windowStart, [Thesis], null, false, onPlan) : null);
        }
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeNotifier : INudgeNotifier
    {
        public List<Nudge> Shown { get; } = [];
        public List<NudgeKind> Removed { get; } = [];

        public void Show(Nudge nudge) => Shown.Add(nudge);
        public void Remove(NudgeKind kind) => Removed.Add(kind);
        public void Dispose() { }
    }
}
