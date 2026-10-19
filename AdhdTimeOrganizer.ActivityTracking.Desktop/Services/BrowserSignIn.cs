using System.Security.Cryptography;
using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>Two halves of PKCE (RFC 7636, S256): the verifier stays here, the challenge goes to the browser.</summary>
public sealed record PkcePair(string Verifier, string Challenge);

public enum RedirectKind
{
    /// <summary>The sign-in: a code, under our state.</summary>
    Code,
    /// <summary>Not the callback at all — a favicon, another path, another method. Answered 404 and ignored.</summary>
    NotOurs,
    /// <summary>The callback, but not our sign-in: missing or different state. A forged or stale redirect.</summary>
    WrongState,
    /// <summary>The callback with our state, but no single code in it.</summary>
    Malformed
}

/// <param name="Code">Set only with <see cref="RedirectKind.Code"/>.</param>
public sealed record RedirectVerdict(RedirectKind Kind, string? Code = null);

/// <summary>
/// <b>Pure</b> decisions of the browser sign-in (the loopback flow for native apps, RFC 8252): the PKCE pair and state,
/// the page the browser opens, and whether a request that reached the listener is our sign-in.
/// </summary>
/// <remarks>
/// <para>
/// The tracker listens on <c>127.0.0.1</c> on a free port, opens the web app's <c>/connect/tracker</c> page with that
/// redirect, the PKCE challenge and a state, and waits. The user confirms in the browser, which goes to
/// <c>http://127.0.0.1:&lt;port&gt;/cb?code=…&amp;state=…</c>; the tracker exchanges the code and the verifier for the
/// usual token pair. The server accepts exactly that redirect shape, any port from 1024 up
/// (<c>TrackerSignInRules</c> on the server) — change both together.
/// </para>
/// <para>
/// The state is what tells our sign-in from a redirect something else sent to the listener; the verifier is what makes an
/// intercepted code useless to anyone but this process.
/// </para>
/// </remarks>
public static class BrowserSignIn
{
    public const string CallbackPath = "/cb";

    /// <summary>The web page the tracker opens. Its path is the web app's route (<c>connectTracker</c>).</summary>
    public const string ConnectPagePath = "/connect/tracker";

    /// <summary>
    /// How long the tracker waits for the browser. Long enough to sign in to the web app first — Google, a 2FA code —
    /// short enough that a forgotten sign-in does not leave a port open for the afternoon.
    /// </summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(5);

    public static PkcePair NewPkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new PkcePair(verifier, ChallengeOf(verifier));
    }

    public static string ChallengeOf(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>24 random bytes, base64url: inside the server's 16–128 character bound, and nothing to escape.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    public static string RedirectUri(int port) => $"http://127.0.0.1:{port}{CallbackPath}";

    public static Uri ConnectPage(AppConfig config, string redirectUri, string codeChallenge, string state) =>
        config.WebAppUri($"{ConnectPagePath}?redirect_uri={Uri.EscapeDataString(redirectUri)}"
                         + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}&state={Uri.EscapeDataString(state)}");

    /// <summary>
    /// Reads the request line the browser sent (<c>GET /cb?code=…&amp;state=… HTTP/1.1</c>) against the state this
    /// sign-in made.
    /// </summary>
    public static RedirectVerdict ReadRedirect(string requestLine, string expectedState)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/", StringComparison.Ordinal))
            return new RedirectVerdict(RedirectKind.Malformed);

        if (parts[0] != "GET")
            return new RedirectVerdict(RedirectKind.NotOurs);

        var target = parts[1];
        var queryStart = target.IndexOf('?');
        var path = queryStart < 0 ? target : target[..queryStart];
        if (path != CallbackPath)
            return new RedirectVerdict(RedirectKind.NotOurs);

        var query = ParseQuery(queryStart < 0 ? "" : target[(queryStart + 1)..]);
        if (query is null)
            return new RedirectVerdict(RedirectKind.Malformed);

        if (!query.TryGetValue("state", out var state) || !FixedTimeEquals(state, expectedState))
            return new RedirectVerdict(RedirectKind.WrongState);

        return query.TryGetValue("code", out var code) && code.Length > 0
            ? new RedirectVerdict(RedirectKind.Code, code)
            : new RedirectVerdict(RedirectKind.Malformed);
    }

    /// <summary>Null when a parameter repeats: a second <c>code</c> must not get to choose which one wins.</summary>
    private static Dictionary<string, string>? ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));

            if (!values.TryAdd(name, value))
                return null;
        }
        return values;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
