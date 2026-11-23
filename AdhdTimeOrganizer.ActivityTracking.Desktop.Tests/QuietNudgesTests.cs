using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// "Quiet nudges for…" (prompt 25): when it is on, when each choice ends, what the tray says, and that a restart keeps it.
/// What it holds and what it leaves alone is pinned in <c>NudgeServiceTests</c> and <c>UnmappedReviewServiceTests</c>.
/// </summary>
public sealed class QuietNudgesTests : IDisposable
{
    private static readonly TimeZoneInfo Bratislava = TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"quiet-nudges-{Guid.NewGuid():N}");

    private static DateTime Utc(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void IsOn_UntilItsEnd_AndNotWithoutOne()
    {
        var now = Utc(18, 12, 0);

        Assert.False(QuietNudges.IsOn(null, now));
        Assert.True(QuietNudges.IsOn(now.AddMinutes(1), now));
        Assert.False(QuietNudges.IsOn(now, now));
        Assert.True(QuietNudges.IsOn(DateTime.MaxValue, now));
    }

    /// <summary>An end that has passed is cleared by the tray; one still to come, or none, is not.</summary>
    [Fact]
    public void HasEnded_OnlyOnceItsEndHasPassed()
    {
        var now = Utc(18, 12, 0);

        Assert.True(QuietNudges.HasEnded(now, now));
        Assert.False(QuietNudges.HasEnded(now.AddSeconds(1), now));
        Assert.False(QuietNudges.HasEnded(DateTime.MaxValue, now));
        Assert.False(QuietNudges.HasEnded(null, now));
    }

    [Fact]
    public void ThirtyMinutesAndAnHour_AreFromNow()
    {
        Assert.Equal(Utc(18, 9, 30), QuietNudges.Until(QuietChoice.ThirtyMinutes, Utc(18, 9, 0), Bratislava));
        Assert.Equal(Utc(18, 10, 0), QuietNudges.Until(QuietChoice.OneHour, Utc(18, 9, 0), Bratislava));
    }

    /// <summary>The same morning a pause ends at: 06:00 on the user's clock (CEST, UTC+2), the one that is coming.</summary>
    [Fact]
    public void UntilTomorrowMorning_EndsAtTheNextSixOClock_OnTheUsersClock()
    {
        Assert.Equal(Utc(19, 4, 0), QuietNudges.Until(QuietChoice.UntilTomorrowMorning, Utc(18, 21, 30), Bratislava));
        Assert.Equal(Utc(19, 4, 0), QuietNudges.Until(QuietChoice.UntilTomorrowMorning, Utc(19, 0, 30), Bratislava));
        Assert.Equal(TrayApplicationContext.PauseEnd(PauseChoice.UntilTomorrowMorning, Utc(18, 21, 30), Bratislava),
            QuietNudges.Until(QuietChoice.UntilTomorrowMorning, Utc(18, 21, 30), Bratislava));
    }

    [Fact]
    public void UntilTurnedBackOn_HasNoEnd() =>
        Assert.Equal(DateTime.MaxValue, QuietNudges.Until(QuietChoice.UntilTurnedBackOn, Utc(18, 9, 0), Bratislava));

    [Fact]
    public void TheTray_SaysUntilWhen_InTheUsersTime()
    {
        var now = Utc(18, 10, 0); // 12:00 in Bratislava

        Assert.Null(QuietNudges.Describe(null, now, Bratislava));
        Assert.Null(QuietNudges.Describe(now.AddMinutes(-1), now, Bratislava));
        Assert.Equal("Nudges quiet until 12:30", QuietNudges.Describe(now.AddMinutes(30), now, Bratislava));
        Assert.Equal("Nudges quiet until tomorrow 06:00", QuietNudges.Describe(Utc(19, 4, 0), now, Bratislava));
        Assert.Equal("Nudges quiet until turned back on", QuietNudges.Describe(DateTime.MaxValue, now, Bratislava));
    }

    /// <summary>The status line and tooltip say it after the state: recording goes on, only the toasts wait.</summary>
    [Fact]
    public void TheStatusLine_SaysNudgesAreQuiet_AfterTheState()
    {
        var status = new TrackerStatus(true, false, 0, null, null, null);

        Assert.Equal("Tracking · nudges quiet until 12:30",
            TrayApplicationContext.DescribeStatus(status, hasSession: true, quiet: "Nudges quiet until 12:30"));
        Assert.Equal("Tracking", TrayApplicationContext.DescribeStatus(status, hasSession: true));
    }

    /// <summary>A restart keeps it, like a pause; an ordinary config.json does not gain the key.</summary>
    [Fact]
    public void ARestart_KeepsIt_AndAnOrdinaryConfig_DoesNotWriteIt()
    {
        var path = Path.Combine(_dir, "config.json");
        var until = Utc(18, 14, 30);
        new AppConfig { FilePath = path, NudgesQuietUntilUtc = until }.Save();

        Assert.Equal(until, AppConfig.Load(path).Config.NudgesQuietUntilUtc);

        var load = AppConfig.Load(path);
        load.Config.NudgesQuietUntilUtc = null; // "Turn nudges back on"
        load.Config.Save();

        Assert.Null(AppConfig.Load(path).Config.NudgesQuietUntilUtc);
        Assert.False(JsonDocument.Parse(File.ReadAllText(path)).RootElement.TryGetProperty(nameof(AppConfig.NudgesQuietUntilUtc), out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
