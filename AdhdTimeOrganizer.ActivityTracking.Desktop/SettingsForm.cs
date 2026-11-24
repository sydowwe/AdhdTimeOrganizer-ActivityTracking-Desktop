using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop;

/// <summary>
/// Edits the tracker's settings in place on <see cref="AppConfig"/>. The caller saves and applies them.
/// Ignore and hide-title rules live in <see cref="PrivacyForm"/>, which the form links to.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly TextBox _apiUrlBox;
    private readonly TextBox _webAppUrlBox;
    private readonly NumericUpDown _pollIntervalBox;
    private readonly NumericUpDown _idleThresholdBox;
    private readonly NumericUpDown _passiveIdleBox;
    private readonly NumericUpDown _minBackgroundBox;
    private readonly NumericUpDown _maxBackgroundBox;
    private readonly CheckBox _nudgesEnabledBox;
    private readonly NumericUpDown _nudgeAfterBox;
    private readonly NumericUpDown _appNudgeAfterBox;
    private readonly NumericUpDown _breakLengthBox;
    private readonly NumericUpDown _breakTimerBox;
    private readonly CheckBox _quietFullscreenBox;
    private readonly CheckBox _quietMicrophoneBox;
    private readonly CheckBox _driftEnabledBox;
    private readonly NumericUpDown _driftAfterBox;
    private readonly CheckBox _startRampBox;
    private readonly ComboBox _startRampBlocksBox;
    private readonly CheckBox _blockEndBox;
    private readonly ComboBox _blockEndBlocksBox;
    private readonly CheckBox _uploadNoticesBox;
    private readonly CheckBox _unmappedReviewBox;
    private readonly CheckBox _pauseRemindersBox;
    private readonly CheckBox _chimeEnabledBox;
    private readonly ComboBox _chimeIntervalBox;
    private readonly CheckBox _chimeDistractionsBox;
    private readonly CheckedListBox _chimeTargetsBox;
    private readonly TextBox _captureHotkeyBox;
    private readonly CheckBox _awayBox;
    private readonly ComboBox[] _awayAnswerBoxes;
    private readonly Label _awayAnswersHint;
    private readonly Func<CancellationToken, Task<TrackerRead<TrackerActivities>>>? _loadActivities;
    private readonly CancellationTokenSource _closing = new();
    private readonly IReadOnlyList<ChimeCandidate> _chimeCandidates;
    private readonly Label _errorLabel;
    private readonly TabControl _tabs;

    /// <param name="chimeCandidates">The lines the time chime can be set on (<see cref="ChimeChoices.Candidates"/>).</param>
    /// <param name="loadActivities">
    /// The user's activities, for the away answers (<c>ApiClient.FetchActivitiesAsync</c>); fetched when the form opens, so it
    /// opens at once offline. Null offers only the answers already chosen.
    /// </param>
    public SettingsForm(AppConfig config, IReadOnlyList<ChimeCandidate>? chimeCandidates = null,
        Func<CancellationToken, Task<TrackerRead<TrackerActivities>>>? loadActivities = null)
    {
        _config = config;
        _chimeCandidates = chimeCandidates ?? [];
        _loadActivities = loadActivities;

        Text = "Activity Tracker - Settings";
        // Sizable and scrolling, never auto-sized: the rows outgrew a laptop screen, and an auto-sized dialog put Save
        // below its bottom edge. Each tab scrolls; the error line and the buttons stay in a footer (FitToScreen).
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(12, 12, 12, 0);

        // The switches people change first; the developer settings last.
        _tabs = new TabControl { Dock = DockStyle.Fill };
        var general = AddPage("General");
        var nudges = AddPage("Nudges");
        var chimePage = AddPage("Time chime");
        var recording = AddPage("Recording");
        var advanced = AddPage("Advanced");

        // General: what the tracker tells you outside the nudges, and the capture hotkey.
        _startRampBox = new CheckBox { Text = "On", Checked = config.StartRampEnabled, AutoSize = true };
        AddRow(general, "Start ramp:", _startRampBox,
            "When a planned block starts and you are at the computer: one notice to start its timer. Needs no drift nudges.");

        // In the order of RampBlocks, so the selected index is the setting.
        _startRampBlocksBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        _startRampBlocksBox.Items.AddRange(["Every block", "All but Optional", "Critical only"]);
        _startRampBlocksBox.SelectedIndex = (int)config.StartRampBlocks;
        AddRow(general, "Start ramp asks about:", _startRampBlocksBox, "By the importance a block has in your planner.");

        _blockEndBox = new CheckBox { Text = "On", Checked = config.BlockEndEnabled, AutoSize = true };
        AddRow(general, "Block-end &warning:", _blockEndBox,
            "Five minutes before a block you are working on ends: a silent notice with what comes next. Blocks under 20 minutes get none.");

        _blockEndBlocksBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        _blockEndBlocksBox.Items.AddRange(["Every block", "All but Optional", "Critical only"]);
        _blockEndBlocksBox.SelectedIndex = (int)config.BlockEndBlocks;
        AddRow(general, "Block-end warning &covers:", _blockEndBlocksBox, "Its own choice: the start ramp's doesn't change it.");

        _awayBox = new CheckBox { Text = "On", Checked = config.AwayReturnEnabled, AutoSize = true };
        AddRow(general, "Away &question:", _awayBox,
            "Back after 15 minutes to 4 hours away: one notice asking what it was. One click files it in your history; closing it skips.");

        _awayAnswerBoxes = [AwayAnswerBox(config.AwayAnswers.ElementAtOrDefault(0)), AwayAnswerBox(config.AwayAnswers.ElementAtOrDefault(1))];
        AddRow(general, "Away answer &1:", _awayAnswerBoxes[0], null);
        _awayAnswersHint = AddRow(general, "Away answer &2:", _awayAnswerBoxes[1],
            "Offered besides the planned block, like a Rest or Meetings activity. Loading your activities…")!;

        _uploadNoticesBox = new CheckBox { Text = "On", Checked = config.UploadNoticesEnabled, AutoSize = true };
        AddRow(general, "Upload problem notices:", _uploadNoticesBox,
            "When your session ended, uploads have been stuck for a day, or the server refused minutes. Not a nudge: the quiet conditions don't apply.");

        _unmappedReviewBox = new CheckBox { Text = "On", Checked = config.UnmappedReviewEnabled, AutoSize = true };
        AddRow(general, "Evening review of unmapped time:", _unmappedReviewBox,
            "At your evening-shutdown time: one notice with how much of today on this PC no rule maps, if it is worth a look. Needs no drift nudges.");

        _pauseRemindersBox = new CheckBox { Text = "On", Checked = config.PauseRemindersEnabled, AutoSize = true };
        AddRow(general, "Still-paused reminder:", _pauseRemindersBox,
            "While tracking is paused \"until I resume\": a notice after 3 hours, then at most every 6, only while you are at the computer. A timed pause gets none.");

        _captureHotkeyBox = new TextBox { Text = config.CaptureHotkey, Width = 160 };
        AddRow(general, "Capture hotkey:", _captureHotkeyBox,
            $"Opens a one-line box that parks a thought on your capture list. Like {HotkeyChord.Default} or Ctrl+Shift+F9; empty turns it off. The tray menu's \"Capture a thought…\" works either way.");

        // Nudges: hyperfocus, drift, and when every nudge holds back.
        _nudgesEnabledBox = new CheckBox { Text = "On", Checked = config.HyperfocusNudgesEnabled, AutoSize = true };
        _nudgeAfterBox = Number(config.HyperfocusNudgeMinutes, ConfigLimits.HyperfocusNudgeMinutes, increment: 15);
        _appNudgeAfterBox = Number(config.AppHyperfocusNudgeMinutes, ConfigLimits.AppHyperfocusNudgeMinutes, increment: 15);
        _breakLengthBox = Number(config.BreakMinutes, ConfigLimits.BreakMinutes);
        _breakTimerBox = Number(config.BreakTimerMinutes, ConfigLimits.BreakTimerMinutes);
        _quietFullscreenBox = new CheckBox { Text = "Full screen or presenting", Checked = config.QuietWhenFullscreen, AutoSize = true };
        _quietMicrophoneBox = new CheckBox { Text = "Microphone in use (a call)", Checked = config.QuietWhenMicrophoneInUse, AutoSize = true };

        AddRow(nudges, "Hyperfocus nudges:", _nudgesEnabledBox, null);
        AddRow(nudges, "Nudge after (min):", _nudgeAfterBox, "At the computer this long without a break.");
        AddRow(nudges, "Same app, nudge after (min):", _appNudgeAfterBox,
            "In one app this long without a break from it. 0 turns it off.");
        AddRow(nudges, "A break is away for (min):", _breakLengthBox,
            "No input, no sound in the foreground and no call for this long ends a stretch.");
        AddRow(nudges, "Break timer (min):", _breakTimerBox, "How long \"Taking a break\" lasts before the back-to-it nudge.");

        _driftEnabledBox = new CheckBox { Text = "On", Checked = config.DriftNudgesEnabled, AutoSize = true };
        _driftAfterBox = Number(config.DriftNudgeMinutes, ConfigLimits.DriftNudgeMinutes, increment: 5);

        AddRow(nudges, "Drift nudges:", _driftEnabledBox,
            "When your planner says one thing and your apps another. Apps without a mapping rule don't count either way.");
        AddRow(nudges, "Off plan, nudge after (min):", _driftAfterBox, "At most once per planned block.");

        AddRow(nudges, "Stay quiet while:", _quietFullscreenBox, null);
        AddRow(nudges, "", _quietMicrophoneBox, null);

        // Time chime.
        var chime = ChimeSettings.From(config);
        _chimeEnabledBox = new CheckBox { Text = "On", Checked = config.TimeChimeEnabled, AutoSize = true };
        AddRow(chimePage, "Time chime:", _chimeEnabledBox,
            "A silent \"15 min on YouTube\" now and then, while you stay on something you chose. Quiet inside a planned block while drift nudges are on.");

        _chimeIntervalBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        _chimeIntervalBox.Items.AddRange(ChimeSettings.IntervalChoices.Select(m => (object)$"{m} min").ToArray());
        _chimeIntervalBox.SelectedIndex = Array.IndexOf(ChimeSettings.IntervalChoices, (int)chime.Interval.TotalMinutes);
        AddRow(chimePage, "Chime every:", _chimeIntervalBox, "Of continuous time. More than 3 minutes on something else starts it over.");

        _chimeDistractionsBox = new CheckBox { Text = "Chime my distractions", Checked = config.TimeChimeDistractions, AutoSize = true };
        AddRow(chimePage, "", _chimeDistractionsBox, "The activities and categories you marked a distraction in the web app.");

        _chimeTargetsBox = new CheckedListBox { CheckOnClick = true, Width = 260, Height = 110, IntegralHeight = false };
        foreach (var candidate in _chimeCandidates)
            _chimeTargetsBox.Items.Add(candidate.IsDistraction ? $"{candidate.Name} (distraction)" : candidate.Name,
                chime.Chimes(candidate.Key, candidate.IsDistraction));
        _chimeDistractionsBox.CheckedChanged += (_, _) => FollowDistractions(_chimeDistractionsBox.Checked);
        AddRow(chimePage, "Chime on:", _chimeTargetsBox,
            _chimeCandidates.Count == 0 ? "Nothing sent today yet: what you use today appears here." : "Today's activities, your distractions and earlier choices.");

        // Recording: what counts as being at the computer, which background windows count, what is kept private.
        _apiUrlBox = new TextBox { Text = config.ApiBaseUrl, Width = 260 };
        _webAppUrlBox = new TextBox { Text = config.WebAppUrl, Width = 260 };
        _pollIntervalBox = Number(config.PollIntervalMs, ConfigLimits.PollIntervalMs, increment: 250);
        _idleThresholdBox = Number(config.IdleThresholdSeconds, ConfigLimits.IdleThresholdSeconds);
        _passiveIdleBox = Number(config.MaxPassiveIdleMinutes, ConfigLimits.MaxPassiveIdleMinutes, increment: 10);
        _minBackgroundBox = Number(config.MinBackgroundWindowPercent, ConfigLimits.MinBackgroundWindowPercent);
        _maxBackgroundBox = Number(config.MaxBackgroundWindowsPerMonitor, ConfigLimits.MaxBackgroundWindowsPerMonitor);

        // The idle hint names a figure that depends on the poll interval, on the Advanced tab.
        var idleHint = AddRow(recording, "Idle after (s):", _idleThresholdBox,
            IdleHint(config.IdleThresholdSeconds, config.PollIntervalMs))!;
        void RefreshIdleHint(object? sender, EventArgs e) =>
            idleHint.Text = IdleHint((int)_idleThresholdBox.Value, (int)_pollIntervalBox.Value);
        _idleThresholdBox.ValueChanged += RefreshIdleHint;
        _pollIntervalBox.ValueChanged += RefreshIdleHint;
        AddRow(recording, "Watching / listening (min):", _passiveIdleBox,
            "How long a foreground window playing sound keeps you present without input. 0 turns it off.");
        AddRow(recording, "Background window min area (%):", _minBackgroundBox, null);
        AddRow(recording, "Background windows per monitor:", _maxBackgroundBox, null);

        var ignoreRulesLink = new LinkLabel { Text = "Ignore rules and private windows…", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
        ignoreRulesLink.LinkClicked += OnEditPrivacy;
        recording.Controls.Add(ignoreRulesLink);
        recording.SetColumnSpan(ignoreRulesLink, 2);

        // Advanced: where the server is, and how often the rest of the desktop is looked at.
        AddRow(advanced, "Server URL:", _apiUrlBox, "Changing it signs you out.");
        AddRow(advanced, "Web app URL:", _webAppUrlBox, "Where the planner and your mappings open.");
        AddRow(advanced, "Poll interval (ms):", _pollIntervalBox,
            "Idleness, background windows, sound. The window in front is followed as it changes.");

        _errorLabel = new Label { AutoSize = true, ForeColor = Color.Red, MaximumSize = new Size(560, 0) };

        var okButton = new Button { Text = "Save", AutoSize = true };
        okButton.Click += OnSave;
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right };
        buttons.Controls.AddRange([cancelButton, okButton]);

        // The error line above the buttons, both always in view, under every tab.
        var footer = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 6, 0, 12)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.Controls.Add(_errorLabel);
        footer.Controls.Add(buttons);

        // The Fill control first: docking runs from the last added, so the footer takes the bottom and the tabs the rest.
        Controls.Add(_tabs);
        Controls.Add(footer);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        Load += (_, _) => FitToScreen(footer);
        Load += async (_, _) => await LoadActivitiesAsync();
        FormClosed += (_, _) => _closing.Cancel();
    }

    /// <summary>An away answer: an activity by id, shown by name. <see cref="None"/> is "no answer here".</summary>
    private sealed record ActivityItem(long Id, string Name)
    {
        public static readonly ActivityItem None = new(0, "(none)");
        public override string ToString() => Name;
    }

    /// <summary>"(none)" and the answer already chosen, until the server's list arrives.</summary>
    private static ComboBox AwayAnswerBox(AwayChoice? chosen)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        box.Items.Add(ActivityItem.None);
        if (chosen is { ActivityId: > 0 }) box.Items.Add(new ActivityItem(chosen.ActivityId, chosen.Name));
        box.SelectedIndex = box.Items.Count - 1;
        return box;
    }

    /// <summary>
    /// Fills the away answer lists from the server, keeping what each one has selected. Offline, signed out or an older server,
    /// the lists keep the answers already chosen and the hint says why there are no others. Never throws: it runs from Load.
    /// </summary>
    private async Task LoadActivitiesAsync()
    {
        const string hint = "Offered besides the planned block, like a Rest or Meetings activity.";
        if (_loadActivities is null)
        {
            _awayAnswersHint.Text = hint;
            return;
        }

        try
        {
            var read = await _loadActivities(_closing.Token);
            if (IsDisposed) return;
            if (read is not { Status: CoverageFetch.Fetched, Value: { } fetched })
            {
                _awayAnswersHint.Text = hint + (read.Status == CoverageFetch.NotSupported
                    ? " The server is too old to list your activities."
                    : " Your activities couldn’t be loaded: sign in or check the connection, then open Settings again.");
                return;
            }

            foreach (var box in _awayAnswerBoxes)
            {
                var selected = (box.SelectedItem as ActivityItem)?.Id ?? 0;
                var items = new List<ActivityItem> { ActivityItem.None };
                items.AddRange(fetched.Activities.Select(a => new ActivityItem(a.Id, a.Name)));
                // A chosen activity the list no longer has (archived since) stays chosen until changed.
                if (selected > 0 && items.All(i => i.Id != selected) && box.SelectedItem is ActivityItem kept) items.Add(kept);

                box.BeginUpdate();
                box.Items.Clear();
                box.Items.AddRange(items.ToArray<object>());
                box.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Id == selected));
                box.EndUpdate();
            }
            _awayAnswersHint.Text = hint;
        }
        catch (OperationCanceledException)
        {
            // The form closed first.
        }
        catch (Exception ex)
        {
            Serilog.Log.ForContext<SettingsForm>().Warning(ex, "Could not list activities for the away answers");
            if (!IsDisposed) _awayAnswersHint.Text = hint;
        }
    }

    /// <summary>A tab whose rows scroll on their own when the form is shorter than they are.</summary>
    private TableLayoutPanel AddPage(string title)
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = Point.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(9), UseVisualStyleBackColor = true };
        page.Controls.Add(layout);
        _tabs.TabPages.Add(page);
        return layout;
    }

    /// <summary>
    /// As wide as the widest tab and as tall as the tallest, so switching tabs never resizes the form, but never taller
    /// than most of the screen it opens on: past that the tab scrolls and the footer's Save stays in view.
    /// </summary>
    private void FitToScreen(TableLayoutPanel footer)
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var frame = Size - ClientSize;

        var content = Size.Empty;
        foreach (TabPage page in _tabs.TabPages)
        {
            var rows = page.Controls[0].GetPreferredSize(Size.Empty);
            content = new Size(Math.Max(content.Width, rows.Width + page.Padding.Horizontal),
                Math.Max(content.Height, rows.Height + page.Padding.Vertical));
        }
        // The tab strip and border: what the control takes beyond the page area.
        var tabChrome = _tabs.Size - _tabs.DisplayRectangle.Size;

        var width = content.Width + SystemInformation.VerticalScrollBarWidth + tabChrome.Width + Padding.Horizontal;
        var height = content.Height + tabChrome.Height + Padding.Vertical + footer.GetPreferredSize(Size.Empty).Height;

        ClientSize = new Size(
            Math.Min(width, area.Width - frame.Width),
            Math.Min(height, area.Height * 9 / 10 - frame.Height));
        MinimumSize = new Size(Math.Min(Width, area.Width), Math.Min(320, area.Height));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    /// <summary>Says what is wrong in the footer and takes the user to the field, on whichever tab it is.</summary>
    private void ShowError(Control field, string message)
    {
        _errorLabel.Text = message;
        for (Control? parent = field.Parent; parent is not null; parent = parent.Parent)
            if (parent is TabPage page)
            {
                _tabs.SelectedTab = page;
                break;
            }
        field.Focus();
    }

    private static NumericUpDown Number(int value, IntRange range, int increment = 1) => new()
    {
        Minimum = range.Min,
        Maximum = range.Max,
        Increment = increment,
        Value = range.Clamp(value),
        Width = 100
    };

    /// <returns>The hint's label, for a hint that follows other fields; null without a hint.</returns>
    private static Label? AddRow(TableLayoutPanel layout, string label, Control input, string? hint)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) });
        layout.Controls.Add(input);

        if (hint is null) return null;

        var hintLabel = new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0) };
        layout.Controls.Add(new Label { AutoSize = true });
        layout.Controls.Add(hintLabel);
        return hintLabel;
    }

    /// <summary>
    /// The idle setting's hint, with what it really does (review L3, decided 2026-11-23: say it, don't cap it). Idle
    /// backdating can take back only what is still held back, at most <see cref="ActivityTrackerService.MaxHoldBack"/> less
    /// one credited gap — 35 s at the default 5 s poll. A longer threshold is kept for reading without input; past the figure,
    /// up to the rest of the wait is counted as time at the computer (less when idleness is noticed sooner than a full gap).
    /// </summary>
    internal static string IdleHint(int idleThresholdSeconds, int pollIntervalMs)
    {
        var takenBack = ActivityTrackerService.FullyTakenBackSeconds(pollIntervalMs);
        return idleThresholdSeconds <= takenBack
            ? "No keyboard or mouse input for this long. The time since the last input is then not counted."
            : $"No keyboard or mouse input for this long. Only about {takenBack} s of that wait are then taken back; "
              + $"up to {idleThresholdSeconds - takenBack} s of it count as time at the computer.";
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var url = _apiUrlBox.Text.Trim().TrimEnd('/');
        if (!IsHttpUrl(url))
        {
            ShowError(_apiUrlBox, "Server URL must be an absolute http(s) address.");
            return;
        }

        var webAppUrl = _webAppUrlBox.Text.Trim().TrimEnd('/');
        if (!IsHttpUrl(webAppUrl))
        {
            ShowError(_webAppUrlBox, "Web app URL must be an absolute http(s) address.");
            return;
        }

        var hotkeyText = _captureHotkeyBox.Text.Trim();
        var hotkey = HotkeyChord.TryParse(hotkeyText);
        if (hotkeyText.Length > 0 && hotkey is null)
        {
            ShowError(_captureHotkeyBox,
                "Capture hotkey: Ctrl, Alt or Win (and Shift if you like) with a letter, a digit, F1–F24 or Space, like Win+Alt+N.");
            return;
        }

        _config.ApiBaseUrl = url;
        _config.WebAppUrl = webAppUrl;
        _config.CaptureHotkey = hotkey?.ToString() ?? "";
        _config.PollIntervalMs = (int)_pollIntervalBox.Value;
        _config.IdleThresholdSeconds = (int)_idleThresholdBox.Value;
        _config.MaxPassiveIdleMinutes = (int)_passiveIdleBox.Value;
        _config.MinBackgroundWindowPercent = (int)_minBackgroundBox.Value;
        _config.MaxBackgroundWindowsPerMonitor = (int)_maxBackgroundBox.Value;
        _config.HyperfocusNudgesEnabled = _nudgesEnabledBox.Checked;
        _config.HyperfocusNudgeMinutes = (int)_nudgeAfterBox.Value;
        _config.AppHyperfocusNudgeMinutes = (int)_appNudgeAfterBox.Value;
        _config.BreakMinutes = (int)_breakLengthBox.Value;
        _config.BreakTimerMinutes = (int)_breakTimerBox.Value;
        _config.QuietWhenFullscreen = _quietFullscreenBox.Checked;
        _config.QuietWhenMicrophoneInUse = _quietMicrophoneBox.Checked;
        _config.DriftNudgesEnabled = _driftEnabledBox.Checked;
        _config.DriftNudgeMinutes = (int)_driftAfterBox.Value;
        _config.StartRampEnabled = _startRampBox.Checked;
        _config.StartRampBlocks = (RampBlocks)_startRampBlocksBox.SelectedIndex;
        _config.BlockEndEnabled = _blockEndBox.Checked;
        _config.BlockEndBlocks = (RampBlocks)_blockEndBlocksBox.SelectedIndex;
        _config.AwayReturnEnabled = _awayBox.Checked;
        _config.AwayAnswers = _awayAnswerBoxes
            .Select(b => b.SelectedItem as ActivityItem)
            .OfType<ActivityItem>()
            .Where(i => i.Id > 0)
            .DistinctBy(i => i.Id)
            .Select(i => new AwayChoice(i.Id, i.Name))
            .ToList();
        _config.UploadNoticesEnabled = _uploadNoticesBox.Checked;
        _config.UnmappedReviewEnabled = _unmappedReviewBox.Checked;
        _config.PauseRemindersEnabled = _pauseRemindersBox.Checked;
        _config.TimeChimeEnabled = _chimeEnabledBox.Checked;
        _config.TimeChimeMinutes = ChimeSettings.IntervalChoices[Math.Max(0, _chimeIntervalBox.SelectedIndex)];
        _config.TimeChimeDistractions = _chimeDistractionsBox.Checked;
        var choices = _config.TimeChimeChoices;
        for (var i = 0; i < _chimeCandidates.Count; i++)
        {
            var candidate = _chimeCandidates[i];
            choices = ChimeChoices.With(choices, candidate.Key, candidate.Name, _chimeTargetsBox.GetItemChecked(i),
                candidate.IsDistraction, _chimeDistractionsBox.Checked);
        }
        _config.TimeChimeChoices = choices;

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// Following distractions or not moves the tick of every distraction the user never set by hand, so saving keeps
    /// them following the server's flag instead of turning the old default into a choice.
    /// </summary>
    private void FollowDistractions(bool follow)
    {
        var handSet = _config.TimeChimeChoices.Select(c => c.Key).ToHashSet();
        for (var i = 0; i < _chimeCandidates.Count; i++)
            if (_chimeCandidates[i].IsDistraction && !handSet.Contains(_chimeCandidates[i].Key))
                _chimeTargetsBox.SetItemChecked(i, follow);
    }

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>The privacy form saves on its own Save: its changes do not wait for, or depend on, this form's.</summary>
    private void OnEditPrivacy(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        using var privacyForm = new PrivacyForm(_config);
        if (privacyForm.ShowDialog(this) == DialogResult.OK)
            _config.Save();
    }
}
