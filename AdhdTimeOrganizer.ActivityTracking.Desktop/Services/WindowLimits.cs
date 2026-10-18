using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// The server's heartbeat rules, applied before a window is queued, so nothing the agent sends can be
/// refused for its shape. A refused window is a lost minute — the agent cannot repair a batch the server
/// calls malformed — so every bound here mirrors one in the server's <c>DesktopActivityHeartbeatValidator</c>
/// and <c>TrackingIngestLimits</c>.
/// </summary>
public static class WindowLimits
{
    public const int SecondsPerWindow = 60;
    public const int MaxEntriesPerWindow = 200;
    public const int MaxProcessNameLength = 255;
    public const int MaxProductNameLength = 255;
    public const int MaxWindowTitleLength = 1024;
    public const int MaxExecutablePathLength = 1024;
    public const int MaxIndexedEntryTextBytes = 2000;

    /// <summary>
    /// The server's <c>MaxDesktopSwitchesPerEntry</c>. More is two windows fighting over focus; the count is clamped
    /// to it rather than have the minute refused.
    /// </summary>
    public const int MaxSwitchesPerEntry = 120;

    /// <summary>
    /// Clamps each entry to 60 seconds of active plus background time, drops empty ones, keeps the 200
    /// busiest, and trims the active total to 60 seconds — taking the excess from the busiest entries, where
    /// a rounding second is the smallest relative change.
    /// </summary>
    public static List<ActivityEntry> Apply(IEnumerable<ActivityEntry> entries)
    {
        var clamped = entries
            .Select(e =>
            {
                var active = Math.Clamp(e.ActiveSeconds, 0, SecondsPerWindow);
                var background = Math.Clamp(e.BackgroundSeconds, 0, SecondsPerWindow - active);
                return (Entry: e, Active: active, Background: background);
            })
            .Where(x => x.Active > 0 || x.Background > 0)
            .OrderByDescending(x => x.Active)
            .ThenByDescending(x => x.Background)
            .Take(MaxEntriesPerWindow)
            .ToList();

        var excess = clamped.Sum(x => x.Active) - SecondsPerWindow;
        for (var i = 0; excess > 0 && i < clamped.Count; i++)
        {
            var cut = Math.Min(excess, clamped[i].Active);
            clamped[i] = clamped[i] with { Active = clamped[i].Active - cut };
            excess -= cut;
        }

        return clamped
            .Where(x => x.Active > 0 || x.Background > 0)
            .Select(x => Sanitize(x.Entry, x.Active, x.Background))
            .ToList();
    }

    private static ActivityEntry Sanitize(ActivityEntry entry, int activeSeconds, int backgroundSeconds)
    {
        var processName = Truncate(entry.ProcessName, MaxProcessNameLength);
        var title = Truncate(entry.WindowTitle ?? "", MaxWindowTitleLength);

        // The (process, title) pair sits in a btree index limited in bytes, not characters.
        while (title.Length > 0
               && Encoding.UTF8.GetByteCount(processName) + Encoding.UTF8.GetByteCount(title) > MaxIndexedEntryTextBytes)
            title = title[..^Math.Max(1, title.Length / 10)];

        // A missing product name or path goes out as "" rather than null. The server stores "" either way,
        // but a server from before 2026-10-18 lets a null through its validator into a NOT NULL insert and
        // answers 500 — so "" keeps this agent working against an older server too.
        return new ActivityEntry
        {
            ProcessName = processName,
            WindowTitle = title,
            // An over-long path is dropped rather than cut: half a path is not a path.
            ExecutablePath = entry.ExecutablePath is { Length: <= MaxExecutablePathLength } path ? path : "",
            ProductName = entry.ProductName is null ? "" : Truncate(entry.ProductName, MaxProductNameLength),
            ActiveSeconds = activeSeconds,
            BackgroundSeconds = backgroundSeconds,
            ActiveMonitor = entry.ActiveMonitor,
            IsPlayingSound = entry.IsPlayingSound,
            IsFullscreen = entry.IsFullscreen,
            SwitchCount = entry.SwitchCount is { } switches ? Math.Clamp(switches, 0, MaxSwitchesPerEntry) : null,
            IsInCall = entry.IsInCall
        };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
