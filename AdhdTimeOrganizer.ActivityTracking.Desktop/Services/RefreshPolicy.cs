using System.Net;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>How one refresh ended, as far as the agent can tell.</summary>
internal abstract record RefreshAttempt
{
    /// <summary>The server answered. <paramref name="HasTokens"/>: the answer carried both tokens.</summary>
    public sealed record Answered(HttpStatusCode Status, bool HasTokens = false) : RefreshAttempt;

    /// <summary>Failed before the request was written: no DNS answer, no connection, no TLS. Nothing was rotated.</summary>
    public sealed record NotSent : RefreshAttempt;

    /// <summary>
    /// Failed after the request was written: a timeout, a dropped connection, an unreadable answer, an exit that
    /// would not wait. The server may have rotated the token and the answer is gone.
    /// </summary>
    public sealed record Lost : RefreshAttempt;

    /// <summary>What a failed send says about whether the request ever left the machine.</summary>
    public static RefreshAttempt FromFailure(Exception ex) =>
        ex is HttpRequestException
        {
            HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
        }
            ? new NotSent()
            : new Lost();
}

internal enum RefreshDecision
{
    /// <summary>Store the new pair.</summary>
    Save,
    /// <summary>The server refused the token: the session is over.</summary>
    SignOut,
    /// <summary>The saved token is still ours to use; this refresh fails and a later one tries again.</summary>
    KeepTokens
}

/// <summary>
/// What a refresh outcome means for the session, and when the same token may be sent again. Pure, like
/// <see cref="DriftPolicy"/> — <see cref="ApiClient"/> does the sending and the clock.
/// </summary>
/// <remarks>
/// <para>Only the server refusing the token (400/401/403) ends the session. Offline, a throttle or a 5xx keeps it.</para>
/// <para>The server rotates the refresh token on every call. For a token client it exchanges a replay of the rotated
/// token whose successor nobody used again — a lost answer, not theft — but only once the rotation is more than 5 s
/// old. Inside those 5 s it answers 409 ("rotated moments ago, try again shortly"); a server from before 409 answers
/// 401 there, which would end the session. So a token that may have been rotated without us seeing the answer is held
/// back for <see cref="ReplayDelay"/>, whichever caller sends it next.</para>
/// </remarks>
internal static class RefreshPolicy
{
    /// <summary>Past the server's 5 s grace, with a second to spare for a request that arrives late.</summary>
    internal static readonly TimeSpan ReplayDelay = TimeSpan.FromSeconds(6);

    public static RefreshDecision Decide(RefreshAttempt attempt) => attempt switch
    {
        RefreshAttempt.Answered { Status: >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices, HasTokens: true } => RefreshDecision.Save,
        RefreshAttempt.Answered { Status: HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => RefreshDecision.SignOut,
        _ => RefreshDecision.KeepTokens
    };

    /// <summary>
    /// Whether the attempt may have rotated the token without us seeing the new one: sent and then lost, a 2xx
    /// without both tokens, a 409, or a 5xx (rotation commits before the access token is minted).
    /// </summary>
    public static bool LeftTheTokenUncertain(RefreshAttempt attempt) => attempt switch
    {
        RefreshAttempt.Lost => true,
        RefreshAttempt.NotSent => false,
        RefreshAttempt.Answered { Status: HttpStatusCode.Conflict } => true,
        RefreshAttempt.Answered { Status: >= HttpStatusCode.InternalServerError } => true,
        RefreshAttempt.Answered answered => (int)answered.Status is >= 200 and < 300 && !answered.HasTokens,
        _ => false
    };

    /// <summary>How long to hold a send back, given how long ago an attempt left the token uncertain (null: never).</summary>
    public static TimeSpan WaitBeforeSending(TimeSpan? sinceUncertain) =>
        sinceUncertain is { } since && since < ReplayDelay ? ReplayDelay - since : TimeSpan.Zero;
}
