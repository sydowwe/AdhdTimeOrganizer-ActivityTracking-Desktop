using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// A poll failing on every pass: the first failure in full, the same one as a line every 10 min, a new one in full, and one
/// line when it recovers — not a stack every 5 s.
/// </summary>
public class PollFailureLogTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);
    private const string Denied = "System.ComponentModel.Win32Exception: Access is denied.";

    [Fact]
    public void TheFirstFailure_IsWrittenInFull()
    {
        var log = new PollFailureLog();

        Assert.Equal((PollLogEntry.Full, 1, TimeSpan.Zero), log.Failed(Now, Denied));
    }

    /// <summary>The same failure every 5 s for 10 min: one full entry, then nothing until the summary is due.</summary>
    [Fact]
    public void TheSameFailure_IsSummed_EveryTenMinutes()
    {
        var log = new PollFailureLog();
        log.Failed(Now, Denied);

        var written = new List<(PollLogEntry, int, TimeSpan)>();
        for (var second = 5; second <= 20 * 60; second += 5)
        {
            var result = log.Failed(Now.AddSeconds(second), Denied);
            if (result.Entry != PollLogEntry.None) written.Add(result);
        }

        Assert.Equal(
            [(PollLogEntry.StillFailing, 121, TimeSpan.FromMinutes(10)), (PollLogEntry.StillFailing, 241, TimeSpan.FromMinutes(20))],
            written);
    }

    /// <summary>A failure unlike the last is new information: in full, inside the same run.</summary>
    [Fact]
    public void ADifferentFailure_IsWrittenInFull_AndTheRunGoesOn()
    {
        var log = new PollFailureLog();
        log.Failed(Now, Denied);
        log.Failed(Now.AddSeconds(5), Denied);

        Assert.Equal((PollLogEntry.Full, 3, TimeSpan.FromSeconds(10)),
            log.Failed(Now.AddSeconds(10), "System.IO.IOException: The disk is full."));
    }

    [Fact]
    public void ARecovery_IsSaidOnce_AndTheNextFailureStartsAFreshRun()
    {
        var log = new PollFailureLog();
        log.Failed(Now, Denied);
        log.Failed(Now.AddMinutes(3), Denied);

        Assert.Equal((2, TimeSpan.FromSeconds(185)), log.Recovered(Now.AddMinutes(3).AddSeconds(5)));
        Assert.Null(log.Recovered(Now.AddMinutes(4)));

        Assert.Equal((PollLogEntry.Full, 1, TimeSpan.Zero), log.Failed(Now.AddMinutes(5), Denied));
    }

    [Fact]
    public void NothingFailing_HasNothingToRecoverFrom() => Assert.Null(new PollFailureLog().Recovered(Now));

    /// <summary>A clock set back mid-run must not mute it until the clock catches up.</summary>
    [Fact]
    public void AClockSetBack_DoesNotMuteTheRun()
    {
        var log = new PollFailureLog();
        log.Failed(Now, Denied);

        Assert.Equal(PollLogEntry.StillFailing, log.Failed(Now.AddHours(-1), Denied).Entry);
    }
}
