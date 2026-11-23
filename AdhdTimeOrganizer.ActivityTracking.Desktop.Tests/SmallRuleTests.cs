using System.Net;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Native;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class SendResultClassificationTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, SendResult.Sent)]
    // The server answers a concurrent duplicate with 409 and asks for a retry; it used to be dropped.
    [InlineData(HttpStatusCode.Conflict, SendResult.RetryLater)]
    [InlineData(HttpStatusCode.TooManyRequests, SendResult.RetryLater)]
    [InlineData(HttpStatusCode.RequestTimeout, SendResult.RetryLater)]
    [InlineData(HttpStatusCode.Forbidden, SendResult.RetryLater)]
    [InlineData(HttpStatusCode.Unauthorized, SendResult.RetryLater)]
    [InlineData(HttpStatusCode.BadGateway, SendResult.RetryLater)]
    [InlineData(HttpStatusCode.InternalServerError, SendResult.RetryLater)]
    [InlineData(HttpStatusCode.BadRequest, SendResult.Rejected)]
    [InlineData(HttpStatusCode.NotFound, SendResult.Rejected)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, SendResult.Rejected)]
    public void Classify(HttpStatusCode status, SendResult expected) =>
        Assert.Equal(expected, ApiClient.Classify(status));

    /// <summary>Review M7: a 5xx's database detail can quote a window title, and the log tail goes into "Copy diagnostics".</summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void TheBodyOfAnythingButARefusal_IsNeverLogged(HttpStatusCode status) =>
        Assert.Null(ApiClient.BodyWorthLogging(ApiClient.Classify(status), status,
            "duplicate key value violates unique constraint: (chrome, My secret document - Google Docs)"));

    [Fact]
    public void ARefusalsBody_IsLogged_CutToItsLimit()
    {
        Assert.Equal("""{"errors":{"entries[0].processName":["must not be empty"]}}""",
            ApiClient.BodyWorthLogging(SendResult.Rejected, HttpStatusCode.BadRequest,
                """{"errors":{"entries[0].processName":["must not be empty"]}}"""));

        var page = "<html>" + new string('x', 5000) + "</html>";
        var logged = ApiClient.BodyWorthLogging(SendResult.Rejected, HttpStatusCode.BadRequest, page)!;
        Assert.Equal(ApiClient.MaxLoggedBodyLength + 1, logged.Length);
        Assert.EndsWith("…", logged);

        Assert.Null(ApiClient.BodyWorthLogging(SendResult.Rejected, HttpStatusCode.BadRequest, "  "));
        Assert.Null(ApiClient.BodyWorthLogging(SendResult.Sent, HttpStatusCode.OK, "{}"));
    }

    [Fact]
    public void TheCut_NeverSplitsASurrogatePair()
    {
        var body = new string('x', ApiClient.MaxLoggedBodyLength - 1) + "😀" + "tail";
        var logged = ApiClient.BodyWorthLogging(SendResult.Rejected, HttpStatusCode.BadRequest, body)!;
        Assert.False(char.IsHighSurrogate(logged[^2]));
    }
}

public class PassivePresenceTests
{
    [Theory]
    [InlineData(600, true, 150, true)]        // a film ten minutes in
    [InlineData(600, false, 150, false)]      // silent foreground: idle
    [InlineData(150 * 60, true, 150, false)]  // autoplay past the cap
    [InlineData(60, true, 0, false)]          // turned off
    public void IsPassivelyPresent(int idleSeconds, bool playing, int maxMinutes, bool expected) =>
        Assert.Equal(expected, ActivityTrackerService.IsPassivelyPresent(idleSeconds, playing, maxMinutes));
}

public class ProcessNameTests
{
    /// <summary>Must match <c>Process.ProcessName</c>, which the user's mapping rules were written against.</summary>
    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome")]
    [InlineData(@"C:\Tools\APP.EXE", "APP")]
    [InlineData(@"C:\Tools\runner.bin", "runner.bin")]
    public void ProcessNameFromPath(string path, string expected) =>
        Assert.Equal(expected, ProcessInfoCache.ProcessNameFromPath(path));
}

public class MicrophoneUsageTests
{
    public static TheoryData<object?, object?, bool> Values => new()
    {
        { 133791285319274277L, 0L, true },                    // started, not stopped: in use
        { 133791285319274277L, 133791285419274277L, false },  // stopped
        { null, null, false },                                // never used
        { 133791285319274277L, null, false },                 // half-written: not in use (fail closed)
        { 0L, 0L, false },
        { "garbage", 0L, false }
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void IsHolding(object? start, object? stop, bool expected) =>
        Assert.Equal(expected, MicrophoneUsage.IsHolding(start, stop));

    [Fact]
    public void ExecutablePathFromKeyName() =>
        Assert.Equal(@"C:\Program Files\Zoom\bin\Zoom.exe",
            MicrophoneUsage.ExecutablePathFromKeyName("C:#Program Files#Zoom#bin#Zoom.exe"));
}

public class TrayStatusTests
{
    [Fact]
    public void DescribeStatus_SaysABreakIsRunning_AndHowLongIsLeft() =>
        Assert.Equal("Tracking · on a break, 7 min left",
            TrayApplicationContext.DescribeStatus(
                new TrackerStatus(true, false, 0, null, null, null), hasSession: true,
                breakLeft: TimeSpan.FromMinutes(6.2)));

    [Fact]
    public void DescribeStatus_SaysOfflineAndHowMuchIsQueued() =>
        Assert.Equal("Tracking · offline · 14 min unsent",
            TrayApplicationContext.DescribeStatus(
                new TrackerStatus(true, false, 14, DateTime.UtcNow, SendResult.RetryLater, null), hasSession: true));

    /// <summary>Review L2: a 403 used to read "offline", sending a user with a permission problem to check their Wi-Fi.</summary>
    [Theory]
    [InlineData(403, "Tracking · the server refuses this account (403) · 14 min unsent")]
    [InlineData(429, "Tracking · the server is busy (429) · 14 min unsent")]
    [InlineData(502, "Tracking · server error (502) · 14 min unsent")]
    [InlineData(409, "Tracking · the server answered 409 · 14 min unsent")]
    public void DescribeStatus_NamesWhatTheServerAnswered(int code, string expected) =>
        Assert.Equal(expected,
            TrayApplicationContext.DescribeStatus(
                new TrackerStatus(true, false, 14, DateTime.UtcNow, SendResult.RetryLater, null, LastStatusCode: code), hasSession: true));

    [Fact]
    public void DescribeStatus_SaysSignedOut() =>
        Assert.Equal("Tracking · signed out · 3 min unsent",
            TrayApplicationContext.DescribeStatus(
                new TrackerStatus(true, false, 3, null, SendResult.SignedOut, null), hasSession: false));

    [Fact]
    public void DescribeStatus_CountsMinutesTheServerRefused() =>
        Assert.Equal("Tracking · sent 12:00 · 4 min dropped",
            TrayApplicationContext.DescribeStatus(
                new TrackerStatus(true, false, 0, DateTime.Today.AddHours(12).ToUniversalTime(), SendResult.Sent, null, RejectedWindows: 4),
                hasSession: true));

    [Fact]
    public void DescribeStatus_SurfacesAPollError() =>
        Assert.StartsWith("Error: boom",
            TrayApplicationContext.DescribeStatus(
                new TrackerStatus(true, false, 0, null, null, "boom"), hasSession: true));
}
