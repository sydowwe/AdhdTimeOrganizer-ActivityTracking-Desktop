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

public class TrayStatusTests
{
    [Fact]
    public void DescribeStatus_SaysOfflineAndHowMuchIsQueued() =>
        Assert.Equal("Tracking · offline · 14 min unsent",
            TrayApplicationContext.DescribeStatus(
                new TrackerStatus(true, false, 14, DateTime.UtcNow, SendResult.RetryLater, null), hasSession: true));

    [Fact]
    public void DescribeStatus_SaysSignedOut() =>
        Assert.Equal("Tracking · signed out · 3 min unsent",
            TrayApplicationContext.DescribeStatus(
                new TrackerStatus(true, false, 3, null, SendResult.SignedOut, null), hasSession: false));

    [Fact]
    public void DescribeStatus_SurfacesAPollError() =>
        Assert.StartsWith("Error: boom",
            TrayApplicationContext.DescribeStatus(
                new TrackerStatus(true, false, 0, null, null, "boom"), hasSession: true));
}
