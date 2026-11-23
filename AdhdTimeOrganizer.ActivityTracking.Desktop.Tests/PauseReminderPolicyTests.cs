using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class PauseReminderPolicyTests
{
    /// <summary>When the pause began; every test counts from here.</summary>
    private static readonly DateTime Since = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan AtTheKeyboard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Away = TimeSpan.FromMinutes(20);

    private readonly PauseReminderPolicy _policy = new();

    private static PauseSnapshot UntilResumed(TimeSpan? idle = null, DateTime? since = null) =>
        new(DateTime.MaxValue, since ?? Since, idle ?? AtTheKeyboard);

    private static PauseSnapshot Timed(TimeSpan forHow) => new(Since + forHow, Since, AtTheKeyboard);

    private static readonly PauseSnapshot Running = new(null, null, AtTheKeyboard);

    private PauseReminderChange At(TimeSpan after, PauseSnapshot snapshot, bool enabled = true) =>
        _policy.Evaluate(Since + after, snapshot, enabled);

    private static readonly PauseReminderChange Nothing = new(null, false);

    // ---- an open-ended pause ----------------------------------------------

    [Fact]
    public void AnIndefinitePause_IsRemindedAbout_AtThreeHours_ThenEverySix()
    {
        Assert.Equal(Nothing, At(TimeSpan.Zero, UntilResumed()));
        Assert.Equal(Nothing, At(PauseReminderPolicy.FirstAfter - TimeSpan.FromSeconds(2), UntilResumed()));

        Assert.Equal(new PauseReminderChange(TimeSpan.FromHours(3), false), At(TimeSpan.FromHours(3), UntilResumed()));
        Assert.Equal(Nothing, At(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(2), UntilResumed()));
        Assert.Equal(Nothing, At(TimeSpan.FromHours(9) - TimeSpan.FromSeconds(2), UntilResumed()));

        Assert.Equal(new PauseReminderChange(TimeSpan.FromHours(9), false), At(TimeSpan.FromHours(9), UntilResumed()));
        Assert.Equal(TimeSpan.FromHours(15), At(TimeSpan.FromHours(15), UntilResumed()).Show);
    }

    /// <summary>A reminder to an empty desk is buried by the time anyone looks: it waits for input, then comes at once.</summary>
    [Fact]
    public void AReminder_WaitsForSomeoneAtTheKeyboard()
    {
        Assert.Null(At(TimeSpan.FromHours(3), UntilResumed(idle: Away)).Show);
        Assert.Null(At(TimeSpan.FromHours(7), UntilResumed(idle: PauseReminderPolicy.PresentWithin)).Show);

        Assert.Equal(TimeSpan.FromHours(7.5), At(TimeSpan.FromHours(7.5), UntilResumed()).Show);
        // Six hours from when it was shown, not from when it was due.
        Assert.Null(At(TimeSpan.FromHours(13), UntilResumed()).Show);
        Assert.NotNull(At(TimeSpan.FromHours(13.5), UntilResumed()).Show);
    }

    /// <summary>"Keep paused" only closes the toast: the next one keeps its six hours.</summary>
    [Fact]
    public void KeepPaused_DoesNotBringTheNextReminderForward()
    {
        At(TimeSpan.FromHours(3), UntilResumed());
        _policy.Answered();

        Assert.Equal(Nothing, At(TimeSpan.FromHours(4), UntilResumed()));
        Assert.NotNull(At(TimeSpan.FromHours(9), UntilResumed()).Show);
    }

    // ---- a timed pause ----------------------------------------------------

    [Fact]
    public void ATimedPause_IsNeverRemindedAbout_ItEndsByItself()
    {
        Assert.Equal(Nothing, At(TimeSpan.FromHours(4), Timed(TimeSpan.FromHours(21))));
        Assert.Equal(Nothing, At(TimeSpan.FromHours(20), Timed(TimeSpan.FromHours(21))));
    }

    /// <summary>"Until I resume" turned into "until tomorrow morning": the reminder still out no longer applies.</summary>
    [Fact]
    public void AnIndefinitePauseMadeTimed_WithdrawsItsReminder()
    {
        Assert.NotNull(At(TimeSpan.FromHours(3), UntilResumed()).Show);

        Assert.Equal(new PauseReminderChange(null, true), At(TimeSpan.FromHours(4), Timed(TimeSpan.FromHours(21))));
        Assert.Equal(Nothing, At(TimeSpan.FromHours(4.1), Timed(TimeSpan.FromHours(21))));
    }

    // ---- resume -----------------------------------------------------------

    [Fact]
    public void Resuming_WithdrawsTheReminder_AndTheNextPauseStartsAfresh()
    {
        Assert.NotNull(At(TimeSpan.FromHours(3), UntilResumed()).Show);

        Assert.Equal(new PauseReminderChange(null, true), At(TimeSpan.FromHours(3.5), Running));
        Assert.Equal(Nothing, At(TimeSpan.FromHours(3.6), Running));

        // Paused again an hour later: three hours from then, not six from the last reminder.
        var again = Since + TimeSpan.FromHours(4.5);
        Assert.Equal(Nothing, At(TimeSpan.FromHours(7), UntilResumed(since: again)));
        Assert.Equal(TimeSpan.FromHours(3), At(TimeSpan.FromHours(7.5), UntilResumed(since: again)).Show);
    }

    /// <summary>Resumed and paused again between two ticks of the tray's timer: the new start says it is a new pause.</summary>
    [Fact]
    public void APauseResumedAndRepausedBetweenTicks_IsANewPause()
    {
        Assert.NotNull(At(TimeSpan.FromHours(3), UntilResumed()).Show);

        var again = Since + TimeSpan.FromHours(5);
        Assert.Null(At(TimeSpan.FromHours(5), UntilResumed(since: again)).Show);
        Assert.Equal(TimeSpan.FromHours(3), At(TimeSpan.FromHours(8), UntilResumed(since: again)).Show);
    }

    // ---- a restart in the middle ------------------------------------------

    /// <summary>The pause's start is saved, so a tracker restarted ten hours in knows it is ten hours old.</summary>
    [Fact]
    public void ARestartMidPause_KnowsHowOldThePauseIs()
    {
        var restarted = new PauseReminderPolicy();

        Assert.Equal(TimeSpan.FromHours(10), restarted.Evaluate(Since + TimeSpan.FromHours(10), UntilResumed(), true).Show);
    }

    /// <summary>The start-up balloon already said "paused": it counts as the reminder, and the next is six hours on.</summary>
    [Fact]
    public void ARestartMidPause_CountsItsStartUpBalloonAsTheReminder()
    {
        var restarted = new PauseReminderPolicy();
        restarted.Announced(Since + TimeSpan.FromHours(10), UntilResumed());

        Assert.Null(restarted.Evaluate(Since + TimeSpan.FromHours(10), UntilResumed(), true).Show);
        Assert.Null(restarted.Evaluate(Since + TimeSpan.FromHours(15.9), UntilResumed(), true).Show);
        Assert.Equal(TimeSpan.FromHours(16), restarted.Evaluate(Since + TimeSpan.FromHours(16), UntilResumed(), true).Show);
    }

    /// <summary>A config from before the start was saved: the pause is counted from the first time it is seen.</summary>
    [Fact]
    public void APauseWithoutASavedStart_IsCountedFromWhenItWasFirstSeen()
    {
        var noStart = new PauseSnapshot(DateTime.MaxValue, null, AtTheKeyboard);

        Assert.Equal(Nothing, At(TimeSpan.Zero, noStart));
        Assert.Equal(Nothing, At(TimeSpan.FromHours(2.9), noStart));
        Assert.Equal(TimeSpan.FromHours(3), At(TimeSpan.FromHours(3), noStart).Show);
    }

    // ---- the switch -------------------------------------------------------

    [Fact]
    public void SwitchedOff_SaysNothing()
    {
        Assert.Equal(Nothing, At(TimeSpan.FromHours(3), UntilResumed(), enabled: false));
        Assert.Equal(Nothing, At(TimeSpan.FromHours(30), UntilResumed(), enabled: false));
    }

    /// <summary>Turning it off withdraws the reminder that is out; on again, the next keeps its six hours from the last.</summary>
    [Fact]
    public void SwitchingOff_WithdrawsTheReminderOut_AndOnAgainCarriesOn()
    {
        Assert.NotNull(At(TimeSpan.FromHours(3), UntilResumed()).Show);

        Assert.Equal(new PauseReminderChange(null, true), At(TimeSpan.FromHours(4), UntilResumed(), enabled: false));
        Assert.Equal(Nothing, At(TimeSpan.FromHours(5), UntilResumed(), enabled: false));

        Assert.Equal(Nothing, At(TimeSpan.FromHours(8), UntilResumed()));
        Assert.Equal(TimeSpan.FromHours(9), At(TimeSpan.FromHours(9), UntilResumed()).Show);
    }
}
