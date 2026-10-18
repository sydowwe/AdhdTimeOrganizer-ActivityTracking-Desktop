using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services.Matching;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop;

/// <summary>
/// Edits what is kept on this computer: the user's own ignore / hide-title rules and how private browser windows
/// are handled. Writes into <see cref="AppConfig"/> on Save; the caller saves the file. The rule list is replaced,
/// not edited in place, because the poll loop reads it on its own thread — which is also why the rules apply from
/// the next poll, without a restart.
/// </summary>
public sealed class PrivacyForm : Form
{
    private const string ActionIgnore = "Ignore";
    private const string ActionHideTitle = "Hide title";
    private const string EmptyTitle = "Empty title";

    private static readonly string[] MatchTypes = Enum.GetNames<PatternMatchType>();

    private readonly AppConfig _config;
    private readonly CheckBox _hidePrivateBox;
    private readonly ComboBox _handlingBox;
    private readonly DataGridView _rulesGrid;
    private readonly Label _errorLabel;

    private readonly DataGridViewComboBoxColumn _actionColumn;
    private readonly DataGridViewTextBoxColumn _processColumn;
    private readonly DataGridViewComboBoxColumn _processMatchColumn;
    private readonly DataGridViewTextBoxColumn _titleColumn;
    private readonly DataGridViewComboBoxColumn _titleMatchColumn;

    public PrivacyForm(AppConfig config)
    {
        _config = config;

        Text = "Activity Tracker - Privacy";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Fill
        };

        _hidePrivateBox = new CheckBox
        {
            Text = "Hide the titles of private browser windows (Firefox Private Browsing, Edge InPrivate)",
            Checked = config.HidePrivateWindows,
            AutoSize = true
        };
        layout.Controls.Add(_hidePrivateBox);

        var handlingRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        handlingRow.Controls.Add(new Label { Text = "Private and hidden-title windows:", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        _handlingBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
        _handlingBox.Items.AddRange(["Record as \"<app> - PRIVATE\" (the time still counts)", "Don't record them"]);
        _handlingBox.SelectedIndex = config.PrivateWindowHandling == PrivateWindowHandling.Drop ? 1 : 0;
        handlingRow.Controls.Add(_handlingBox);
        layout.Controls.Add(handlingRow);

        layout.Controls.Add(Hint(
            "Rules below apply before anything is saved or sent, so a matching title never leaves this computer. " +
            "Leave Process empty for any app and Title empty for any title. A title rule matches the window's full " +
            "title and the shortened one the dashboards show. Ignore wins over Hide title. " +
            "Your rules from the web app are applied too, just before upload."));

        _actionColumn = new DataGridViewComboBoxColumn { HeaderText = "Action", Width = 90, FlatStyle = FlatStyle.Flat };
        _actionColumn.Items.AddRange(ActionIgnore, ActionHideTitle);
        _processColumn = new DataGridViewTextBoxColumn { HeaderText = "Process", Width = 150 };
        _processMatchColumn = new DataGridViewComboBoxColumn { HeaderText = "Process match", Width = 95, FlatStyle = FlatStyle.Flat };
        _processMatchColumn.Items.AddRange(MatchTypes.Cast<object>().ToArray());
        _titleColumn = new DataGridViewTextBoxColumn { HeaderText = "Title", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill };
        _titleMatchColumn = new DataGridViewComboBoxColumn { HeaderText = "Title match", Width = 95, FlatStyle = FlatStyle.Flat };
        _titleMatchColumn.Items.AddRange(MatchTypes.Append(EmptyTitle).Cast<object>().ToArray());

        _rulesGrid = new DataGridView
        {
            Width = 760,
            Height = 300,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AllowUserToResizeRows = false,
            RowHeadersWidth = 24,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            EditMode = DataGridViewEditMode.EditOnEnter
        };
        _rulesGrid.Columns.AddRange(_actionColumn, _processColumn, _processMatchColumn, _titleColumn, _titleMatchColumn);
        _rulesGrid.DefaultValuesNeeded += OnDefaultValuesNeeded;
        foreach (var rule in config.LocalRules)
            _rulesGrid.Rows.Add(ToRow(rule));
        layout.Controls.Add(_rulesGrid);

        layout.Controls.Add(Hint("Select a row and press Delete to remove it. A localized Firefox names its private " +
                                 "windows in its own language: add a Hide title rule with Contains on that text."));

        _errorLabel = new Label { AutoSize = true, ForeColor = Color.Red, MaximumSize = new Size(760, 0) };
        layout.Controls.Add(_errorLabel);

        var okButton = new Button { Text = "Save", AutoSize = true };
        okButton.Click += OnSave;
        var cancelButton = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = 760 };
        buttons.Controls.AddRange([cancelButton, okButton]);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = okButton;
        CancelButton = cancelButton;
    }

    private static Label Hint(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(760, 0), Margin = new Padding(3, 6, 3, 6) };

    private static object[] ToRow(LocalRule rule) =>
    [
        rule.Action == LocalRuleAction.HideTitle ? ActionHideTitle : ActionIgnore,
        rule.ProcessName ?? "",
        rule.ProcessNameMatchType.ToString(),
        rule.WindowTitle ?? "",
        rule.WindowTitle == "" ? EmptyTitle : rule.WindowTitleMatchType.ToString()
    ];

    private void OnDefaultValuesNeeded(object? sender, DataGridViewRowEventArgs e)
    {
        e.Row.Cells[_actionColumn.Index].Value = ActionHideTitle;
        e.Row.Cells[_processMatchColumn.Index].Value = nameof(PatternMatchType.Exact);
        e.Row.Cells[_titleMatchColumn.Index].Value = nameof(PatternMatchType.Contains);
    }

    private void OnSave(object? sender, EventArgs e)
    {
        _rulesGrid.EndEdit();

        var rules = new List<LocalRule>();
        foreach (DataGridViewRow row in _rulesGrid.Rows)
        {
            if (row.IsNewRow) continue;

            var process = CellText(row, _processColumn).Trim();
            var title = CellText(row, _titleColumn).Trim();
            var titleMatch = CellText(row, _titleMatchColumn);
            var isEmptyTitle = titleMatch == EmptyTitle;

            if (process.Length == 0 && title.Length == 0 && !isEmptyTitle) continue; // a row left blank

            var rule = new LocalRule
            {
                Action = CellText(row, _actionColumn) == ActionHideTitle ? LocalRuleAction.HideTitle : LocalRuleAction.Ignore,
                ProcessName = process.Length == 0 ? null : process,
                ProcessNameMatchType = ParseMatch(CellText(row, _processMatchColumn)),
                WindowTitle = isEmptyTitle ? "" : title.Length == 0 ? null : title,
                WindowTitleMatchType = isEmptyTitle ? PatternMatchType.Exact : ParseMatch(titleMatch)
            };

            if (Invalid(rule) is { } error)
            {
                _errorLabel.Text = $"Row {row.Index + 1}: {error}";
                _rulesGrid.CurrentCell = row.Cells[0];
                return;
            }

            rules.Add(rule);
        }

        _config.HidePrivateWindows = _hidePrivateBox.Checked;
        _config.PrivateWindowHandling = _handlingBox.SelectedIndex == 1 ? PrivateWindowHandling.Drop : PrivateWindowHandling.KeepTimeAsAppName;
        _config.LocalRules = rules;

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string? Invalid(LocalRule rule)
    {
        if (!PrivacyFilter.IsUsable(rule))
            return "give it a process, a title, or the Empty title match.";
        if (rule.ProcessName is { } process && !PatternMatcher.IsValid(process, rule.ProcessNameMatchType))
            return "the process pattern is not a valid regular expression.";
        if (rule.WindowTitle is { Length: > 0 } title && !PatternMatcher.IsValid(title, rule.WindowTitleMatchType))
            return "the title pattern is not a valid regular expression.";
        return null;
    }

    private static string CellText(DataGridViewRow row, DataGridViewColumn column) =>
        row.Cells[column.Index].Value as string ?? "";

    private static PatternMatchType ParseMatch(string value) =>
        Enum.TryParse<PatternMatchType>(value, out var matchType) ? matchType : PatternMatchType.Exact;
}
