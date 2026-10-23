using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services.Matching;

/// <summary>
/// A port of the server's <c>DesktopMappingRuleSet</c>: the user's active rules, ordered the way the heartbeat
/// walks them, and the one answer to "which rule covers this (process, product, title)?" — <b>first match wins</b>.
/// </summary>
/// <remarks>
/// <para>
/// The tracker uses the answer for one thing: an entry whose winning rule ignores is dropped before upload, so its
/// title never leaves the machine. It needs <em>every</em> rule for that, not only the ignoring ones, because a
/// narrower rule that maps to an activity shadows a broad ignore rule ("ignore anything titled *GitHub*, except in
/// Chrome") and the entry must then go up.
/// </para>
/// <para>
/// <b>Specificity first, then id</b> — the number of pattern columns a rule sets, descending, then the lower id.
/// The server sends the rules already in that order; they are sorted again here so the answer never depends on it.
/// </para>
/// <para>
/// <b>Must answer exactly what the server answers</b>, or it either uploads a title the user ignored or drops a
/// minute the server would have attributed. <c>DesktopMappingRuleSet.vectors.json</c> is the contract. The server
/// keeps applying the same rules on the heartbeat regardless; this is the before-upload half.
/// </para>
/// </remarks>
public sealed class MappingRuleSet
{
    private readonly List<TrackerRule> _ordered;
    private readonly PatternMatcher _matcher = new();

    public MappingRuleSet(IEnumerable<TrackerRule> activeRules)
    {
        _ordered = OrderBySpecificity(activeRules);
    }

    public static MappingRuleSet Empty { get; } = new([]);

    public IReadOnlyList<TrackerRule> Rules => _ordered;

    /// <summary>
    /// The rule the heartbeat would pick for this tuple, or null. Pass what the heartbeat resolves: it stores a
    /// missing product or title as <c>""</c> before matching, and <c>""</c> is not null to every pattern
    /// (<c>*</c> matches it).
    /// </summary>
    public TrackerRule? Resolve(string? processName, string? productName, string? windowTitle)
    {
        foreach (var rule in _ordered)
            if (Matches(rule, processName, productName, windowTitle))
                return rule;

        return null;
    }

    private bool Matches(TrackerRule rule, string? processName, string? productName, string? windowTitle)
    {
        if (rule.ProcessName != null && rule.ProcessNameMatchType != null)
            if (!_matcher.Matches(processName, rule.ProcessName, rule.ProcessNameMatchType.Value))
                return false;

        if (rule.ProductName != null && rule.ProductNameMatchType != null)
            if (!_matcher.Matches(productName, rule.ProductName, rule.ProductNameMatchType.Value))
                return false;

        if (rule.WindowTitle != null && rule.WindowTitleMatchType != null)
            if (!_matcher.Matches(windowTitle, rule.WindowTitle, rule.WindowTitleMatchType.Value))
                return false;

        return true;
    }

    private static List<TrackerRule> OrderBySpecificity(IEnumerable<TrackerRule> rules) =>
        rules
            .OrderByDescending(r =>
                (r.ProcessName != null ? 1 : 0) +
                (r.ProductName != null ? 1 : 0) +
                (r.WindowTitle != null ? 1 : 0))
            .ThenBy(r => r.Id)
            .ToList();
}
