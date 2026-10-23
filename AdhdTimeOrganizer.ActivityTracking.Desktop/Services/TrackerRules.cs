using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services.Matching;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// The user's server rules as last downloaded, and the before-upload filter built on them: an entry whose
/// winning rule ignores is taken out of its window before the window is sent.
/// </summary>
/// <remarks>
/// <para>
/// Applied at <b>send</b> time, not when a window is recorded, so every window is filtered with the freshest
/// rules there are — including the ones that queued up while offline, recorded before a rule existed. The local
/// <see cref="LocalRule"/>s are different: they are the user's own and never stale, so they apply at recording
/// time and their titles never reach the outbox at all.
/// </para>
/// <para>
/// Memory only. After a start, a sign-out or a server change nothing is known until the first download, and the
/// send loop holds its pass until then (<see cref="IsKnown"/>) rather than upload what a rule might cover.
/// </para>
/// <para>
/// <see cref="Filter"/> runs on the send loop only — the matcher's regex cache is not thread-safe.
/// <see cref="Clear"/> may come from any thread (a sign-out), so the set is swapped under a lock, never mutated.
/// </para>
/// </remarks>
public sealed class TrackerRules
{
    private readonly Lock _lock = new();
    private MappingRuleSet _rules = MappingRuleSet.Empty;
    private string? _etag;
    private DateTime? _checkedUtc;

    /// <summary>Whether a download (or the server saying it has no rules route) happened since the last <see cref="Clear"/>.</summary>
    public bool IsKnown
    {
        get { lock (_lock) return _checkedUtc is not null; }
    }

    public string? ETag
    {
        get { lock (_lock) return _etag; }
    }

    public int Count
    {
        get { lock (_lock) return _rules.Rules.Count; }
    }

    public bool IsDue(DateTime utcNow, TimeSpan interval)
    {
        lock (_lock) return _checkedUtc is not { } checkedUtc || utcNow - checkedUtc >= interval;
    }

    public void Replace(IEnumerable<TrackerRule> rules, string? etag, DateTime utcNow)
    {
        var set = new MappingRuleSet(rules);
        lock (_lock)
        {
            _rules = set;
            _etag = etag;
            _checkedUtc = utcNow;
        }
    }

    /// <summary>The server answered 304: what is held is still current.</summary>
    public void MarkChecked(DateTime utcNow)
    {
        lock (_lock) _checkedUtc = utcNow;
    }

    /// <summary>The rules belong to a user on a server; a sign-out or a new server URL forgets them.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _rules = MappingRuleSet.Empty;
            _etag = null;
            _checkedUtc = null;
        }
    }

    /// <summary>
    /// <paramref name="window"/> without the entries an ignoring rule covers: the same instance when nothing is
    /// dropped, null when everything is (the heartbeat refuses an empty window, so there is nothing to send).
    /// </summary>
    public ActivityWindow? Filter(ActivityWindow window)
    {
        MappingRuleSet rules;
        lock (_lock) rules = _rules;

        if (rules.Rules.Count == 0) return window;

        // Resolved on what the heartbeat resolves on: it stores a missing product or a blank title as "" first.
        var kept = window.Entries
            .Where(e => rules.Resolve(
                e.ProcessName,
                e.ProductName ?? "",
                string.IsNullOrWhiteSpace(e.WindowTitle) ? "" : e.WindowTitle) is not { IsIgnored: true })
            .ToList();

        if (kept.Count == window.Entries.Count) return window;
        return kept.Count == 0 ? null : new ActivityWindow { WindowStart = window.WindowStart, Entries = kept };
    }
}
