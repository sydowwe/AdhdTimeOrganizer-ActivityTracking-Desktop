using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

public enum SendResult
{
    /// <summary>Stored by the server.</summary>
    Sent,
    /// <summary>Refused for its content; retrying cannot help, so it is dropped.</summary>
    Rejected,
    /// <summary>Offline, server error, conflict or throttled — keep it and try again.</summary>
    RetryLater,
    /// <summary>No usable session; keep it until the user signs in.</summary>
    SignedOut
}

internal enum RefreshOutcome { Refreshed, Rejected, Unavailable }

public enum LoginStatus { SignedIn, TwoFactorRequired, TwoFactorSetupRequired, Failed }

public enum TrackerRulesFetch
{
    /// <summary>A new set, in <see cref="TrackerRulesResult.Rules"/>.</summary>
    Updated,
    /// <summary>304: the set already held is current.</summary>
    Unchanged,
    /// <summary>The server predates the route. It still filters on the heartbeat; the tracker sends unfiltered.</summary>
    NotSupported,
    /// <summary>Offline, signed out, refused or failed — keep what is held and try again.</summary>
    Unavailable
}

public sealed record TrackerRulesResult(TrackerRulesFetch Status, IReadOnlyList<TrackerRule>? Rules = null, string? ETag = null);

/// <param name="Response">What the server resolved the minute to; null unless sent, and from a server that predates the answer.</param>
public sealed record HeartbeatOutcome(SendResult Result, HeartbeatResponse? Response = null);

public enum CoverageFetch
{
    Fetched,
    /// <summary>The server predates the route: no prompt, and no point asking again this session.</summary>
    NotSupported,
    /// <summary>Offline, signed out or failed — try again later.</summary>
    Unavailable
}

public sealed record CoverageResult(CoverageFetch Status, TrackerCoverage? Coverage = null);

/// <summary>An answer the tracker only reads (<see cref="CoverageFetch"/> says how the read went); the value is set only when fetched.</summary>
public sealed record TrackerRead<T>(CoverageFetch Status, T? Value = default) where T : class;

/// <param name="PendingAuthToken">Set with <see cref="LoginStatus.TwoFactorRequired"/>; goes back with the code.</param>
/// <param name="Message">What to tell the user when it did not work.</param>
public sealed record LoginOutcome(LoginStatus Status, string? PendingAuthToken = null, string? Message = null);

/// <summary>
/// Handles API communication with JWT bearer token auth and refresh.
/// </summary>
/// <remarks>
/// The base URL is read from the config on every request, so changing it in Settings takes effect
/// without a restart. The bearer token is set per request rather than on <c>DefaultRequestHeaders</c>,
/// which is not safe to mutate while the send loop has a request in flight.
/// </remarks>
/// <param name="handler">Null for the real network; tests pass one that answers for the server.</param>
/// <param name="time">Null for the system clock; tests pass one they can move.</param>
public sealed class ApiClient(AppConfig config, HttpMessageHandler? handler = null, TimeProvider? time = null) : IDisposable
{
    private const string HeartbeatPath = "/api/tracker/desktop/heartbeat";
    private const string TrackerRulesPath = "/api/tracker/desktop/rules";
    private const string TrackerCoveragePath = "/api/tracker/desktop/coverage";
    private const string TrackerUnmappedPath = "/api/tracker/desktop/unmapped";
    private const string EveningMomentPath = "/api/tracker/evening-shutdown-moment";
    private const string TrackerStartBlockPath = "/api/tracker/desktop/start-block";

    /// <summary>
    /// How long a refresh already sent may still finish after its caller was cancelled — an exit, a session end.
    /// Cancelling it outright throws its answer away, and with it the rotated token the server already committed.
    /// The owner's shutdown wait (<see cref="ActivityTrackerService"/>) has to be longer.
    /// </summary>
    internal static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http = CreateHttpClient(handler);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ILogger _log = Log.ForContext<ApiClient>();
    private volatile string? _accessToken;

    /// <summary>When the last refresh that may have rotated the token without us seeing the answer ended; only under the gate.</summary>
    private long? _uncertainSince;

    /// <summary>
    /// True while a refresh token is saved. A session survives being offline; it ends only when the
    /// server refuses the refresh token, or on sign-out.
    /// </summary>
    public bool HasSession => !string.IsNullOrEmpty(config.RefreshToken);

    /// <summary>
    /// The version goes on <c>DefaultRequestHeaders</c>, unlike the bearer token: it is set once, here, before any request
    /// exists, and never changes — so every request names the build, sign-in and refresh included (<see cref="AppVersion"/>).
    /// </summary>
    private static HttpClient CreateHttpClient(HttpMessageHandler? handler)
    {
        var http = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation(AppVersion.HeaderName, AppVersion.Header);
        return http;
    }

    /// <summary>
    /// Authenticate with email and password.
    /// </summary>
    /// <remarks>
    /// An account with 2FA answers the password step with a 200 that carries no tokens, only a pending
    /// token for the code step. That 200 used to be taken as a successful sign-in with empty tokens: the
    /// form closed, the tray said "tracking", and nothing was ever accepted by the server.
    /// </remarks>
    public async Task<LoginOutcome> LoginAsync(string email, string password)
    {
        // No email in the log: log files outlive an account deletion.
        _log.Information("Attempting login");
        return await PostLoginAsync("/api/auth/tracker/login", new LoginRequest { Email = email, Password = password });
    }

    /// <summary>
    /// Second login step for an account with 2FA: an authenticator or recovery code plus the pending token.
    /// </summary>
    public async Task<LoginOutcome> CompleteTwoFactorAsync(string pendingAuthToken, string code)
    {
        _log.Information("Submitting 2FA code");
        return await PostLoginAsync("/api/auth/tracker/login/2fa",
            new TwoFactorLoginRequest { Token = code, PendingAuthToken = pendingAuthToken });
    }

    /// <summary>
    /// Browser sign-in, last step: the code the loopback listener received, with the PKCE verifier only this process
    /// holds, for the same token pair the password login gives (see <see cref="BrowserSignIn"/>).
    /// </summary>
    public async Task<LoginOutcome> ExchangeBrowserCodeAsync(string code, string codeVerifier, string redirectUri)
    {
        _log.Information("Exchanging browser sign-in code");
        return await PostLoginAsync("/api/auth/tracker/token",
            new BrowserCodeExchangeRequest { Code = code, CodeVerifier = codeVerifier, RedirectUri = redirectUri },
            DescribeBrowserCodeFailure);
    }

    private async Task<LoginOutcome> PostLoginAsync<TRequest>(
        string path, TRequest request, Func<HttpStatusCode, string>? describeFailure = null)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(BuildUri(path), request);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warning("Login step {Path} failed with status {StatusCode}", path, response.StatusCode);
                return new LoginOutcome(LoginStatus.Failed,
                    Message: (describeFailure ?? DescribeLoginFailure)(response.StatusCode));
            }

            var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();

            if (tokens is { RequiresTwoFactor: true, RequiresTwoFactorSetup: true })
                return new LoginOutcome(LoginStatus.TwoFactorSetupRequired,
                    Message: "Set up two-factor authentication in the web app first, then sign in here.");

            if (tokens is { RequiresTwoFactor: true, PendingAuthToken: { } pending })
                return new LoginOutcome(LoginStatus.TwoFactorRequired, pending);

            if (tokens is not { AccessToken.Length: > 0, RefreshToken.Length: > 0 })
                return new LoginOutcome(LoginStatus.Failed, Message: "The server's answer had no session in it.");

            StoreTokens(tokens);
            _log.Information("Login successful");
            return new LoginOutcome(LoginStatus.SignedIn);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Login request failed");
            return new LoginOutcome(LoginStatus.Failed, Message: "Couldn't reach the server.");
        }
    }

    private static string DescribeLoginFailure(HttpStatusCode statusCode) => (int)statusCode switch
    {
        400 or 401 => "Wrong email, password or code.",
        403 => "Sign-in refused. Check your account in the web app.",
        423 or 429 => "Too many attempts. Try again later.",
        _ => $"Sign-in failed ({(int)statusCode})."
    };

    private static string DescribeBrowserCodeFailure(HttpStatusCode statusCode) => (int)statusCode switch
    {
        400 => "The browser sign-in expired or was already used. Try again.",
        403 => "Sign-in refused. Check your account in the web app.",
        // A server from before 2026-10-19 has no exchange route.
        404 => "This server doesn't support browser sign-in yet. Use a password instead.",
        429 => "Too many attempts. Try again later.",
        _ => $"Sign-in failed ({(int)statusCode})."
    };

    /// <summary>
    /// Attempt to restore session from saved refresh token. True unless there is no token or the server
    /// refused it — an unreachable server at boot is not a reason to ask for the password again.
    /// </summary>
    public async Task<bool> TryRestoreSessionAsync()
    {
        if (!HasSession)
        {
            _log.Debug("No saved refresh token — skipping session restore");
            return false;
        }

        _log.Information("Restoring session from saved refresh token");
        return await RefreshTokenAsync(staleAccessToken: null, CancellationToken.None) != RefreshOutcome.Rejected;
    }

    /// <summary>Raised after every sign-out, including the server refusing the refresh token. Any thread.</summary>
    public event Action? SignedOut;

    public void SignOut()
    {
        _accessToken = null;
        config.RefreshToken = null;
        config.Save();
        SignedOut?.Invoke();
    }

    /// <summary>
    /// Send an aggregated activity window to the backend. When stored, the outcome carries what the server resolved
    /// the minute to (<see cref="HeartbeatResponse"/>), if the server is new enough to say.
    /// </summary>
    public async Task<HeartbeatOutcome> SendActivityWindowAsync(ActivityWindow window, CancellationToken ct)
    {
        if (!HasSession) return new HeartbeatOutcome(SendResult.SignedOut);

        try
        {
            var (response, outcome) = await SendAuthorizedAsync(
                () => new HttpRequestMessage(HttpMethod.Post, BuildUri(HeartbeatPath)) { Content = JsonContent.Create(window) }, ct);
            if (response is null) return new HeartbeatOutcome(ToSendResult(outcome));
            using var owned = response;

            var result = Classify(response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (result != SendResult.Sent)
            {
                _log.Warning("Window {WindowStart} answered {StatusCode} ({Result}): {Body}",
                    window.WindowStart, (int)response.StatusCode, result, body);
                return new HeartbeatOutcome(result);
            }
            return new HeartbeatOutcome(result, ReadHeartbeatResponse(body));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Network error sending window {WindowStart} — will retry", window.WindowStart);
            return new HeartbeatOutcome(SendResult.RetryLater);
        }
    }

    /// <summary>
    /// The heartbeat's answer, or null when it has none to give: a server from before 2026-10-18 answers with the bare
    /// stored count, and anything unreadable is treated the same. The window is stored either way; only the tray glance
    /// and the drift nudge go without.
    /// </summary>
    internal static HeartbeatResponse? ReadHeartbeatResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Deserialize<HeartbeatResponse>()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What a heartbeat status means for the window.
    /// </summary>
    /// <remarks>
    /// <para>409 is a retry, not a rejection: the server answers a concurrent duplicate with it and says
    /// so in the body — "Retry it, rows already stored are skipped". It used to be dropped with every
    /// other 4xx.</para>
    /// <para>403 is kept as well. It means the account lacks the tracking permission, which is a setting
    /// the user can fix; dropping every window until they do loses the days in between.</para>
    /// </remarks>
    internal static SendResult Classify(HttpStatusCode statusCode) => (int)statusCode switch
    {
        >= 200 and < 300 => SendResult.Sent,
        401 or 403 or 408 or 409 or 425 or 429 => SendResult.RetryLater,
        >= 400 and < 500 => SendResult.Rejected,
        _ => SendResult.RetryLater
    };

    /// <summary>
    /// Downloads the user's active server rules for the before-upload filter (<see cref="TrackerRules"/>), sending
    /// <paramref name="etag"/> so an unchanged set costs a 304 and no body.
    /// </summary>
    public async Task<TrackerRulesResult> FetchTrackerRulesAsync(string? etag, CancellationToken ct)
    {
        if (!HasSession) return new TrackerRulesResult(TrackerRulesFetch.Unavailable);

        try
        {
            var (response, _) = await SendAuthorizedAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(TrackerRulesPath));
                if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
                    request.Headers.IfNoneMatch.Add(tag);
                return request;
            }, ct);
            if (response is null) return new TrackerRulesResult(TrackerRulesFetch.Unavailable);
            using var owned = response;

            var status = ClassifyTrackerRules(response.StatusCode);
            if (status == TrackerRulesFetch.Unavailable)
                _log.Warning("Tracker rules answered {StatusCode} — keeping the rules already held", (int)response.StatusCode);
            if (status != TrackerRulesFetch.Updated)
                return new TrackerRulesResult(status);

            var rules = await response.Content.ReadFromJsonAsync<List<TrackerRule>>(ct) ?? [];
            return new TrackerRulesResult(TrackerRulesFetch.Updated, rules, response.Headers.ETag?.ToString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Tracker rules download failed — will retry");
            return new TrackerRulesResult(TrackerRulesFetch.Unavailable);
        }
    }

    /// <remarks>
    /// A 404 is a server from before the route existed: it has rules but cannot hand them out, and it still
    /// applies them on the heartbeat, so sending goes on unfiltered rather than waiting for a route that is not
    /// coming. Everything else that is not a 200 or a 304 is "try again", like a heartbeat.
    /// </remarks>
    internal static TrackerRulesFetch ClassifyTrackerRules(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.OK => TrackerRulesFetch.Updated,
        HttpStatusCode.NotModified => TrackerRulesFetch.Unchanged,
        HttpStatusCode.NotFound => TrackerRulesFetch.NotSupported,
        _ => TrackerRulesFetch.Unavailable
    };

    /// <summary>
    /// How much of the last week the user's server rules map, and the busiest unmapped apps — what the "map these apps
    /// to enable drift nudges" prompt names. A 404 is a server that predates the route.
    /// </summary>
    public async Task<CoverageResult> FetchCoverageAsync(CancellationToken ct)
    {
        var (status, coverage) = await GetTrackerJsonAsync<TrackerCoverage>(TrackerCoveragePath, "Mapping coverage", ct);
        return new CoverageResult(status, coverage);
    }

    /// <summary>
    /// A day's unmapped time on this PC, the evening invite's figures. A 404 is a server that predates the route; an
    /// empty <see cref="UnmappedToday.Apps"/> is the server's "don't ask".
    /// </summary>
    /// <param name="day">The evening's own day (yesterday's date for an evening past midnight); null is the server's today.</param>
    public async Task<TrackerRead<UnmappedToday>> FetchUnmappedTodayAsync(DateOnly? day, CancellationToken ct)
    {
        var path = day is { } d
            ? $"{TrackerUnmappedPath}?date={d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}"
            : TrackerUnmappedPath;
        var (status, value) = await GetTrackerJsonAsync<UnmappedToday>(path, "Unmapped time", ct);
        return new TrackerRead<UnmappedToday>(status, value);
    }

    /// <summary>The instant of the user's next evening — the server's, shared with the phone's review. A 404 is an older server.</summary>
    public async Task<TrackerRead<EveningMoment>> FetchEveningMomentAsync(CancellationToken ct)
    {
        var (status, value) = await GetTrackerJsonAsync<EveningMoment>(EveningMomentPath, "Evening moment", ct);
        return new TrackerRead<EveningMoment>(status, value);
    }

    /// <summary>
    /// The start ramp's Start now: the planned block goes in progress on the server. True when it is in progress
    /// afterwards. Never throws but for the caller's cancellation, and nothing waits on it — the web app's timer opens
    /// either way, and the web app marks the block started too. A 409 is a block no longer open (done, skipped, moved off
    /// today); a 404 is a server from before the route.
    /// </summary>
    public async Task<bool> StartBlockAsync(long plannerTaskId, CancellationToken ct)
    {
        if (!HasSession) return false;

        try
        {
            var (response, _) = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Post, BuildUri(TrackerStartBlockPath))
            {
                Content = JsonContent.Create(new { plannerTaskId })
            }, ct);
            if (response is null) return false;
            using var owned = response;

            if (response.IsSuccessStatusCode) return true;
            _log.Information("Start block {PlannerTaskId} answered {StatusCode}", plannerTaskId, (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Start block {PlannerTaskId} failed", plannerTaskId);
            return false;
        }
    }

    /// <summary>
    /// One authorized GET of a server answer the tracker only reads. Never throws but for the caller's cancellation: a
    /// failure is <see cref="CoverageFetch.Unavailable"/>, "try again later", and a 404 is
    /// <see cref="CoverageFetch.NotSupported"/>, a server from before the route. <paramref name="what"/> is for the log.
    /// </summary>
    private async Task<(CoverageFetch Status, T? Value)> GetTrackerJsonAsync<T>(string path, string what, CancellationToken ct)
    {
        if (!HasSession) return (CoverageFetch.Unavailable, default);

        try
        {
            var (response, _) = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, BuildUri(path)), ct);
            if (response is null) return (CoverageFetch.Unavailable, default);
            using var owned = response;

            if (response.StatusCode == HttpStatusCode.NotFound) return (CoverageFetch.NotSupported, default);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _log.Warning("{What} answered {StatusCode}", what, (int)response.StatusCode);
                return (CoverageFetch.Unavailable, default);
            }

            return (CoverageFetch.Fetched, await response.Content.ReadFromJsonAsync<T>(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "{What} download failed — will retry", what);
            return (CoverageFetch.Unavailable, default);
        }
    }

    /// <summary>
    /// Sends a request with the session's access token, refreshing it first when there is none and once more on a
    /// 401. The response is null when no usable token could be had; the outcome then says why.
    /// </summary>
    /// <remarks><paramref name="createRequest"/> is called once per attempt: a request message cannot be sent twice.</remarks>
    private async Task<(HttpResponseMessage? Response, RefreshOutcome Outcome)> SendAuthorizedAsync(
        Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        var token = _accessToken;
        if (token is null)
        {
            var outcome = await RefreshTokenAsync(null, ct);
            if (outcome != RefreshOutcome.Refreshed) return (null, outcome);
            token = _accessToken;
        }

        var response = await SendWithTokenAsync(createRequest, token, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _log.Information("Got 401 from {Path} — refreshing the access token", response.RequestMessage?.RequestUri?.AbsolutePath);
            response.Dispose();

            var outcome = await RefreshTokenAsync(token, ct);
            if (outcome != RefreshOutcome.Refreshed) return (null, outcome);

            response = await SendWithTokenAsync(createRequest, _accessToken, ct);
        }

        return (response, RefreshOutcome.Refreshed);
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(Func<HttpRequestMessage> createRequest, string? token, CancellationToken ct)
    {
        using var request = createRequest();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request, ct);
    }

    /// <summary>
    /// Exchanges the refresh token for a new pair.
    /// </summary>
    /// <remarks>
    /// <para>Only a 400/401/403 ends the session. It used to be cleared on <em>any</em> failure, so a 502
    /// during a deploy signed the user out for good — silently, while the tray kept saying "tracking".</para>
    /// <para>Serialized, and skipped when another caller already replaced <paramref name="staleAccessToken"/>:
    /// the server rotates refresh tokens, so two concurrent refreshes would race on the same one.</para>
    /// <para>What an outcome means is <see cref="RefreshPolicy"/>'s call: a lost answer never ends the session,
    /// and a token that may have been rotated without us seeing the answer is held back for
    /// <see cref="RefreshPolicy.ReplayDelay"/> — while held, a refresh reports <see cref="RefreshOutcome.Unavailable"/>
    /// without sending.</para>
    /// <para>Once sent, a refresh outlives its caller's cancellation by <see cref="ExitGrace"/>, so an exit
    /// mid-refresh keeps the rotated pair instead of leaving the next start to replay the old token.</para>
    /// </remarks>
    private async Task<RefreshOutcome> RefreshTokenAsync(string? staleAccessToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        var sent = false;
        try
        {
            if (_accessToken is not null && _accessToken != staleAccessToken)
                return RefreshOutcome.Refreshed;

            var refreshToken = config.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken)) return RefreshOutcome.Rejected;

            var sinceUncertain = _uncertainSince is { } since ? _time.GetElapsedTime(since) : (TimeSpan?)null;
            if (RefreshPolicy.WaitBeforeSending(sinceUncertain) > TimeSpan.Zero)
            {
                _log.Debug("Holding the refresh back — the last one may have rotated the token");
                return RefreshOutcome.Unavailable;
            }

            using var send = new CancellationTokenSource();
            await using var exit = ct.Register(() => send.CancelAfter(ExitGrace));

            _log.Debug("Refreshing access token");
            sent = true;
            using var response = await _http.PostAsJsonAsync(
                BuildUri("/api/auth/tracker/refresh"), new { refreshToken }, send.Token);

            var tokens = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<TokenResponse>(send.Token) : null;
            var attempt = new RefreshAttempt.Answered(response.StatusCode, tokens is { AccessToken.Length: > 0, RefreshToken.Length: > 0 });
            _uncertainSince = RefreshPolicy.LeftTheTokenUncertain(attempt) ? _time.GetTimestamp() : null;

            switch (RefreshPolicy.Decide(attempt))
            {
                case RefreshDecision.Save:
                    StoreTokens(tokens!);
                    _log.Information("Token refreshed successfully");
                    return RefreshOutcome.Refreshed;
                case RefreshDecision.SignOut:
                    _log.Warning("Refresh token refused ({StatusCode}) — session ended", response.StatusCode);
                    SignOut();
                    return RefreshOutcome.Rejected;
                default:
                    _log.Warning("Token refresh answered {StatusCode} — keeping the session, will retry", response.StatusCode);
                    return RefreshOutcome.Unavailable;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (sent) _uncertainSince = _time.GetTimestamp();
            throw;
        }
        catch (Exception ex)
        {
            if (sent && RefreshPolicy.LeftTheTokenUncertain(RefreshAttempt.FromFailure(ex))) _uncertainSince = _time.GetTimestamp();
            _log.Warning(ex, "Token refresh failed — keeping the session, will retry");
            return RefreshOutcome.Unavailable;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void StoreTokens(TokenResponse tokens)
    {
        _accessToken = tokens.AccessToken;
        config.RefreshToken = tokens.RefreshToken;
        config.Save();
    }

    private Uri BuildUri(string path) => new(new Uri(config.ApiBaseUrl), path);

    private static SendResult ToSendResult(RefreshOutcome outcome) =>
        outcome == RefreshOutcome.Rejected ? SendResult.SignedOut : SendResult.RetryLater;

    public void Dispose()
    {
        _http.Dispose();
        _refreshGate.Dispose();
    }
}
