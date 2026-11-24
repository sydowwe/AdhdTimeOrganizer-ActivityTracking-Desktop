using System.Globalization;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <param name="Kind">Which policy issued the nudge: ids are counted per policy.</param>
/// <param name="Choice">Which of the away question's answers was pressed; null on every other button.</param>
internal sealed record NudgeActivation(NudgeKind Kind, int NudgeId, NudgeResponse Response, int? Choice = null);

/// <summary>What a nudge says and which buttons it has, and how a button press comes back.</summary>
internal static class NudgeToast
{
    /// <summary>One toast per kind: a repeat replaces the one still in the notification centre.</summary>
    public const string Group = "nudges";

    private const string KindArgument = "kind";
    private const string IdArgument = "nudge";
    private const string ActionArgument = "action";
    private const string ChoiceArgument = "choice";

    /// <summary>An activity's name on a button is cut to this many characters: the toast's buttons share one row.</summary>
    internal const int MaxAnswerLabel = 32;

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
            // Five, the most a toast takes. Capture parks the thought that pulled the user away, then counts as Back to it.
            builder.SetDuration(AppNotificationDuration.Long)
                .AddButton(Button("Back to it", nudge, NudgeResponse.Dismiss))
                .AddButton(Button("Capture a thought", nudge, NudgeResponse.Capture))
                .AddButton(Button("Switch plan", nudge, NudgeResponse.SwitchPlan))
                .AddButton(Button("Snooze 30 min", nudge, NudgeResponse.Snooze))
                .AddButton(Button("Turn off", nudge, NudgeResponse.TurnOff));
        }
        else if (nudge.Kind == NudgeKind.StartRamp)
        {
            // A reminder, like the break-over one: starting is the hard part, and a toast that slides away after a few
            // seconds into the notification centre is a cue easily missed. It stays until answered or withdrawn.
            builder.SetScenario(AppNotificationScenario.Reminder)
                .AddButton(Button("Start now", nudge, NudgeResponse.StartNow));
            if (nudge.StartRamp?.CanDefer != false)
                builder.AddButton(Button("In 2 minutes", nudge, NudgeResponse.Later));
            builder.AddButton(Button("Not this one", nudge, NudgeResponse.NotThisOne));
        }
        else if (nudge.Kind == NudgeKind.TimeChime)
        {
            // A time check, not a nudge: no sound, the short on-screen duration, gone from the notification centre soon
            // after (Expiration, below). The next chime replaces it by its tag.
            builder.MuteAudio()
                .AddButton(Button("Stop for today", nudge, NudgeResponse.StopForToday));
        }
        else if (nudge.Kind == NudgeKind.BlockEnd)
        {
            // Information, not a question: no sound, the ordinary duration, one OK. It leaves the notification centre when
            // the block ends (Expiration, below) — after that "ends in 5 min" is false.
            builder.MuteAudio()
                .AddButton(Button("OK", nudge, NudgeResponse.Dismiss));
        }
        else if (nudge.Kind == NudgeKind.AwayReturn)
        {
            // A question at the return: an ordinary toast, one button per activity (the planned block first), then Something
            // else. Closing it is "skip". It leaves the notification centre once a press would no longer count (Expiration).
            var answers = nudge.Away?.Answers ?? [];
            for (var i = 0; i < answers.Count; i++)
                builder.AddButton(Button(AnswerLabel(answers[i].Name), nudge, NudgeResponse.AwayAnswer, i));
            builder.AddButton(Button("Something else", nudge, NudgeResponse.SomethingElse));
        }
        else if (nudge.Kind == NudgeKind.UploadHealth)
        {
            builder.SetDuration(AppNotificationDuration.Long);
            builder.AddButton(nudge.Upload?.Problem == UploadProblem.SignedOut
                ? Button("Sign in", nudge, NudgeResponse.SignIn)
                : Button("Open log folder", nudge, NudgeResponse.OpenLogs));
            builder.AddButton(Button("Turn off these notices", nudge, NudgeResponse.TurnOff));
        }
        else if (nudge.Kind == NudgeKind.RecordingHealth)
        {
            builder.SetDuration(AppNotificationDuration.Long)
                .AddButton(Button("Open log folder", nudge, NudgeResponse.OpenLogs))
                .AddButton(Button("Turn off these notices", nudge, NudgeResponse.TurnOff));
        }
        else if (nudge.Kind == NudgeKind.PauseReminder)
        {
            builder.SetDuration(AppNotificationDuration.Long)
                .AddButton(Button("Resume", nudge, NudgeResponse.Resume))
                .AddButton(Button("Keep paused", nudge, NudgeResponse.KeepPaused));
        }
        else if (nudge.Kind == NudgeKind.UnmappedReview)
        {
            builder.SetDuration(AppNotificationDuration.Long)
                .AddButton(Button("Review", nudge, NudgeResponse.Review))
                .AddButton(Button("Not today", nudge, NudgeResponse.NotToday));
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
        if (nudge.Kind == NudgeKind.TimeChime)
            notification.Expiration = DateTimeOffset.Now + ChimeSchedule.ToastLifetime;
        if (nudge is { Kind: NudgeKind.BlockEnd, BlockEnd: { } blockEnd } && blockEnd.EndsUtc > DateTime.UtcNow)
            notification.Expiration = new DateTimeOffset(DateTime.SpecifyKind(blockEnd.EndsUtc, DateTimeKind.Utc));
        if (nudge is { Kind: NudgeKind.AwayReturn, Away: { } away } && away.ToUtc + AwayReturnPolicy.AnswerFor > DateTime.UtcNow)
            notification.Expiration = new DateTimeOffset(DateTime.SpecifyKind(away.ToUtc + AwayReturnPolicy.AnswerFor, DateTimeKind.Utc));
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
            ? new NudgeActivation(kind, nudgeId, response,
                arguments.TryGetValue(ChoiceArgument, out var choiceText)
                && int.TryParse(choiceText, NumberStyles.None, CultureInfo.InvariantCulture, out var choice)
                    ? choice
                    : null)
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
        NudgeKind.StartRamp when nudge.StartRamp is { } ramp => StartRampText(ramp),
        NudgeKind.TimeChime => ($"{FormatLength(nudge.Length)} on {nudge.AppName}", "A time check, nothing more."),
        NudgeKind.BlockEnd when nudge.BlockEnd is { } blockEnd => BlockEndText(blockEnd),
        NudgeKind.AwayReturn when nudge.Away is { } away => AwayText(away),
        NudgeKind.UploadHealth when nudge.Upload is { } upload => UploadText(upload),
        NudgeKind.RecordingHealth when nudge.Recording is { } recording => RecordingText(recording),
        NudgeKind.UnmappedReview when nudge.Review is { } review => UnmappedReviewPolicy.Text(review),
        NudgeKind.PauseReminder => ($"Tracking has been paused for {FormatPause(nudge.Length)}",
            "Nothing on this PC is recorded until you resume."),
        _ =>("Break's over", $"That was {FormatLength(nudge.Length)}. Ready to get back to it?")
    };

    /// <summary>
    /// "Thesis starts now (until 11:30)" with the block's length, or — sat down late — "Thesis started 20 min ago" with
    /// what is left. The repeat "In 2 minutes" asked for says so, and no more.
    /// </summary>
    private static (string Title, string Body) StartRampText(StartRampDetails ramp)
    {
        var until = TimeZoneInfo.ConvertTimeFromUtc(ramp.EndsUtc, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture);
        var isLate = ramp.LateBy >= TimeSpan.FromMinutes(1);
        var title = isLate
            ? $"{ramp.PlannedName} started {FormatLength(ramp.LateBy)} ago (until {until})"
            : $"{ramp.PlannedName} starts now (until {until})";

        var body = !ramp.CanDefer ? "Ready now? One click starts the timer."
            : isLate ? $"{FormatLength(ramp.EndsUtc - ramp.StartsUtc - ramp.LateBy)} left. Start now?"
            : $"{FormatLength(ramp.EndsUtc - ramp.StartsUtc)} planned. Start the timer?";
        return (title, body);
    }

    /// <summary>
    /// "Thesis ends in 5 min" over "Next: Email at 11:30.", "Nothing planned after." or — the next block overlaps this one, so
    /// it isn't what follows — a line that doesn't guess.
    /// </summary>
    private static (string Title, string Body) BlockEndText(BlockEndDetails blockEnd)
    {
        var title = $"{blockEnd.PlannedName} ends in {FormatLength(blockEnd.Left)}";
        var body = blockEnd switch
        {
            { After: BlockEndAfter.Next, NextName: { } name, NextStartsUtc: { } startsUtc } =>
                $"Next: {name} at {TimeZoneInfo.ConvertTimeFromUtc(startsUtc, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.InvariantCulture)}.",
            { After: BlockEndAfter.Nothing } => "Nothing planned after.",
            _ => "Time to wrap up the thought."
        };
        return (title, body);
    }

    /// <summary>
    /// "Away 47 min — what was it?" over the span on the local clock, then what the server knew — "20 min on the phone: YouTube,
    /// Instagram" from a minute up — and, when a planned block covered it, its name: the planned answer is the first button,
    /// and the line says why it is there.
    /// </summary>
    private static (string Title, string Body) AwayText(AwayDetails away)
    {
        static string Local(DateTime utc) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local)
                .ToString("HH:mm", CultureInfo.InvariantCulture);

        var parts = new List<string> { $"{Local(away.FromUtc)}–{Local(away.ToUtc)}" };
        if (away.Known is { PhoneSeconds: >= 60 } known)
            parts.Add($"{FormatLength(TimeSpan.FromSeconds(known.PhoneSeconds))} on the phone"
                      + (known.PhoneApps.Count > 0 ? ": " + string.Join(", ", known.PhoneApps.Select(a => a.Name)) : ""));
        if (away.Answers.FirstOrDefault(a => a.IsPlanned) is { } planned)
            parts.Add($"planned then: {planned.Name}");

        return ($"Away {FormatLength(away.ToUtc - away.FromUtc)} — what was it?",
            string.Join(" · ", parts) + ". One click files it in your history.");
    }

    /// <summary>
    /// An activity's name as a button label. The SDK writes a label into a single-quoted XML attribute unescaped (see
    /// <see cref="Button"/>), so the markup characters a name may hold are escaped here, and a long name is cut.
    /// </summary>
    internal static string AnswerLabel(string name)
    {
        var label = name.Trim();
        if (label.Length > MaxAnswerLabel) label = label[..(MaxAnswerLabel - 1)].TrimEnd() + "…";
        return label.Length == 0 ? "?" : label.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    /// <summary>What went wrong and what to do about it. Counts and durations only.</summary>
    private static (string Title, string Body) UploadText(UploadNotice upload)
    {
        var waiting = FormatLength(TimeSpan.FromMinutes(upload.PendingWindows));
        return upload.Problem switch
        {
            UploadProblem.SignedOut => ("Activity Tracker is signed out",
                $"{waiting} of tracked time is waiting to be sent. Tracking carries on; sign in to upload it."),
            UploadProblem.Stalled => ($"Nothing has been uploaded for {FormatLength(upload.StalledFor)}",
                $"{waiting} of tracked time is waiting on this PC. " + upload.StatusCode switch
                {
                    403 => "The server refuses this account (403): check its access in the web app.",
                    null => "Check the connection; the log folder says what the server answered.",
                    var code => $"The server answered {code}; the log folder says more."
                }),
            _ => ($"{FormatLength(TimeSpan.FromMinutes(upload.Dropped))} of tracked time was refused",
                "The server turned those minutes down and they are gone. A tracker older than the server is the usual cause: update it.")
        };
    }

    /// <summary>What is broken and what to do about it. Durations only: the log says the rest.</summary>
    private static (string Title, string Body) RecordingText(RecordingNotice recording) => recording.Problem switch
    {
        RecordingProblem.PollFailing => ("Activity Tracker can’t record this PC",
            $"Reading the screen has failed for {FormatLength(recording.For)}, so nothing is being recorded. The log folder says why; restarting the tracker may help."),
        RecordingProblem.PollStopped => ("Activity Tracker has stopped recording",
            $"It hasn’t looked at the screen for {FormatLength(recording.For)}, though you are at the computer. Restart the tracker; the log folder says what happened."),
        _ => ("Activity Tracker isn’t storing anything",
            $"{FormatLength(recording.For)} at the computer and not one minute stored. The log folder says why; restarting the tracker may help.")
    };

    /// <summary>"4 h", "47 h", then "3 days": whole units, rounded down — a pause is never younger than it says.</summary>
    internal static string FormatPause(TimeSpan length)
    {
        var hours = Math.Max(0, (int)length.TotalHours);
        if (hours < 48) return $"{hours} h";
        return $"{hours / 24} days";
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
    private static AppNotificationButton Button(string label, Nudge nudge, NudgeResponse response, int? choice = null)
    {
        var button = new AppNotificationButton(label.Replace('\'', '’'))
            .AddArgument(KindArgument, nudge.Kind.ToString())
            .AddArgument(IdArgument, nudge.Id.ToString(CultureInfo.InvariantCulture))
            .AddArgument(ActionArgument, response.ToString());
        return choice is { } i ? button.AddArgument(ChoiceArgument, i.ToString(CultureInfo.InvariantCulture)) : button;
    }
}
