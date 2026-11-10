using System.Net;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// Prompt 10: the break the user started goes to the server, latest first, and never at the cost of anything else. Driven
/// through <see cref="BreakReporter.PumpAsync"/> so a retry needs no real minute.
/// </summary>
public class BreakReporterTests
{
    private static readonly DateTime Start = new(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly BreakReport Started = new(Start, Start.AddMinutes(10), null);
    private static readonly BreakReport Ended = new(Start, Start.AddMinutes(10), Start.AddMinutes(6));

    [Theory]
    [InlineData(HttpStatusCode.NoContent, BreakSendResult.Delivered)]
    [InlineData(HttpStatusCode.OK, BreakSendResult.Delivered)]
    [InlineData(HttpStatusCode.NotFound, BreakSendResult.Dropped)]      // a server from before the route
    [InlineData(HttpStatusCode.BadRequest, BreakSendResult.Dropped)]
    [InlineData(HttpStatusCode.Forbidden, BreakSendResult.Dropped)]
    [InlineData(HttpStatusCode.Unauthorized, BreakSendResult.Later)]
    [InlineData(HttpStatusCode.TooManyRequests, BreakSendResult.Later)]
    [InlineData(HttpStatusCode.ServiceUnavailable, BreakSendResult.Later)]
    public void Classify_SaysWhatEachAnswerMeans(HttpStatusCode status, BreakSendResult expected) =>
        Assert.Equal(expected, BreakReporter.Classify(status));

    [Fact]
    public async Task ADeliveredReport_IsSentOnce_AndGone()
    {
        var fake = new FakeServer(BreakSendResult.Delivered);
        using var reporter = fake.Reporter(Start);

        reporter.Report(Started);
        await reporter.PumpAsync();

        Assert.Equal([Started], fake.Sent);
        Assert.False(reporter.HasPending);
    }

    /// <summary>Offline: kept and tried again, and by then only the latest break counts — the end replaces the start.</summary>
    [Fact]
    public async Task OfflineReports_AreKept_AndOnlyTheLatestGoesWhenBackOnline()
    {
        var fake = new FakeServer(BreakSendResult.Later);
        using var reporter = fake.Reporter(Start);

        reporter.Report(Started);
        await reporter.PumpAsync();
        Assert.True(reporter.HasPending);

        fake.Answer = BreakSendResult.Delivered;
        reporter.Report(Ended);
        await reporter.PumpAsync();

        Assert.Equal(Ended, fake.Sent[^1]);
        Assert.False(reporter.HasPending);
    }

    /// <summary>An older server (404) drops the report: the desk's own toast still speaks, nothing waits on it.</summary>
    [Fact]
    public async Task AnOlderServer_DropsTheReport()
    {
        var fake = new FakeServer(BreakSendResult.Dropped);
        using var reporter = fake.Reporter(Start);

        reporter.Report(Started);
        await reporter.PumpAsync();

        Assert.Single(fake.Sent);
        Assert.False(reporter.HasPending);
    }

    /// <summary>Two hours past the planned end the server no longer shows the break, so a report still waiting is let go unsent.</summary>
    [Fact]
    public async Task AReportPastItsUse_IsLetGoUnsent()
    {
        var fake = new FakeServer(BreakSendResult.Delivered);
        using var reporter = fake.Reporter(Start.AddMinutes(10) + BreakReporter.WorthSendingFor);

        reporter.Report(Started);
        await reporter.PumpAsync();

        Assert.Empty(fake.Sent);
        Assert.False(reporter.HasPending);
    }

    /// <summary>A send that throws is a retry, never an exception out of the poll loop that reported it.</summary>
    [Fact]
    public async Task ASendThatThrows_IsKeptForARetry()
    {
        var fake = new FakeServer(BreakSendResult.Delivered) { Throw = true };
        using var reporter = fake.Reporter(Start);

        reporter.Report(Started);
        await reporter.PumpAsync();

        Assert.True(reporter.HasPending);
    }

    private sealed class FakeServer(BreakSendResult answer)
    {
        public BreakSendResult Answer { get; set; } = answer;
        public bool Throw { get; init; }
        public List<BreakReport> Sent { get; } = [];

        public BreakReporter Reporter(DateTime now) => new(Send, () => now);

        private Task<BreakSendResult> Send(BreakReport report, CancellationToken ct)
        {
            if (Throw) throw new HttpRequestException("offline");
            lock (Sent) Sent.Add(report);
            return Task.FromResult(Answer);
        }
    }
}
