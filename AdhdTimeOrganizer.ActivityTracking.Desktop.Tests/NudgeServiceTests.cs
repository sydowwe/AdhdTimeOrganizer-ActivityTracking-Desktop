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
    private int _signIns;

    private DateTime _start = DateTime.UtcNow.AddHours(-2);
    private AppConfig _config = null!; // the latest Create()'s

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

    // ---- upload notices ---------------------------------------------------

    /// <summary>A stall reaches the notifier as a notice of its own kind, once, and leaves when the outbox drains.</summary>
    [Fact]
    public void AStall_IsShownOnce_AndWithdrawnWhenItResolves()
    {
        var (service, notifier, _) = Create();
        var now = DateTime.UtcNow;

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

    // ---- helpers ---------------------------------------------------------

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
            signIn: () => _signIns++, openFolder: _folders.Add);
        _services.Add(service);
        return (service, notifier, opened);
    }

    private static UploadSnapshot Stalled(DateTime nowUtc) =>
        new(HasSession: true, IsPaused: false, PendingWindows: 90, OldestPendingUtc: nowUtc.AddHours(-25), RejectedWindows: 0);

    private void OffPlan(NudgeService service, int minutes) => Minutes(service, minutes, onPlan: false);

    /// <summary>Minute windows from <see cref="_start"/> on, each answered 65 s after it began, as the send loop would.</summary>
    private void Minutes(NudgeService service, int count, bool onPlan)
    {
        for (var i = 0; i < count; i++)
        {
            var windowStart = _start;
            _start = _start.AddMinutes(1);
            service.OnMinuteResolved(windowStart.AddSeconds(65), new MinuteVerdict(windowStart, Thesis, onPlan, onPlan ? null : "YouTube"));
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
