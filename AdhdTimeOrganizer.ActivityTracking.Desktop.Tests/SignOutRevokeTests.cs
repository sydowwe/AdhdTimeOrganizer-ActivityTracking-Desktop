using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// Signing out revokes the refresh token on the server (review M5), best-effort: the local sign-out happens at once and
/// stands whatever the server says; the revoke goes to the server that issued the session, refreshes once when the held
/// access token is gone or expired, and is never sent when the server itself refused the token.
/// </summary>
public sealed class SignOutRevokeTests : IDisposable
{
    private const string OldServer = "https://old.test";
    private const string NewServer = "https://new.test";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sign-out-{Guid.NewGuid():N}");
    private readonly FakeServer _server = new();
    private readonly AppConfig _config;
    private readonly ApiClient _api;

    public SignOutRevokeTests()
    {
        _config = new AppConfig
        {
            FilePath = Path.Combine(_dir, "config.json"),
            ApiBaseUrl = OldServer,
            RefreshToken = "refresh-0"
        };
        _api = new ApiClient(_config, _server);
    }

    [Fact]
    public async Task WithALiveAccessToken_TheRefreshTokenIsRevokedWithIt()
    {
        await SignInAsync();

        await _api.SignOut();

        var logout = Assert.Single(_server.Requests, r => r.Path == "/api/auth/tracker/logout");
        Assert.Equal("Bearer access-1", logout.Authorization);
        Assert.Contains("\"refresh-1\"", logout.Body);
        Assert.Equal(new Uri(OldServer).Host, logout.Host);
        Assert.False(_api.HasSession);
        Assert.Null(_config.RefreshToken);
    }

    /// <summary>A changed server URL in Settings: the session is the old server's, so the revoke goes there.</summary>
    [Fact]
    public async Task AfterTheServerUrlChanged_TheRevokeGoesToTheOldServer()
    {
        await SignInAsync();
        _config.ApiBaseUrl = NewServer;

        await _api.SignOut(OldServer);

        var logout = Assert.Single(_server.Requests, r => r.Path == "/api/auth/tracker/logout");
        Assert.Equal(new Uri(OldServer).Host, logout.Host);
        Assert.DoesNotContain(_server.Requests, r => r.Host == new Uri(NewServer).Host);
    }

    /// <summary>An access token held through a long pause has expired: one refresh, the revoke again, nothing more.</summary>
    [Fact]
    public async Task AnExpiredAccessToken_IsRefreshedOnce_AndTheNewPairRevoked_WithoutBeingSaved()
    {
        await SignInAsync();
        _server.Logout.Enqueue(HttpStatusCode.Unauthorized);

        await _api.SignOut();

        Assert.Equal(["/api/auth/tracker/logout", "/api/auth/tracker/refresh", "/api/auth/tracker/logout"],
            _server.Requests.Skip(1).Select(r => r.Path));
        var last = _server.Requests[^1];
        Assert.Equal("Bearer access-2", last.Authorization);
        Assert.Contains("\"refresh-2\"", last.Body);
        Assert.Null(_config.RefreshToken);
        Assert.False(_api.HasSession);
    }

    /// <summary>After a restart nothing has fetched an access token yet: refresh first, then revoke.</summary>
    [Fact]
    public async Task WithNoAccessTokenYet_TheRevokeRefreshesFirst()
    {
        await _api.SignOut();

        Assert.Equal(["/api/auth/tracker/refresh", "/api/auth/tracker/logout"], _server.Requests.Select(r => r.Path));
        Assert.Contains("\"refresh-0\"", _server.Requests[0].Body);
        Assert.Null(_config.RefreshToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]          // a server from before the route
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AnyOtherAnswer_IsNotRetried(HttpStatusCode answer)
    {
        await SignInAsync();
        _server.Logout.Enqueue(answer);

        await _api.SignOut();

        Assert.Single(_server.Requests, r => r.Path == "/api/auth/tracker/logout");
        Assert.Single(_server.Requests, r => r.Path == "/api/auth/tracker/refresh"); // the sign-in's
        Assert.False(_api.HasSession);
    }

    /// <summary>A server that refused the refresh token has nothing left to revoke; it is not asked.</summary>
    [Fact]
    public async Task WhenTheServerRefusedTheToken_NoRevokeIsSent()
    {
        _server.Refresh.Enqueue(HttpStatusCode.Unauthorized);

        Assert.False(await _api.TryRestoreSessionAsync());
        await Task.Delay(50, TestContext.Current.CancellationToken); // a background revoke would have started by now

        Assert.DoesNotContain(_server.Requests, r => r.Path == "/api/auth/tracker/logout");
        Assert.False(_api.HasSession);
    }

    /// <summary>The UI never waits for the server: signed out locally before the revoke has an answer.</summary>
    [Fact]
    public async Task TheLocalSignOut_DoesNotWaitForTheServer()
    {
        await SignInAsync();
        var answer = new TaskCompletionSource();
        _server.Hold = answer.Task;
        var signedOut = 0;
        _api.SignedOut += () => signedOut++;

        var revoke = _api.SignOut();

        Assert.False(_api.HasSession);
        Assert.Equal(1, signedOut);
        Assert.False(revoke.IsCompleted);

        answer.SetResult();
        await revoke;
        Assert.Single(_server.Requests, r => r.Path == "/api/auth/tracker/logout");
    }

    [Fact]
    public async Task WithoutASession_NothingIsSent()
    {
        _config.RefreshToken = null;

        await _api.SignOut();

        Assert.Empty(_server.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, "Revoked")]
    [InlineData(HttpStatusCode.OK, "Revoked")]
    [InlineData(HttpStatusCode.Unauthorized, "NeedsFreshToken")]
    [InlineData(HttpStatusCode.NotFound, "NotSupported")]
    [InlineData(HttpStatusCode.Forbidden, "Failed")]
    [InlineData(HttpStatusCode.TooManyRequests, "Failed")]
    [InlineData(HttpStatusCode.BadGateway, "Failed")]
    public void ClassifyLogout(HttpStatusCode status, string expected) =>
        Assert.Equal(expected, ApiClient.ClassifyLogout(status).ToString());

    private async Task SignInAsync() => Assert.True(await _api.TryRestoreSessionAsync());

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed record Seen(string Host, string Path, string? Authorization, string Body);

    /// <summary>
    /// Refresh answers from <see cref="Refresh"/>, then a fresh pair (<c>access-n</c> / <c>refresh-n</c>); logout answers from
    /// <see cref="Logout"/>, then 204.
    /// </summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        private int _pairs;

        public ConcurrentQueue<HttpStatusCode> Refresh { get; } = new();
        public ConcurrentQueue<HttpStatusCode> Logout { get; } = new();
        public List<Seen> Requests { get; } = [];

        /// <summary>Awaited before a logout is answered.</summary>
        public Task? Hold { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add(new Seen(request.RequestUri.Host, path, request.Headers.Authorization?.ToString(), body));

            switch (path)
            {
                case "/api/auth/tracker/refresh":
                    if (Refresh.TryDequeue(out var refused)) return new HttpResponseMessage(refused);
                    var n = Interlocked.Increment(ref _pairs);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($$"""{ "accessToken": "access-{{n}}", "refreshToken": "refresh-{{n}}" }""",
                            Encoding.UTF8, "application/json")
                    };
                case "/api/auth/tracker/logout":
                    if (Hold is { } hold) await hold.WaitAsync(ct);
                    return new HttpResponseMessage(Logout.TryDequeue(out var answer) ? answer : HttpStatusCode.NoContent);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }
}
