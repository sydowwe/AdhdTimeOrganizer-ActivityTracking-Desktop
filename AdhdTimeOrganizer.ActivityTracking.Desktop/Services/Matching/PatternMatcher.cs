using System.Text.RegularExpressions;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services.Matching;

/// <summary>The server's <c>PatternMatchType</c>: same members, same order, sent as the member name.</summary>
public enum PatternMatchType
{
    Exact,
    Wildcard,
    Contains,
    Regex
}

/// <summary>
/// A port of the server's <c>PatternMatcher</c> (Tracking slice, <c>domain/helper/mapping/</c>): one tracked value
/// against one pattern. Exact and Contains ignore case ordinally; Wildcard is an anchored, case-insensitive regex
/// with <c>*</c> and <c>?</c>; Regex is unanchored and case-insensitive; a match that runs past 100 ms is no match.
/// </summary>
/// <remarks>
/// <para>
/// <b>Must answer exactly what the server answers.</b> <c>DesktopMappingRuleSet.vectors.json</c> in the test
/// project is the contract, shared byte for byte with the server repo; change matching there first.
/// </para>
/// <para>
/// One deliberate difference: a pattern that does not parse is no match here instead of an exception. The
/// server validates patterns before storing them, so its rules never take that path; local rules are typed
/// into a form, and a bad one must not stop the poll loop.
/// </para>
/// <para>Not thread-safe (the regex cache): one instance per thread that matches.</para>
/// </remarks>
public sealed class PatternMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Dictionary<(string Pattern, PatternMatchType MatchType), Regex?> _compiled = new();

    public bool Matches(string? value, string pattern, PatternMatchType matchType)
    {
        if (value == null)
            return false;
        return matchType switch
        {
            PatternMatchType.Exact => string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase),
            PatternMatchType.Contains => value.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            PatternMatchType.Wildcard or PatternMatchType.Regex => IsRegexMatch(value, pattern, matchType),
            _ => false
        };
    }

    /// <summary>Whether <paramref name="pattern"/> parses as <paramref name="matchType"/> — for the rules form.</summary>
    public static bool IsValid(string pattern, PatternMatchType matchType) =>
        matchType != PatternMatchType.Regex || TryCreate(pattern, matchType) is not null;

    private bool IsRegexMatch(string value, string pattern, PatternMatchType matchType)
    {
        try
        {
            return GetRegex(pattern, matchType)?.IsMatch(value) ?? false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private Regex? GetRegex(string pattern, PatternMatchType matchType)
    {
        if (_compiled.TryGetValue((pattern, matchType), out var regex))
            return regex;

        regex = TryCreate(pattern, matchType);
        _compiled[(pattern, matchType)] = regex;
        return regex;
    }

    private static Regex? TryCreate(string pattern, PatternMatchType matchType)
    {
        var expression = matchType == PatternMatchType.Wildcard
            ? "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$"
            : pattern;

        try
        {
            return new Regex(expression, RegexOptions.IgnoreCase, MatchTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
