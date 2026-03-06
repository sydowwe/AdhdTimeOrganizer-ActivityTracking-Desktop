using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopActivityTracker.Models;

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
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopActivityTracker",
        "config.json");

    public string ApiBaseUrl { get; set; } = "https://localhost:8080";
    public int PollIntervalMs { get; set; } = 2000;
    public int IdleThresholdSeconds { get; set; } = 30;
    public bool AutoStart { get; set; } = true;
    public string? RefreshToken { get; set; }
    public int MinBackgroundWindowPercent { get; set; } = 15;
    public int MaxBackgroundWindowsPerMonitor { get; set; } = 2;

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
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
        }
        catch
        {
            // Fall through to default
        }

        return new AppConfig();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
