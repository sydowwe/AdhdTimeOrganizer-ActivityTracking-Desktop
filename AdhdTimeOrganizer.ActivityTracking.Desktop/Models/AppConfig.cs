using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services.Matching;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

public enum LocalRuleAction
{
    /// <summary>Not recorded at all — system noise, or an app the user wants no trace of.</summary>
    Ignore,
    /// <summary>Recorded, but the title never leaves the machine (see <see cref="PrivateWindowHandling"/>).</summary>
    HideTitle
}

/// <summary>What a private browser window or a <see cref="LocalRuleAction.HideTitle"/> window turns into.</summary>
public enum PrivateWindowHandling
{
    /// <summary>Sent with the title replaced by "&lt;app&gt; - PRIVATE", so the time still adds up.</summary>
    KeepTimeAsAppName,
    /// <summary>Not recorded, like an ignored window.</summary>
    Drop
}

/// <summary>
/// One of the user's own rules, applied when a window is recorded — before anything is written to the outbox.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProcessName"/> null or empty → any process. <see cref="WindowTitle"/> null → any title; <c>""</c> →
/// only an empty title; otherwise matched with <see cref="WindowTitleMatchType"/> against the raw title <em>and</em>
/// the normalized one, so a rule written from what the dashboards show still catches the window, and a rule
/// written against a part normalization strips does too. A rule that names neither matches nothing.
/// </para>
/// <para>
/// The JSON is the old ignore rule's (<c>processName</c>, <c>windowTitle</c>, exact) plus three optional fields,
/// so a config.json written before them reads unchanged.
/// </para>
/// </remarks>
public sealed class LocalRule
{
    [JsonPropertyName("action")]
    [JsonConverter(typeof(JsonStringEnumConverter<LocalRuleAction>))]
    public LocalRuleAction Action { get; set; } = LocalRuleAction.Ignore;

    [JsonPropertyName("processName")]
    public string? ProcessName { get; set; }

    [JsonPropertyName("processNameMatchType")]
    [JsonConverter(typeof(JsonStringEnumConverter<PatternMatchType>))]
    public PatternMatchType ProcessNameMatchType { get; set; } = PatternMatchType.Exact;

    [JsonPropertyName("windowTitle")]
    public string? WindowTitle { get; set; }

    [JsonPropertyName("windowTitleMatchType")]
    [JsonConverter(typeof(JsonStringEnumConverter<PatternMatchType>))]
    public PatternMatchType WindowTitleMatchType { get; set; } = PatternMatchType.Exact;
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

    // ── Hyperfocus nudges (see HyperfocusPolicy) ──────────────────────────────

    /// <summary>Off from a nudge's own "Turn off" button too. A break timer already running still ends with its nudge.</summary>
    public bool HyperfocusNudgesEnabled { get; set; } = true;

    /// <summary>At the computer this long without a break → "you've been at it for 1 h 30 m".</summary>
    public int HyperfocusNudgeMinutes { get; set; } = 90;

    /// <summary>Foreground time in one app without a break from it → "2 h straight in Rider". 0 turns it off.</summary>
    public int AppHyperfocusNudgeMinutes { get; set; } = 120;

    /// <summary>Away this long — no input, no sound in the foreground, no call — ends a stretch.</summary>
    public int BreakMinutes { get; set; } = 5;

    /// <summary>How long "Taking a break" lasts before the nudge that says the break is over.</summary>
    public int BreakTimerMinutes { get; set; } = 10;

    /// <summary>Hold nudges while an app is full screen or presenting (Windows' own definition).</summary>
    public bool QuietWhenFullscreen { get; set; } = true;

    /// <summary>Hold nudges while any app has the microphone open — a call, even a muted one.</summary>
    public bool QuietWhenMicrophoneInUse { get; set; } = true;

    // ── Privacy (see PrivacyFilter) ───────────────────────────────────────────

    /// <summary>Firefox Private Browsing and Edge InPrivate windows are handled like a hide-title rule.</summary>
    public bool HidePrivateWindows { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter<PrivateWindowHandling>))]
    public PrivateWindowHandling PrivateWindowHandling { get; set; } = PrivateWindowHandling.KeepTimeAsAppName;

    /// <summary>
    /// Tracking is paused until then: a time from "Pause for…", <see cref="DateTime.MaxValue"/> for "until I
    /// resume", null when not paused. Saved, so a restart during a pause does not quietly start recording again.
    /// </summary>
    public DateTime? PausedUntilUtc { get; set; }

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
    /// The user's own rules (see <see cref="LocalRule"/>), edited in the Privacy form. Still stored under the old
    /// <c>IgnoreRules</c> key, which configs written before hide-title rules carry. Replaced as a whole on save,
    /// never mutated, because the poll loop reads it on its own thread.
    /// </summary>
    [JsonPropertyName("IgnoreRules")]
    public List<LocalRule> LocalRules { get; set; } =
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
