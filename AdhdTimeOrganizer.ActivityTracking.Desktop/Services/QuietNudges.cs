namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>The tray's "Quiet nudges for" choices.</summary>
public enum QuietChoice { ThirtyMinutes, OneHour, UntilTomorrowMorning, UntilTurnedBackOn }

/// <summary>
/// "Quiet nudges for…" (prompt 25): an hour without toasts that keeps recording, where a pause would lose the hour's data.
/// Pure — no clock, no config, no toast — like <see cref="UploadHealthPolicy"/>: the caller passes the time and the saved end
/// (<c>AppConfig.NudgesQuietUntilUtc</c>, <see cref="DateTime.MaxValue"/> for "until I turn them back on").
/// </summary>
/// <remarks>
/// <para>
/// It is one more quiet condition, with the rules the others already follow (<c>NudgeService.IsQuiet</c>): a due start ramp,
/// hyperfocus or drift nudge is <b>held</b>, and shows when the quiet ends only if it is still true then — each family
/// checks that itself (the stretch still running, still off plan in a block not over, ≥ 10 min of the block left), so no
/// staleness limit is added here (decided 2026-11-23). A time-chime mark is skipped. The evening review is held too, inside
/// its own 3 h window, and missed past it (decided 2026-11-23).
/// </para>
/// <para>
/// Not quieted: the break-over toast (the user set it), the upload and recording notices and the "still paused" reminder —
/// those say the record itself is at risk.
/// </para>
/// </remarks>
public static class QuietNudges
{
    /// <summary>"Until tomorrow morning" ends at this local time — the same morning a pause ends at.</summary>
    internal static readonly TimeSpan MorningAt = TimeSpan.FromHours(6);

    public static bool IsOn(DateTime? untilUtc, DateTime nowUtc) => untilUtc is { } until && until > nowUtc;

    /// <summary>A saved end that has passed: the tray clears it, so the config says "not quiet" again.</summary>
    public static bool HasEnded(DateTime? untilUtc, DateTime nowUtc) => untilUtc is { } until && until <= nowUtc;

    /// <summary>When a quiet period chosen at <paramref name="utcNow"/> ends; <see cref="DateTime.MaxValue"/> for "until I turn them back on".</summary>
    public static DateTime Until(QuietChoice choice, DateTime utcNow, TimeZoneInfo zone) => choice switch
    {
        QuietChoice.ThirtyMinutes => utcNow.AddMinutes(30),
        QuietChoice.OneHour => utcNow.AddHours(1),
        QuietChoice.UntilTomorrowMorning => NextMorningUtc(utcNow, zone),
        _ => DateTime.MaxValue
    };

    /// <summary>The next <see cref="MorningAt"/> on the user's clock: chosen tonight it is tomorrow's, at 02:00 it is today's.</summary>
    public static DateTime NextMorningUtc(DateTime utcNow, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var morning = local.Date + MorningAt;
        if (morning <= local) morning = morning.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(morning, DateTimeKind.Unspecified), zone);
    }

    /// <summary>"Nudges quiet until 14:30" / "until tomorrow 06:00" / "until turned back on"; null when not quiet.</summary>
    public static string? Describe(DateTime? untilUtc, DateTime nowUtc, TimeZoneInfo zone)
    {
        if (!IsOn(untilUtc, nowUtc)) return null;

        var until = untilUtc!.Value;
        if (until == DateTime.MaxValue) return "Nudges quiet until turned back on";

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(until, DateTimeKind.Utc), zone);
        var today = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date;
        var day = local.Date == today ? "" : local.Date == today.AddDays(1) ? "tomorrow " : $"{local:ddd} ";
        return $"Nudges quiet until {day}{local:HH:mm}";
    }
}
