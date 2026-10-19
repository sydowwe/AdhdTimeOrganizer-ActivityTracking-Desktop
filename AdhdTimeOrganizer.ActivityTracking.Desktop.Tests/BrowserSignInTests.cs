using System.Text.RegularExpressions;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The pure half of the browser sign-in: PKCE, the state, the page the browser opens, and which requests reaching the
/// listener are our sign-in. The formats are the server's (<c>TrackerSignInRules</c>): a pair or a redirect it refuses
/// fails the sign-in only after the user has already confirmed in the browser.
/// </summary>
public sealed class BrowserSignInTests
{
    private const string State = "our-state-0123456789abcdef";

    [Fact]
    public void Challenge_IsS256_AsRfc7636Defines()
    {
        // RFC 7636, Appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            BrowserSignIn.ChallengeOf("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void NewPkce_IsFreshEachTime_InTheFormatsTheServerAccepts()
    {
        var first = BrowserSignIn.NewPkce();
        var second = BrowserSignIn.NewPkce();

        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.Matches("^[A-Za-z0-9._~-]{43,128}$", first.Verifier);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", first.Challenge);
        Assert.Equal(BrowserSignIn.ChallengeOf(first.Verifier), first.Challenge);
    }

    [Fact]
    public void NewState_IsFresh_AndInsideTheServersBound()
    {
        var state = BrowserSignIn.NewState();

        Assert.NotEqual(state, BrowserSignIn.NewState());
        Assert.Matches("^[A-Za-z0-9_-]{16,128}$", state);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(53111)]
    [InlineData(65535)]
    public void RedirectUri_IsTheOneShapeTheServerAccepts(int port)
    {
        Assert.Matches(@"^http://127\.0\.0\.1:[0-9]{4,5}/cb$", BrowserSignIn.RedirectUri(port));
    }

    [Theory]
    [InlineData("https://app.test")]
    [InlineData("https://app.test/")]
    public void ConnectPage_CarriesTheRedirectChallengeAndState_Escaped(string webAppUrl)
    {
        var config = new AppConfig { FilePath = Path.Combine(Path.GetTempPath(), $"unused-{Guid.NewGuid():N}.json"), WebAppUrl = webAppUrl };

        var page = BrowserSignIn.ConnectPage(config, "http://127.0.0.1:53111/cb", "challenge-value", State);

        Assert.Equal("https://app.test/connect/tracker", page.GetLeftPart(UriPartial.Path));
        Assert.Equal("?redirect_uri=http%3A%2F%2F127.0.0.1%3A53111%2Fcb&code_challenge=challenge-value&state=" + State, page.Query);
    }

    [Fact]
    public void ReadRedirect_OurCallback_GivesTheCode()
    {
        var verdict = BrowserSignIn.ReadRedirect($"GET /cb?code=abc_DEF-123&state={State} HTTP/1.1", State);

        Assert.Equal(new RedirectVerdict(RedirectKind.Code, "abc_DEF-123"), verdict);
    }

    [Fact]
    public void ReadRedirect_DecodesTheCode()
    {
        var verdict = BrowserSignIn.ReadRedirect($"GET /cb?state={State}&code=a%2Bb%3D HTTP/1.1", State);

        Assert.Equal("a+b=", verdict.Code);
    }

    [Theory]
    [InlineData("GET /cb?code=abc&state=someone-elses-state-0123 HTTP/1.1")]
    [InlineData("GET /cb?code=abc HTTP/1.1")]
    [InlineData("GET /cb?code=abc&state= HTTP/1.1")]
    public void ReadRedirect_WithoutOurState_IsRefused(string requestLine)
    {
        Assert.Equal(RedirectKind.WrongState, BrowserSignIn.ReadRedirect(requestLine, State).Kind);
    }

    [Theory]
    [InlineData("GET /favicon.ico HTTP/1.1")]
    [InlineData("GET / HTTP/1.1")]
    [InlineData("GET /cb/x?code=abc&state=" + State + " HTTP/1.1")]
    [InlineData("POST /cb?code=abc&state=" + State + " HTTP/1.1")]
    public void ReadRedirect_AnythingButTheCallback_IsNotOurs(string requestLine)
    {
        Assert.Equal(RedirectKind.NotOurs, BrowserSignIn.ReadRedirect(requestLine, State).Kind);
    }

    [Theory]
    [InlineData("GET /cb?state=" + State + " HTTP/1.1")]
    [InlineData("GET /cb?code=&state=" + State + " HTTP/1.1")]
    [InlineData("GET /cb?code=one&code=two&state=" + State + " HTTP/1.1")]
    [InlineData("GET /cb?code=abc&state=" + State + "&state=" + State + " HTTP/1.1")]
    [InlineData("GET /cb?code=abc&state=" + State)]
    [InlineData("not a request line")]
    [InlineData("")]
    public void ReadRedirect_WithoutExactlyOneCode_IsMalformed(string requestLine)
    {
        var verdict = BrowserSignIn.ReadRedirect(requestLine, State);

        Assert.NotEqual(RedirectKind.Code, verdict.Kind);
        Assert.Null(verdict.Code);
    }

    [Fact]
    public void ServerRedirectShape_MatchesTheServerRule()
    {
        // The server's TrackerSignInRules pattern, copied: a change on either side must change both.
        var serverRule = new Regex(@"^http://127\.0\.0\.1:([0-9]{4,5})/cb$");

        Assert.Matches(serverRule, BrowserSignIn.RedirectUri(49152));
        Assert.Equal("/cb", BrowserSignIn.CallbackPath);
    }
}
