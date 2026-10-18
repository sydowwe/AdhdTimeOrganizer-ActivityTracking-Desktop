using System.Diagnostics;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Native;

internal sealed record ProcessInfo(string ProcessName, string? ExecutablePath, string? ProductName);

/// <summary>
/// Name, executable path and product name per process, resolved once per process lifetime.
/// </summary>
/// <remarks>
/// <para>
/// The path comes from <c>QueryFullProcessImageName</c> on a <c>PROCESS_QUERY_LIMITED_INFORMATION</c>
/// handle, which a normal user may open even on an elevated process. <c>Process.MainModule</c> needs full
/// read access, so it threw for every elevated window (admin terminal, installer, IDE run as admin) and the
/// entry went out with no path and no product name. It also enumerates every loaded module, every poll.
/// </para>
/// <para>
/// A pid is reused once its process exits, so each entry is keyed on the pid <b>and</b> the process
/// creation time — the cheap call that is still made on every lookup.
/// </para>
/// </remarks>
internal static class ProcessInfoCache
{
    private const int MaxEntries = 2048;
    private static readonly TimeSpan FallbackTtl = TimeSpan.FromMinutes(1);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<int, (long CreationTime, DateTime CachedAtUtc, ProcessInfo Info)> ByPid = new();
    private static readonly Dictionary<string, string?> ProductNameByPath = new(StringComparer.OrdinalIgnoreCase);

    public static ProcessInfo? Get(int pid)
    {
        if (pid <= 0) return null;

        var handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == nint.Zero)
            return GetWithoutHandle(pid);

        try
        {
            Win32.GetProcessTimes(handle, out var creationTime, out _, out _, out _);

            lock (Gate)
            {
                if (ByPid.TryGetValue(pid, out var cached) && cached.CreationTime == creationTime)
                    return cached.Info;
            }

            var path = QueryImagePath(handle);
            var info = path is not null
                ? new ProcessInfo(ProcessNameFromPath(path), path, GetProductName(path))
                : FromProcessObject(pid);
            if (info is null) return null;

            Store(pid, creationTime, info);
            return info;
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    /// <summary>
    /// The process that owns <paramref name="hWnd"/>'s content. A Store (UWP) app's top-level window
    /// belongs to <c>ApplicationFrameHost</c>; the app itself owns a child window, so Settings, Calculator,
    /// Photos and every Store app used to be recorded as the same "ApplicationFrameHost".
    /// </summary>
    public static ProcessInfo? GetForWindow(nint hWnd, out int pid)
    {
        pid = Win32.GetWindowProcessId(hWnd);
        var info = Get(pid);
        if (info is null || !info.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            return info;

        foreach (var child in Win32.GetChildWindows(hWnd))
        {
            var childPid = Win32.GetWindowProcessId(child);
            if (childPid == 0 || childPid == pid) continue;

            var childInfo = Get(childPid);
            if (childInfo is null) continue;

            pid = childPid;
            return childInfo;
        }

        return info;
    }

    /// <summary>
    /// <c>Process.ProcessName</c> drops a trailing ".exe" and nothing else; this matches it so process
    /// names stay identical to what the agent reported before, which the user's mapping rules match on.
    /// </summary>
    internal static string ProcessNameFromPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
    }

    private static ProcessInfo? GetWithoutHandle(int pid)
    {
        // Protected and system processes refuse even a limited handle. Process.GetProcessById snapshots
        // every process on the machine, so its answer is cached for a minute rather than per lifetime
        // (no creation time to key on).
        lock (Gate)
        {
            if (ByPid.TryGetValue(pid, out var cached) && cached.CreationTime == 0
                && DateTime.UtcNow - cached.CachedAtUtc < FallbackTtl)
                return cached.Info;
        }

        var info = FromProcessObject(pid);
        if (info is not null) Store(pid, 0, info);
        return info;
    }

    private static ProcessInfo? FromProcessObject(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new ProcessInfo(process.ProcessName, null, null);
        }
        catch
        {
            return null; // exited
        }
    }

    private static void Store(int pid, long creationTime, ProcessInfo info)
    {
        lock (Gate)
        {
            if (ByPid.Count >= MaxEntries) ByPid.Clear();
            ByPid[pid] = (creationTime, DateTime.UtcNow, info);
        }
    }

    private static string? QueryImagePath(nint handle)
    {
        var buffer = new char[1024];
        var size = (uint)buffer.Length;
        return Win32.QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0
            ? new string(buffer, 0, (int)size)
            : null;
    }

    private static string? GetProductName(string path)
    {
        lock (Gate)
        {
            if (ProductNameByPath.TryGetValue(path, out var cached)) return cached;
        }

        string? name = null;
        try { name = FileVersionInfo.GetVersionInfo(path).ProductName; }
        catch { /* access denied or file gone */ }

        if (string.IsNullOrWhiteSpace(name)) name = null;

        lock (Gate) ProductNameByPath[path] = name;
        return name;
    }
}
