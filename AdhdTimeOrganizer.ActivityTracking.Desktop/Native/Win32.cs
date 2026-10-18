using System.Runtime.InteropServices;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Native;

internal static partial class Win32
{
    // ── P/Invokes ────────────────────────────────────────────────────────────

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowText(nint hWnd, [Out] char[] lpString, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetTickCount();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumChildWindows(nint hWndParent, delegate* unmanaged<nint, nint, int> lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    [LibraryImport("shell32.dll")]
    internal static partial int SHQueryUserNotificationState(out UserNotificationState state);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static partial int GetWindowLong(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(nint hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(nint hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    // ── Constants ────────────────────────────────────────────────────────────

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_APPWINDOW = 0x00040000;
    internal const int DWMWA_CLOAKED = 14;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary><c>QUERY_USER_NOTIFICATION_STATE</c> — what the shell itself uses to hold back notifications.</summary>
    internal enum UserNotificationState
    {
        NotPresent = 1,          // QUNS_NOT_PRESENT: screen saver, locked, or another user's session
        Busy = 2,                // QUNS_BUSY: a full-screen app, or presentation settings applied
        RunningD3DFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,           // the first hour after a new user signs in on this machine
        App = 7                  // a Store app running full screen
    }

    // ── Structs ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public int Area => Math.Max(0, Width) * Math.Max(0, Height);

        public static RECT Intersect(RECT a, RECT b) => new()
        {
            Left   = Math.Max(a.Left,   b.Left),
            Top    = Math.Max(a.Top,    b.Top),
            Right  = Math.Min(a.Right,  b.Right),
            Bottom = Math.Min(a.Bottom, b.Bottom)
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    public static string? GetWindowTitle(nint hWnd)
    {
        var buffer = new char[512];
        var length = GetWindowText(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    public static int GetIdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return (int)((GetTickCount() - info.dwTime) / 1000);
    }

    /// <summary>
    /// True while the shell itself holds notifications back: a full-screen app or game, presentation mode,
    /// a locked or screen-saver session. This is Windows' own "full screen" — a rectangle check on the
    /// foreground window would also catch a maximised window behind an auto-hiding taskbar, and the desktop.
    /// A failed call or a state this code doesn't know is "not holding": a wrong "yes" would hold every
    /// nudge back with no sign of why.
    /// </summary>
    public static bool IsShellHoldingNotifications() =>
        SHQueryUserNotificationState(out var state) == 0
        && state is UserNotificationState.Busy
            or UserNotificationState.RunningD3DFullScreen
            or UserNotificationState.PresentationMode
            or UserNotificationState.App
            or UserNotificationState.NotPresent
            or UserNotificationState.QuietTime;

    public static int GetWindowProcessId(nint hWnd)
    {
        if (hWnd == nint.Zero) return 0;
        GetWindowThreadProcessId(hWnd, out var processId);
        return (int)processId;
    }

    /// <summary>
    /// Top-level windows in Z-order, topmost first. The callback only appends the handle — no
    /// managed work happens inside the native enumeration.
    /// </summary>
    public static unsafe List<nint> GetTopLevelWindows()
    {
        var handles = new List<nint>();
        var gcHandle = GCHandle.Alloc(handles);
        try { EnumWindows(&CollectHandleCallback, GCHandle.ToIntPtr(gcHandle)); }
        finally { gcHandle.Free(); }
        return handles;
    }

    public static unsafe List<nint> GetChildWindows(nint parent)
    {
        var handles = new List<nint>();
        var gcHandle = GCHandle.Alloc(handles);
        try { EnumChildWindows(parent, &CollectHandleCallback, GCHandle.ToIntPtr(gcHandle)); }
        finally { gcHandle.Free(); }
        return handles;
    }

    [UnmanagedCallersOnly]
    private static int CollectHandleCallback(nint hWnd, nint lParam)
    {
        var handle = GCHandle.FromIntPtr(lParam);
        if (handle.Target is List<nint> list) list.Add(hWnd);
        return 1; // continue enumeration
    }

    /// <summary>
    /// True for a window DWM keeps hidden while it still reports itself visible — windows on another
    /// virtual desktop and suspended Store apps. Without this they count as background windows.
    /// </summary>
    public static bool IsCloaked(nint hWnd) =>
        DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>
    /// Returns true for top-level windows that represent real user-visible apps:
    /// visible, not minimised, not cloaked, has a title, and not a floating tool window.
    /// </summary>
    public static bool IsRealWindow(nint hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;
        if (IsIconic(hWnd)) return false;
        if (string.IsNullOrEmpty(GetWindowTitle(hWnd))) return false;

        var exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0) return false;

        if (IsCloaked(hWnd)) return false;

        if (!GetWindowRect(hWnd, out var rect)) return false;
        return rect.Area > 0;
    }
}
