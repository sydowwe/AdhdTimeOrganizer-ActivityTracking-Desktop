using System.Globalization;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <param name="Kind">Which policy issued the nudge: ids are counted per policy.</param>
internal sealed record NudgeActivation(NudgeKind Kind, int NudgeId, NudgeResponse Response);

/// <summary>What a nudge says and which buttons it has, and how a button press comes back.</summary>
internal static class NudgeToast
{
    /// <summary>One toast per kind: a repeat replaces the one still in the notification centre.</summary>
    public const string Group = "nudges";

    private const string KindArgument = "kind";
    private const string IdArgument = "nudge";
    private const string ActionArgument = "action";

    public static string Tag(NudgeKind kind) => kind.ToString();

    public static AppNotification Build(Nudge nudge)
    {
        var (title, body) = Text(nudge);
        var builder = new AppNotificationBuilder()
            .AddArgument(KindArgument, nudge.Kind.ToString())
            .AddArgument(IdArgument, nudge.Id.ToString(CultureInfo.InvariantCulture))
            .AddText(title)
            .AddText(body);

        if (nudge.Kind == NudgeKind.BreakOver)
        {
            // A reminder stays on screen until answered: the point is to catch someone who drifted off.
            builder.SetScenario(AppNotificationScenario.Reminder)
                .AddButton(Button("I’m back", nudge, NudgeResponse.Dismiss))
                .AddButton(Button("5 more minutes", nudge, NudgeResponse.Snooze));
        }
        else if (nudge.Kind == NudgeKind.Drift)
        {
            builder.SetDuration(AppNotificationDuration.Long)
                .AddButton(Button("Back to it", nudge, NudgeResponse.Dismiss))
                .AddButton(Button("Switch plan", nudge, NudgeResponse.SwitchPlan))
                .AddButton(Button("Snooze 30 min", nudge, NudgeResponse.Snooze))
                .AddButton(Button("Turn off", nudge, NudgeResponse.TurnOff));
        }
        else if (nudge.Kind == NudgeKind.UploadHealth)
        {
            builder.SetDuration(AppNotificationDuration.Long);
            builder.AddButton(nudge.Upload?.Problem == UploadProblem.SignedOut
                ? Button("Sign in", nudge, NudgeResponse.SignIn)
                : Button("Open log folder", nudge, NudgeResponse.OpenLogs));
            builder.AddButton(Button("Turn off these notices", nudge, NudgeResponse.TurnOff));
        }
        else
        {
            builder.SetDuration(AppNotificationDuration.Long)
                .AddButton(Button("Taking a break", nudge, NudgeResponse.TakeBreak))
                .AddButton(Button("5 more minutes", nudge, NudgeResponse.Snooze))
                .AddButton(Button("Not now", nudge, NudgeResponse.NotNow))
                .AddButton(Button("Turn off", nudge, NudgeResponse.TurnOff));
        }

        var notification = builder.BuildNotification();
        notification.Tag = Tag(nudge.Kind);
        notification.Group = Group;
        return notification;
    }

    /// <summary>The button pressed, or null for a click on the toast's body, which only dismisses it.</summary>
    public static NudgeActivation? Parse(IDictionary<string, string> arguments) =>
        arguments.TryGetValue(KindArgument, out var kindText)
        && Enum.TryParse<NudgeKind>(kindText, out var kind)
        && arguments.TryGetValue(IdArgument, out var id)
        && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var nudgeId)
        && arguments.TryGetValue(ActionArgument, out var action)
        && Enum.TryParse<NudgeResponse>(action, out var response)
            ? new NudgeActivation(kind, nudgeId, response)
            : null;

    internal static (string Title, string Body) Text(Nudge nudge) => nudge.Kind switch
    {
        NudgeKind.Stretch => ($"You've been at it for {FormatLength(nudge.Length)}",
            "Stand up, stretch, drink some water?"),
        NudgeKind.AppStretch => ($"{FormatLength(nudge.Length)} straight in {nudge.AppName}",
            "Time to look up from it for a few minutes?"),
        NudgeKind.Drift when nudge.Drift is { } drift =>
            ($"Planned: {drift.PlannedName} (until {TimeZoneInfo.ConvertTimeFromUtc(drift.PlannedEndsUtc, TimeZoneInfo.Local):HH:mm})",
                $"Last {FormatLength(nudge.Length)}: {nudge.AppName}."),
        NudgeKind.UploadHealth when nudge.Upload is { } upload => UploadText(upload),
        _ => ("Break's over", $"That was {FormatLength(nudge.Length)}. Ready to get back to it?")
    };

    /// <summary>What went wrong and what to do about it. Counts and durations only.</summary>
    private static (string Title, string Body) UploadText(UploadNotice upload)
    {
        var waiting = FormatLength(TimeSpan.FromMinutes(upload.PendingWindows));
        return upload.Problem switch
        {
            UploadProblem.SignedOut => ("Activity Tracker is signed out",
                $"{waiting} of tracked time is waiting to be sent. Tracking carries on; sign in to upload it."),
            UploadProblem.Stalled => ($"Nothing has been uploaded for {FormatLength(upload.StalledFor)}",
                $"{waiting} of tracked time is waiting on this PC. Check the connection; the log folder says what the server answered."),
            _ => ($"{FormatLength(TimeSpan.FromMinutes(upload.Dropped))} of tracked time was refused",
                "The server turned those minutes down and they are gone. A tracker older than the server is the usual cause: update it.")
        };
    }

    internal static string FormatLength(TimeSpan length)
    {
        var minutes = (int)Math.Round(length.TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes < 60) return $"{minutes} min";
        return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} m";
    }

    /// <remarks>
    /// The SDK's builder writes a button's label into a single-quoted XML attribute without escaping it, so
    /// "I'm back" made <c>BuildNotification</c> throw ("Required white space was missing") and the nudge never
    /// appeared. Labels use the typographic apostrophe; text elements are escaped and may use either.
    /// </remarks>
    private static AppNotificationButton Button(string label, Nudge nudge, NudgeResponse response) =>
        new AppNotificationButton(label.Replace('\'', '’'))
            .AddArgument(KindArgument, nudge.Kind.ToString())
            .AddArgument(IdArgument, nudge.Id.ToString(CultureInfo.InvariantCulture))
            .AddArgument(ActionArgument, response.ToString());
}
