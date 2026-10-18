using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class ScreenStateTests
{
    private static readonly DateTime NineAm = new(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);

    // ── Switches ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A switch is the entry in front changing: another title of the same app is one (a new tab, a new document), the
    /// same window polled again is not, and the first window ever seen is where counting starts, not a switch into it.
    /// </summary>
    [Fact]
    public void ASwitchIsTheEntryInFrontChanging()
    {
        var screen = new ScreenState();

        screen.Polled(NineAm, Present(Front("chrome", "Mail")));
        Assert.False(ForegroundOf(screen).IsSwitch);

        screen.Polled(NineAm.AddSeconds(5), Present(Front("chrome", "Mail")));
        Assert.False(ForegroundOf(screen).IsSwitch);

        screen.ForegroundChanged(Front("chrome", "YouTube"));
        Assert.True(ForegroundOf(screen).IsSwitch);

        screen.ForegroundChanged(Front("code", "Project"));
        Assert.True(ForegroundOf(screen).IsSwitch);
    }

    /// <summary>A switch belongs to the span it begins. Every later span of the same window is it staying in front.</summary>
    [Fact]
    public void TakeForSpan_HandsTheSwitchOutOnce()
    {
        var screen = new ScreenState();
        screen.Polled(NineAm, Present(Front("chrome", "Mail")));
        screen.ForegroundChanged(Front("code", "Project"));

        Assert.True(Assert.Single(screen.TakeForSpan()).IsSwitch);
        Assert.False(Assert.Single(screen.TakeForSpan()).IsSwitch);
    }

    /// <summary>
    /// Stepping away — or locking the screen — and coming back to the window that was in front is not a switch; the
    /// user did not change what they were doing.
    /// </summary>
    [Fact]
    public void ComingBackToTheSameWindow_IsNotASwitch()
    {
        var screen = new ScreenState();
        screen.Polled(NineAm, Present(Front("code", "Project")));

        screen.Polled(NineAm.AddSeconds(40), Away(TimeSpan.FromSeconds(35)));
        screen.Polled(NineAm.AddSeconds(45), PollResult.Locked);
        screen.Polled(NineAm.AddSeconds(90), Present(Front("code", "Project")));

        Assert.False(ForegroundOf(screen).IsSwitch);
    }

    /// <summary>
    /// A window that may not be recorded is still a window in front. Going to a private window and back is two
    /// switches; only the one back to the recorded window can be counted, and it must be.
    /// </summary>
    [Fact]
    public void SwitchingBackFromAnUnrecordedWindow_IsASwitch()
    {
        var screen = new ScreenState();
        screen.Polled(NineAm, Present(Front("code", "Project")));

        screen.ForegroundChanged(null);
        Assert.Empty(screen.Observations);

        screen.ForegroundChanged(Front("code", "Project"));
        Assert.True(ForegroundOf(screen).IsSwitch);
    }

    /// <summary>
    /// A process is observed once at a time. When a background window comes to the front between polls, its background
    /// observation goes — or the same entry would be credited as active and background for the same seconds, and an
    /// entry could pass 60 seconds a minute.
    /// </summary>
    [Fact]
    public void ForegroundChanged_DropsTheBackgroundObservationOfTheProcessThatCameToTheFront()
    {
        var screen = new ScreenState();
        screen.Polled(NineAm, Present(Front("code", "Project"), Behind("chrome", "Docs"), Behind("spotify", "Playlist")));

        screen.ForegroundChanged(Front("chrome", "Docs"));

        Assert.Equal(
            [("chrome", false), ("spotify", true)],
            screen.Observations.Select(o => (o.Sample.ProcessName, o.IsBackground)));
    }

    // ── Idle backdating ──────────────────────────────────────────────────────

    /// <summary>
    /// The first poll to find the user gone answers where their last input was, so the seconds credited after it can be
    /// taken back. Only the first: once away, nothing more is credited, and asking again would be a no-op at best.
    /// </summary>
    [Fact]
    public void Polled_AnswersTheLastInput_OnTheFirstPollThatFindsTheUserGone()
    {
        var screen = new ScreenState();
        screen.Polled(NineAm, Present(Front("code", "Project")));

        Assert.Equal(NineAm.AddSeconds(5), screen.Polled(NineAm.AddSeconds(38), Away(TimeSpan.FromSeconds(33))));
        Assert.Null(screen.Polled(NineAm.AddSeconds(43), Away(TimeSpan.FromSeconds(38))));
    }

    /// <summary>A locked screen is an explicit "gone", with nothing idle about it to take back.</summary>
    [Fact]
    public void Polled_TakesNothingBack_WhenTheScreenLocks()
    {
        var screen = new ScreenState();
        screen.Polled(NineAm, Present(Front("code", "Project")));

        Assert.Null(screen.Polled(NineAm.AddSeconds(5), PollResult.Locked));
    }

    /// <summary>
    /// Watching without touching anything is presence (sound in the foreground), so it stays credited: when the film
    /// ends and the user turns out to be gone, time is taken back only from the last poll that heard them there.
    /// </summary>
    [Fact]
    public void Polled_KeepsPassivePresence_WhenTheUserTurnsOutToBeGone()
    {
        var screen = new ScreenState();
        screen.Polled(NineAm, Present(Front("vlc", "Film")));
        screen.Polled(NineAm.AddMinutes(30), Passive(Front("vlc", "Film"), TimeSpan.FromMinutes(29)));

        var from = screen.Polled(NineAm.AddMinutes(30).AddSeconds(5), Away(TimeSpan.FromMinutes(29).Add(TimeSpan.FromSeconds(5))));

        Assert.Equal(NineAm.AddMinutes(30), from);
    }

    /// <summary>A foreground change is input: the user is there, so going idle after it is backdated too.</summary>
    [Fact]
    public void ForegroundChanged_CountsAsPresence()
    {
        var screen = new ScreenState();
        screen.Polled(NineAm, Away(TimeSpan.FromMinutes(5)));

        screen.ForegroundChanged(Front("code", "Project"));

        Assert.Equal(NineAm.AddSeconds(10), screen.Polled(NineAm.AddSeconds(45), Away(TimeSpan.FromSeconds(35))));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Observation ForegroundOf(ScreenState screen) => Assert.Single(screen.Observations, o => !o.IsBackground);

    private static PollResult Present(params Observation[] observations) =>
        new([.. observations], IsPresent: true, observations.FirstOrDefault(o => !o.IsBackground)?.Sample);

    private static PollResult Passive(Observation foreground, TimeSpan idle) =>
        new([foreground], IsPresent: true, foreground.Sample, IsPassive: true, IdleTime: idle);

    private static PollResult Away(TimeSpan idle) => new([], IsPresent: false, null, IdleTime: idle);

    private static Observation Front(string process, string title) =>
        new(Sample(process, title), 0, IsBackground: false, IsPlayingSound: false, IsFullscreen: false);

    private static Observation Behind(string process, string title) =>
        new(Sample(process, title), 1, IsBackground: true, IsPlayingSound: false, IsFullscreen: false);

    private static ActivitySample Sample(string process, string title) => new()
    {
        ProcessName = process,
        WindowTitle = title,
        ExecutablePath = $@"C:\apps\{process}.exe"
    };
}
