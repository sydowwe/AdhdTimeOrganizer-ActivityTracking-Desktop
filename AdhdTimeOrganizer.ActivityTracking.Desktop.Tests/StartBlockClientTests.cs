using System.Net;
using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The start ramp's Start now on the wire: one authorized POST naming the block, and an answer that is only ever true or
/// false — a 409 (the block is no longer open), a 404 (a server from before the route) or no network must not throw into
/// the button press that sent it.
/// </summary>
public sealed class StartBlockClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"start-block-{Guid.NewGuid():N}");
    private readonly FakeServer _server = new();
    private readonly AppConfig _config;
    private readonly ApiClient _api;

    public StartBlockClientTests()
    {
        _config = new AppConfig { FilePath = Path.Combine(_dir, "config.json"), ApiBaseUrl = "https://server.test", RefreshToken = "refresh" };
        _api = new ApiClient(_config, _server);
    }

    [Fact]
    public async Task PostsTheBlock_WithTheSessionsToken()
    {
        Assert.True(await _api.StartBlockAsync(7, TestContext.Current.CancellationToken));

        var request = Assert.Single(_server.StartRequests);
        Assert.Equal("""{"plannerTaskId":7}""", request.Body);
        Assert.Equal("Bearer access", request.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnythingButSuccess_IsFalse(HttpStatusCode status)
    {
        _server.StartStatus = status;

        Assert.False(await _api.StartBlockAsync(7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoNetwork_IsFalse_NotAnException()
    {
        _server.Offline = true;

        Assert.False(await _api.StartBlockAsync(7, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SignedOut_SendsNothing()
    {
        _config.RefreshToken = null;

        Assert.False(await _api.StartBlockAsync(7, TestContext.Current.CancellationToken));
        Assert.Empty(_server.StartRequests);
    }

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public List<(string Body, string? Authorization)> StartRequests { get; } = [];
        public HttpStatusCode StartStatus { get; set; } = HttpStatusCode.NoContent;
        public bool Offline { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("No such host is known.");

            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/extension/refresh":
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{ "accessToken": "access", "refreshToken": "refresh-2" }""", Encoding.UTF8, "application/json")
                    };

                case "/api/activity-tracking/desktop/agent-start-block" when request.Method == HttpMethod.Post:
                    StartRequests.Add((await request.Content!.ReadAsStringAsync(ct), request.Headers.Authorization?.ToString()));
                    return new HttpResponseMessage(StartStatus);

                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }
    }
}
