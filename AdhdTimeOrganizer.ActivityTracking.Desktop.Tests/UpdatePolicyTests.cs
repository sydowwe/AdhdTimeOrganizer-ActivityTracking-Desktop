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
    public void AtStart_TheFirstInstanceOfTheInstalledBuild_AppliesAWaitingRelease() =>
        Assert.True(UpdatePolicy.ApplyAtStart(installed: true, firstInstance: true, pendingVersion: "1.2.1", startedByUpdater: false));

    [Fact]
    public void AtStart_ASecondLaunch_NeverApplies_TheUpdaterWouldEndTheRunningTracker() =>
        Assert.False(UpdatePolicy.ApplyAtStart(installed: true, firstInstance: false, pendingVersion: "1.2.1", startedByUpdater: false));

    [Theory]
    [InlineData(true, null)]     // nothing downloaded
    [InlineData(false, "1.2.1")] // a build from source is not Velopack's to replace
    public void AtStart_NothingToApply(bool installed, string? pendingVersion) =>
        Assert.False(UpdatePolicy.ApplyAtStart(installed, firstInstance: true, pendingVersion, startedByUpdater: false));

    /// <summary>
    /// A failed apply ends with the updater starting the old build again, and the same release still waits. Applying it at
    /// that start would loop — apply, fail, restart — with nothing tracked in between. The release waits for the next start.
    /// </summary>
    [Fact]
    public void AtStart_AStartMadeByTheUpdater_NeverApplies_OrAFailedApplyWouldLoop() =>
        Assert.False(UpdatePolicy.ApplyAtStart(installed: true, firstInstance: true, pendingVersion: "1.2.1", startedByUpdater: true));

    /// <summary>The guard holds one start only: the next ordinary start (a sign-in, the Start menu) applies again.</summary>
    [Fact]
    public void TheNextOrdinaryStart_AppliesTheReleaseThatWaited()
    {
        Assert.False(UpdatePolicy.ApplyAtStart(installed: true, firstInstance: true, pendingVersion: "1.2.1", startedByUpdater: true));
        Assert.True(UpdatePolicy.ApplyAtStart(installed: true, firstInstance: true, pendingVersion: "1.2.1", startedByUpdater: false));
    }

    [Fact]
    public void ABuildFromSource_SaysItDoesNotUpdate()
    {
        Assert.Equal("Version 1.1.0", UpdatePolicy.VersionText("1.1.0", installed: true));
        Assert.Contains("no updates", UpdatePolicy.VersionText("1.1.0", installed: false));
    }
}
