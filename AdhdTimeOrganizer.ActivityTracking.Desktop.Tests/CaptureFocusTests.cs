using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Native;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The capture box's pure rules (desktop prompt 08): the hotkey as typed in Settings, where focus goes back, what the tracker
/// credits while the box is in front, the plan line, and the tray item's text.
/// </summary>
public class CaptureFocusTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 10, 0, 30, DateTimeKind.Utc);

    // ---- the hotkey ---------------------------------------------------------

    [Theory]
    [InlineData("Win+Alt+N", HotkeyModifiers.Win | HotkeyModifiers.Alt, 'N', "Win+Alt+N")]
    [InlineData(" alt + WIN + n ", HotkeyModifiers.Win | HotkeyModifiers.Alt, 'N', "Win+Alt+N")]
    [InlineData("Ctrl+Shift+Space", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x20, "Ctrl+Shift+Space")]
    [InlineData("Control+Alt+F9", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x78, "Ctrl+Alt+F9")]
    [InlineData("Win+Shift+F24", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x87, "Win+Shift+F24")]
    [InlineData("Ctrl+7", HotkeyModifiers.Ctrl, '7', "Ctrl+7")]
    public void AChord_IsParsed_AndWrittenTheOneWay(string text, HotkeyModifiers modifiers, uint key, string canonical)
    {
        var chord = HotkeyChord.TryParse(text);

        Assert.Equal(new HotkeyChord(modifiers, key), chord);
        Assert.Equal(canonical, chord!.ToString());
    }

    /// <summary>A bare key or Shift alone would take a character from every app; the rest is not a chord at all.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("N")]
    [InlineData("Shift+N")]
    [InlineData("Win+Win+N")]
    [InlineData("Win+Alt+")]
    [InlineData("Win+Alt+Enter")]
    [InlineData("Win+Alt+F25")]
    [InlineData("Hyper+N")]
    public void NotAChord_IsNull(string? text) => Assert.Null(HotkeyChord.TryParse(text));

    [Fact]
    public void TheDefault_IsAChord_AndTheConfigsDefault()
    {
        Assert.NotNull(HotkeyChord.TryParse(HotkeyChord.Default));
        Assert.Equal(HotkeyChord.Default, new AppConfig().CaptureHotkey); // never saved: the user's config.json is not touched
    }

    // ---- focus and the tracker ----------------------------------------------

    /// <summary>A tray click: the menu and the taskbar are newest, and the window before them is where the user was.</summary>
    [Fact]
    public void TheReturnTarget_IsTheNewestEligibleWindow()
    {
        nint menu = 1, taskbar = 2, editor = 3, browser = 4;

        Assert.Equal(editor, CaptureFocus.PickReturnTarget([menu, taskbar, editor, browser], h => h is 3 or 4));
        Assert.Equal(nint.Zero, CaptureFocus.PickReturnTarget([menu, nint.Zero], _ => false));
        Assert.Equal(browser, CaptureFocus.PickReturnTarget([nint.Zero, browser], _ => true));
    }

    [Fact]
    public void WhileTheBoxIsInFront_TheTrackerSeesTheWindowItReturnsTo()
    {
        nint box = 10, editor = 3, other = 7;

        Assert.Equal(editor, CaptureFocus.StandIn(box, box, editor));
        Assert.Equal(nint.Zero, CaptureFocus.StandIn(box, box, nint.Zero)); // nothing recorded in front, as for the tray menu
        Assert.Equal(other, CaptureFocus.StandIn(other, box, editor));      // the box open, but the user went elsewhere
        Assert.Equal(other, CaptureFocus.StandIn(other, nint.Zero, nint.Zero));
    }

    // ---- the plan line --------------------------------------------------------

    [Fact]
    public void ThePlanLine_NamesTheBlockNow_AndWhenItEnds()
    {
        var line = CaptureFocus.PlanLine(Minute(Now.AddSeconds(-90), Block("Thesis", 11, 30)), Now, TimeZoneInfo.Utc);

        Assert.Equal("Thesis until 11:30 — park it and get back", line);
    }

    [Fact]
    public void ThePlanLine_PrefersTheStartedBlock_AndSkipsOneAlreadyOver()
    {
        var over = Block("Email", 10, 0);
        var planned = Block("Reading", 10, 45);
        var started = Block("Thesis", 11, 30) with { InProgress = true };

        Assert.StartsWith("Thesis until", CaptureFocus.PlanLine(Minute(Now.AddMinutes(-1), over, planned, started), Now, TimeZoneInfo.Utc));
        Assert.StartsWith("Reading until", CaptureFocus.PlanLine(Minute(Now.AddMinutes(-1), over, planned), Now, TimeZoneInfo.Utc));
    }

    /// <summary>The drift nudge's freshness: an answer older than that may describe a plan since changed — no line beats a wrong one.</summary>
    [Fact]
    public void ThePlanLine_IsNull_WithoutAFreshMinuteOrABlock()
    {
        Assert.Null(CaptureFocus.PlanLine(null, Now, TimeZoneInfo.Utc));
        Assert.Null(CaptureFocus.PlanLine(Minute(Now - DriftPolicy.FreshFor - TimeSpan.FromSeconds(1), Block("Thesis", 11, 30)), Now, TimeZoneInfo.Utc));
        Assert.Null(CaptureFocus.PlanLine(Minute(Now.AddMinutes(-1)), Now, TimeZoneInfo.Utc));
    }

    // ---- the tray item ----------------------------------------------------------

    [Fact]
    public void TheTrayItem_SaysWhatIsWrong_InCountsOnly()
    {
        Assert.Equal("Capture a thought…", TrayApplicationContext.DescribeCaptureItem(HotkeyRegistration.Registered, "Win+Alt+N", new CaptureStatus(0, 0, null)));
        Assert.Equal("Capture a thought… (Win+Alt+N is used by another app)",
            TrayApplicationContext.DescribeCaptureItem(HotkeyRegistration.Taken, "Win+Alt+N", null));
        Assert.Equal("Capture a thought… (2 waiting to upload)",
            TrayApplicationContext.DescribeCaptureItem(HotkeyRegistration.Off, "", new CaptureStatus(2, 0, null)));
        Assert.Equal("Capture a thought… (Win+Alt+N is used by another app; 1 waiting: the server can't take them yet)",
            TrayApplicationContext.DescribeCaptureItem(HotkeyRegistration.Taken, "Win+Alt+N", new CaptureStatus(1, 0, CaptureSendResult.NotSupported)));
        Assert.Equal("Capture a thought… (1 waiting: add a task priority in the web app)",
            TrayApplicationContext.DescribeCaptureItem(HotkeyRegistration.Registered, "Win+Alt+N", new CaptureStatus(1, 3, CaptureSendResult.NoPriority)));
    }

    private static PlannedBlock Block(string name, int endHour, int endMinute) => new()
    {
        PlannerTaskId = name.Length,
        ActivityId = 12,
        ActivityName = name,
        StartsAtUtc = new DateTime(2026, 11, 8, 9, 0, 0, DateTimeKind.Utc),
        EndsAtUtc = new DateTime(2026, 11, 8, endHour, endMinute, 0, DateTimeKind.Utc)
    };

    private static ResolvedMinute Minute(DateTime windowStartUtc, params PlannedBlock[] planned) =>
        new(windowStartUtc, new HeartbeatResponse { Planned = [.. planned] });
}
