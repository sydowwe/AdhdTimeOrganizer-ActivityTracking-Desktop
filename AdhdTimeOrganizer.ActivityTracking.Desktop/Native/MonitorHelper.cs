using System.Runtime.InteropServices;

namespace DesktopActivityTracker.Native;

internal static partial class MonitorHelper
{
    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumDisplayMonitors(nint hdc, nint lprcClip, delegate* unmanaged<nint, nint, RECT*, nint, int> lpfnEnum, nint dwData);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// Gets the monitor index (0-based) of the foreground window.
    /// </summary>
    public static unsafe int GetActiveMonitorIndex()
    {
        var hWnd = Win32.GetForegroundWindow();
        if (hWnd == nint.Zero) return 0;

        var targetMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        var monitors = new List<nint>();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(monitors);
        try
        {
            EnumDisplayMonitors(nint.Zero, nint.Zero, &EnumMonitorCallback, System.Runtime.InteropServices.GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        var index = monitors.IndexOf(targetMonitor);
        return index >= 0 ? index : 0;
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static unsafe int EnumMonitorCallback(nint hMonitor, nint hdcMonitor, RECT* lprcMonitor, nint dwData)
    {
        var handle = System.Runtime.InteropServices.GCHandle.FromIntPtr(dwData);
        if (handle.Target is List<nint> monitors)
            monitors.Add(hMonitor);
        return 1;
    }
}
