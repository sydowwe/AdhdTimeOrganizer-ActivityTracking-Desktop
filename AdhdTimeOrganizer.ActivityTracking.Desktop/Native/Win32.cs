using System.Runtime.InteropServices;
using System.Text;

namespace DesktopActivityTracker.Native;

internal static partial class Win32
{
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    /// <summary>
    /// Gets the title of the foreground window.
    /// </summary>
    public static string? GetForegroundWindowTitle()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == nint.Zero) return null;

        var buffer = new char[512];
        var length = GetWindowText(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    /// <summary>
    /// Gets the process ID of the foreground window.
    /// </summary>
    public static uint? GetForegroundProcessId()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == nint.Zero) return null;

        GetWindowThreadProcessId(hWnd, out var processId);
        return processId;
    }

    /// <summary>
    /// Gets the number of seconds since the last user input (mouse/keyboard).
    /// </summary>
    public static int GetIdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;

        return (int)((GetTickCount() - info.dwTime) / 1000);
    }
}
