namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Native;

/// <summary>
/// The open capture box and the window it hands focus back to, for the poll loop: while the box is in front, the tracker
/// credits that window instead (<see cref="Services.CaptureFocus.StandIn"/>). Set and cleared on the UI thread, read on the
/// poll loop's; zero when no box is open.
/// </summary>
internal static class CaptureBoxHandles
{
    private static long _box;
    private static long _returnTo;

    public static nint Box => (nint)Interlocked.Read(ref _box);

    public static nint ReturnTo => (nint)Interlocked.Read(ref _returnTo);

    /// <summary>Before the box can come to the front, so the first foreground event already sees it.</summary>
    public static void Open(nint box, nint returnTo)
    {
        Interlocked.Exchange(ref _returnTo, returnTo);
        Interlocked.Exchange(ref _box, box);
    }

    public static void Close()
    {
        Interlocked.Exchange(ref _box, 0);
        Interlocked.Exchange(ref _returnTo, 0);
    }
}
