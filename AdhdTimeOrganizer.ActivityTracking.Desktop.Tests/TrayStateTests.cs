using System.Drawing;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>A paused tracker must look paused: which icon each state gets, what a double-click does, and the icons themselves.</summary>
public class TrayStateTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private static TrackerStatus Status(bool running = true, DateTime? pausedUntil = null, string? pollError = null) =>
        new(running, IsLocked: false, PendingWindows: 0, LastSentUtc: null, LastSendResult: null, pollError, pausedUntil);

    [Fact]
    public void TheIcon_PausedWinsOverEverything_SignedOutAndErrorsAskForAttention()
    {
        Assert.Equal(TrayIconState.Normal, TrayIconStates.For(Status(), hasSession: true, Now));
        Assert.Equal(TrayIconState.Paused, TrayIconStates.For(Status(false, DateTime.MaxValue), hasSession: true, Now));
        Assert.Equal(TrayIconState.Paused, TrayIconStates.For(Status(false, Now.AddMinutes(15)), hasSession: false, Now));
        Assert.Equal(TrayIconState.Attention, TrayIconStates.For(Status(), hasSession: false, Now));
        Assert.Equal(TrayIconState.Attention, TrayIconStates.For(Status(pollError: "boom"), hasSession: true, Now));
        // A timed pause past its end, not yet resumed by the timer, is no longer a pause.
        Assert.Equal(TrayIconState.Normal, TrayIconStates.For(Status(pausedUntil: Now.AddSeconds(-1)), hasSession: true, Now));
    }

    /// <summary>The log showed double-click pauses undone within seconds: it no longer stops recording.</summary>
    [Fact]
    public void DoubleClick_NeverPauses()
    {
        Assert.Equal(TrayApplicationContext.TrayDoubleClick.OpenMenu, TrayApplicationContext.DoubleClickAction(hasSession: true, isPaused: false));
        Assert.Equal(TrayApplicationContext.TrayDoubleClick.Resume, TrayApplicationContext.DoubleClickAction(hasSession: true, isPaused: true));
        Assert.Equal(TrayApplicationContext.TrayDoubleClick.SignIn, TrayApplicationContext.DoubleClickAction(hasSession: false, isPaused: true));
        Assert.Equal(TrayApplicationContext.TrayDoubleClick.SignIn, TrayApplicationContext.DoubleClickAction(hasSession: false, isPaused: false));
    }

    /// <summary>
    /// Double-click opens the menu through <see cref="System.Windows.Forms.NotifyIcon"/>'s private ShowContextMenu, which
    /// fronts it so a click elsewhere closes it. A WinForms update that renames it falls back quietly; this says so.
    /// </summary>
    [Fact]
    public void NotifyIcon_StillHasThePrivateShowContextMenu() =>
        Assert.NotNull(TrayApplicationContext.NotifyIconShowContextMenu);

    /// <summary>Drawn from the embedded icon at the tray's size, three different pictures — and the drawn ones load as icons.</summary>
    [Fact]
    public void TheThreeIcons_AreDrawn_AtTheTraySize_AndDiffer()
    {
        using var stream = typeof(TrayApplicationContext).Assembly
            .GetManifestResourceStream("AdhdTimeOrganizer.ActivityTracking.Desktop.timeorganizer-icon.ico")!;
        using var source = new Icon(stream);
        using var icons = new TrayIcons(source);

        var pictures = Enum.GetValues<TrayIconState>().Select(state =>
        {
            var icon = icons.For(state);
            Assert.Equal(System.Windows.Forms.SystemInformation.SmallIconSize, icon.Size);
            using var bitmap = icon.ToBitmap();
            return Pixels(bitmap);
        }).ToList();

        Assert.Equal(3, pictures.Distinct().Count());
    }

    private static string Pixels(Bitmap bitmap) =>
        string.Concat(Enumerable.Range(0, bitmap.Height)
            .SelectMany(y => Enumerable.Range(0, bitmap.Width).Select(x => bitmap.GetPixel(x, y).ToArgb().ToString("x8"))));
}
