using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class UploadHealthPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    private readonly UploadHealthPolicy _policy = new();

    private static UploadSnapshot Healthy() => new(HasSession: true, IsPaused: false, 0, null, 0);

    private static UploadSnapshot StalledFor(TimeSpan age, int pending = 90) =>
        Healthy() with { PendingWindows = pending, OldestPendingUtc = Now - age };

    private static UploadSnapshot SignedOut(int pending = 5) =>
        Healthy() with { HasSession = false, PendingWindows = pending, OldestPendingUtc = Now.AddMinutes(-pending) };

    private UploadHealthChange At(TimeSpan after, UploadSnapshot snapshot, bool enabled = true) =>
        _policy.Evaluate(Now + after, snapshot, enabled);

    /// <summary>A stall first seen at <see cref="Now"/>, evaluated again once its grace has run out.</summary>
    private UploadHealthChange PastTheStallGrace(UploadSnapshot snapshot)
    {
        At(TimeSpan.Zero, snapshot);
        return At(UploadHealthPolicy.StallGrace, snapshot);
    }

    [Fact]
    public void AHealthyTracker_SaysNothing()
    {
        Assert.Equal(new UploadHealthChange(null, false), At(TimeSpan.Zero, Healthy()));
        Assert.Equal(new UploadHealthChange(null, false), At(TimeSpan.FromHours(30), Healthy()));
    }

    // ---- signed out -------------------------------------------------------

    /// <summary>Signing out on purpose and cancelling the form is not reported the moment it happens.</summary>
    [Fact]
    public void SignedOut_IsReported_OnceMinutesHaveWaitedPastTheGrace()
    {
        Assert.Null(At(TimeSpan.Zero, SignedOut()).Show);
        Assert.Null(At(UploadHealthPolicy.SignedOutGrace - TimeSpan.FromSeconds(1), SignedOut()).Show);

        var notice = At(UploadHealthPolicy.SignedOutGrace, SignedOut(pending: 14)).Show;

        Assert.Equal(new UploadNotice(UploadProblem.SignedOut, 14, TimeSpan.Zero, 0), notice);
    }

    [Fact]
    public void SignedOut_WithNothingWaiting_IsNotAProblem()
    {
        var idle = Healthy() with { HasSession = false };

        Assert.Null(At(TimeSpan.Zero, idle).Show);
        Assert.Null(At(TimeSpan.FromHours(3), idle).Show);
    }

    [Fact]
    public void SigningIn_WithdrawsTheNotice()
    {
        At(TimeSpan.Zero, SignedOut());
        Assert.NotNull(At(UploadHealthPolicy.SignedOutGrace, SignedOut()).Show);

        var change = At(TimeSpan.FromMinutes(11), StalledFor(TimeSpan.FromMinutes(11), pending: 11));

        Assert.True(change.Withdraw);
        Assert.Null(change.Show);
    }

    /// <summary>A grace that ran out while the switch was off is not restarted by turning it on.</summary>
    [Fact]
    public void TheGrace_RunsWhateverTheSwitchSays()
    {
        At(TimeSpan.Zero, SignedOut(), enabled: false);

        Assert.NotNull(At(UploadHealthPolicy.SignedOutGrace, SignedOut(), enabled: true).Show);
    }

    // ---- stalled ----------------------------------------------------------

    [Fact]
    public void Stalled_IsReported_WhenTheOldestMinuteIsADayOld_AndStaysSoPastTheGrace()
    {
        var young = StalledFor(UploadHealthPolicy.StallAfter - TimeSpan.FromHours(1));
        Assert.Null(At(TimeSpan.Zero, young).Show);
        Assert.Null(At(UploadHealthPolicy.StallGrace, young).Show);

        var overdue = StalledFor(TimeSpan.FromHours(26), pending: 300);
        Assert.Null(At(TimeSpan.FromMinutes(20), overdue).Show);
        Assert.Null(At(TimeSpan.FromMinutes(20) + UploadHealthPolicy.StallGrace - TimeSpan.FromSeconds(1), overdue).Show);

        var notice = At(TimeSpan.FromMinutes(20) + UploadHealthPolicy.StallGrace, overdue).Show;

        var age = TimeSpan.FromHours(26) + TimeSpan.FromMinutes(20) + UploadHealthPolicy.StallGrace;
        Assert.Equal(new UploadNotice(UploadProblem.Stalled, 300, age, 0), notice);
    }

    /// <summary>
    /// A PC back from a weekend asleep holds a minute from before it: days old, but nothing has tried to send it yet. It
    /// goes out within the grace, so there was no stall to report, and the next one starts its own grace.
    /// </summary>
    [Fact]
    public void ABacklogFoundOnWaking_ThatDrainsInTheGrace_SaysNothing()
    {
        var waking = StalledFor(TimeSpan.FromHours(60), pending: 2);

        Assert.Equal(new UploadHealthChange(null, false), At(TimeSpan.Zero, waking));
        Assert.Equal(new UploadHealthChange(null, false), At(TimeSpan.FromMinutes(1), Healthy()));

        Assert.Null(At(UploadHealthPolicy.StallGrace, waking).Show);
    }

    /// <summary>The outbox drained: the connection is back, and a toast saying otherwise must leave.</summary>
    [Fact]
    public void Stalled_ResolvingWithdrawsTheNotice()
    {
        PastTheStallGrace(StalledFor(TimeSpan.FromHours(25)));

        var change = At(TimeSpan.FromMinutes(20), Healthy());

        Assert.True(change.Withdraw);
        Assert.Null(change.Show);
        Assert.False(At(TimeSpan.FromMinutes(21), Healthy()).Withdraw); // once
    }

    /// <summary>A day signed out is also a day of unsent minutes; the notice names the cause, not a stall.</summary>
    [Fact]
    public void ADaySignedOut_IsNamedAsSignedOut_NotAsAStall()
    {
        var snapshot = SignedOut() with { OldestPendingUtc = Now.AddHours(-30) };
        At(TimeSpan.Zero, snapshot);

        var notice = At(UploadHealthPolicy.SignedOutGrace, snapshot).Show;

        Assert.Equal(UploadProblem.SignedOut, notice!.Problem);
    }

    // ---- dropped ----------------------------------------------------------

    [Fact]
    public void Dropped_NeedsABatch_AndCountsFromTheLastNotice()
    {
        Assert.Null(At(TimeSpan.Zero, Healthy() with { RejectedWindows = UploadHealthPolicy.DroppedBatch - 1 }).Show);

        var notice = At(TimeSpan.FromMinutes(1), Healthy() with { RejectedWindows = 3 }).Show;
        Assert.Equal(new UploadNotice(UploadProblem.Dropped, 0, TimeSpan.Zero, 3), notice);

        // Two more are not a batch: the count started again at the notice.
        Assert.Null(At(TimeSpan.FromDays(2), Healthy() with { RejectedWindows = 5 }).Show);
        Assert.Equal(3, At(TimeSpan.FromDays(2), Healthy() with { RejectedWindows = 6 }).Show!.Dropped);
    }

    /// <summary>Refused minutes are gone for good; "things look better" doesn't make the notice untrue.</summary>
    [Fact]
    public void Dropped_IsNotWithdrawn_WhenTheNextWindowIsSent()
    {
        At(TimeSpan.Zero, Healthy() with { RejectedWindows = 3 });

        Assert.False(At(TimeSpan.FromHours(1), Healthy() with { RejectedWindows = 3 }).Withdraw);
    }

    /// <summary>A stall is in the way of the drops notice, not instead of it.</summary>
    [Fact]
    public void Dropped_WaitsForAHigherProblemToResolve()
    {
        var both = StalledFor(TimeSpan.FromHours(25)) with { RejectedWindows = 4 };

        Assert.Null(At(TimeSpan.Zero, both).Show); // not during the stall's grace either
        Assert.Equal(UploadProblem.Stalled, At(UploadHealthPolicy.StallGrace, both).Show!.Problem);
        Assert.Null(At(TimeSpan.FromHours(1), both).Show);

        var change = At(TimeSpan.FromHours(2), Healthy() with { RejectedWindows = 4 });

        Assert.Equal(UploadProblem.Dropped, change.Show!.Problem);
        Assert.Equal(4, change.Show.Dropped);
    }

    // ---- once a day -------------------------------------------------------

    [Fact]
    public void ALastingProblem_IsShownOnce_AndAgainOnlyAfterADay()
    {
        Assert.NotNull(PastTheStallGrace(StalledFor(TimeSpan.FromHours(25))).Show);

        for (var hours = 1; hours < 24; hours++)
            Assert.Null(At(TimeSpan.FromHours(hours), StalledFor(TimeSpan.FromHours(25 + hours))).Show);

        // The snapshots are built against the first instant, so the oldest minute is a day older again by now.
        var repeat = UploadHealthPolicy.StallGrace + UploadHealthPolicy.RepeatEvery;
        var again = At(repeat, StalledFor(TimeSpan.FromHours(49))).Show;
        Assert.Equal(TimeSpan.FromHours(73) + UploadHealthPolicy.StallGrace, again!.StalledFor);
        Assert.Null(At(repeat + TimeSpan.FromHours(1), StalledFor(TimeSpan.FromHours(50))).Show);
    }

    /// <summary>The daily limit is per episode: past its own grace, a stall that came back is shown the same day.</summary>
    [Fact]
    public void AProblemThatReturns_IsANewEpisode_NotHeldBackByTheDailyLimit()
    {
        PastTheStallGrace(StalledFor(TimeSpan.FromHours(25)));
        At(TimeSpan.FromHours(1), Healthy());

        var returned = StalledFor(TimeSpan.FromHours(24));
        Assert.Null(At(TimeSpan.FromHours(2), returned).Show);
        Assert.NotNull(At(TimeSpan.FromHours(2) + UploadHealthPolicy.StallGrace, returned).Show);
    }

    [Fact]
    public void ADifferentProblem_IsNotHeldBackByTheOnesCooldown()
    {
        Assert.Equal(UploadProblem.Stalled, PastTheStallGrace(StalledFor(TimeSpan.FromHours(25))).Show!.Problem);

        var signedOut = SignedOut();
        At(TimeSpan.FromHours(1), signedOut);
        var change = At(TimeSpan.FromHours(1) + UploadHealthPolicy.SignedOutGrace, signedOut);

        Assert.Equal(UploadProblem.SignedOut, change.Show!.Problem);
    }

    // ---- paused and switched off -----------------------------------------

    [Fact]
    public void WhilePaused_NothingIsShown_UntilTheResume()
    {
        var paused = StalledFor(TimeSpan.FromHours(30)) with { IsPaused = true };

        Assert.Null(At(TimeSpan.Zero, paused).Show);
        Assert.Null(At(TimeSpan.FromHours(5), paused).Show);

        Assert.Equal(UploadProblem.Stalled, At(TimeSpan.FromHours(6), paused with { IsPaused = false }).Show!.Problem);
    }

    [Fact]
    public void AProblemThatResolvesDuringAPause_StillWithdrawsItsToast()
    {
        PastTheStallGrace(StalledFor(TimeSpan.FromHours(25)));

        Assert.True(At(TimeSpan.FromHours(1), Healthy() with { IsPaused = true }).Withdraw);
    }

    [Fact]
    public void WhenSwitchedOff_NothingIsShown_AndTurningOffWithdrawsWhatIsOut()
    {
        var stalled = StalledFor(TimeSpan.FromHours(30));
        Assert.Null(At(TimeSpan.Zero, stalled, enabled: false).Show);
        Assert.Null(At(UploadHealthPolicy.StallGrace, stalled, enabled: false).Show);

        Assert.NotNull(At(UploadHealthPolicy.StallGrace, stalled, enabled: true).Show); // the grace ran while it was off
        var change = At(UploadHealthPolicy.StallGrace + TimeSpan.FromMinutes(1), stalled, enabled: false);

        Assert.True(change.Withdraw);
        Assert.Null(change.Show);
    }
}
