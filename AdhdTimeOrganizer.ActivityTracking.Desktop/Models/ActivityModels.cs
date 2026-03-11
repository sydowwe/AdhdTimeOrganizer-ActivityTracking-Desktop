using System.Text.Json.Serialization;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

/// <summary>
/// A single poll sample captured every 1-2 seconds.
/// </summary>
public sealed class ActivitySample
{
    public required string ProcessName { get; init; }
    public string? WindowTitle { get; init; }
    public string? ExecutablePath { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

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
}

/// <summary>
/// Aggregated 1-minute activity window sent to the backend.
/// </summary>
public sealed class ActivityWindow
{
    [JsonPropertyName("windowStart")]
    public required DateTime WindowStart { get; init; }

    [JsonPropertyName("idleSeconds")]
    public required int IdleSeconds { get; init; }

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
}

public sealed class TokenResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = "";
}
