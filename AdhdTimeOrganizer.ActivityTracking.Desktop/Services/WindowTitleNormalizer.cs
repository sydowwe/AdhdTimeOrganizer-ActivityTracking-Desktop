using System.Text.RegularExpressions;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// Strips the parts of a window title that change without the user doing anything different, so one
/// activity is one row instead of dozens. Every distinct title is its own entry — and its own row in the
/// distinct-entries grid the user writes mapping rules from.
/// </summary>
public static partial class WindowTitleNormalizer
{
    /// <summary>
    /// Apps whose last title segment is the open file, which changes constantly: the whole segment is
    /// dropped and the title keeps the project. (JetBrains IDEs title as "project – file".)
    /// </summary>
    private static readonly HashSet<string> DropLastSegmentProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "rider64", "webstorm64", "studio64", "chrome",
        "idea64", "pycharm64", "clion64", "goland64", "phpstorm64", "datagrip64", "rustrover64", "rubymine64"
    };

    // Rider uses an en dash, Firefox an em dash, most others a hyphen.
    private static readonly string[] Separators = [" – ", " — ", " - "];

    /// <summary>
    /// Leading noise: unread counters ("(3) Inbox", "(99+) Feed") and unsaved-file markers
    /// ("● file.ts" in VS Code, "*Untitled" in Notepad / Notepad++), possibly stacked.
    /// </summary>
    [GeneratedRegex(@"^(?:\(\d+\+?\)\s*|[●•*]\s*)+")]
    private static partial Regex NoisePrefix();

    public static string Normalize(string processName, string? productName, string? rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle)) return "";

        var title = NoisePrefix().Replace(rawTitle.Trim(), "");
        if (title.Length == 0) return rawTitle.Trim();

        var trimmed = DropLastSegmentProcesses.Contains(processName)
            ? DropLastSegment(title)
            : DropProductSuffix(title, productName);

        return trimmed.Length > 0 ? trimmed.Trim() : title;
    }

    /// <remarks>
    /// The <em>last</em> separator of any kind. This used to try the en dash first, so a Chrome page
    /// titled "Foo – Bar - Google Chrome" was cut to "Foo".
    /// </remarks>
    private static string DropLastSegment(string title)
    {
        var (idx, _) = LastSeparator(title);
        return idx > 0 ? title[..idx] : title;
    }

    private static (int Index, string Separator) LastSeparator(string title) =>
        Separators
            .Select(s => (Index: title.LastIndexOf(s, StringComparison.Ordinal), Separator: s))
            .MaxBy(x => x.Index);

    /// <summary>
    /// Drops a trailing " - Google Chrome", " — Mozilla Firefox", " - Visual Studio Code"… by matching the
    /// last segment against the executable's product name, so it works for apps nobody listed. Edge
    /// writes a zero-width space into its own name ("Microsoft​ Edge"), hence the replace.
    /// </summary>
    private static string DropProductSuffix(string title, string? productName)
    {
        if (productName is not { Length: >= 3 }) return title;

        var (idx, separator) = LastSeparator(title);
        if (idx <= 0) return title;

        var segment = title[(idx + separator.Length)..].Replace("​", "").Trim();
        if (segment.Length < 4) return title;

        var matches = segment.Contains(productName, StringComparison.OrdinalIgnoreCase)
                      || productName.Contains(segment, StringComparison.OrdinalIgnoreCase);
        return matches ? title[..idx] : title;
    }
}
