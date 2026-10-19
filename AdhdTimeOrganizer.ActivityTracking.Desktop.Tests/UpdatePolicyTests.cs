using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class UpdatePolicyTests
{
    [Theory]
    [InlineData(UpdateCheckOutcome.UpToDate)]
    [InlineData(UpdateCheckOutcome.Downloaded)] // a later release replaces the one waiting
    public void AfterAnAnswer_ItLooksAgainOnSchedule(UpdateCheckOutcome outcome) =>
        Assert.Equal(UpdatePolicy.CheckEvery, UpdatePolicy.NextCheckAfter(outcome));

    [Fact]
    public void AfterAFailure_ItWaitsBeforeTryingAgain_ButLessThanASchedule()
    {
        var retry = UpdatePolicy.NextCheckAfter(UpdateCheckOutcome.Failed);

        Assert.True(retry >= TimeSpan.FromMinutes(30), "a failure must not retry hot against GitHub's anonymous limit");
        Assert.True(retry < UpdatePolicy.CheckEvery);
    }

    [Fact]
    public void TheFirstLook_WaitsOutTheStart() =>
        Assert.True(UpdatePolicy.FirstCheckAfter > TimeSpan.Zero);

    [Fact]
    public void TheRestartItem_ShowsOnlyWhileAReleaseWaits()
    {
        Assert.Null(UpdatePolicy.RestartText(null));
        Assert.Equal("Restart to update to 1.2.0", UpdatePolicy.RestartText("1.2.0"));
    }

    [Fact]
    public void ABuildFromSource_SaysItDoesNotUpdate()
    {
        Assert.Equal("Version 1.1.0", UpdatePolicy.VersionText("1.1.0", installed: true));
        Assert.Contains("no updates", UpdatePolicy.VersionText("1.1.0", installed: false));
    }
}
