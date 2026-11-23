namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>What the tray icon shows. Three looks only: at 16 px a fourth would not be told apart.</summary>
public enum TrayIconState
{
    /// <summary>Recording, and nothing the user has to act on.</summary>
    Normal,

    /// <summary>Not recording because the user paused it: the icon greyed, with pause bars.</summary>
    Paused,

    /// <summary>
    /// Recording, but something needs the user: signed out (minutes wait in the outbox) or the poll loop's last pass failed.
    /// The normal icon with an amber dot — signed out still records, so it must not look paused.
    /// </summary>
    Attention
}

public static class TrayIconStates
{
    /// <summary>A pause wins: it is the one state in which nothing is recorded, and the one the user can forget.</summary>
    public static TrayIconState For(TrackerStatus status, bool hasSession, DateTime nowUtc) =>
        status.PausedUntilUtc is { } until && until > nowUtc ? TrayIconState.Paused
        : !hasSession || status.LastPollError is not null ? TrayIconState.Attention
        : TrayIconState.Normal;
}
