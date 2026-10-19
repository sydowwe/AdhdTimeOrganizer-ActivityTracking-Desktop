using System.Net;
using System.Text;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The sign-in listener against fake connections — no socket, no network. Exactly one redirect is accepted; a stray or
/// forged request is answered and the wait goes on, so it cannot end the user's sign-in; anything not from loopback is
/// dropped unanswered.
/// </summary>
public sealed class LoopbackRedirectHandlerTests
{
    private const string State = "our-state-0123456789abcdef";
    private static readonly IPAddress Loopback = IPAddress.Loopback;

    private readonly LoopbackRedirectHandler _handler = new(State);

    [Fact]
    public async Task OurRedirect_GivesTheCode_AndTellsTheBrowserItIsDone()
    {
        var connection = FakeConnection.Get($"/cb?code=the-code&state={State}");

        Assert.Equal("the-code", await _handler.HandleAsync(connection, Loopback, TestContext.Current.CancellationToken));
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", connection.Response);
        Assert.Contains("You can close this tab", connection.Response);
        Assert.Contains("Cache-Control: no-store", connection.Response);
    }

    [Fact]
    public async Task ASecondRedirect_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await _handler.HandleAsync(FakeConnection.Get($"/cb?code=first&state={State}"), Loopback, ct);
        var second = FakeConnection.Get($"/cb?code=second&state={State}");

        Assert.Null(await _handler.HandleAsync(second, Loopback, ct));
        Assert.StartsWith("HTTP/1.1 409 ", second.Response);
    }

    [Fact]
    public async Task AWrongState_IsRefused_AndOurRedirectStillCountsAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var forged = FakeConnection.Get("/cb?code=forged&state=someone-elses-state-0123");

        Assert.Null(await _handler.HandleAsync(forged, Loopback, ct));
        Assert.StartsWith("HTTP/1.1 400 ", forged.Response);

        Assert.Equal("ours", await _handler.HandleAsync(FakeConnection.Get($"/cb?code=ours&state={State}"), Loopback, ct));
    }

    [Fact]
    public async Task AFavicon_IsNotFound_AndOurRedirectStillCountsAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var favicon = FakeConnection.Get("/favicon.ico");

        Assert.Null(await _handler.HandleAsync(favicon, Loopback, ct));
        Assert.StartsWith("HTTP/1.1 404 ", favicon.Response);

        Assert.Equal("ours", await _handler.HandleAsync(FakeConnection.Get($"/cb?code=ours&state={State}"), Loopback, ct));
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.5")]
    [InlineData("fe80::1")]
    public async Task AConnectionNotFromLoopback_IsDroppedUnanswered(string remote)
    {
        var connection = FakeConnection.Get($"/cb?code=the-code&state={State}");

        Assert.Null(await _handler.HandleAsync(connection, IPAddress.Parse(remote), TestContext.Current.CancellationToken));
        Assert.Equal("", connection.Response);

        // And it did not use up the one sign-in.
        Assert.Equal("ours", await _handler.HandleAsync(
            FakeConnection.Get($"/cb?code=ours&state={State}"), Loopback, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IPv6Loopback_IsLoopback()
    {
        Assert.Equal("the-code", await _handler.HandleAsync(
            FakeConnection.Get($"/cb?code=the-code&state={State}"), IPAddress.IPv6Loopback, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AHeadThatNeverEnds_IsRefused()
    {
        var connection = new FakeConnection("GET /cb?code=x&state=" + State + " HTTP/1.1\r\nX-Pad: "
                                            + new string('a', LoopbackRedirectHandler.MaxHeadBytes));

        Assert.Null(await _handler.HandleAsync(connection, Loopback, TestContext.Current.CancellationToken));
        Assert.StartsWith("HTTP/1.1 400 ", connection.Response);
    }

    [Fact]
    public async Task AConnectionClosedBeforeTheHeadEnds_IsRefused()
    {
        var connection = new FakeConnection($"GET /cb?code=x&state={State} HTTP/1.1\r\n");

        Assert.Null(await _handler.HandleAsync(connection, Loopback, TestContext.Current.CancellationToken));
    }

    /// <summary>A browser's request in, whatever the handler writes back out.</summary>
    private sealed class FakeConnection(string request) : Stream
    {
        private readonly MemoryStream _in = new(Encoding.ASCII.GetBytes(request));
        private readonly MemoryStream _out = new();

        public static FakeConnection Get(string target) =>
            new($"GET {target} HTTP/1.1\r\nHost: 127.0.0.1:53111\r\nUser-Agent: test\r\n\r\n");

        public string Response => Encoding.UTF8.GetString(_out.ToArray());

        public override int Read(byte[] buffer, int offset, int count) => _in.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _out.Write(buffer, offset, count);
        public override void Flush() { }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
