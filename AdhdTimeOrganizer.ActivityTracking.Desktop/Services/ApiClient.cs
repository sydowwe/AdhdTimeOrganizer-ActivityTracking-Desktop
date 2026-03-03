using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DesktopActivityTracker.Models;
using Serilog;

namespace DesktopActivityTracker.Services;

/// <summary>
/// Handles API communication with JWT bearer token auth and refresh.
/// </summary>
public sealed class ApiClient(AppConfig config) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(config.ApiBaseUrl) };
    private readonly ILogger _log = Log.ForContext<ApiClient>();
    private string? _accessToken;

    public bool IsAuthenticated => _accessToken is not null;

    /// <summary>
    /// Authenticate with email and password.
    /// </summary>
    public async Task<bool> LoginAsync(string email, string password)
    {
        _log.Information("Attempting login for {Email}", email);
        try
        {
            var response = await _http.PostAsJsonAsync("/api/auth/extension/login", new LoginRequest
            {
                Email = email,
                Password = password
            });

            if (!response.IsSuccessStatusCode)
            {
                _log.Warning("Login failed with status {StatusCode}", response.StatusCode);
                return false;
            }

            var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
            if (tokens is null) return false;

            _accessToken = tokens.AccessToken;
            config.RefreshToken = tokens.RefreshToken;
            config.Save();

            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _accessToken);

            _log.Information("Login successful");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Login request failed");
            return false;
        }
    }

    /// <summary>
    /// Attempt to restore session from saved refresh token.
    /// </summary>
    public async Task<bool> TryRestoreSessionAsync()
    {
        if (string.IsNullOrEmpty(config.RefreshToken))
        {
            _log.Debug("No saved refresh token — skipping session restore");
            return false;
        }

        _log.Information("Restoring session from saved refresh token");
        return await RefreshTokenAsync();
    }

    /// <summary>
    /// Send an aggregated activity window to the backend.
    /// Returns true if the window was sent successfully or should be dropped (permanent 4xx).
    /// Returns false only for transient failures (5xx, network errors) that should be retried.
    /// </summary>
    public async Task<bool> SendActivityWindowAsync(ActivityWindow window)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/activity-tracking/desktop/heartbeat", window);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log.Warning("Got 401 sending activity window — attempting token refresh");
                if (await RefreshTokenAsync())
                {
                    response = await _http.PostAsJsonAsync("/api/activity-tracking/desktop/heartbeat", window);
                }
                else
                {
                    return false; // auth failed — retry later
                }
            }

            if (response.IsSuccessStatusCode)
                return true;

            var statusCode = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync();

            if (statusCode is >= 400 and < 500)
            {
                // Client error (including 409 Conflict) — permanent, do not retry
                _log.Warning("Window {WindowStart} permanently rejected: {StatusCode} — {Body}",
                    window.WindowStart, response.StatusCode, body);
                return true;
            }

            // 5xx — transient, retry
            _log.Warning("Server error {StatusCode} for window {WindowStart} — will retry. Body: {Body}",
                response.StatusCode, window.WindowStart, body);
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Network error sending window {WindowStart} — will retry", window.WindowStart);
            return false;
        }
    }

    private async Task<bool> RefreshTokenAsync()
    {
        _log.Debug("Refreshing access token");
        try
        {
            var response = await _http.PostAsJsonAsync("/api/auth/extension/refresh", new
            {
                refreshToken = config.RefreshToken
            });

            if (!response.IsSuccessStatusCode)
            {
                _log.Warning("Token refresh failed with status {StatusCode} — clearing session", response.StatusCode);
                _accessToken = null;
                config.RefreshToken = null;
                config.Save();
                return false;
            }

            var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
            if (tokens is null) return false;

            _accessToken = tokens.AccessToken;
            config.RefreshToken = tokens.RefreshToken;
            config.Save();

            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _accessToken);

            _log.Information("Token refreshed successfully");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Exception during token refresh");
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
