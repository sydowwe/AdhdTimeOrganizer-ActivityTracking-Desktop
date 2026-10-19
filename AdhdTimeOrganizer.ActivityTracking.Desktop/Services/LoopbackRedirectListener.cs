using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// One connection to the sign-in listener: reads the request head, decides with <see cref="BrowserSignIn.ReadRedirect"/>,
/// answers the browser with a small page. Works on a <see cref="Stream"/> so tests can drive it with a fake request.
/// </summary>
/// <remarks>
/// Exactly one sign-in is accepted. A request with the wrong state or the wrong path is answered and the wait goes on, so
/// a stray request (a favicon, a probe) cannot end the user's sign-in; a second redirect after the accepted one is
/// refused. A connection from anywhere but loopback is closed without a word — the socket binds to <c>127.0.0.1</c>, so
/// this is a second lock on the same door.
/// </remarks>
internal sealed class LoopbackRedirectHandler(string expectedState)
{
    /// <summary>A browser's request head is well under this; anything longer is not a browser following a redirect.</summary>
    internal const int MaxHeadBytes = 8 * 1024;

    internal static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(5);

    private readonly ILogger _log = Log.ForContext<LoopbackRedirectHandler>();
    private int _accepted;

    /// <summary>The code, when this connection was the sign-in; null otherwise.</summary>
    public async Task<string?> HandleAsync(Stream connection, IPAddress? remote, CancellationToken ct)
    {
        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            _log.Warning("Sign-in listener refused a connection that is not from loopback");
            return null;
        }

        using var readLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readLimit.CancelAfter(ReadLimit);

        string? requestLine;
        try
        {
            requestLine = await ReadRequestLineAsync(connection, readLimit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            requestLine = null;
        }

        var verdict = requestLine is null
            ? new RedirectVerdict(RedirectKind.Malformed)
            : BrowserSignIn.ReadRedirect(requestLine, expectedState);

        switch (verdict.Kind)
        {
            case RedirectKind.Code when Interlocked.Exchange(ref _accepted, 1) == 0:
                await RespondAsync(connection, 200, "Back to the tracker",
                    "The browser's part is done. You can close this tab and go back to the activity tracker.", ct);
                return verdict.Code;

            case RedirectKind.Code:
                _log.Warning("Sign-in listener refused a second redirect");
                await RespondAsync(connection, 409, "Already used",
                    "This sign-in has already been handed to the tracker. You can close this tab.", ct);
                return null;

            case RedirectKind.NotOurs:
                await RespondAsync(connection, 404, "Not found", "Nothing here.", ct);
                return null;

            default:
                _log.Warning("Sign-in listener refused a redirect: {Kind}", verdict.Kind);
                await RespondAsync(connection, 400, "Not this sign-in",
                    "This link does not belong to the sign-in the tracker is waiting for. Start again from the tracker.", ct);
                return null;
        }
    }

    /// <summary>The first line of the request, once the whole head has arrived; null if it never ends inside the limit.</summary>
    private static async Task<string?> ReadRequestLineAsync(Stream connection, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await connection.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
                return null;
            length += read;

            var head = Encoding.ASCII.GetString(buffer, 0, length);
            if (head.Contains("\r\n\r\n", StringComparison.Ordinal))
                return head[..head.IndexOf("\r\n", StringComparison.Ordinal)];
        }
        return null;
    }

    private static async Task RespondAsync(Stream connection, int status, string title, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{title}</title></head>"
            + $"<body style=\"font-family:sans-serif;margin:3em auto;max-width:32em\"><h2>{title}</h2><p>{message}</p></body></html>");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {ReasonPhrase(status)}\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n");

        try
        {
            await connection.WriteAsync(head, ct);
            await connection.WriteAsync(body, ct);
            await connection.FlushAsync(ct);
        }
        catch (IOException)
        {
            // The browser hung up first; the decision stands either way.
        }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        409 => "Conflict",
        _ => "Error"
    };
}

/// <summary>
/// The socket half of the browser sign-in: a <see cref="TcpListener"/> on <c>127.0.0.1</c>, on a port the system picks,
/// alive only while the sign-in form waits. A <see cref="TcpListener"/> rather than <c>HttpListener</c>: that one needs
/// no URL ACL for <c>http://127.0.0.1:&lt;port&gt;/</c> as a standard user either (checked 2026-10-19, Windows 11), but it
/// cannot bind port 0 and say which port it got, so it would have to guess a free port and retry — and the handler on a
/// plain stream is what the tests drive.
/// </summary>
public sealed class LoopbackRedirectListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly LoopbackRedirectHandler _handler;

    public LoopbackRedirectListener(string expectedState)
    {
        _handler = new LoopbackRedirectHandler(expectedState);
        _listener.Start();
    }

    public string RedirectUri => BrowserSignIn.RedirectUri(((IPEndPoint)_listener.LocalEndpoint).Port);

    /// <summary>
    /// The code from the first redirect that is our sign-in, or null when <paramref name="waitLimit"/> passed first. Throws
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> is cancelled — the user pressed Cancel or closed
    /// the form.
    /// </summary>
    public async Task<string?> WaitForCodeAsync(TimeSpan waitLimit, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(waitLimit);

        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(limit.Token);
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                await using var stream = client.GetStream();

                if (await _handler.HandleAsync(stream, remote, limit.Token) is { } code)
                    return code;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public void Dispose() => _listener.Stop();
}
