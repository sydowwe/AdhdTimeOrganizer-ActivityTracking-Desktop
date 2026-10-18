using System.Net;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class RefreshPolicyTests
{
    [Fact]
    public void AnAnswerWithBothTokens_IsSaved_AndSettlesTheToken()
    {
        var attempt = new RefreshAttempt.Answered(HttpStatusCode.OK, HasTokens: true);

        Assert.Equal(RefreshDecision.Save, RefreshPolicy.Decide(attempt));
        Assert.False(RefreshPolicy.LeftTheTokenUncertain(attempt));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void OnlyTheServerRefusingTheToken_SignsOut(HttpStatusCode status)
    {
        var attempt = new RefreshAttempt.Answered(status);

        Assert.Equal(RefreshDecision.SignOut, RefreshPolicy.Decide(attempt));
        Assert.False(RefreshPolicy.LeftTheTokenUncertain(attempt));
    }

    /// <summary>
    /// The server may have rotated: a 409 says so outright ("rotated moments ago"), a 5xx can follow the rotation's
    /// commit, and a 2xx without both tokens may be a rotation we cannot read. The tokens stay, and the next send waits.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.OK)]
    public void AnAnswerThatMayFollowARotation_KeepsTheTokens_AndLeavesThemUncertain(HttpStatusCode status)
    {
        var attempt = new RefreshAttempt.Answered(status);

        Assert.Equal(RefreshDecision.KeepTokens, RefreshPolicy.Decide(attempt));
        Assert.True(RefreshPolicy.LeftTheTokenUncertain(attempt));
    }

    /// <summary>A throttle or a stray 4xx is refused before any rotation: keep the tokens, and the next send may go at once.</summary>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    public void AnAnswerFromBeforeTheRotation_KeepsTheTokens_AndSettlesThem(HttpStatusCode status)
    {
        var attempt = new RefreshAttempt.Answered(status);

        Assert.Equal(RefreshDecision.KeepTokens, RefreshPolicy.Decide(attempt));
        Assert.False(RefreshPolicy.LeftTheTokenUncertain(attempt));
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public void ARequestThatNeverLeft_CannotHaveRotatedAnything(HttpRequestError error)
    {
        var attempt = RefreshAttempt.FromFailure(new HttpRequestException(error, "never sent"));

        Assert.IsType<RefreshAttempt.NotSent>(attempt);
        Assert.Equal(RefreshDecision.KeepTokens, RefreshPolicy.Decide(attempt));
        Assert.False(RefreshPolicy.LeftTheTokenUncertain(attempt));
    }

    public static TheoryData<Exception> FailuresAfterSending =>
    [
        new HttpRequestException(HttpRequestError.ResponseEnded, "dropped"),
        new HttpRequestException(HttpRequestError.Unknown, "reset"),
        new TaskCanceledException("HttpClient.Timeout"),
        new System.Text.Json.JsonException("unreadable answer")
    ];

    /// <summary>A timeout, a dropped connection or an unreadable answer may have reached the server first.</summary>
    [Theory]
    [MemberData(nameof(FailuresAfterSending))]
    public void AFailureAfterSending_KeepsTheTokens_AndLeavesThemUncertain(Exception failure)
    {
        var attempt = RefreshAttempt.FromFailure(failure);

        Assert.IsType<RefreshAttempt.Lost>(attempt);
        Assert.Equal(RefreshDecision.KeepTokens, RefreshPolicy.Decide(attempt));
        Assert.True(RefreshPolicy.LeftTheTokenUncertain(attempt));
    }

    [Fact]
    public void AnUncertainToken_IsHeldBackUntilPastTheServersGrace()
    {
        Assert.Equal(TimeSpan.Zero, RefreshPolicy.WaitBeforeSending(null));
        Assert.Equal(RefreshPolicy.ReplayDelay, RefreshPolicy.WaitBeforeSending(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(2), RefreshPolicy.WaitBeforeSending(RefreshPolicy.ReplayDelay - TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.Zero, RefreshPolicy.WaitBeforeSending(RefreshPolicy.ReplayDelay));
        Assert.True(RefreshPolicy.ReplayDelay > TimeSpan.FromSeconds(5), "the server soft-refuses a replay for 5 s after the rotation");
    }
}
