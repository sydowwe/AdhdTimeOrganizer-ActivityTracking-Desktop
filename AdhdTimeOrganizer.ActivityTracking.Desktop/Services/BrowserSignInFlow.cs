using System.Diagnostics;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// The browser sign-in end to end (<see cref="BrowserSignIn"/>): listen on loopback, open the web app's confirm page in the
/// system browser, wait for its one redirect, exchange the code. The listener lives exactly as long as one run.
/// </summary>
/// <param name="openBrowser">Null for the system browser.</param>
public sealed class BrowserSignInFlow(ApiClient apiClient, AppConfig config, Action<Uri>? openBrowser = null)
{
    /// <summary>
    /// Signs in, or says why not. Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled,
    /// and whatever starting the browser throws when there is no browser to start.
    /// </summary>
    public async Task<LoginOutcome> RunAsync(CancellationToken ct)
    {
        var pkce = BrowserSignIn.NewPkce();
        var state = BrowserSignIn.NewState();
        using var listener = new LoopbackRedirectListener(state);

        (openBrowser ?? OpenSystemBrowser)(BrowserSignIn.ConnectPage(config, listener.RedirectUri, pkce.Challenge, state));

        var code = await listener.WaitForCodeAsync(BrowserSignIn.WaitLimit, ct);
        if (code is null)
            return new LoginOutcome(LoginStatus.Failed, Message: "The browser didn't answer in time. Try again.");

        return await apiClient.ExchangeBrowserCodeAsync(code, pkce.Verifier, listener.RedirectUri);
    }

    private static void OpenSystemBrowser(Uri uri) =>
        Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
}
