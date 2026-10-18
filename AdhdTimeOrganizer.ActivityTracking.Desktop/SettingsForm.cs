using System.Diagnostics;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop;

/// <summary>
/// Edits the tracker's settings in place on <see cref="AppConfig"/>. The caller saves and applies them.
/// Ignore rules are a list, so they stay in config.json — the form links to it.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly TextBox _apiUrlBox;
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
    private readonly Label _errorLabel;

    public SettingsForm(AppConfig config)
    {
        _config = config;

        Text = "Activity Tracker - Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            Dock = DockStyle.Fill
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _apiUrlBox = new TextBox { Text = config.ApiBaseUrl, Width = 260 };
        _pollIntervalBox = Number(config.PollIntervalMs, 500, 10_000, increment: 250);
        _idleThresholdBox = Number(config.IdleThresholdSeconds, 5, 600);
        _passiveIdleBox = Number(config.MaxPassiveIdleMinutes, 0, 600, increment: 10);
        _minBackgroundBox = Number(config.MinBackgroundWindowPercent, 0, 100);
        _maxBackgroundBox = Number(config.MaxBackgroundWindowsPerMonitor, 0, 10);

        AddRow(layout, "Server URL:", _apiUrlBox, "Changing it signs you out.");
        AddRow(layout, "Poll interval (ms):", _pollIntervalBox, null);
        AddRow(layout, "Idle after (s):", _idleThresholdBox, "No keyboard or mouse input for this long.");
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
        AddRow(layout, "Stay quiet while:", _quietFullscreenBox, null);
        AddRow(layout, "", _quietMicrophoneBox, null);

        var ignoreRulesLink = new LinkLabel { Text = "Edit ignore rules (config.json)…", AutoSize = true };
        ignoreRulesLink.LinkClicked += OnEditIgnoreRules;
        layout.Controls.Add(ignoreRulesLink);
        layout.SetColumnSpan(ignoreRulesLink, 2);

        _errorLabel = new Label { AutoSize = true, ForeColor = Color.Red };
        layout.Controls.Add(_errorLabel);
        layout.SetColumnSpan(_errorLabel, 2);

        var okButton = new Button { Text = "Save", AutoSize = true };
        okButton.Click += OnSave;
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancelButton, okButton]);
        layout.Controls.Add(buttons);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            _errorLabel.Text = "Server URL must be an absolute http(s) address.";
            return;
        }

        _config.ApiBaseUrl = url;
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

        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnEditIgnoreRules(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        _config.Save(); // make sure the file exists
        Process.Start(new ProcessStartInfo { FileName = AppConfig.ConfigPath, UseShellExecute = true });
        MessageBox.Show(this, "Restart the tracker after editing config.json for the ignore rules to apply.",
            "Ignore rules", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
