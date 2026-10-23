using System.Text.Json.Serialization;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

/// <summary>
/// One window as seen by a single poll.
/// </summary>
public sealed record ActivitySample
{
    public required string ProcessName { get; init; }
    public required string WindowTitle { get; init; }
    public string? ExecutablePath { get; init; }
    public string? ProductName { get; init; }
}

/// <summary>
/// A sample plus how it was seen. The tracker holds a set of these as "what is on screen now" and credits each span of
/// time to the set that held during it; a set holds at most one non-background observation.
/// </summary>
/// <param name="IsInCall">The app holds the microphone. A flag on the entry only: it changes nothing that is credited.</param>
/// <param name="IsSwitch">
/// The window has just come to the front, from another window or another title. Counted once, for the span that
/// begins with it — the tracker clears it after that span.
/// </param>
public sealed record Observation(
    ActivitySample Sample,
    int MonitorIndex,
    bool IsBackground,
    bool IsPlayingSound,
    bool IsFullscreen,
    bool IsInCall = false,
    bool IsSwitch = false);

/// <summary>
/// A single process entry within an aggregated 1-minute window.
/// </summary>
public sealed class ActivityEntry
{
    [JsonPropertyName("processName")]
    public required string ProcessName { get; init; }

    [JsonPropertyName("windowTitle")]
    public string? WindowTitle { get; init; }

    [JsonPropertyName("executablePath")]
    public string? ExecutablePath { get; init; }

    [JsonPropertyName("activeSeconds")]
    public required int ActiveSeconds { get; init; }

    [JsonPropertyName("backgroundSeconds")]
    public required int BackgroundSeconds { get; init; }

    [JsonPropertyName("activeMonitor")]
    public required int ActiveMonitor { get; init; }

    [JsonPropertyName("isPlayingSound")]
    public required bool IsPlayingSound { get; init; }

    [JsonPropertyName("isFullscreen")]
    public required bool IsFullscreen { get; init; }

    [JsonPropertyName("productName")]
    public string? ProductName { get; init; }

    /// <summary>
    /// Times the window came to the front in this minute. Null only in an outbox file written by an tracker from before
    /// it was measured: the server stores that as "unknown", which is what it is.
    /// </summary>
    [JsonPropertyName("switchCount")]
    public int? SwitchCount { get; init; }

    /// <summary>The app held the microphone during the minute.</summary>
    [JsonPropertyName("isInCall")]
    public bool IsInCall { get; init; }
}

/// <summary>
/// Aggregated 1-minute activity window sent to the backend.
/// </summary>
public sealed class ActivityWindow
{
    [JsonPropertyName("windowStart")]
    public required DateTime WindowStart { get; init; }

    [JsonPropertyName("entries")]
    public required List<ActivityEntry> Entries { get; init; }
}

/// <summary>
/// Auth request/response models.
/// </summary>
public sealed class LoginRequest
{
    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("password")]
    public required string Password { get; init; }

    /// <summary>
    /// Which tracker is signing in. The server refuses a login without it, and the session it issues may call the
    /// desktop routes only. The browser sign-in needs no such field: the server reads the kind off the loopback redirect.
    /// </summary>
    [JsonPropertyName("client")]
    public string Client { get; init; } = "DesktopTracker";
}

/// <summary>
/// The extension login answer. When the account has 2FA, the password step returns no tokens — only
/// <see cref="RequiresTwoFactor"/> and a <see cref="PendingAuthToken"/> to send with the code.
/// </summary>
public sealed class TokenResponse
{
    [JsonPropertyName("requiresTwoFactor")]
    public bool RequiresTwoFactor { get; set; }

    [JsonPropertyName("requiresTwoFactorSetup")]
    public bool RequiresTwoFactorSetup { get; set; }

    [JsonPropertyName("pendingAuthToken")]
    public string? PendingAuthToken { get; set; }

    [JsonPropertyName("accessToken")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }
}

public sealed class TwoFactorLoginRequest
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("pendingAuthToken")]
    public required string PendingAuthToken { get; init; }
}

/// <summary>Browser sign-in, second half (<c>POST /api/auth/tracker/token</c>): the code the loopback listener received.</summary>
public sealed class BrowserCodeExchangeRequest
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("codeVerifier")]
    public required string CodeVerifier { get; init; }

    /// <summary>The redirect the code was issued for, byte for byte.</summary>
    [JsonPropertyName("redirectUri")]
    public required string RedirectUri { get; init; }
}
