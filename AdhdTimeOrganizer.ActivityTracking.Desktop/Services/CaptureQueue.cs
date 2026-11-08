using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>What a capture's send came to.</summary>
public enum CaptureSendResult
{
    /// <summary>On the capture list (added, already open, or reopened): remove it.</summary>
    Delivered,

    /// <summary>The server called the text malformed. Kept on disk — it is the user's — but never sent again.</summary>
    Refused,

    /// <summary>404: a server from before the route. Kept; asked again after <see cref="CaptureQueue.HoldFor"/>.</summary>
    NotSupported,

    /// <summary>409: the account has no task priority, so no to-do can be made. Kept; asked again after <see cref="CaptureQueue.HoldFor"/>.</summary>
    NoPriority,

    /// <summary>Offline, a 5xx, a throttle, a token that wouldn't refresh: kept, and tried on the next pass.</summary>
    Later,

    /// <summary>No session: kept until the user signs in.</summary>
    SignedOut
}

/// <param name="Refused">Kept on disk, never sent again: the server called them malformed.</param>
public sealed record CaptureSummary(int Pending, int Refused);

/// <summary>One parked thought on disk. <see cref="Text"/> is user-authored: never log it.</summary>
public sealed record CaptureItem(string Path, string Id, string Text, DateTime CreatedUtc, bool IsRefused);

/// <summary>
/// Thoughts parked with the capture box, waiting for the send loop: one JSON file each, under LocalAppData beside the outbox,
/// so neither an exit, a crash, being offline nor being signed out loses one. Only a delivery removes a file; the queue never
/// drops a thought on its own — not an old one, not an unreadable one, not one the server refused.
/// </summary>
/// <remarks>
/// The rules are the phone's (android prompt 18): text cut to the server's 10 000 characters before queuing, a 2xx removes,
/// a 400 / 413 / 422 marks it refused and keeps it, everything else waits. A retry after a lost answer is harmless: the
/// server finds the item already open and changes nothing.
/// </remarks>
public sealed class CaptureQueue(string directory)
{
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdhdTimeOrganizer.ActivityTracking.Desktop",
        "captures");

    /// <summary>The server's limit (<c>TrackerCaptureValidator.MaxTextLength</c>): longer is a 400, a thought kept and never delivered.</summary>
    public const int MaxTextLength = 10_000;

    /// <summary>How long a server without the route, or an account without a task priority, is left before it is asked again.</summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(30);

    private const string FileNameTimeFormat = "yyyyMMdd'T'HHmmssfff";

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly ILogger _log = Log.ForContext<CaptureQueue>();
    private readonly Lock _gate = new();

    /// <summary>
    /// The typed text as it is queued: trimmed, null when nothing is left (Enter on an empty box is a cancel), and cut to
    /// <see cref="MaxTextLength"/> without splitting a surrogate pair.
    /// </summary>
    public static string? Prepare(string? typed)
    {
        var text = typed?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length <= MaxTextLength) return text;

        var cut = MaxTextLength;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd();
    }

    /// <summary>What a capture's status code means for it.</summary>
    public static CaptureSendResult Classify(HttpStatusCode statusCode) => (int)statusCode switch
    {
        >= 200 and < 300 => CaptureSendResult.Delivered,
        400 or 413 or 422 => CaptureSendResult.Refused,
        404 => CaptureSendResult.NotSupported,
        409 => CaptureSendResult.NoPriority,
        _ => CaptureSendResult.Later
    };

    /// <summary>Persists a thought; <paramref name="text"/> is <see cref="Prepare"/>d first. Null when there was nothing to park.</summary>
    public CaptureItem? Add(string? text, DateTime nowUtc)
    {
        var prepared = Prepare(text);
        if (prepared is null) return null;

        var id = Guid.NewGuid().ToString("N");
        var stored = new StoredCapture(id, prepared, nowUtc, Refused: false);
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{nowUtc.ToString(FileNameTimeFormat, CultureInfo.InvariantCulture)}-{id}.json");
            Write(path, stored);
            return ToItem(path, stored);
        }
    }

    /// <summary>Up to <paramref name="max"/> thoughts still to send, oldest first. Refused and unreadable ones are skipped, never deleted.</summary>
    public List<CaptureItem> Pending(int max)
    {
        lock (_gate)
        {
            var items = new List<CaptureItem>();
            foreach (var (path, stored) in ReadAll())
            {
                if (items.Count >= max) break;
                if (!stored.Refused) items.Add(ToItem(path, stored));
            }
            return items;
        }
    }

    /// <summary>Deletes a delivered thought. False when it was already gone.</summary>
    public bool Remove(CaptureItem item)
    {
        lock (_gate)
        {
            if (!File.Exists(item.Path)) return false;
            File.Delete(item.Path);
            return true;
        }
    }

    /// <summary>Keeps a thought the server refused, but out of every later send.</summary>
    public void MarkRefused(CaptureItem item)
    {
        lock (_gate)
        {
            if (!File.Exists(item.Path)) return;
            Write(item.Path, new StoredCapture(item.Id, item.Text, item.CreatedUtc, Refused: true));
        }
    }

    public CaptureSummary Summary()
    {
        lock (_gate)
        {
            var all = ReadAll();
            var refused = all.Count(c => c.Stored.Refused);
            return new CaptureSummary(all.Count - refused, refused);
        }
    }

    private List<(string Path, StoredCapture Stored)> ReadAll()
    {
        if (!Directory.Exists(directory)) return [];

        var all = new List<(string, StoredCapture)>();
        foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            if (TryRead(path) is { } stored) all.Add((path, stored));
            else _log.Warning("Skipping unreadable capture file {File} — kept, it may be the only copy of a thought", Path.GetFileName(path));
        }
        return all;
    }

    private static CaptureItem ToItem(string path, StoredCapture stored) =>
        new(path, stored.Id, stored.Text, stored.CreatedUtc, stored.Refused);

    private static void Write(string path, StoredCapture stored)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(stored, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    private static StoredCapture? TryRead(string path)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredCapture>(File.ReadAllText(path), JsonOptions);
            return stored is { Id.Length: > 0, Text.Length: > 0 } ? stored : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record StoredCapture(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("createdUtc")] DateTime CreatedUtc,
        [property: JsonPropertyName("refused")] bool Refused);
}
