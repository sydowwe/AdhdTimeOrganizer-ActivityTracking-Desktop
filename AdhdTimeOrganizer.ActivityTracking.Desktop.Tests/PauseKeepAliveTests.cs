using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public sealed class PauseKeepAliveTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 10, 30, 42, 500, DateTimeKind.Utc);

    [Fact]
    public void A_pause_with_nothing_stored_since_start_is_due_at_once()
    {
        Assert.True(new PauseKeepAlive().IsDue(paused: true, hasSession: true, lastStoredUtc: null, Now));
    }

    [Fact]
    public void Only_a_pause_is_due()
    {
        Assert.False(new PauseKeepAlive().IsDue(paused: false, hasSession: true, lastStoredUtc: null, Now));
    }

    [Fact]
    public void Signed_out_there_is_nothing_to_say_it_with()
    {
        Assert.False(new PauseKeepAlive().IsDue(paused: true, hasSession: false, lastStoredUtc: null, Now));
    }

    [Fact]
    public void A_window_stored_recently_already_said_it()
    {
        var keepAlive = new PauseKeepAlive();

        Assert.False(keepAlive.IsDue(true, true, Now - PauseKeepAlive.Every + TimeSpan.FromMinutes(1), Now));
        Assert.True(keepAlive.IsDue(true, true, Now - PauseKeepAlive.Every, Now));
    }

    [Theory]
    [InlineData(SendResult.Sent)]
    [InlineData(SendResult.Rejected)] // a server from before empty heartbeats: an answer, and asking sooner changes nothing
    public void An_answer_waits_the_full_interval(SendResult result)
    {
        var keepAlive = new PauseKeepAlive();
        keepAlive.Answered(result, Now);

        Assert.False(keepAlive.IsDue(true, true, null, Now + PauseKeepAlive.Every - TimeSpan.FromSeconds(1)));
        Assert.True(keepAlive.IsDue(true, true, null, Now + PauseKeepAlive.Every));
    }

    [Theory]
    [InlineData(SendResult.RetryLater)]
    [InlineData(SendResult.SignedOut)]
    public void No_answer_tries_again_soon_but_not_every_pass(SendResult result)
    {
        var keepAlive = new PauseKeepAlive();
        keepAlive.Answered(result, Now);

        Assert.False(keepAlive.IsDue(true, true, null, Now + TimeSpan.FromSeconds(10)));
        Assert.True(keepAlive.IsDue(true, true, null, Now + PauseKeepAlive.RetryAfter));
    }

    [Fact]
    public void The_last_answer_is_kept_for_diagnostics_and_the_newest_wins()
    {
        var keepAlive = new PauseKeepAlive();
        Assert.Null(keepAlive.LastAnswer);

        keepAlive.Answered(SendResult.RetryLater, Now);
        keepAlive.Answered(SendResult.Sent, Now + PauseKeepAlive.RetryAfter);

        Assert.Equal(new KeepAliveAnswer(SendResult.Sent, Now + PauseKeepAlive.RetryAfter), keepAlive.LastAnswer);
    }

    [Fact]
    public void Every_interval_leaves_several_tries_inside_the_servers_48_hours()
    {
        Assert.True(PauseKeepAlive.Every <= TimeSpan.FromHours(12));
    }

    [Fact]
    public void The_window_is_empty_and_on_a_whole_minute()
    {
        var window = PauseKeepAlive.Window(Now);

        Assert.Empty(window.Entries);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 30, 0, DateTimeKind.Utc), window.WindowStart);
        Assert.Equal(DateTimeKind.Utc, window.WindowStart.Kind);
    }
}
