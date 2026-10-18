using System.Net;
using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The refresh against a fake server. The server rotates on every refresh; a refresh answer lost after the rotation
/// committed leaves the agent holding the old token. The server exchanges that token again once the rotation is
/// more than 5 s old, and answers 409 inside those 5 s (401 from a server older than that) — so the agent must hold
/// it back until then, and never end the session over it. The decisions themselves are <c>RefreshPolicyTests</c>.
/// </summary>
public sealed class TokenRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"token-refresh-{Guid.NewGuid():N}");
    private readonly RefreshServer _server = new();
    private readonly ManualClock _clock = new();
    private readonly AppConfig _config;
    private readonly ApiClient _api;

    public TokenRefreshTests()
    {
        _config = new AppConfig
        {
            FilePath = Path.Combine(_dir, "config.json"),
            ApiBaseUrl = "https://server.test",
            RefreshToken = "refresh"
        };
        _api = new ApiClient(_config, _server, _clock);
    }

    /// <summary>"409": the server saying the token was rotated moments ago — by the refresh whose answer we lost.</summary>
    public static TheoryData<string> LostAnswers => ["500", "409", "timeout", "dropped connection", "200 without tokens"];

    [Theory]
    [MemberData(nameof(LostAnswers))]
    public async Task ALostAnswer_HoldsTheTokenBackPastTheGrace_ThenReplaysIt(string lostAnswer)
    {
        _server.Answers.Enqueue(lostAnswer switch
        {
            "500" => () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "409" => () => new HttpResponseMessage(HttpStatusCode.Conflict),
            "timeout" => () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
            "dropped connection" => () => throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely"),
            _ => () => Json("""{ "accessToken": "", "refreshToken": "" }""")
        });

        Assert.True(await _api.TryRestoreSessionAsync(), "a lost answer keeps the session");

        _clock.Advance(RefreshPolicy.ReplayDelay - TimeSpan.FromMilliseconds(1));
        Assert.True(await _api.TryRestoreSessionAsync());
        Assert.Single(_server.Requests);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(await _api.TryRestoreSessionAsync());

        Assert.Equal(["refresh", "refresh"], _server.Requests);
        Assert.Equal("refresh-2", _config.RefreshToken);
    }

    /// <summary>A request that never left the machine cannot have rotated anything; the next one goes at once.</summary>
    [Fact]
    public async Task ARefusedConnection_DoesNotHoldTheTokenBack()
    {
        _server.Answers.Enqueue(() => throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made"));

        Assert.True(await _api.TryRestoreSessionAsync());
        Assert.True(await _api.TryRestoreSessionAsync());

        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal("refresh-2", _config.RefreshToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task OnlyTheServerRefusingTheToken_EndsTheSession(HttpStatusCode status)
    {
        _server.Answers.Enqueue(() => new HttpResponseMessage(status));

        Assert.False(await _api.TryRestoreSessionAsync());

        Assert.False(_api.HasSession);
        Assert.Null(_config.RefreshToken);
    }

    /// <summary>
    /// Exiting while a refresh is on the wire: the server has rotated, and the answer is on its way back. Cancelling
    /// the request outright threw that answer away and left the next start to replay the old token; now it is waited
    /// for and saved.
    /// </summary>
    [Fact]
    public async Task AnExitMidRefresh_StillSavesTheAnswer()
    {
        var answer = new TaskCompletionSource();
        _server.InFlight = ct => answer.Task.WaitAsync(ct);
        using var exit = new CancellationTokenSource();
        var window = new ActivityWindow { WindowStart = DateTime.UtcNow, Entries = [] };

        var send = Task.Run(() => _api.SendActivityWindowAsync(window, exit.Token), TestContext.Current.CancellationToken);
        while (_server.Requests.Count == 0) await Task.Delay(10, TestContext.Current.CancellationToken);
        await exit.CancelAsync();
        answer.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Equal("refresh-2", _config.RefreshToken);
    }

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers the refresh route from <see cref="Answers"/>, then with a fresh pair.</summary>
    private sealed class RefreshServer : HttpMessageHandler
    {
        public Queue<Func<HttpResponseMessage>> Answers { get; } = new();

        /// <summary>The refresh token each request carried.</summary>
        public List<string> Requests { get; } = [];

        /// <summary>Awaited once a refresh is received, before it is answered; cancelling aborts it, like a socket.</summary>
        public Func<CancellationToken, Task>? InFlight { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/api/auth/extension/refresh")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(body.Contains("\"refresh\"") ? "refresh" : body);
            if (InFlight is { } inFlight) await inFlight(ct);

            return Answers.TryDequeue(out var answer)
                ? answer()
                : Json("""{ "accessToken": "access", "refreshToken": "refresh-2" }""");
        }
    }

    /// <summary>A monotonic clock that moves only when told; all <see cref="ApiClient"/> reads is the timestamp.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
