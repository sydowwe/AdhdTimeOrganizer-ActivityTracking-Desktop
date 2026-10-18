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

public enum AgentRulesFetch
{
    /// <summary>A new set, in <see cref="AgentRulesResult.Rules"/>.</summary>
    Updated,
    /// <summary>304: the set already held is current.</summary>
    Unchanged,
    /// <summary>The server predates the route. It still filters on the heartbeat; the agent sends unfiltered.</summary>
    NotSupported,
    /// <summary>Offline, signed out, refused or failed — keep what is held and try again.</summary>
    Unavailable
}

public sealed record AgentRulesResult(AgentRulesFetch Status, IReadOnlyList<AgentRule>? Rules = null, string? ETag = null);

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

public sealed record CoverageResult(CoverageFetch Status, AgentCoverage? Coverage = null);

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
    private const string HeartbeatPath = "/api/activity-tracking/desktop/heartbeat";
    private const string AgentRulesPath = "/api/activity-tracking/desktop/agent-rules";
    private const string AgentCoveragePath = "/api/activity-tracking/desktop/agent-coverage";

    /// <summary>
    /// How long a refresh token whose answer may have been lost is held back. The server exchanges such a
    /// replay again only once the rotation is more than 5 s old (its lost-response recovery); inside those 5 s
    /// it answers 401, which ends the session. One more second for a request that arrives late.
    /// </summary>
    internal static readonly TimeSpan ReplayDelay = TimeSpan.FromSeconds(6);

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(30) };
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
        return await PostLoginAsync("/api/auth/extension/login", new LoginRequest { Email = email, Password = password });
    }

    /// <summary>
    /// Second login step for an account with 2FA: an authenticator or recovery code plus the pending token.
    /// </summary>
    public async Task<LoginOutcome> CompleteTwoFactorAsync(string pendingAuthToken, string code)
    {
        _log.Information("Submitting 2FA code");
        return await PostLoginAsync("/api/auth/login/2fa/extension",
            new TwoFactorLoginRequest { Token = code, PendingAuthToken = pendingAuthToken });
    }

    private async Task<LoginOutcome> PostLoginAsync<TRequest>(string path, TRequest request)
    {
        try
        {
            var response = await _http.PostAsJsonAsync(BuildUri(path), request);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warning("Login step {Path} failed with status {StatusCode}", path, response.StatusCode);
                return new LoginOutcome(LoginStatus.Failed, Message: DescribeLoginFailure(response.StatusCode));
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
    /// Downloads the user's active server rules for the before-upload filter (<see cref="AgentRules"/>), sending
    /// <paramref name="etag"/> so an unchanged set costs a 304 and no body.
    /// </summary>
    public async Task<AgentRulesResult> FetchAgentRulesAsync(string? etag, CancellationToken ct)
    {
        if (!HasSession) return new AgentRulesResult(AgentRulesFetch.Unavailable);

        try
        {
            var (response, _) = await SendAuthorizedAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(AgentRulesPath));
                if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
                    request.Headers.IfNoneMatch.Add(tag);
                return request;
            }, ct);
            if (response is null) return new AgentRulesResult(AgentRulesFetch.Unavailable);
            using var owned = response;

            var status = ClassifyAgentRules(response.StatusCode);
            if (status == AgentRulesFetch.Unavailable)
                _log.Warning("Agent rules answered {StatusCode} — keeping the rules already held", (int)response.StatusCode);
            if (status != AgentRulesFetch.Updated)
                return new AgentRulesResult(status);

            var rules = await response.Content.ReadFromJsonAsync<List<AgentRule>>(ct) ?? [];
            return new AgentRulesResult(AgentRulesFetch.Updated, rules, response.Headers.ETag?.ToString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Agent rules download failed — will retry");
            return new AgentRulesResult(AgentRulesFetch.Unavailable);
        }
    }

    /// <remarks>
    /// A 404 is a server from before the route existed: it has rules but cannot hand them out, and it still
    /// applies them on the heartbeat, so sending goes on unfiltered rather than waiting for a route that is not
    /// coming. Everything else that is not a 200 or a 304 is "try again", like a heartbeat.
    /// </remarks>
    internal static AgentRulesFetch ClassifyAgentRules(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.OK => AgentRulesFetch.Updated,
        HttpStatusCode.NotModified => AgentRulesFetch.Unchanged,
        HttpStatusCode.NotFound => AgentRulesFetch.NotSupported,
        _ => AgentRulesFetch.Unavailable
    };

    /// <summary>
    /// How much of the last week the user's server rules map, and the busiest unmapped apps — what the "map these apps
    /// to enable drift nudges" prompt names. A 404 is a server that predates the route.
    /// </summary>
    public async Task<CoverageResult> FetchCoverageAsync(CancellationToken ct)
    {
        if (!HasSession) return new CoverageResult(CoverageFetch.Unavailable);

        try
        {
            var (response, _) = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, BuildUri(AgentCoveragePath)), ct);
            if (response is null) return new CoverageResult(CoverageFetch.Unavailable);
            using var owned = response;

            if (response.StatusCode == HttpStatusCode.NotFound) return new CoverageResult(CoverageFetch.NotSupported);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                _log.Warning("Mapping coverage answered {StatusCode}", (int)response.StatusCode);
                return new CoverageResult(CoverageFetch.Unavailable);
            }

            return new CoverageResult(CoverageFetch.Fetched, await response.Content.ReadFromJsonAsync<AgentCoverage>(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Mapping coverage download failed — will retry");
            return new CoverageResult(CoverageFetch.Unavailable);
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
    /// <para>A refresh that failed after it was sent, or answered 5xx (rotation commits before the access token
    /// is minted), may have rotated the token with the answer lost. The server exchanges that token again when
    /// it comes back — but only after its 5 s grace, so no caller sends it again until <see cref="ReplayDelay"/>
    /// has passed. A lost answer never ends the session.</para>
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

            if (_uncertainSince is { } since && _time.GetElapsedTime(since) < ReplayDelay)
            {
                _log.Debug("Holding the refresh back — the last one may have rotated the token");
                return RefreshOutcome.Unavailable;
            }

            _log.Debug("Refreshing access token");
            sent = true;
            var response = await _http.PostAsJsonAsync(
                BuildUri("/api/auth/extension/refresh"), new { refreshToken }, ct);
            _uncertainSince = (int)response.StatusCode >= 500 ? _time.GetTimestamp() : null;

            if (response.IsSuccessStatusCode)
            {
                var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
                if (tokens is not { AccessToken.Length: > 0, RefreshToken.Length: > 0 })
                {
                    _uncertainSince = _time.GetTimestamp();
                    return RefreshOutcome.Unavailable;
                }

                StoreTokens(tokens);
                _log.Information("Token refreshed successfully");
                return RefreshOutcome.Refreshed;
            }

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _log.Warning("Refresh token refused ({StatusCode}) — session ended", response.StatusCode);
                SignOut();
                return RefreshOutcome.Rejected;
            }

            _log.Warning("Token refresh failed with {StatusCode} — keeping the session, will retry", response.StatusCode);
            return RefreshOutcome.Unavailable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (sent) _uncertainSince = _time.GetTimestamp();
            throw;
        }
        catch (Exception ex)
        {
            if (sent && !NeverReachedTheServer(ex)) _uncertainSince = _time.GetTimestamp();
            _log.Warning(ex, "Token refresh failed — keeping the session, will retry");
            return RefreshOutcome.Unavailable;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// A failure before the request could be written: no DNS answer, no connection, no TLS. Anything else — a
    /// timeout, a dropped connection — may have reached the server after it was written.
    /// </summary>
    private static bool NeverReachedTheServer(Exception ex) =>
        ex is HttpRequestException
        {
            HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
        };

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
