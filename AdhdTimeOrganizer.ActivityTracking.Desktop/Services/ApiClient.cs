using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
public sealed class ApiClient(AppConfig config) : IDisposable
{
    private const string HeartbeatPath = "/api/activity-tracking/desktop/heartbeat";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ILogger _log = Log.ForContext<ApiClient>();
    private volatile string? _accessToken;

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

    public void SignOut()
    {
        _accessToken = null;
        config.RefreshToken = null;
        config.Save();
    }

    /// <summary>
    /// Send an aggregated activity window to the backend.
    /// </summary>
    public async Task<SendResult> SendActivityWindowAsync(ActivityWindow window, CancellationToken ct)
    {
        if (!HasSession) return SendResult.SignedOut;

        try
        {
            var token = _accessToken;
            if (token is null)
            {
                var outcome = await RefreshTokenAsync(null, ct);
                if (outcome != RefreshOutcome.Refreshed) return ToSendResult(outcome);
                token = _accessToken;
            }

            var response = await PostWindowAsync(window, token, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log.Information("Got 401 sending activity window — refreshing the access token");
                var outcome = await RefreshTokenAsync(token, ct);
                if (outcome != RefreshOutcome.Refreshed) return ToSendResult(outcome);

                response = await PostWindowAsync(window, _accessToken, ct);
            }

            var result = Classify(response.StatusCode);
            if (result != SendResult.Sent)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _log.Warning("Window {WindowStart} answered {StatusCode} ({Result}): {Body}",
                    window.WindowStart, (int)response.StatusCode, result, body);
            }
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Network error sending window {WindowStart} — will retry", window.WindowStart);
            return SendResult.RetryLater;
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

    private async Task<HttpResponseMessage> PostWindowAsync(ActivityWindow window, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(HeartbeatPath))
        {
            Content = JsonContent.Create(window)
        };
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
    /// </remarks>
    private async Task<RefreshOutcome> RefreshTokenAsync(string? staleAccessToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && _accessToken != staleAccessToken)
                return RefreshOutcome.Refreshed;

            var refreshToken = config.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken)) return RefreshOutcome.Rejected;

            _log.Debug("Refreshing access token");
            var response = await _http.PostAsJsonAsync(
                BuildUri("/api/auth/extension/refresh"), new { refreshToken }, ct);

            if (response.IsSuccessStatusCode)
            {
                var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
                if (tokens is not { AccessToken.Length: > 0, RefreshToken.Length: > 0 }) return RefreshOutcome.Unavailable;

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
            throw;
        }
        catch (Exception ex)
        {
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
