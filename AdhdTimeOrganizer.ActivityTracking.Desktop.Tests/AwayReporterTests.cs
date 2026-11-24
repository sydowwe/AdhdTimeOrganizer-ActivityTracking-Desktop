using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// Answered absences reaching the server (prompt 23): oldest first, kept through "offline", dropped when the server refuses
/// one or it has grown too old. The send is a fake; nothing touches the network.
/// </summary>
public sealed class AwayReporterTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly List<AwayAnswered> _sent = [];
    private readonly Queue<BreakSendResult> _answers = new();
    private DateTime _now = Now;
    private readonly AwayReporter _reporter;

    public AwayReporterTests()
    {
        _reporter = new AwayReporter((answered, _) =>
        {
            _sent.Add(answered);
            return Task.FromResult(_answers.TryDequeue(out var result) ? result : BreakSendResult.Delivered);
        }, () => _now);
    }

    [Fact]
    public async Task Answers_GoOldestFirst_AndAWaitingOneIsTriedAgain()
    {
        var first = Answer(-90);
        var second = Answer(-30);
        _answers.Enqueue(BreakSendResult.Later);

        _reporter.Report(first);
        _reporter.Report(second);
        await _reporter.PumpAsync();

        Assert.Equal(0, _reporter.PendingCount);
        Assert.Equal([first, first, second], _sent);
    }

    [Fact]
    public async Task ARefusedAnswer_IsDropped_AndTheNextGoes()
    {
        _answers.Enqueue(BreakSendResult.Dropped);

        _reporter.Report(Answer(-90));
        _reporter.Report(Answer(-30));
        await _reporter.PumpAsync();

        Assert.Equal(2, _sent.Count);
        Assert.Equal(0, _reporter.PendingCount);
    }

    [Fact]
    public async Task AnAnswerADayOld_IsLetGo_Unsent()
    {
        _answers.Enqueue(BreakSendResult.Later);
        _reporter.Report(Answer(-30)); // offline: waits
        Assert.Equal(1, _reporter.PendingCount);

        _now = Now.AddMinutes(-30) + AwayReporter.WorthSendingFor + TimeSpan.FromSeconds(1);
        await _reporter.PumpAsync();

        Assert.Single(_sent);
        Assert.Equal(0, _reporter.PendingCount);
    }

    /// <summary>An absence that ended <paramref name="endedMinutesAgo"/> (negative) before now, 47 minutes long.</summary>
    private static AwayAnswered Answer(int endedMinutesAgo) =>
        new(Now.AddMinutes(endedMinutesAgo - 47), Now.AddMinutes(endedMinutesAgo), 21);

    public void Dispose() => _reporter.Dispose();
}
