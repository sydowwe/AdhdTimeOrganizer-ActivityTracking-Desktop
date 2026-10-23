using System.Net;
using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The evening invite end to end except the toast: the server's evening moment in, the server's unmapped time in, an
/// invite or nothing out — and how often it asks. Wrong in either direction nothing errors: an invite every ten
/// seconds, or never again after a restart, is just a toast.
/// </summary>
/// <remarks>The server is an <see cref="HttpMessageHandler"/>; nothing reaches the network or the user's files.</remarks>
public sealed class UnmappedReviewServiceTests : IDisposable
{
    private static readonly DateTime Moment = new(2026, 10, 19, 20, 30, 0, DateTimeKind.Utc);

    private const string TwoApps = """
        { "date": "2026-10-19", "trackedSeconds": 21600, "unmappedSeconds": 7800, "apps": [
          { "processName": "discord", "productName": "Discord", "activeSeconds": 3000, "isBrowser": false },
          { "processName": "chrome", "productName": "Google Chrome", "activeSeconds": 1800, "isBrowser": true } ] }
        """;

    private const string NoApps = """{ "date": "2026-10-19", "trackedSeconds": 3600, "unmappedSeconds": 600, "apps": [] }""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"review-service-{Guid.NewGuid():N}");
    private readonly FakeServer _server = new();
    private readonly AppConfig _config;
    private readonly ApiClient _api;
    private readonly List<UnmappedInvite> _shown = [];
    private bool _paused;

    public UnmappedReviewServiceTests()
    {
        _config = new AppConfig { FilePath = Path.Combine(_dir, "config.json"), ApiBaseUrl = "https://server.test", RefreshToken = "refresh" };
        _api = new ApiClient(_config, _server);
        _server.MomentUtc = Moment;
        _server.Unmapped = TwoApps;
    }

    [Fact]
    public async Task BeforeTheEvening_NothingButTheMomentIsAsked()
    {
        var service = Service(new UnmappedReviewStore());

        await service.TickAsync(Moment.AddMinutes(-30), CancellationToken.None);
        await service.TickAsync(Moment.AddMinutes(-29), CancellationToken.None);

        Assert.Empty(_shown);
        Assert.Equal(1, _server.MomentRequests); // held, not asked again every pass
        Assert.Equal(0, _server.UnmappedRequests);
    }

    [Fact]
    public async Task AtTheEvening_WithAppsToMap_InvitesOnce()
    {
        var service = Service(new UnmappedReviewStore());

        await service.TickAsync(Moment.AddMinutes(1), CancellationToken.None);
        await service.TickAsync(Moment.AddMinutes(2), CancellationToken.None);
        await service.TickAsync(Moment.AddMinutes(30), CancellationToken.None);

        var invite = Assert.Single(_shown);
        Assert.Equal(TimeSpan.FromMinutes(130), invite.Unmapped);
        Assert.Equal(new[] { "Discord", "Google Chrome" }, invite.Apps.Select(a => a.Name));
        Assert.Equal(1, _server.UnmappedRequests);
    }

    /// <summary>The server's "don't ask": the evening is spent all the same, so it is not asked about again at 21:00.</summary>
    [Fact]
    public async Task AtTheEvening_WithNothingWorthAsking_SaysNothing_AndDoesNotAskAgain()
    {
        _server.Unmapped = NoApps;
        var service = Service(new UnmappedReviewStore());

        await service.TickAsync(Moment.AddMinutes(1), CancellationToken.None);
        await service.TickAsync(Moment.AddMinutes(61), CancellationToken.None);

        Assert.Empty(_shown);
        Assert.Equal(1, _server.UnmappedRequests);
    }

    [Fact]
    public async Task ARestartTheSameEvening_DoesNotInviteAgain()
    {
        var store = new UnmappedReviewStore();
        await Service(store).TickAsync(Moment.AddMinutes(1), CancellationToken.None);

        await Service(store).TickAsync(Moment.AddMinutes(10), CancellationToken.None);

        Assert.Single(_shown);
        Assert.Equal(1, _server.UnmappedRequests);
    }

    /// <summary>A laptop opened at 2 a.m. is not reminded of last night's ritual.</summary>
    [Fact]
    public async Task AnEveningMissed_IsSkipped()
    {
        var service = Service(new UnmappedReviewStore());
        await service.TickAsync(Moment.AddMinutes(-10), CancellationToken.None); // learns the moment

        await service.TickAsync(Moment.AddHours(5), CancellationToken.None);

        Assert.Empty(_shown);
        Assert.Equal(0, _server.UnmappedRequests);
    }

    [Fact]
    public async Task TomorrowsEvening_IsInvitedAfterToday()
    {
        var service = Service(new UnmappedReviewStore());
        await service.TickAsync(Moment.AddMinutes(1), CancellationToken.None);

        _server.MomentUtc = Moment.AddDays(1); // what the server answers once tonight's has passed
        await service.TickAsync(Moment.AddMinutes(2), CancellationToken.None);
        await service.TickAsync(Moment.AddDays(1).AddMinutes(1), CancellationToken.None);

        Assert.Equal(2, _shown.Count);
        Assert.Equal(2, _server.UnmappedRequests);
    }

    [Fact]
    public async Task WhilePaused_Disabled_OrSignedOut_NothingIsAsked()
    {
        var service = Service(new UnmappedReviewStore());

        _paused = true;
        await service.TickAsync(Moment.AddMinutes(1), CancellationToken.None);
        _paused = false;
        _config.UnmappedReviewEnabled = false;
        await service.TickAsync(Moment.AddMinutes(1), CancellationToken.None);
        _config.UnmappedReviewEnabled = true;
        _config.RefreshToken = "";
        await service.TickAsync(Moment.AddMinutes(1), CancellationToken.None);

        Assert.Empty(_shown);
        Assert.Equal(0, _server.MomentRequests + _server.UnmappedRequests);
    }

    /// <summary>A server from before the routes: asked once, then left alone for the session.</summary>
    [Fact]
    public async Task AnOlderServer_EndsItForTheSession()
    {
        _server.HasRoutes = false;
        var service = Service(new UnmappedReviewStore());

        await service.TickAsync(Moment.AddMinutes(1), CancellationToken.None);
        await service.TickAsync(Moment.AddMinutes(2), CancellationToken.None);

        Assert.Empty(_shown);
        Assert.Equal(1, _server.MomentRequests);
    }

    /// <summary>A failed read is retried in minutes, not on every ten-second pass, and the evening is still there to invite.</summary>
    [Fact]
    public async Task AFailedRead_IsRetriedLater_NotEveryPass()
    {
        _server.UnmappedStatus = HttpStatusCode.ServiceUnavailable;
        var service = Service(new UnmappedReviewStore());

        await service.TickAsync(Moment.AddMinutes(1), CancellationToken.None);
        await service.TickAsync(Moment.AddMinutes(1).AddSeconds(10), CancellationToken.None);
        Assert.Equal(1, _server.UnmappedRequests);

        _server.UnmappedStatus = HttpStatusCode.OK;
        await service.TickAsync(Moment.AddMinutes(1) + UnmappedReviewService.RetryAfter, CancellationToken.None);

        Assert.Single(_shown);
        Assert.Equal(2, _server.UnmappedRequests);
    }

    private UnmappedReviewService Service(UnmappedReviewStore store) => new(_config, _api, store, _shown.Add, _ => _paused);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        private int _momentRequests;
        private int _unmappedRequests;

        public DateTime MomentUtc { get; set; }
        public string Unmapped { get; set; } = "";
        public HttpStatusCode UnmappedStatus { get; set; } = HttpStatusCode.OK;
        public bool HasRoutes { get; set; } = true;
        public int MomentRequests => Volatile.Read(ref _momentRequests);
        public int UnmappedRequests => Volatile.Read(ref _unmappedRequests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/tracker/refresh":
                    return Task.FromResult(Json("""{ "accessToken": "access", "refreshToken": "refresh-2" }"""));

                case "/api/tracker/evening-shutdown-moment":
                    Interlocked.Increment(ref _momentRequests);
                    return Task.FromResult(HasRoutes
                        ? Json($$"""{ "enabled": true, "momentUtc": "{{MomentUtc:yyyy-MM-ddTHH:mm:ss}}Z" }""")
                        : new HttpResponseMessage(HttpStatusCode.NotFound));

                case "/api/tracker/desktop/unmapped":
                    Interlocked.Increment(ref _unmappedRequests);
                    return Task.FromResult(HasRoutes ? Json(Unmapped, UnmappedStatus) : new HttpResponseMessage(HttpStatusCode.NotFound));

                default:
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
