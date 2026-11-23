using System.Net;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// Pausing puts the minute in progress into the outbox (review L1), as suspend and exit do; restarting the poll loop for a
/// new poll interval does not. Signed out, with a server that answers nothing, so the outbox only fills.
/// </summary>
public sealed class PauseFlushTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pause-flush-{Guid.NewGuid():N}");
    private readonly AppConfig _config;
    private readonly OutboxStore _outbox;
    private readonly ApiClient _api;
    private readonly ActivityTrackerService _tracker;

    public PauseFlushTests()
    {
        _config = new AppConfig { FilePath = Path.Combine(_dir, "config.json"), ApiBaseUrl = "https://server.test" };
        _outbox = new OutboxStore(Path.Combine(_dir, "outbox"), TimeSpan.FromDays(30));
        _api = new ApiClient(_config, new NoServer());
        _tracker = new ActivityTrackerService(_config, _api, _outbox, new TodayTotal(Path.Combine(_dir, "today.json"), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Pausing_QueuesTheMinuteInProgress()
    {
        CreditTwentySeconds();
        Assert.Equal(0, _outbox.Summary().Count);

        _tracker.Pause(DateTime.MaxValue);

        var item = Assert.Single(_outbox.Pending(DateTime.UtcNow, 10));
        Assert.Equal(20, item.Window.Entries.Sum(e => e.ActiveSeconds));
    }

    /// <summary>What a poll-interval change in Settings does: recording carries on, so nothing is cut short.</summary>
    [Fact]
    public void StoppingTheLoop_DoesNotFlush()
    {
        CreditTwentySeconds();

        _tracker.Stop();

        Assert.Equal(0, _outbox.Summary().Count);
    }

    private void CreditTwentySeconds()
    {
        var sample = new ActivitySample { ProcessName = "code", WindowTitle = "notes.md", ExecutablePath = @"C:\apps\code.exe" };
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 10, DateTimeKind.Utc);
        _tracker.Credit(start.AddSeconds(10), TimeSpan.FromSeconds(10), [new Observation(sample, 0, false, false, false)]);
        _tracker.Credit(start.AddSeconds(20), TimeSpan.FromSeconds(10), [new Observation(sample, 0, false, false, false)]);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _api.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class NoServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
