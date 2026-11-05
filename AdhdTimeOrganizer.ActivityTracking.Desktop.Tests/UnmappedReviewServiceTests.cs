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

    /// <summary>
    /// A laptop opened at 2 a.m. is not reminded of last night's ritual — with the server answering as it really does
    /// once the moment has passed: tonight's as the evening just gone, tomorrow's as the next.
    /// </summary>
    [Fact]
    public async Task AnEveningMissed_IsSkipped()
    {
        var service = Service(new UnmappedReviewStore());
        await service.TickAsync(Moment.AddMinutes(-10), CancellationToken.None); // learns the moment

        MoveServerPast(Moment);
        await service.TickAsync(Moment.AddHours(5), CancellationToken.None);

        Assert.Empty(_shown);
        Assert.Equal(0, _server.UnmappedRequests);
    }

    /// <summary>
    /// The laptop slept through the moment and wakes 90 minutes later. The hourly refresh brings tomorrow's moment as
    /// the next — which alone would lose tonight's review — and tonight's as the evening just gone, still in its window.
    /// </summary>
    [Fact]
    public async Task AWakeAfterTheMoment_StillInvites_ForTheEveningJustGone()
    {
        var service = Service(new UnmappedReviewStore());
        await service.TickAsync(Moment.AddHours(-2), CancellationToken.None); // learns tonight's, then sleeps

        MoveServerPast(Moment);
        await service.TickAsync(Moment.AddMinutes(90), CancellationToken.None);

        Assert.Single(_shown);
        Assert.Equal(2, _server.MomentRequests); // the wake refreshed the moment, and tonight's was not lost to it
        Assert.Equal("2026-10-19", _server.LastUnmappedDate);
    }

    /// <summary>A tracker started after the moment — the PC was off at 22:00 — has only ever heard the server's answer since.</summary>
    [Fact]
    public async Task AStartAfterTheMoment_StillInvites()
    {
        MoveServerPast(Moment);
        var service = Service(new UnmappedReviewStore());

        await service.TickAsync(Moment.AddMinutes(40), CancellationToken.None);
        await service.TickAsync(Moment.AddMinutes(41), CancellationToken.None);

        Assert.Single(_shown);
        Assert.Equal(1, _server.UnmappedRequests);
    }

    /// <summary>
    /// Bed at 01:30 puts the evening at 00:30, after the date has turned: the figures asked for — and the day the invite's
    /// Review opens — are the evening's own day, not the half hour since midnight.
    /// </summary>
    [Fact]
    public async Task AnEveningPastMidnight_AsksAboutTheDayBefore()
    {
        var pastMidnight = new DateTime(2026, 10, 20, 0, 30, 0, DateTimeKind.Utc);
        _server.MomentUtc = pastMidnight;
        _server.Date = new DateOnly(2026, 10, 19);
        _server.Unmapped = TwoApps; // its "date" is 2026-10-19
        var service = Service(new UnmappedReviewStore());

        await service.TickAsync(pastMidnight.AddMinutes(1), CancellationToken.None);

        Assert.Equal("2026-10-19", _server.LastUnmappedDate);
        Assert.Equal(new DateOnly(2026, 10, 19), Assert.Single(_shown).Date);
    }

    /// <summary>A server from before dates and the previous evening: no <c>date</c> is sent, and the next moment still invites.</summary>
    [Fact]
    public async Task AServerWithoutDates_AsksAboutToday()
    {
        _server.SendsDates = false;
        var service = Service(new UnmappedReviewStore());

        await service.TickAsync(Moment.AddMinutes(1), CancellationToken.None);

        Assert.Single(_shown);
        Assert.Null(_server.LastUnmappedDate);
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

    /// <summary>What the server answers once <paramref name="moment"/> has passed: it as the evening just gone, the next day's as the next.</summary>
    private void MoveServerPast(DateTime moment)
    {
        _server.PreviousUtc = moment;
        _server.PreviousDate = DateOnly.FromDateTime(moment);
        _server.MomentUtc = moment.AddDays(1);
        _server.Date = DateOnly.FromDateTime(moment.AddDays(1));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        private int _momentRequests;
        private int _unmappedRequests;

        public DateTime MomentUtc { get; set; }

        /// <summary>The next evening's day; defaults to the moment's own date.</summary>
        public DateOnly? Date { get; set; }

        /// <summary>The evening just gone; null is a moment no evening has passed since the test began.</summary>
        public DateTime? PreviousUtc { get; set; }

        public DateOnly? PreviousDate { get; set; }

        /// <summary>False is a server from before the dates and the previous evening: only <c>enabled</c> and <c>momentUtc</c>.</summary>
        public bool SendsDates { get; set; } = true;

        public string Unmapped { get; set; } = "";
        public HttpStatusCode UnmappedStatus { get; set; } = HttpStatusCode.OK;
        public bool HasRoutes { get; set; } = true;
        public int MomentRequests => Volatile.Read(ref _momentRequests);
        public int UnmappedRequests => Volatile.Read(ref _unmappedRequests);

        /// <summary>The <c>date</c> the last unmapped request named; null when it named none.</summary>
        public string? LastUnmappedDate { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/tracker/refresh":
                    return Task.FromResult(Json("""{ "accessToken": "access", "refreshToken": "refresh-2" }"""));

                case "/api/tracker/evening-shutdown-moment":
                    Interlocked.Increment(ref _momentRequests);
                    return Task.FromResult(HasRoutes ? Json(MomentJson()) : new HttpResponseMessage(HttpStatusCode.NotFound));

                case "/api/tracker/desktop/unmapped":
                    Interlocked.Increment(ref _unmappedRequests);
                    LastUnmappedDate = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["date"];
                    return Task.FromResult(HasRoutes ? Json(Unmapped, UnmappedStatus) : new HttpResponseMessage(HttpStatusCode.NotFound));

                default:
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private string MomentJson()
        {
            var moment = $$"""{ "enabled": true, "momentUtc": "{{MomentUtc:yyyy-MM-ddTHH:mm:ss}}Z" """;
            if (!SendsDates) return moment + "}";

            var date = Date ?? DateOnly.FromDateTime(MomentUtc);
            var previous = PreviousUtc is { } p
                ? $$""", "previousMomentUtc": "{{p:yyyy-MM-ddTHH:mm:ss}}Z", "previousDate": "{{PreviousDate ?? DateOnly.FromDateTime(p):yyyy-MM-dd}}" """
                // A server always names one; a day before the next is close enough for tests that never reach it.
                : $$""", "previousMomentUtc": "{{MomentUtc.AddDays(-1):yyyy-MM-ddTHH:mm:ss}}Z", "previousDate": "{{date.AddDays(-1):yyyy-MM-dd}}" """;
            return moment + $$""", "date": "{{date:yyyy-MM-dd}}" """ + previous + "}";
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
