using System.Net;
using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The real send loop against a fake server: what a sent window does to today's figures, the tray's "now" and the
/// mapping coverage. The parts are pure and tested on their own; this is the wiring between them, where a window
/// counted twice or an answer dropped on the floor would pass every other test.
/// </summary>
/// <remarks>
/// Nothing reaches the network or the user's files: the server is an <see cref="HttpMessageHandler"/>, and the config,
/// outbox and today's total live in a temp folder.
/// </remarks>
public sealed class SendLoopTests : IDisposable
{
    private const string Answer = """
        {
          "processedCount": 1,
          "resolution": [ { "target": "Activity", "activityId": 12, "name": "Thesis", "activeSeconds": 40, "onPlan": true } ],
          "planned": []
        }
        """;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"send-loop-{Guid.NewGuid():N}");
    private readonly DateTime _windowStart;
    private readonly FakeServer _server = new();
    private readonly AppConfig _config;
    private readonly OutboxStore _outbox;
    private readonly TodayTotal _today;

    public SendLoopTests()
    {
        var now = DateTime.UtcNow;
        _windowStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        _config = new AppConfig
        {
            FilePath = Path.Combine(_dir, "config.json"),
            ApiBaseUrl = "https://server.test",
            RefreshToken = "refresh",
            DriftNudgesEnabled = true
        };
        _outbox = new OutboxStore(Path.Combine(_dir, "outbox"), TimeSpan.FromDays(30));
        _today = new TodayTotal(Path.Combine(_dir, "today.json"), TimeZoneInfo.Utc);
    }

    /// <summary>
    /// The window goes out without the entry a server rule ignores, today's total loses that entry's seconds, the
    /// breakdown gains what the server resolved, the tray's "now" is the answer, and the coverage is fetched.
    /// </summary>
    [Fact]
    public async Task ASentWindow_SettlesTodaysFigures_AndFeedsTheTray()
    {
        Queue(("code", 40), ("ignored", 20));

        using (var tracker = Start())
        {
            var status = await WaitFor(tracker, s => s.PendingWindows == 0 && s.Coverage is not null);

            Assert.Equal(_windowStart, status.LastResolved?.WindowStartUtc);
            Assert.Equal("Thesis", status.LastResolved?.Response.Resolution[0].Name);
            Assert.Equal("Slack", Assert.Single(status.Coverage!.Uncovered).ProductName);
            Assert.Equal(TimeSpan.FromSeconds(40), status.TodayOnThisPc);
            Assert.Equal(new[] { new ActivityTotal("activity:12", "Thesis", 40) }, tracker.GetTodayTop(5));
        }

        var sent = Assert.Single(_server.Heartbeats);
        Assert.Contains("\"code\"", sent);
        Assert.DoesNotContain("\"ignored\"", sent);
    }

    /// <summary>A minute the server refuses is a lost minute; today's total must not keep counting it.</summary>
    [Fact]
    public async Task ARefusedWindow_LeavesTodaysTotal()
    {
        _server.HeartbeatStatus = HttpStatusCode.BadRequest;
        Queue(("code", 40));

        using var tracker = Start();
        var status = await WaitFor(tracker, s => s.PendingWindows == 0);

        Assert.Equal(TimeSpan.Zero, status.TodayOnThisPc);
        Assert.Null(status.LastResolved);
    }

    /// <summary>The upload-health notice counts these: each is a lost minute, and nothing else says so.</summary>
    [Fact]
    public async Task ARefusedWindow_IsCounted_AndAnUnsentOneNamesTheOldestMinute()
    {
        _server.HeartbeatStatus = HttpStatusCode.BadRequest;
        Queue(("code", 40));

        using var tracker = Start();
        var status = await WaitFor(tracker, s => s.PendingWindows == 0 && s.RejectedWindows == 1);

        Assert.Null(status.OldestPendingUtc);

        _server.HeartbeatStatus = HttpStatusCode.ServiceUnavailable; // a retry, not a loss
        Queue(("code", 40));
        tracker.RequestSend();
        var waiting = await WaitFor(tracker, s => s.LastSendResult == SendResult.RetryLater);

        Assert.Equal(1, waiting.PendingWindows);
        Assert.Equal(_windowStart, waiting.OldestPendingUtc);
        Assert.Equal(1, waiting.RejectedWindows);
    }

    /// <summary>A server from before the answer: the window is sent and counted, there is just nothing to resolve it by.</summary>
    [Fact]
    public async Task AnOldServersBareCount_SendsTheWindow_WithoutAnAnswer()
    {
        _server.HeartbeatBody = "1";
        _server.HasCoverageRoute = false;
        Queue(("code", 40));

        using var tracker = Start();
        var status = await WaitFor(tracker, s => s.PendingWindows == 0);
        await WaitUntil(() => _server.CoverageRequests > 0);

        Assert.Equal(TimeSpan.FromSeconds(40), status.TodayOnThisPc);
        Assert.Null(status.LastResolved);
        Assert.Empty(tracker.GetTodayTop(5));
        Assert.Null(tracker.GetStatus().Coverage);
    }

    [Fact]
    public async Task Coverage_IsNotAskedFor_WhileDriftNudgesAreOff()
    {
        _config.DriftNudgesEnabled = false;
        Queue(("code", 40));

        using var tracker = Start();
        await WaitFor(tracker, s => s.PendingWindows == 0);
        await Task.Delay(500);

        Assert.Equal(0, _server.CoverageRequests);
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>What the poll loop does with a finished window: to the outbox, and into today's total.</summary>
    private void Queue(params (string Process, int ActiveSeconds)[] entries)
    {
        var window = new ActivityWindow
        {
            WindowStart = _windowStart,
            Entries = entries.Select(e => new ActivityEntry
            {
                ProcessName = e.Process,
                WindowTitle = e.Process,
                ActiveSeconds = e.ActiveSeconds,
                BackgroundSeconds = 0,
                ActiveMonitor = 0,
                IsPlayingSound = false,
                IsFullscreen = false
            }).ToList()
        };
        _outbox.Save(window);
        _today.Add(window.WindowStart, entries.Sum(e => e.ActiveSeconds));
    }

    private ActivityTrackerService Start() =>
        new(_config, new ApiClient(_config, _server), _outbox, _today);

    private static async Task<TrackerStatus> WaitFor(ActivityTrackerService tracker, Func<TrackerStatus, bool> condition)
    {
        TrackerStatus? status = null;
        await WaitUntil(() => condition(status = tracker.GetStatus()));
        return status!;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the send loop did not get there in time");
            await Task.Delay(50);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        private int _coverageRequests;

        public HttpStatusCode HeartbeatStatus { get; set; } = HttpStatusCode.OK;
        public string HeartbeatBody { get; set; } = Answer;
        public bool HasCoverageRoute { get; set; } = true;
        public List<string> Heartbeats { get; } = [];
        public int CoverageRequests => Volatile.Read(ref _coverageRequests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/tracker/refresh":
                    return Json("""{ "accessToken": "access", "refreshToken": "refresh-2" }""");

                case "/api/tracker/desktop/rules":
                    return Json("""[ { "id": 1, "processName": "ignored", "processNameMatchType": "Exact", "isIgnored": true } ]""");

                case "/api/tracker/desktop/heartbeat":
                    var body = await request.Content!.ReadAsStringAsync(ct);
                    lock (Heartbeats) Heartbeats.Add(body);
                    return Json(HeartbeatBody, HeartbeatStatus);

                case "/api/tracker/desktop/coverage":
                    Interlocked.Increment(ref _coverageRequests);
                    return HasCoverageRoute
                        ? Json("""{ "days": 7, "trackedSeconds": 7200, "mappedSeconds": 1800, "uncovered": [ { "processName": "slack", "productName": "Slack", "activeSeconds": 5400 } ] }""")
                        : new HttpResponseMessage(HttpStatusCode.NotFound);

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
