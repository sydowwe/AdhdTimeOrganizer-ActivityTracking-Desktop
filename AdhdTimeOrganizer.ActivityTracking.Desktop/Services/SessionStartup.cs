namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// The tray's start-up order, apart from WinForms so it can be tested: with a saved token, recording starts at once and the
/// session is restored in the background (review L8); the sign-in form comes only once the restore says there is no session.
/// </summary>
/// <remarks>
/// Recording needs no network. Waiting for <see cref="ApiClient.TryRestoreSessionAsync"/> first left a network that swallows
/// the request (a captive portal, a VPN not up yet) unrecorded for up to the 30 s HTTP timeout. A restore the server refuses
/// ends the session while recording goes on into the outbox, as any sign-out does mid-run. Without a saved token nothing starts
/// before a sign-in, as before.
/// </remarks>
/// <param name="hasSavedToken">Whether a refresh token is saved (<see cref="ApiClient.HasSession"/>).</param>
/// <param name="restore">The session restore; true unless there is no token or the server refused it.</param>
/// <param name="startTracking">Starts recording, unless a saved pause is in force (which it announces).</param>
/// <param name="showConfigNotice">What loading the config had to do, said once — before the sign-in form, which a config
/// restored without its token brings up, so the user knows why.</param>
/// <param name="showLogin">The sign-in form.</param>
public sealed class SessionStartup(
    Func<bool> hasSavedToken,
    Func<Task<bool>> restore,
    Action startTracking,
    Action showConfigNotice,
    Action showLogin)
{
    /// <param name="onStarted">Called once tracking is decided, before the restore is awaited: the tray stops saying "Connecting…".</param>
    public async Task RunAsync(Action onStarted)
    {
        var hadToken = hasSavedToken();
        if (hadToken) startTracking();
        onStarted();
        showConfigNotice();

        var hasSession = hadToken && await restore();
        if (!hasSession) showLogin();
    }
}
