using System.Runtime.InteropServices;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Native;

internal readonly record struct MonitorInfo(nint Handle, Win32.RECT Bounds);

internal sealed record BackgroundWindowInfo(
    ProcessInfo Process,
    string? WindowTitle,
    int MonitorIndex,
    int Pid);

internal static partial class MonitorHelper
{
    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumDisplayMonitors(
        nint hdc, nint lprcClip,
        delegate* unmanaged<nint, nint, Win32.RECT*, nint, int> lpfnEnum,
        nint dwData);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// All monitors in a stable order: the primary first (its top-left corner is always 0,0), then left
    /// to right, top to bottom. <c>EnumDisplayMonitors</c> order is not guaranteed and changes when a
    /// laptop docks, which made the reported monitor index mean different screens on different days.
    /// Fetch once per poll and pass it down — every helper below needs it.
    /// </summary>
    public static unsafe List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var handle = GCHandle.Alloc(monitors);
        try { EnumDisplayMonitors(nint.Zero, nint.Zero, &EnumMonitorCallback, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }

        return monitors
            .OrderBy(m => m.Bounds.Left == 0 && m.Bounds.Top == 0 ? 0 : 1)
            .ThenBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();
    }

    public static int GetWindowMonitorIndex(nint hWnd, List<MonitorInfo> monitors)
    {
        if (hWnd == nint.Zero) return 0;
        var targetMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        var index = monitors.FindIndex(m => m.Handle == targetMonitor);
        return index >= 0 ? index : 0;
    }

    /// <summary>
    /// Returns true if the window occupies the full bounds of its monitor
    /// (fullscreen game, video player, presentation, etc.).
    /// </summary>
    public static bool IsWindowFullscreen(nint hWnd, List<MonitorInfo> monitors)
    {
        if (hWnd == nint.Zero) return false;
        if (!Win32.GetWindowRect(hWnd, out var windowRect)) return false;

        var targetMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        var monitor = monitors.FirstOrDefault(m => m.Handle == targetMonitor);
        if (monitor.Handle == nint.Zero) return false;

        var b = monitor.Bounds;
        // Use <= / >= to tolerate 1-pixel off-by-one from some apps
        return windowRect.Left <= b.Left && windowRect.Top <= b.Top
            && windowRect.Right >= b.Right && windowRect.Bottom >= b.Bottom;
    }

    /// <summary>
    /// Returns background windows on each monitor (excluding the foreground window),
    /// ranked by approximate visible area (window area minus occlusion by higher Z-order windows).
    /// Windows covering less than <paramref name="minAreaPercent"/>% of the monitor are ignored.
    /// At most <paramref name="maxPerMonitor"/> windows are returned per monitor.
    /// </summary>
    public static List<BackgroundWindowInfo> GetBackgroundWindows(
        nint foregroundHwnd, List<MonitorInfo> monitors, int minAreaPercent, int maxPerMonitor)
    {
        if (monitors.Count == 0 || maxPerMonitor <= 0) return [];

        // Step 1 — all top-level windows, Z-order, topmost first.
        var rawHwnds = Win32.GetTopLevelWindows();

        // Step 2 — filter to real windows and snapshot their bounding rects.
        var windows = new List<(nint Hwnd, Win32.RECT Rect)>(rawHwnds.Count);
        foreach (var hwnd in rawHwnds)
        {
            if (!Win32.IsRealWindow(hwnd)) continue;
            if (!Win32.GetWindowRect(hwnd, out var rect)) continue;
            windows.Add((hwnd, rect));
        }

        // Step 3 — per monitor, rank candidates by visible area and take top N.
        var result = new List<BackgroundWindowInfo>();

        for (var mi = 0; mi < monitors.Count; mi++)
        {
            var monitorBounds = monitors[mi].Bounds;
            int monitorArea = monitorBounds.Area;
            if (monitorArea == 0) continue;

            int minArea = monitorArea * minAreaPercent / 100;

            // processedRects accumulates rects of all windows above the current one
            // in Z-order so we can approximate occlusion across the whole desktop.
            var processedRects = new List<Win32.RECT>(windows.Count);
            var candidates = new List<(nint Hwnd, int VisibleArea)>();

            foreach (var (hwnd, rect) in windows)
            {
                // Is any part of this window on this monitor?
                if (Win32.RECT.Intersect(rect, monitorBounds).Area > 0 && hwnd != foregroundHwnd)
                {
                    int visibleArea = ApproximateVisibleArea(rect, processedRects, monitorBounds);
                    if (visibleArea >= minArea)
                        candidates.Add((hwnd, visibleArea));
                }

                // Always add to processedRects so higher-Z windows occlude lower ones
                // even across monitor boundaries (e.g. maximised spanning window).
                processedRects.Add(rect);
            }

            foreach (var (hwnd, _) in candidates.OrderByDescending(c => c.VisibleArea).Take(maxPerMonitor))
            {
                var process = ProcessInfoCache.GetForWindow(hwnd, out var pid);
                if (process is not null)
                    result.Add(new BackgroundWindowInfo(process, Win32.GetWindowTitle(hwnd), mi, pid));
            }
        }

        return result;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    [UnmanagedCallersOnly]
    private static unsafe int EnumMonitorCallback(
        nint hMonitor, nint hdcMonitor, Win32.RECT* lprcMonitor, nint dwData)
    {
        var handle = GCHandle.FromIntPtr(dwData);
        if (handle.Target is List<MonitorInfo> list)
            list.Add(new MonitorInfo(hMonitor, *lprcMonitor));
        return 1;
    }

    /// <summary>
    /// Approximates the visible area of <paramref name="windowRect"/> on
    /// <paramref name="monitorBounds"/> by subtracting the intersection area of each
    /// higher-Z-order window rect. Slightly over-subtracts when two higher windows
    /// overlap each other inside the target, but is accurate enough in practice.
    /// </summary>
    private static int ApproximateVisibleArea(
        Win32.RECT windowRect, List<Win32.RECT> higherWindows, Win32.RECT monitorBounds)
    {
        // Clip to monitor so windows that barely touch the edge don't get credited
        var clipped = Win32.RECT.Intersect(windowRect, monitorBounds);
        if (clipped.Area == 0) return 0;

        int covered = 0;
        foreach (var higher in higherWindows)
            covered += Win32.RECT.Intersect(clipped, higher).Area;

        // Cap covered at 100 % to absorb the double-counting approximation error
        return Math.Max(0, clipped.Area - Math.Min(covered, clipped.Area));
    }
}
