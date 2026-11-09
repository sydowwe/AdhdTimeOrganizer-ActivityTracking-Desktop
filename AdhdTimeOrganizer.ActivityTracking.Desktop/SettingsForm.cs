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
    private readonly CheckBox _uploadNoticesBox;
    private readonly CheckBox _unmappedReviewBox;
    private readonly CheckBox _chimeEnabledBox;
    private readonly ComboBox _chimeIntervalBox;
    private readonly CheckBox _chimeDistractionsBox;
    private readonly CheckedListBox _chimeTargetsBox;
    private readonly TextBox _captureHotkeyBox;
    private readonly IReadOnlyList<ChimeCandidate> _chimeCandidates;
    private readonly Label _errorLabel;

    /// <param name="chimeCandidates">The lines the time chime can be set on (<see cref="ChimeChoices.Candidates"/>).</param>
    public SettingsForm(AppConfig config, IReadOnlyList<ChimeCandidate>? chimeCandidates = null)
    {
        _config = config;
        _chimeCandidates = chimeCandidates ?? [];

        Text = "Activity Tracker - Settings";
        // Sizable and scrolling, never auto-sized: the rows outgrew a laptop screen, and an auto-sized dialog put Save
        // below its bottom edge. The rows scroll; the error line and the buttons stay in a footer (FitToScreen).
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = Point.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _apiUrlBox = new TextBox { Text = config.ApiBaseUrl, Width = 260 };
        _webAppUrlBox = new TextBox { Text = config.WebAppUrl, Width = 260 };
        _pollIntervalBox = Number(config.PollIntervalMs, 500, 10_000, increment: 250);
        _idleThresholdBox = Number(config.IdleThresholdSeconds, 5, 600);
        _passiveIdleBox = Number(config.MaxPassiveIdleMinutes, 0, 600, increment: 10);
        _minBackgroundBox = Number(config.MinBackgroundWindowPercent, 0, 100);
        _maxBackgroundBox = Number(config.MaxBackgroundWindowsPerMonitor, 0, 10);

        AddRow(layout, "Server URL:", _apiUrlBox, "Changing it signs you out.");
        AddRow(layout, "Web app URL:", _webAppUrlBox, "Where the planner and your mappings open.");
        AddRow(layout, "Poll interval (ms):", _pollIntervalBox,
            "Idleness, background windows, sound. The window in front is followed as it changes.");
        AddRow(layout, "Idle after (s):", _idleThresholdBox,
            "No keyboard or mouse input for this long. The time since the last input is then not counted.");
        AddRow(layout, "Watching / listening (min):", _passiveIdleBox,
            "How long a foreground window playing sound keeps you present without input. 0 turns it off.");
        AddRow(layout, "Background window min area (%):", _minBackgroundBox, null);
        AddRow(layout, "Background windows per monitor:", _maxBackgroundBox, null);

        _nudgesEnabledBox = new CheckBox { Text = "On", Checked = config.HyperfocusNudgesEnabled, AutoSize = true };
        _nudgeAfterBox = Number(config.HyperfocusNudgeMinutes, 15, 600, increment: 15);
        _appNudgeAfterBox = Number(config.AppHyperfocusNudgeMinutes, 0, 600, increment: 15);
        _breakLengthBox = Number(config.BreakMinutes, 1, 60);
        _breakTimerBox = Number(config.BreakTimerMinutes, 1, 120);
        _quietFullscreenBox = new CheckBox { Text = "Full screen or presenting", Checked = config.QuietWhenFullscreen, AutoSize = true };
        _quietMicrophoneBox = new CheckBox { Text = "Microphone in use (a call)", Checked = config.QuietWhenMicrophoneInUse, AutoSize = true };

        AddRow(layout, "Hyperfocus nudges:", _nudgesEnabledBox, null);
        AddRow(layout, "Nudge after (min):", _nudgeAfterBox, "At the computer this long without a break.");
        AddRow(layout, "Same app, nudge after (min):", _appNudgeAfterBox,
            "In one app this long without a break from it. 0 turns it off.");
        AddRow(layout, "A break is away for (min):", _breakLengthBox,
            "No input, no sound in the foreground and no call for this long ends a stretch.");
        AddRow(layout, "Break timer (min):", _breakTimerBox, "How long \"Taking a break\" lasts before the back-to-it nudge.");

        _driftEnabledBox = new CheckBox { Text = "On", Checked = config.DriftNudgesEnabled, AutoSize = true };
        _driftAfterBox = Number(config.DriftNudgeMinutes, 10, 120, increment: 5);

        AddRow(layout, "Drift nudges:", _driftEnabledBox,
            "When your planner says one thing and your apps another. Apps without a mapping rule don't count either way.");
        AddRow(layout, "Off plan, nudge after (min):", _driftAfterBox, "At most once per planned block.");

        _startRampBox = new CheckBox { Text = "On", Checked = config.StartRampEnabled, AutoSize = true };
        AddRow(layout, "Start ramp:", _startRampBox,
            "When a planned block starts and you are at the computer: one notice to start its timer. Needs no drift nudges.");

        // In the order of RampBlocks, so the selected index is the setting.
        _startRampBlocksBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        _startRampBlocksBox.Items.AddRange(["Every block", "All but Optional", "Critical only"]);
        _startRampBlocksBox.SelectedIndex = (int)config.StartRampBlocks;
        AddRow(layout, "Start ramp asks about:", _startRampBlocksBox, "By the importance a block has in your planner.");

        var chime = ChimeSettings.From(config);
        _chimeEnabledBox = new CheckBox { Text = "On", Checked = config.TimeChimeEnabled, AutoSize = true };
        AddRow(layout, "Time chime:", _chimeEnabledBox,
            "A silent \"15 min on YouTube\" now and then, while you stay on something you chose. Quiet inside a planned block while drift nudges are on.");

        _chimeIntervalBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        _chimeIntervalBox.Items.AddRange(ChimeSettings.IntervalChoices.Select(m => (object)$"{m} min").ToArray());
        _chimeIntervalBox.SelectedIndex = Array.IndexOf(ChimeSettings.IntervalChoices, (int)chime.Interval.TotalMinutes);
        AddRow(layout, "Chime every:", _chimeIntervalBox, "Of continuous time. More than 3 minutes on something else starts it over.");

        _chimeDistractionsBox = new CheckBox { Text = "Chime my distractions", Checked = config.TimeChimeDistractions, AutoSize = true };
        AddRow(layout, "", _chimeDistractionsBox, "The activities and categories you marked a distraction in the web app.");

        _chimeTargetsBox = new CheckedListBox { CheckOnClick = true, Width = 260, Height = 110, IntegralHeight = false };
        foreach (var candidate in _chimeCandidates)
            _chimeTargetsBox.Items.Add(candidate.IsDistraction ? $"{candidate.Name} (distraction)" : candidate.Name,
                chime.Chimes(candidate.Key, candidate.IsDistraction));
        _chimeDistractionsBox.CheckedChanged += (_, _) => FollowDistractions(_chimeDistractionsBox.Checked);
        AddRow(layout, "Chime on:", _chimeTargetsBox,
            _chimeCandidates.Count == 0 ? "Nothing sent today yet: what you use today appears here." : "Today's activities, your distractions and earlier choices.");

        _captureHotkeyBox = new TextBox { Text = config.CaptureHotkey, Width = 160 };
        AddRow(layout, "Capture hotkey:", _captureHotkeyBox,
            $"Opens a one-line box that parks a thought on your capture list. Like {HotkeyChord.Default} or Ctrl+Shift+F9; empty turns it off. The tray menu's \"Capture a thought…\" works either way.");

        AddRow(layout, "Stay quiet while:", _quietFullscreenBox, null);
        AddRow(layout, "", _quietMicrophoneBox, null);

        _uploadNoticesBox = new CheckBox { Text = "On", Checked = config.UploadNoticesEnabled, AutoSize = true };
        AddRow(layout, "Upload problem notices:", _uploadNoticesBox,
            "When your session ended, uploads have been stuck for a day, or the server refused minutes. Not a nudge: the quiet conditions don't apply.");

        _unmappedReviewBox = new CheckBox { Text = "On", Checked = config.UnmappedReviewEnabled, AutoSize = true };
        AddRow(layout, "Evening review of unmapped time:", _unmappedReviewBox,
            "At your evening-shutdown time: one notice with how much of today on this PC no rule maps, if it is worth a look. Needs no drift nudges.");

        var ignoreRulesLink = new LinkLabel { Text = "Ignore rules and private windows…", AutoSize = true };
        ignoreRulesLink.LinkClicked += OnEditPrivacy;
        layout.Controls.Add(ignoreRulesLink);
        layout.SetColumnSpan(ignoreRulesLink, 2);

        _errorLabel = new Label { AutoSize = true, ForeColor = Color.Red, MaximumSize = new Size(560, 0) };

        var okButton = new Button { Text = "Save", AutoSize = true };
        okButton.Click += OnSave;
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right };
        buttons.Controls.AddRange([cancelButton, okButton]);

        // The error line above the buttons, both always in view.
        var footer = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 6, 12, 12)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.Controls.Add(_errorLabel);
        footer.Controls.Add(buttons);

        var rows = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 12, 12, 0) };
        rows.Controls.Add(layout);

        // The Fill panel first: docking runs from the last added, so the footer takes the bottom and the rows the rest.
        Controls.Add(rows);
        Controls.Add(footer);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        Load += (_, _) => FitToScreen(layout, rows, footer);
    }

    /// <summary>
    /// As wide as the rows and as tall as they are, but never taller than most of the screen it opens on: past that the
    /// rows scroll and the footer's Save stays in view.
    /// </summary>
    private void FitToScreen(TableLayoutPanel layout, Panel rows, TableLayoutPanel footer)
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var content = layout.GetPreferredSize(Size.Empty);
        var frame = Size - ClientSize;

        var width = content.Width + rows.Padding.Horizontal + SystemInformation.VerticalScrollBarWidth;
        var height = content.Height + rows.Padding.Vertical + footer.GetPreferredSize(Size.Empty).Height;

        ClientSize = new Size(
            Math.Min(width, area.Width - frame.Width),
            Math.Min(height, area.Height * 9 / 10 - frame.Height));
        MinimumSize = new Size(Math.Min(Width, area.Width), Math.Min(320, area.Height));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    private static NumericUpDown Number(int value, int min, int max, int increment = 1) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = increment,
        Value = Math.Clamp(value, min, max),
        Width = 100
    };

    private static void AddRow(TableLayoutPanel layout, string label, Control input, string? hint)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) });
        layout.Controls.Add(input);

        if (hint is null) return;

        var hintLabel = new Label { Text = hint, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0) };
        layout.Controls.Add(new Label { AutoSize = true });
        layout.Controls.Add(hintLabel);
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var url = _apiUrlBox.Text.Trim().TrimEnd('/');
        if (!IsHttpUrl(url))
        {
            _errorLabel.Text = "Server URL must be an absolute http(s) address.";
            return;
        }

        var webAppUrl = _webAppUrlBox.Text.Trim().TrimEnd('/');
        if (!IsHttpUrl(webAppUrl))
        {
            _errorLabel.Text = "Web app URL must be an absolute http(s) address.";
            return;
        }

        var hotkeyText = _captureHotkeyBox.Text.Trim();
        var hotkey = HotkeyChord.TryParse(hotkeyText);
        if (hotkeyText.Length > 0 && hotkey is null)
        {
            _errorLabel.Text = "Capture hotkey: Ctrl, Alt or Win (and Shift if you like) with a letter, a digit, F1–F24 or Space, like Win+Alt+N.";
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
        _config.UploadNoticesEnabled = _uploadNoticesBox.Checked;
        _config.UnmappedReviewEnabled = _unmappedReviewBox.Checked;
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
