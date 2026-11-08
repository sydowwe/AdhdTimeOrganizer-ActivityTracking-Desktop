using System.Runtime.InteropServices;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Native;

/// <summary>How the last <see cref="GlobalHotkey.Register"/> went.</summary>
public enum HotkeyRegistration
{
    /// <summary>No chord set: the hotkey is off.</summary>
    Off,
    Registered,
    /// <summary>Another app holds the chord. The tray says so; Settings can pick another.</summary>
    Taken,
    /// <summary>Refused for another reason (a chord Windows reserves, say).</summary>
    Failed
}

/// <summary>
/// The capture box's global hotkey: a message-only window that holds one <c>RegisterHotKey</c> registration and raises
/// <see cref="Pressed"/> on its <c>WM_HOTKEY</c>.
/// </summary>
/// <remarks>
/// <b>Create, register and dispose it on the UI thread</b>, like <see cref="ForegroundEvents"/>: <c>WM_HOTKEY</c> is posted to
/// the thread that owns the window, and only that thread may unregister it. Receiving the hotkey is also what lets the tracker
/// bring its box to the front — Windows grants that to the process the user just spoke to.
/// </remarks>
internal sealed class GlobalHotkey : NativeWindow, IDisposable
{
    private const int HotkeyId = 0x0C47;

    /// <summary>A message-only window: never shown, never in a window list.</summary>
    private static readonly nint HwndMessage = -3;

    private bool _registered;

    public event Action? Pressed;

    public GlobalHotkey() => CreateHandle(new CreateParams { Parent = HwndMessage });

    /// <summary>Replaces the registration with <paramref name="chord"/>; null switches the hotkey off.</summary>
    public HotkeyRegistration Register(HotkeyChord? chord)
    {
        Unregister();
        if (chord is null) return HotkeyRegistration.Off;

        if (Win32.RegisterHotKey(Handle, HotkeyId, (uint)chord.Modifiers | Win32.MOD_NOREPEAT, chord.VirtualKey))
        {
            _registered = true;
            return HotkeyRegistration.Registered;
        }

        return Marshal.GetLastPInvokeError() == Win32.ERROR_HOTKEY_ALREADY_REGISTERED
            ? HotkeyRegistration.Taken
            : HotkeyRegistration.Failed;
    }

    private void Unregister()
    {
        if (!_registered) return;
        Win32.UnregisterHotKey(Handle, HotkeyId);
        _registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_HOTKEY && m.WParam == HotkeyId)
        {
            Pressed?.Invoke();
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
