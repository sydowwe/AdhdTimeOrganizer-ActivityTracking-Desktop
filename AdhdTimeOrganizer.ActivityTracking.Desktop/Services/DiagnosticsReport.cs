using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <param name="AppVersion">The assembly's informational version. 1.0.0 until the tracker gets real versions.</param>
public sealed record DiagnosticsEnvironment(string AppVersion, string Windows, string Architecture)
{
    public static DiagnosticsEnvironment Current() => new(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        $"{RuntimeInformation.OSDescription} (build {Environment.OSVersion.Version.Build})",
        $"OS {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}");
}

/// <summary>
/// The text "Copy diagnostics" puts on the clipboard, for a bug report. Pure: everything it prints is handed to it.
/// </summary>
/// <remarks>
/// <para>
/// What it must never print: the refresh token, an e-mail address, a window title or path, a rule's pattern. It reads
/// <see cref="AppConfig"/> field by field rather than serializing it — a new field is left out until someone decides it
/// is safe — and rules appear as counts. The log tail is scrubbed too (<see cref="Scrub"/>), because the logs hold no
/// titles by policy but do hold exception messages, and a few of those carry the Windows user's profile path.
/// </para>
/// </remarks>
public static class DiagnosticsReport
{
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);

    /// <param name="serverRules">How many server rules are held; null before the first download.</param>
    /// <param name="logLines">Oldest first. Scrubbed here, so the caller passes them as read.</param>
    /// <param name="userProfile">The Windows profile folder, masked wherever it appears in a log line.</param>
    public static string Format(DiagnosticsEnvironment env, AppConfig config, TrackerStatus status, int? serverRules,
        IReadOnlyList<string> logLines, DateTime nowUtc, string userProfile)
    {
        var text = new StringBuilder();
        text.AppendLine($"Activity Tracker diagnostics — {nowUtc:yyyy-MM-dd HH:mm:ss} UTC");
        text.AppendLine();

        text.AppendLine("== Environment");
        text.AppendLine($"App version: {env.AppVersion}");
        text.AppendLine($"Windows: {env.Windows}");
        text.AppendLine($"Architecture: {env.Architecture}");
        text.AppendLine();

        text.AppendLine("== State");
        text.AppendLine($"Signed in: {(!string.IsNullOrEmpty(config.RefreshToken) ? "yes" : "no")}");
        text.AppendLine($"Tracking: {Describe(status, nowUtc)}");
        text.AppendLine($"Outbox: {status.PendingWindows} min waiting" +
                        (status.OldestPendingUtc is { } oldest ? $", oldest from {oldest:yyyy-MM-dd HH:mm} UTC" : ""));
        text.AppendLine($"Last send: {(status.LastSendResult?.ToString() ?? "none yet")}" +
                        (status.LastSentUtc is { } sent ? $", last stored {sent:yyyy-MM-dd HH:mm:ss} UTC" : ", nothing stored since start"));
        text.AppendLine($"Dropped by the server since start: {status.RejectedWindows} min");
        text.AppendLine($"Poll error: {(status.LastPollError is null ? "none" : "yes, see the log")}");
        text.AppendLine();

        text.AppendLine("== Settings");
        text.AppendLine($"Server: {config.ApiBaseUrl}");
        text.AppendLine($"Web app: {config.WebAppUrl}");
        text.AppendLine($"Poll {config.PollIntervalMs} ms, idle after {config.IdleThresholdSeconds} s, passive up to {config.MaxPassiveIdleMinutes} min");
        text.AppendLine($"Background windows: min {config.MinBackgroundWindowPercent}% area, {config.MaxBackgroundWindowsPerMonitor} per monitor");
        text.AppendLine($"Hyperfocus nudges: {OnOff(config.HyperfocusNudgesEnabled)}, after {config.HyperfocusNudgeMinutes} min, " +
                        $"same app {config.AppHyperfocusNudgeMinutes} min, a break is {config.BreakMinutes} min, break timer {config.BreakTimerMinutes} min");
        text.AppendLine($"Drift nudges: {OnOff(config.DriftNudgesEnabled)}, after {config.DriftNudgeMinutes} min");
        text.AppendLine($"Quiet while: full screen {OnOff(config.QuietWhenFullscreen)}, microphone {OnOff(config.QuietWhenMicrophoneInUse)}");
        text.AppendLine($"Upload notices: {OnOff(config.UploadNoticesEnabled)}");
        text.AppendLine($"Evening review: {OnOff(config.UnmappedReviewEnabled)}");
        text.AppendLine($"Private windows: {OnOff(config.HidePrivateWindows)}, handled as {config.PrivateWindowHandling}");
        text.AppendLine($"Rules: {config.LocalRules.Count} local, {(serverRules?.ToString() ?? "not downloaded yet")} from the server");
        text.AppendLine();

        text.AppendLine($"== Log (last {logLines.Count} lines; profile paths and e-mail addresses masked)");
        foreach (var line in logLines) text.AppendLine(Scrub(line, userProfile));

        return text.ToString();
    }

    /// <summary>Masks the Windows profile folder and anything that looks like an e-mail address.</summary>
    public static string Scrub(string line, string userProfile)
    {
        if (!string.IsNullOrEmpty(userProfile))
            line = line.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                .Replace(userProfile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return Email.Replace(line, "[email]");
    }

    private static string Describe(TrackerStatus status, DateTime nowUtc) =>
        status.PausedUntilUtc is { } until && until > nowUtc
            ? until == DateTime.MaxValue ? "paused until resumed" : $"paused until {until:yyyy-MM-dd HH:mm} UTC"
            : status.IsRunning ? status.IsLocked ? "running, screen locked" : "running"
            : "stopped";

    private static string OnOff(bool value) => value ? "on" : "off";
}

/// <summary>The newest lines of the tracker's own log, for <see cref="DiagnosticsReport"/>.</summary>
public static class DiagnosticsLog
{
    /// <summary>
    /// Up to <paramref name="maxLines"/> lines, oldest first, from the newest daily files. The logger holds today's file
    /// open for writing, so it is read with write sharing allowed — a plain <c>File.ReadAllLines</c> would throw.
    /// A file that cannot be read is skipped.
    /// </summary>
    public static IReadOnlyList<string> Tail(string directory, int maxLines)
    {
        if (!Directory.Exists(directory)) return [];

        var lines = new List<string>();
        foreach (var path in Directory.GetFiles(directory, "log-*.txt").OrderDescending(StringComparer.Ordinal))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var file = new List<string>();
                while (reader.ReadLine() is { } line) file.Add(line);

                lines.InsertRange(0, file.TakeLast(maxLines - lines.Count));
            }
            catch (IOException)
            {
                // Rolled away or locked in a way that refuses even this: the report is better without it than not at all.
            }

            if (lines.Count >= maxLines) break;
        }
        return lines;
    }
}
