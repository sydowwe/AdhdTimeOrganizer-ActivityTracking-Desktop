using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

/// <summary>
/// A rule that suppresses tracking for a process.
/// WindowTitle = null  → ignore the process regardless of its window title.
/// WindowTitle = ""    → ignore only when the window title is empty/whitespace.
/// WindowTitle = "Foo" → ignore only when the window title equals "Foo" (case-insensitive).
/// </summary>
public sealed class IgnoreRule
{
    [JsonPropertyName("processName")]
    public string ProcessName { get; set; } = "";

    [JsonPropertyName("windowTitle")]
    public string? WindowTitle { get; set; }
}

public sealed class AppConfig
{
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdhdTimeOrganizer.ActivityTracking.Desktop");

    public static string ConfigPath { get; } = Path.Combine(ConfigDirectory, "config.json");

    // Ties the DPAPI blob to this app as well as to the Windows user.
    private static readonly byte[] TokenEntropy = "AdhdTimeOrganizer.ActivityTracking.Desktop"u8.ToArray();

    private static readonly Lock SaveGate = new();

#if DEBUG
    public string ApiBaseUrl { get; set; } = "https://localhost:8080";
#else
    public string ApiBaseUrl { get; set; } = "https://adhdtimeorganizer.com";
#endif
    public int PollIntervalMs { get; set; } = 2000;
    public int IdleThresholdSeconds { get; set; } = 30;
    public int MinBackgroundWindowPercent { get; set; } = 15;
    public int MaxBackgroundWindowsPerMonitor { get; set; } = 2;

    /// <summary>
    /// How long a foreground window that is playing sound keeps the user "present" without keyboard or
    /// mouse input — a film, a talk, a call. Past this, autoplay left running counts as idle.
    /// </summary>
    public int MaxPassiveIdleMinutes { get; set; } = 150;

    /// <summary>
    /// The refresh token, encrypted with DPAPI for the current Windows user. It used to sit in this file
    /// in plain text, readable by anything that can read the user's profile.
    /// </summary>
    public string? ProtectedRefreshToken
    {
        get => _protectedRefreshToken;
        set
        {
            _protectedRefreshToken = value;
            _isRefreshTokenDecrypted = false;
        }
    }

    /// <summary>Read once from a config written before the token was encrypted, then cleared.</summary>
    [JsonPropertyName("RefreshToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyRefreshToken { get; set; }

    /// <summary>The decrypted token, cached — the tray reads it every couple of seconds.</summary>
    [JsonIgnore]
    public string? RefreshToken
    {
        get
        {
            if (!_isRefreshTokenDecrypted)
            {
                _refreshToken = Unprotect(_protectedRefreshToken);
                _isRefreshTokenDecrypted = true;
            }
            return _refreshToken;
        }
        set
        {
            _protectedRefreshToken = Protect(value);
            _refreshToken = string.IsNullOrEmpty(value) ? null : value;
            _isRefreshTokenDecrypted = true;
        }
    }

    private string? _protectedRefreshToken;
    private string? _refreshToken;
    private bool _isRefreshTokenDecrypted;

    /// <summary>
    /// Processes/windows suppressed from tracking.
    /// Add entries here for any process you consider system noise.
    /// </summary>
    public List<IgnoreRule> IgnoreRules { get; set; } =
    [
        // ── Explorer shell noise ──────────────────────────────────────────────
        // "Program Manager" is the hidden desktop host window; empty title = taskbar focus.
        // File Explorer windows (folder titles) are NOT matched and will still be tracked.
        new() { ProcessName = "explorer",                  WindowTitle = "Program Manager" },
        new() { ProcessName = "explorer",                  WindowTitle = "" },

        // ── Windows Search ────────────────────────────────────────────────────
        new() { ProcessName = "SearchHost" },        // Win 11 search overlay
        new() { ProcessName = "SearchApp" },          // Win 10 search overlay

        // ── Start menu / shell chrome ─────────────────────────────────────────
        new() { ProcessName = "StartMenuExperienceHost" }, // Win 10/11 Start menu
        new() { ProcessName = "ShellExperienceHost" },     // Notification centre, older Start
        new() { ProcessName = "ActionCenter" },
        new() { ProcessName = "Taskmgr" },

        // ── Lock / login screen ───────────────────────────────────────────────
        new() { ProcessName = "LockApp" },
        new() { ProcessName = "LogonUI" },

        // ── Input infrastructure ──────────────────────────────────────────────
        new() { ProcessName = "TextInputHost" },     // Touch keyboard / voice input panel
        new() { ProcessName = "ctfmon" },            // IME/input method manager

        // ── Other shell plumbing ──────────────────────────────────────────────
        new() { ProcessName = "ScreenClippingHost" }, // Snipping Tool host (momentary)
        new() { ProcessName = "PaintStudio" },        // Win 11 Snipping Tool itself is brief
    ];

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();

                if (config.LegacyRefreshToken is { } legacy)
                {
                    config.RefreshToken = legacy;
                    config.LegacyRefreshToken = null;
                    config.Save();
                }

                return config;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read {ConfigPath} — using defaults", ConfigPath);
        }

        return new AppConfig();
    }

    public void Save()
    {
        lock (SaveGate)
        {
            Directory.CreateDirectory(ConfigDirectory);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = ConfigPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
    }

    private static string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), TokenEntropy, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), TokenEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            // Another Windows user's blob, or a profile restored on a different machine: sign in again.
            Log.Warning(ex, "Saved refresh token could not be decrypted");
            return null;
        }
    }
}
