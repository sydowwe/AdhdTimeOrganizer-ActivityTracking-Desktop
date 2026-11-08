using System.Globalization;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// The capture box's rules for focus and for the tracker, pure: which window it gives the keyboard back to, what the tracker
/// takes to be in front while it is open, and the line it shows about the plan.
/// </summary>
/// <remarks>
/// <b>The box is not a window the user went to.</b> While it is in front, the tracker sees the window it will hand focus back
/// to — the work — so parking a thought is neither a switch away from it nor time of its own. With nothing to hand back to (a
/// tray click while tracking was paused), it sees nothing recorded in front, as it does for the tray's own menu.
/// </remarks>
public static class CaptureFocus
{
    /// <summary>
    /// The window to give focus back to: the first candidate, newest first, that <paramref name="isEligible"/> admits — another
    /// process's real window. The hotkey's candidate is the foreground itself; a tray click's foreground is the tray, so the
    /// recent foregrounds behind it are next. Zero when none is.
    /// </summary>
    public static nint PickReturnTarget(IEnumerable<nint> newestFirst, Func<nint, bool> isEligible) =>
        newestFirst.FirstOrDefault(h => h != nint.Zero && isEligible(h));

    /// <summary>What the tracker takes to be in front: the return target while the box is, else the foreground itself.</summary>
    public static nint StandIn(nint foreground, nint box, nint returnTarget) =>
        box != nint.Zero && foreground == box ? returnTarget : foreground;

    /// <summary>
    /// "Thesis until 11:30 — park it and get back": the planned block now, from the newest minute the server resolved, while it
    /// is as fresh as the drift nudge trusts one. A block already started wins over one merely planned. Null with no fresh
    /// minute or no block covering now — a stale plan line is worse than none.
    /// </summary>
    public static string? PlanLine(ResolvedMinute? last, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (last is null || last.WindowStartUtc < nowUtc - DriftPolicy.FreshFor) return null;

        var block = last.Response.Planned
            .Where(b => b.EndsAtUtc > nowUtc && b.ActivityName.Length > 0)
            .OrderByDescending(b => b.InProgress)
            .ThenBy(b => b.EndsAtUtc)
            .FirstOrDefault();
        if (block is null) return null;

        var until = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(block.EndsAtUtc, DateTimeKind.Utc), zone);
        return $"{block.ActivityName} until {until.ToString("HH:mm", CultureInfo.InvariantCulture)} — park it and get back";
    }
}
