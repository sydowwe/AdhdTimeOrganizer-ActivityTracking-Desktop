using System.Net;
using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The browser sign-in's last step on the wire: the code, the verifier and the redirect posted to the exchange route, the
/// token pair stored the way the password login stores it, and a refusal worded for the browser flow rather than as a
/// wrong password.
/// </summary>
public sealed class BrowserCodeExchangeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"browser-exchange-{Guid.NewGuid():N}");
    private readonly FakeServer _server = new();
    private readonly AppConfig _config;
    private readonly ApiClient _api;

    public BrowserCodeExchangeTests()
    {
        _config = new AppConfig { FilePath = Path.Combine(_dir, "config.json"), ApiBaseUrl = "https://server.test" };
        _api = new ApiClient(_config, _server);
    }

    [Fact]
    public async Task PostsCodeVerifierAndRedirect_AndKeepsTheSession()
    {
        var outcome = await _api.ExchangeBrowserCodeAsync("the-code", "the-verifier", "http://127.0.0.1:53111/cb");

        Assert.Equal(LoginStatus.SignedIn, outcome.Status);
        Assert.Equal("""{"code":"the-code","codeVerifier":"the-verifier","redirectUri":"http://127.0.0.1:53111/cb"}""",
            Assert.Single(_server.ExchangeBodies));
        Assert.Equal("refresh-from-browser", _config.RefreshToken);
        Assert.True(File.Exists(_config.FilePath), "saved like a password sign-in");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "expired or was already used")]
    [InlineData(HttpStatusCode.Forbidden, "Sign-in refused")]
    [InlineData(HttpStatusCode.NotFound, "doesn't support browser sign-in")]
    public async Task ARefusal_IsWordedForTheBrowserFlow_AndKeepsNoSession(HttpStatusCode status, string message)
    {
        _server.ExchangeStatus = status;

        var outcome = await _api.ExchangeBrowserCodeAsync("the-code", "the-verifier", "http://127.0.0.1:53111/cb");

        Assert.Equal(LoginStatus.Failed, outcome.Status);
        Assert.Contains(message, outcome.Message);
        Assert.Null(_config.RefreshToken);
    }

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public List<string> ExchangeBodies { get; } = [];
        public HttpStatusCode ExchangeStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/api/auth/tracker/token" || request.Method != HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            ExchangeBodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return ExchangeStatus == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{ "requiresTwoFactor": false, "accessToken": "access", "refreshToken": "refresh-from-browser" }""",
                        Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(ExchangeStatus);
        }
    }
}
