using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The tray's start-up order (review L8): recording needs no network, so with a saved token it starts before the session
/// restore answers — which on a network that swallows the request takes the whole 30 s HTTP timeout.
/// </summary>
public sealed class SessionStartupTests
{
    private readonly List<string> _steps = [];
    private readonly TaskCompletionSource<bool> _restore = new();

    private SessionStartup Startup(bool savedToken) => new(
        hasSavedToken: () => savedToken,
        restore: () =>
        {
            _steps.Add("restore");
            return _restore.Task;
        },
        startTracking: () => _steps.Add("track"),
        showConfigNotice: () => _steps.Add("notice"),
        showLogin: () => _steps.Add("login"));

    [Fact]
    public async Task WithASavedToken_TrackingStartsBeforeTheRestoreAnswers()
    {
        var run = Startup(savedToken: true).RunAsync(() => _steps.Add("started"));

        Assert.Equal(["track", "started", "notice", "restore"], _steps);
        Assert.False(run.IsCompleted);

        _restore.SetResult(true);
        await run;

        Assert.DoesNotContain("login", _steps);
    }

    /// <summary>The server refused the token: the sign-in form, while recording carries on into the outbox.</summary>
    [Fact]
    public async Task ARefusedRestore_AsksForASignIn_AfterTrackingStarted()
    {
        _restore.SetResult(false);

        await Startup(savedToken: true).RunAsync(() => _steps.Add("started"));

        Assert.Equal(["track", "started", "notice", "restore", "login"], _steps);
    }

    [Fact]
    public async Task WithoutAToken_NothingStarts_AndTheNoticeComesBeforeTheSignIn()
    {
        await Startup(savedToken: false).RunAsync(() => _steps.Add("started"));

        Assert.Equal(["started", "notice", "login"], _steps);
    }
}
