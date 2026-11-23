using System.Net;
using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// Review L13: a password login or a code exchange that succeeds after its form was closed saved the session, but the form
/// had already answered "cancelled" and tracking never started. The tray now starts on <see cref="ApiClient.SignedIn"/>,
/// which every sign-in that saves a session raises — and a refresh does not.
/// </summary>
public sealed class SignedInEventTests : IDisposable
{
    private const string Pair = """{ "accessToken": "access", "refreshToken": "refresh-new" }""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"signed-in-{Guid.NewGuid():N}");
    private readonly AppConfig _config;
    private readonly Server _server = new();
    private readonly ApiClient _api;
    private int _signedIn;

    public SignedInEventTests()
    {
        _config = new AppConfig { FilePath = Path.Combine(_dir, "config.json"), ApiBaseUrl = "https://server.test" };
        _api = new ApiClient(_config, _server);
        _api.SignedIn += () => _signedIn++;
    }

    [Fact]
    public async Task APasswordLogin_RaisesIt()
    {
        _server.Answer = () => Json(Pair);

        Assert.Equal(LoginStatus.SignedIn, (await _api.LoginAsync("someone@example.test", "secret")).Status);

        Assert.Equal(1, _signedIn);
    }

    [Fact]
    public async Task ABrowserCodeExchange_RaisesIt()
    {
        _server.Answer = () => Json(Pair);

        await _api.ExchangeBrowserCodeAsync("code", "verifier", "http://127.0.0.1:50000/cb");

        Assert.Equal(1, _signedIn);
    }

    [Fact]
    public async Task TheFirstStepOfATwoFactorLogin_DoesNot()
    {
        _server.Answer = () => Json("""{ "requiresTwoFactor": true, "pendingAuthToken": "pending" }""");

        Assert.Equal(LoginStatus.TwoFactorRequired, (await _api.LoginAsync("someone@example.test", "secret")).Status);

        Assert.Equal(0, _signedIn);
    }

    [Fact]
    public async Task AFailedLogin_DoesNot()
    {
        _server.Answer = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        await _api.LoginAsync("someone@example.test", "wrong");

        Assert.Equal(0, _signedIn);
    }

    [Fact]
    public async Task ARefresh_DoesNot()
    {
        _config.RefreshToken = "refresh-old";
        _server.Answer = () => Json(Pair);

        Assert.True(await _api.TryRestoreSessionAsync());

        Assert.Equal(0, _signedIn);
    }

    public void Dispose()
    {
        _api.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Server : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Answer { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Answer());
    }
}
