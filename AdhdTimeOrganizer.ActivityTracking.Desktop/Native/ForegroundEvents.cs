using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Native;

/// <summary>
/// Tells the poll loop the moment the window in front changes — another window, or a new title on the same one —
/// with the <see cref="Stopwatch"/> timestamp it happened at. Polling saw that only every few seconds, so a minute
/// with fourteen alt-tabs read like a minute with one and each window's seconds were sampled, not measured.
/// </summary>
/// <remarks>
/// <para>
/// <b>Install and uninstall on the UI thread.</b> An out-of-context WinEvent hook is delivered through the message
/// loop of the thread that set it, and only that thread may remove it. The tracker's <c>Start</c>, <c>Stop</c> and
/// <c>Dispose</c> run there.
/// </para>
/// <para>
/// The callback stays tiny, like the enumeration callbacks: it stamps the time, queues, and wakes the poll loop, which
/// does the managed work (window title, process, privacy) on its own thread. The one other thing it does is move the
/// title-change hook to the new foreground process, so title changes are only delivered for that process — hooked
/// system-wide, every accessible-name change of every app on the machine would be a message on the UI thread.
/// </para>
/// <para>
/// Static because a WinEvent callback carries no context. There is one tracker, so one hook.
/// </para>
/// </remarks>
internal static class ForegroundEvents
{
    /// <summary>Plenty for any real burst; past it events are dropped, and the next tick re-reads the foreground anyway.</summary>
    private const int MaxQueued = 256;

    private static readonly Lock Gate = new();
    private static readonly Queue<long> Pending = new();
    private static readonly SemaphoreSlim Signal = new(0, 1);

    private static nint _foregroundHook;
    private static nint _nameChangeHook;
    private static uint _nameChangePid;

    public static bool IsInstalled => _foregroundHook != nint.Zero;

    /// <summary>Starts listening. A failed install leaves the tracker on polling alone, which still works.</summary>
    public static unsafe bool Install()
    {
        if (IsInstalled) return true;

        lock (Gate) Pending.Clear();
        _foregroundHook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, nint.Zero, &OnEvent, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        if (_foregroundHook == nint.Zero) return false;

        FollowForegroundProcess(Win32.GetForegroundWindow());
        return true;
    }

    public static void Uninstall()
    {
        if (_nameChangeHook != nint.Zero) Win32.UnhookWinEvent(_nameChangeHook);
        if (_foregroundHook != nint.Zero) Win32.UnhookWinEvent(_foregroundHook);
        _nameChangeHook = _foregroundHook = nint.Zero;
        _nameChangePid = 0;

        lock (Gate) Pending.Clear();
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a change; true when there is one to <see cref="TryDrain"/>.
    /// </summary>
    public static Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => Signal.WaitAsync(timeout, ct);

    /// <summary>The earliest queued change since the last drain — the moment the window in front stopped being the old one.</summary>
    public static bool TryDrain(out long earliestTimestamp)
    {
        lock (Gate)
        {
            earliestTimestamp = Pending.Count > 0 ? Pending.Peek() : 0;
            var any = Pending.Count > 0;
            Pending.Clear();
            return any;
        }
    }

    /// <summary>
    /// Catches everything: an exception escaping an <see cref="UnmanagedCallersOnlyAttribute"/> method ends the process —
    /// the tracker, mid-minute, with nothing flushed.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint eventType, nint hWnd, int idObject, int idChild, uint threadId, uint eventTime)
    {
        try
        {
            if (idObject != Win32.OBJID_WINDOW || idChild != Win32.CHILDID_SELF) return;

            if (eventType == Win32.EVENT_SYSTEM_FOREGROUND)
                FollowForegroundProcess(hWnd);
            else if (hWnd != Win32.GetForegroundWindow())
                return; // a title change of a window not in front says nothing about what the user is looking at

            var now = Stopwatch.GetTimestamp();
            lock (Gate)
            {
                if (Pending.Count >= MaxQueued) return;
                Pending.Enqueue(now);
            }

            if (Signal.CurrentCount == 0) Signal.Release();
        }
        catch
        {
            // Already signalled (SemaphoreFullException) or worse; the next poll reads the foreground either way.
        }
    }

    /// <summary>Points the title-change hook at <paramref name="hWnd"/>'s process. Runs on the UI thread, inside the callback or the install.</summary>
    private static unsafe void FollowForegroundProcess(nint hWnd)
    {
        var pid = (uint)Win32.GetWindowProcessId(hWnd);
        if (pid == 0 || pid == _nameChangePid) return;

        if (_nameChangeHook != nint.Zero) Win32.UnhookWinEvent(_nameChangeHook);
        _nameChangeHook = Win32.SetWinEventHook(
            Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE, nint.Zero, &OnEvent, pid, 0, Win32.WINEVENT_OUTOFCONTEXT);
        _nameChangePid = _nameChangeHook != nint.Zero ? pid : 0;
    }
}
