using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

public readonly record struct IntRange(int Min, int Max)
{
    public int Clamp(int value) => Math.Clamp(value, Min, Max);
}

/// <summary>
/// The one range each number in <see cref="AppConfig"/> may take. <see cref="AppConfig.Normalize"/> clamps a loaded config to
/// them, the Settings form offers exactly them, and the policies that read a number straight from a config built in code
/// clamp to the same ones — they used to disagree (poll interval 250–60 000 ms in the tracker, 500–10 000 in Settings).
/// </summary>
public static class ConfigLimits
{
    public static readonly IntRange PollIntervalMs = new(500, 10_000);
    public static readonly IntRange IdleThresholdSeconds = new(5, 600);
    public static readonly IntRange MaxPassiveIdleMinutes = new(0, 600);
    public static readonly IntRange MinBackgroundWindowPercent = new(0, 100);
    public static readonly IntRange MaxBackgroundWindowsPerMonitor = new(0, 10);
    public static readonly IntRange HyperfocusNudgeMinutes = new(15, 600);

    /// <summary>0 turns the same-app nudge off.</summary>
    public static readonly IntRange AppHyperfocusNudgeMinutes = new(0, 600);

    public static readonly IntRange BreakMinutes = new(1, 60);
    public static readonly IntRange BreakTimerMinutes = new(1, 120);
    public static readonly IntRange DriftNudgeMinutes = new(DriftSettings.MinMinutes, 120);
}
