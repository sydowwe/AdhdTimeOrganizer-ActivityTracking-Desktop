using System.Runtime.InteropServices;

namespace DesktopActivityTracker.Native;

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
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static partial int GetWindowLong(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    // ── Constants ────────────────────────────────────────────────────────────

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_APPWINDOW = 0x00040000;

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

    public static string? GetForegroundWindowTitle()
    {
        var hWnd = GetForegroundWindow();
        return hWnd == nint.Zero ? null : GetWindowTitle(hWnd);
    }

    public static uint? GetForegroundProcessId()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == nint.Zero) return null;
        GetWindowThreadProcessId(hWnd, out var processId);
        return processId;
    }

    public static int GetIdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return (int)((GetTickCount() - info.dwTime) / 1000);
    }

    /// <summary>
    /// Returns true for top-level windows that represent real user-visible apps:
    /// visible, not minimised, has a title, and not a floating tool window.
    /// </summary>
    public static bool IsRealWindow(nint hWnd)
    {
        if (!IsWindowVisible(hWnd)) return false;
        if (IsIconic(hWnd)) return false;
        if (string.IsNullOrEmpty(GetWindowTitle(hWnd))) return false;

        var exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0) return false;

        if (!GetWindowRect(hWnd, out var rect)) return false;
        return rect.Area > 0;
    }
}
