namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

public enum UpdateCheckOutcome
{
    /// <summary>The feed has nothing newer.</summary>
    UpToDate,

    /// <summary>A newer release is downloaded and waits for a restart.</summary>
    Downloaded,

    /// <summary>The feed or the download failed: offline, GitHub throttling, a broken package.</summary>
    Failed
}

/// <summary>
/// <b>Pure</b> update decisions: when to look, and what the tray says. <see cref="UpdateService"/> does the looking.
/// </summary>
/// <remarks>
/// <para>
/// <b>Quiet download, applied at the next start.</b> A newer release is downloaded in the background and never applied
/// under the user: it is applied the next time the tracker starts (usually the next sign-in; see
/// <see cref="ApplyAtStart"/>), and until then the tray offers "Restart to update". Restarting mid-work would cost the minute in progress and pop the tray out of place
/// for nothing a sign-in won't do anyway.
/// </para>
/// <para>
/// <b>How often.</b> GitHub answers anonymous API calls 60 times an hour per address, shared by every app on the
/// network; one look every few hours is nothing next to that, and a failure waits an hour rather than retrying hot.
/// </para>
/// </remarks>
public static class UpdatePolicy
{
    /// <summary>Not at start: sign-in is the busiest moment of the machine, and the tracker restores its session then.</summary>
    public static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>
    /// Downloaded still looks again on schedule: a second release in the meantime replaces the one waiting, so the restart
    /// installs the newest.
    /// </summary>
    public static TimeSpan NextCheckAfter(UpdateCheckOutcome outcome) =>
        outcome == UpdateCheckOutcome.Failed ? RetryAfterFailure : CheckEvery;

    /// <summary>The tray's restart item, or null to hide it.</summary>
    public static string? RestartText(string? pendingVersion) =>
        pendingVersion is null ? null : $"Restart to update to {pendingVersion}";

    /// <summary>
    /// The tray's version line. A build run from source never updates itself, and says so — otherwise "why am I still on
    /// 1.1.0" has no answer.
    /// </summary>
    public static string VersionText(string version, bool installed) =>
        installed ? $"Version {version}" : $"Version {version} (built from source, no updates)";

    /// <summary>
    /// Whether this start applies the downloaded release before the tracker runs. Only the first instance does: Velopack's
    /// updater stops every process running from the install folder, so a second launch (the Start menu clicked while the
    /// tray already runs) applying it would end the running tracker without its exit — the minute in progress unsaved, its
    /// toasts left behind. The second launch just says "already running", and the release waits for the next start or
    /// "Restart to update".
    /// </summary>
    public static bool ApplyAtStart(bool installed, bool firstInstance, string? pendingVersion) =>
        installed && firstInstance && pendingVersion is not null;
}
