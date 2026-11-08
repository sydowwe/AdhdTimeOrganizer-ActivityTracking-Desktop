using System.Globalization;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>The modifier keys of a global hotkey, as <c>RegisterHotKey</c> takes them.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,     // MOD_ALT
    Ctrl = 0x0002,    // MOD_CONTROL
    Shift = 0x0004,   // MOD_SHIFT
    Win = 0x0008      // MOD_WIN
}

/// <summary>
/// A global hotkey as the user writes it in Settings ("Win+Alt+N") and as <c>RegisterHotKey</c> takes it: modifiers and a
/// virtual-key code. Pure; registering it is <c>Native/GlobalHotkey</c>'s.
/// </summary>
/// <remarks>
/// The key is a letter, a digit, F1–F24 or Space. A chord needs Ctrl, Alt or Win: a bare key or Shift alone would take a
/// character from every app on the machine.
/// </remarks>
public sealed record HotkeyChord(HotkeyModifiers Modifiers, uint VirtualKey)
{
    /// <summary>
    /// The default: N for note. Win+Alt is Windows' own family (Game Bar, the clock, HDR, snapping), and none of its stock
    /// shortcuts is N; a Win chord never collides with an app's own shortcut (Rider's Ctrl+Shift+Space), and unlike Ctrl+Alt
    /// it is not AltGr on a Slovak or German keyboard.
    /// </summary>
    public const string Default = "Win+Alt+N";

    private const uint VkSpace = 0x20;
    private const uint VkF1 = 0x70;

    /// <summary>"Win+Alt+N", "ctrl + shift + space": modifiers in any order, any case. Null for anything else.</summary>
    public static HotkeyChord? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(p => p.Length == 0)) return null;

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "win" or "windows" => HotkeyModifiers.Win,
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                _ => HotkeyModifiers.None
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier)) return null;
            modifiers |= modifier;
        }

        if ((modifiers & (HotkeyModifiers.Win | HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)) == 0) return null;
        return KeyOf(parts[^1]) is { } key ? new HotkeyChord(modifiers, key) : null;
    }

    /// <summary>The canonical text: Win, Ctrl, Alt, Shift, then the key — "Win+Alt+N".</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    private static uint? KeyOf(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetter(key[0])) return char.ToUpperInvariant(key[0]);
        if (key.Length == 1 && char.IsAsciiDigit(key[0])) return key[0];
        if (key.Equals("space", StringComparison.OrdinalIgnoreCase)) return VkSpace;
        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f')
            && int.TryParse(key[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 24)
            return VkF1 + (uint)(n - 1);
        return null;
    }

    private static string KeyName(uint virtualKey) => virtualKey switch
    {
        VkSpace => "Space",
        >= VkF1 and < VkF1 + 24 => $"F{virtualKey - VkF1 + 1}",
        _ => ((char)virtualKey).ToString()
    };
}
