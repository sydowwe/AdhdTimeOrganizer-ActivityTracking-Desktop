using System.Net;
using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// Every request names the build — sign-in, refresh and the authorized calls alike — because the server stores the version
/// off whichever ingest request comes, and "out of date" is only as good as that.
/// </summary>
public sealed class ClientVersionHeaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"client-version-{Guid.NewGuid():N}");
    private readonly RecordingServer _server = new();
    private readonly AppConfig _config;
    private readonly ApiClient _api;

    public ClientVersionHeaderTests()
    {
        _config = new AppConfig { FilePath = Path.Combine(_dir, "config.json"), ApiBaseUrl = "https://server.test", RefreshToken = "refresh" };
        _api = new ApiClient(_config, _server);
    }

    [Fact]
    public async Task EveryRequest_CarriesTheVersion()
    {
        await _api.LoginAsync("someone@example.test", "password");
        await _api.StartBlockAsync(7, TestContext.Current.CancellationToken); // a refresh, then the authorized POST

        Assert.Equal(["/api/auth/extension/login", "/api/auth/extension/refresh", "/api/activity-tracking/desktop/agent-start-block"],
            _server.Requests.Select(r => r.Path));
        Assert.All(_server.Requests, r => Assert.Equal($"desktop/{AppVersion.Current}", r.Version));
    }

    [Fact]
    public void TheHeader_IsTheKindAndTheVersion() =>
        Assert.Equal("desktop/1.1.0", AppVersion.HeaderValue("1.1.0"));

    [Theory]
    [InlineData("1.1.0+3f2a9c1d0e", "1.1.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.3.0-beta.1+abc", "1.3.0-beta.1")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void TheVersion_LeavesTheCommitOut(string? informational, string expected) =>
        Assert.Equal(expected, AppVersion.FromInformational(informational));

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class RecordingServer : HttpMessageHandler
    {
        public List<(string Path, string? Version)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((path, request.Headers.TryGetValues(AppVersion.HeaderName, out var values) ? string.Join(",", values) : null));

            return Task.FromResult(path == "/api/auth/extension/refresh"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "accessToken": "access", "refreshToken": "refresh-2" }""", Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
