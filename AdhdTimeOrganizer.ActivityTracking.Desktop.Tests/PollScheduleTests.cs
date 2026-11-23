using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// When the poll loop polls. The case that mattered: a poll that throws every time used to leave the next poll in the past,
/// and the loop spun — polling, failing and logging on every pass, a core held for as long as the failure lasted.
/// </summary>
public class PollScheduleTests
{
    private const long Interval = 5_000;

    [Fact]
    public void TheFirstPoll_IsDueAtOnce()
    {
        var schedule = new PollSchedule(firstAt: 100, Interval);

        Assert.True(schedule.TakeDue(100));
        Assert.Equal(100 + Interval, schedule.Next);
    }

    /// <summary>
    /// The schedule moves on when a poll is taken, not when it returns: the loop calls nothing after a poll that threw, and
    /// the next pass must still wait an interval.
    /// </summary>
    [Fact]
    public void APollTaken_IsNotDueAgain_UntilAnIntervalLater_WhateverItDid()
    {
        var schedule = new PollSchedule(firstAt: 0, Interval);
        schedule.TakeDue(0); // and the poll throws: nothing else is called

        Assert.False(schedule.TakeDue(1));
        Assert.False(schedule.TakeDue(Interval - 1));
        Assert.True(schedule.TakeDue(Interval));
    }

    [Fact]
    public void OnTime_ItKeepsItsBeat()
    {
        var schedule = new PollSchedule(firstAt: 0, Interval);

        schedule.TakeDue(0);
        schedule.TakeDue(Interval + 30); // a pass that woke a little late

        Assert.Equal(2 * Interval, schedule.Next);
    }

    /// <summary>After a sleep or a hang the next poll is an interval from now, not a burst of polls catching up.</summary>
    [Fact]
    public void AfterASleep_TheNextIsAnIntervalFromNow_NotABurst()
    {
        var schedule = new PollSchedule(firstAt: 0, Interval);
        schedule.TakeDue(0);

        var woke = 100 * Interval + 7;
        Assert.True(schedule.TakeDue(woke));

        Assert.Equal(woke + Interval, schedule.Next);
        Assert.False(schedule.TakeDue(woke + 1));
    }
}
