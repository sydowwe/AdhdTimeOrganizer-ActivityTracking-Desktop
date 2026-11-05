using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// Carries the evening invite from the server's evening moment to a toast: remembers when the user's evening is, and
/// when it comes asks the server what today left unmapped and hands <see cref="UnmappedReviewPolicy"/>'s invite on.
/// The send loop calls <see cref="TickAsync"/> every pass; nearly every pass returns without a request.
/// </summary>
/// <remarks>
/// <para>
/// Why it asks at all: the drift nudge, the tray's "now" and History all read the server's mapping, and time no rule
/// maps is invisible to every one of them. The review itself is the web app's — the user is signed in there and may write
/// any matcher, which the tracker's long-lived token must not — so the invite only says what is waiting and opens the page.
/// </para>
/// <para>
/// It does not depend on drift nudges. It stays quiet while tracking is paused (a pause is the user asking for quiet) and
/// until the user is signed in. A server from before the routes ends it for the session, like the coverage prompt; a
/// failed read is tried again in <see cref="RetryAfter"/>, not on every pass.
/// </para>
/// <para>
/// Nothing here logs a name: counts and durations only.
/// </para>
/// </remarks>
/// <param name="show">Shows the invite; null in tests that only look at what was decided.</param>
/// <param name="isPaused">Whether tracking is paused at the given time.</param>
internal sealed class UnmappedReviewService(
    AppConfig config, ApiClient apiClient, UnmappedReviewStore store, Action<UnmappedInvite>? show, Func<DateTime, bool> isPaused)
{
    /// <summary>The moment moves with the sleep window; an hour is soon enough to follow a change made in the web app.</summary>
    internal static readonly TimeSpan MomentRefreshInterval = TimeSpan.FromHours(1);

    /// <summary>After a failed read, rather than on every ten-second pass.</summary>
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly ILogger _log = Log.ForContext<UnmappedReviewService>();

    /// <summary>The server's next evening; null until asked, and after an evening is dealt with, so the next pass asks again.</summary>
    private Evening? _next;

    /// <summary>The evening just gone, as the server named it with <see cref="_next"/>; null from a server before it.</summary>
    private Evening? _previous;

    private DateTime _momentCheckedUtc = DateTime.MinValue;
    private DateTime _retryAtUtc = DateTime.MinValue;
    private bool _notSupported;

    /// <summary>A sign-out ends the user's evening as far as this session knows; the next sign-in asks again.</summary>
    public void Forget()
    {
        ForgetEvenings();
        _momentCheckedUtc = DateTime.MinValue;
        _retryAtUtc = DateTime.MinValue;
        _notSupported = false;
    }

    public async Task TickAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (!config.UnmappedReviewEnabled || _notSupported || !apiClient.HasSession || isPaused(nowUtc)) return;
        if (nowUtc < _retryAtUtc) return;

        if (_next is null || nowUtc - _momentCheckedUtc >= MomentRefreshInterval || IsSpent(nowUtc))
        {
            if (!await RefreshMomentAsync(nowUtc, ct)) return;
        }

        if (_next is not { } next) return;

        var (evening, timing) = UnmappedReviewPolicy.Pick(nowUtc, _previous, next, store.HandledMomentUtc);
        switch (timing)
        {
            case ReviewTiming.Missed:
                // A held moment gone stale (the tracker slept past it and could not ask since): too late to ring.
                store.MarkHandled(evening.MomentUtc);
                ForgetEvenings();
                _log.Information("The evening review was missed (the tracker was not running or asleep) — skipped");
                break;

            case ReviewTiming.Due:
                await InviteAsync(nowUtc, evening, ct);
                break;
        }
    }

    private void ForgetEvenings()
    {
        _next = null;
        _previous = null;
    }

    /// <summary>
    /// The held next moment has been handled and is past, so the server's next answer is the next evening's. Not asked
    /// for more than once a minute: a server clock a few seconds behind this one still answers with the moment just handled.
    /// </summary>
    private bool IsSpent(DateTime nowUtc) =>
        _next is { } next && store.HandledMomentUtc is { } handled && handled >= next.MomentUtc && nowUtc >= next.MomentUtc
        && nowUtc - _momentCheckedUtc >= TimeSpan.FromMinutes(1);

    private async Task<bool> RefreshMomentAsync(DateTime nowUtc, CancellationToken ct)
    {
        var read = await apiClient.FetchEveningMomentAsync(ct);
        switch (read.Status)
        {
            case CoverageFetch.Fetched when read.Value is { } fetched:
                _next = new Evening(DateTime.SpecifyKind(fetched.MomentUtc, DateTimeKind.Utc), fetched.Date);
                _previous = fetched.PreviousMomentUtc is { } previous
                    ? new Evening(DateTime.SpecifyKind(previous, DateTimeKind.Utc), fetched.PreviousDate)
                    : null;
                _momentCheckedUtc = nowUtc;
                return true;

            case CoverageFetch.NotSupported:
                _notSupported = true;
                _log.Information("The server has no evening-moment route — no evening review");
                return false;

            default:
                _retryAtUtc = nowUtc + RetryAfter;
                return false;
        }
    }

    private async Task InviteAsync(DateTime nowUtc, Evening evening, CancellationToken ct)
    {
        // The evening's own day: past midnight (bed at 01:30) "today" would be the half hour since the date turned.
        var read = await apiClient.FetchUnmappedTodayAsync(evening.Date, ct);
        switch (read.Status)
        {
            case CoverageFetch.Fetched when read.Value is { } today:
                // Handled whether or not there was anything to say: an evening is looked at once.
                store.MarkHandled(evening.MomentUtc);
                ForgetEvenings(); // the server's next answer moves on past this one

                if (UnmappedReviewPolicy.Invite(today) is not { } invite)
                {
                    _log.Information("Evening review: {Unmapped} min unmapped today, nothing worth asking", (int)TimeSpan.FromSeconds(today.UnmappedSeconds).TotalMinutes);
                    break;
                }

                _log.Information("Evening review: inviting, {Unmapped} min unmapped across {Apps} apps", (int)invite.Unmapped.TotalMinutes, invite.Apps.Count + invite.More);
                show?.Invoke(invite);
                break;

            case CoverageFetch.NotSupported:
                _notSupported = true;
                _log.Information("The server has no unmapped-time route — no evening review");
                break;

            default:
                _retryAtUtc = nowUtc + RetryAfter;
                break;
        }
    }
}
