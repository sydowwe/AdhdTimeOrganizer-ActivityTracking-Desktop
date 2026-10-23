using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services.Matching;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// What a window may be recorded as, decided before anything reaches the outbox: dropped, recorded with its title
/// hidden, or recorded as it is. The user's <see cref="LocalRule"/>s and private browser windows decide it; the
/// server's rules are applied later, at send time (<see cref="TrackerRules"/>).
/// </summary>
/// <remarks>
/// <para>
/// An ignore rule beats a hide-title rule. A hidden title becomes "&lt;app&gt; - PRIVATE" — the product name, or
/// the process name when there is none — or the window is dropped, as <see cref="AppConfig.PrivateWindowHandling"/>
/// says. Process and product name stay, so a server rule written on the process still maps the time; one written
/// on the title no longer matches it.
/// </para>
/// <para>
/// Private windows are recognised by what the browser writes into its title: Firefox ends it with "Private
/// Browsing", Edge puts "[InPrivate]" in it. Those are the English strings; a localized Firefox writes its own,
/// which a hide-title rule on that text covers. Chrome's Incognito window has no marker in its title at all.
/// </para>
/// <para>Runs on the poll loop; not thread-safe (the matcher's regex cache).</para>
/// </remarks>
public sealed class PrivacyFilter
{
    private const string PrivateSuffix = " - PRIVATE";

    private readonly PatternMatcher _matcher = new();

    /// <summary>The title to record, or null to record nothing.</summary>
    public string? Apply(AppConfig config, string processName, string? productName, string? rawTitle, string normalizedTitle)
    {
        var hideTitle = config.HidePrivateWindows && IsPrivateWindow(rawTitle);

        foreach (var rule in config.LocalRules)
        {
            if (!Matches(rule, processName, rawTitle, normalizedTitle)) continue;
            if (rule.Action == LocalRuleAction.Ignore) return null;
            hideTitle = true;
        }

        if (!hideTitle) return normalizedTitle;
        return config.PrivateWindowHandling == PrivateWindowHandling.Drop ? null : PrivateTitle(processName, productName);
    }

    internal static bool IsPrivateWindow(string? rawTitle) =>
        rawTitle is not null
        && (rawTitle.TrimEnd().EndsWith("Private Browsing", StringComparison.OrdinalIgnoreCase)
            || rawTitle.Contains("[InPrivate]", StringComparison.OrdinalIgnoreCase));

    internal static string PrivateTitle(string processName, string? productName) =>
        (string.IsNullOrWhiteSpace(productName) ? processName : productName.Trim()) + PrivateSuffix;

    /// <summary>Whether <paramref name="rule"/> names anything at all; one that does not would match every window.</summary>
    public static bool IsUsable(LocalRule rule) => !string.IsNullOrEmpty(rule.ProcessName) || rule.WindowTitle is not null;

    private bool Matches(LocalRule rule, string processName, string? rawTitle, string normalizedTitle)
    {
        if (!IsUsable(rule)) return false;

        if (!string.IsNullOrEmpty(rule.ProcessName)
            && !_matcher.Matches(processName, rule.ProcessName, rule.ProcessNameMatchType))
            return false;

        return rule.WindowTitle switch
        {
            null => true,
            "" => string.IsNullOrWhiteSpace(normalizedTitle),
            var pattern => _matcher.Matches(normalizedTitle, pattern, rule.WindowTitleMatchType)
                           || (rawTitle is not null && _matcher.Matches(rawTitle.Trim(), pattern, rule.WindowTitleMatchType))
        };
    }
}
