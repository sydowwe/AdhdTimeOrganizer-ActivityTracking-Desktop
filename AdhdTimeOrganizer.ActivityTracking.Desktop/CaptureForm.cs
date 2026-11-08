using AdhdTimeOrganizer.ActivityTracking.Desktop.Native;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop;

/// <summary>
/// The quick-capture box: one line of text, Enter parks it, Esc cancels, and the keyboard goes back to the window that had
/// it. Opened by the global hotkey, the tray's "Capture a thought…" and a drift toast's button.
/// </summary>
/// <remarks>
/// <para>
/// A tool window (no taskbar button, not in Alt-Tab, not one of the "real" windows the tracker records as background), and
/// while it is in front the tracker credits the window it returns to (<see cref="CaptureBoxHandles"/>,
/// <see cref="CaptureFocus"/>): parking a thought is neither a switch away from the work nor time of its own.
/// </para>
/// <para>
/// Clicking away from an empty box closes it; from one with text, it stays — a misclick must not lose a typed thought. The
/// text goes to the caller's <c>park</c> and nowhere else: never into a log.
/// </para>
/// </remarks>
public sealed class CaptureForm : Form
{
    private readonly TextBox _textBox;
    private readonly Func<string, bool> _park;
    private readonly nint _returnTo;
    private bool _finishing;

    /// <param name="planLine">"Thesis until 11:30 — park it and get back", or null for no line.</param>
    /// <param name="returnTo">The window to give the keyboard back to; zero leaves it to Windows.</param>
    /// <param name="park">Queues the text; false when there was nothing to queue.</param>
    public CaptureForm(string? planLine, nint returnTo, Func<string, bool> park)
    {
        _park = park;
        _returnTo = returnTo;

        Text = "Capture a thought";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };

        if (planLine is not null)
            layout.Controls.Add(new Label { Text = planLine, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0) });

        _textBox = new TextBox { Width = 440, MaxLength = CaptureQueue.MaxTextLength };
        layout.Controls.Add(_textBox);
        layout.Controls.Add(new Label { Text = "Enter parks it on your capture list · Esc cancels", AutoSize = true, ForeColor = SystemColors.GrayText });

        Controls.Add(layout);
    }

    /// <summary>Back to the front with the keyboard: the hotkey pressed again while the box is open.</summary>
    public void Reactivate()
    {
        Win32.BringToFront(Handle);
        _textBox.Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Before the box can come to the front, so the tracker's first look already stands it in for the work.
        CaptureBoxHandles.Open(Handle, _returnTo);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // The upper third of the screen the user is looking at, where a command palette would be.
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + area.Height / 4);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Reactivate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Enter:
                ParkAndClose();
                return true;
            case Keys.Escape:
                Finish();
                return true;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>Enter, and the tracker's exit: what was typed is parked (blank is nothing), then the box goes.</summary>
    public void ParkAndClose()
    {
        if (CaptureQueue.Prepare(_textBox.Text) is { } text) _park(text);
        Finish();
    }

    /// <summary>The keyboard back to the window that had it, then gone. Windows allows it: the box is in front.</summary>
    private void Finish()
    {
        _finishing = true;
        if (_returnTo != nint.Zero && Win32.IsWindow(_returnTo)) Win32.SetForegroundWindow(_returnTo);
        Close();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_finishing && string.IsNullOrWhiteSpace(_textBox.Text))
            BeginInvoke(Close);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        CaptureBoxHandles.Close();
        base.OnFormClosed(e);
    }
}
