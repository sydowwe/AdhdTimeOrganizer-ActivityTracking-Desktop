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
    public void Stalled_IsReported_WhenTheOldestMinuteIsADayOld()
    {
        Assert.Null(At(TimeSpan.Zero, StalledFor(UploadHealthPolicy.StallAfter - TimeSpan.FromMinutes(1))).Show);

        var notice = At(TimeSpan.Zero, StalledFor(TimeSpan.FromHours(26), pending: 300)).Show;

        Assert.Equal(new UploadNotice(UploadProblem.Stalled, 300, TimeSpan.FromHours(26), 0), notice);
    }

    /// <summary>The outbox drained: the connection is back, and a toast saying otherwise must leave.</summary>
    [Fact]
    public void Stalled_ResolvingWithdrawsTheNotice()
    {
        At(TimeSpan.Zero, StalledFor(TimeSpan.FromHours(25)));

        var change = At(TimeSpan.FromMinutes(5), Healthy());

        Assert.True(change.Withdraw);
        Assert.Null(change.Show);
        Assert.False(At(TimeSpan.FromMinutes(6), Healthy()).Withdraw); // once
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

        Assert.Equal(UploadProblem.Stalled, At(TimeSpan.Zero, both).Show!.Problem);
        Assert.Null(At(TimeSpan.FromHours(1), both).Show);

        var change = At(TimeSpan.FromHours(2), Healthy() with { RejectedWindows = 4 });

        Assert.Equal(UploadProblem.Dropped, change.Show!.Problem);
        Assert.Equal(4, change.Show.Dropped);
    }

    // ---- once a day -------------------------------------------------------

    [Fact]
    public void ALastingProblem_IsShownOnce_AndAgainOnlyAfterADay()
    {
        Assert.NotNull(At(TimeSpan.Zero, StalledFor(TimeSpan.FromHours(25))).Show);

        for (var hours = 1; hours < 24; hours++)
            Assert.Null(At(TimeSpan.FromHours(hours), StalledFor(TimeSpan.FromHours(25 + hours))).Show);

        // The snapshots are built against the first instant, so the oldest minute is a day older again by now.
        var again = At(UploadHealthPolicy.RepeatEvery, StalledFor(TimeSpan.FromHours(49))).Show;
        Assert.Equal(TimeSpan.FromHours(73), again!.StalledFor);
        Assert.Null(At(UploadHealthPolicy.RepeatEvery + TimeSpan.FromHours(1), StalledFor(TimeSpan.FromHours(50))).Show);
    }

    [Fact]
    public void AProblemThatReturns_IsANewEpisode_AndShownAtOnce()
    {
        At(TimeSpan.Zero, StalledFor(TimeSpan.FromHours(25)));
        At(TimeSpan.FromHours(1), Healthy());

        Assert.NotNull(At(TimeSpan.FromHours(2), StalledFor(TimeSpan.FromHours(24))).Show);
    }

    [Fact]
    public void ADifferentProblem_IsNotHeldBackByTheOnesCooldown()
    {
        Assert.Equal(UploadProblem.Stalled, At(TimeSpan.Zero, StalledFor(TimeSpan.FromHours(25))).Show!.Problem);

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
        At(TimeSpan.Zero, StalledFor(TimeSpan.FromHours(25)));

        Assert.True(At(TimeSpan.FromHours(1), Healthy() with { IsPaused = true }).Withdraw);
    }

    [Fact]
    public void WhenSwitchedOff_NothingIsShown_AndTurningOffWithdrawsWhatIsOut()
    {
        Assert.Null(At(TimeSpan.Zero, StalledFor(TimeSpan.FromHours(30)), enabled: false).Show);

        _policy.Evaluate(Now, StalledFor(TimeSpan.FromHours(30)), enabled: true);
        var change = At(TimeSpan.FromMinutes(1), StalledFor(TimeSpan.FromHours(30)), enabled: false);

        Assert.True(change.Withdraw);
        Assert.Null(change.Show);
    }
}
