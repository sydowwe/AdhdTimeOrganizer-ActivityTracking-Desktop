using System.Net;
using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The refresh against a fake server. The server rotates on every refresh; a refresh answer lost after the rotation
/// committed leaves the agent holding the old token. The server exchanges that token again once the rotation is
/// more than 5 s old, and answers 401 inside those 5 s — so the agent must hold it back until then, and never end
/// the session over it.
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

    public static TheoryData<string> LostAnswers => ["500", "timeout", "dropped connection", "200 without tokens"];

    [Theory]
    [MemberData(nameof(LostAnswers))]
    public async Task ALostAnswer_HoldsTheTokenBackPastTheGrace_ThenReplaysIt(string lostAnswer)
    {
        _server.Answers.Enqueue(lostAnswer switch
        {
            "500" => () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "timeout" => () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
            "dropped connection" => () => throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely"),
            _ => () => Json("""{ "accessToken": "", "refreshToken": "" }""")
        });

        Assert.True(await _api.TryRestoreSessionAsync(), "a lost answer keeps the session");

        _clock.Advance(ApiClient.ReplayDelay - TimeSpan.FromMilliseconds(1));
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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/api/auth/extension/refresh")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(body.Contains("\"refresh\"") ? "refresh" : body);

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
