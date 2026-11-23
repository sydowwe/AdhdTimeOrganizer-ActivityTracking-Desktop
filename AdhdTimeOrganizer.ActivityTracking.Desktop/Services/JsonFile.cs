using System.Text.Json;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>What <see cref="JsonFile.TryRead{T}"/> found.</summary>
public enum JsonFileStatus
{
    /// <summary>Read and parsed: <see cref="JsonFileRead{T}.Value"/> is set.</summary>
    Read,

    /// <summary>No such file (or folder).</summary>
    Missing,

    /// <summary>
    /// Opened, but not a value of the type: empty, cut short, not JSON, a JSON <c>null</c>, or a shape that does not bind.
    /// Reading it again will not help.
    /// </summary>
    Unreadable,

    /// <summary>
    /// There, but not readable right now: locked by another process, access denied, a disk error. It may well read on the
    /// next try, so it is never thrown away for this.
    /// </summary>
    CouldNotOpen
}

/// <param name="Error">Why it was <see cref="JsonFileStatus.Unreadable"/> or <see cref="JsonFileStatus.CouldNotOpen"/>; for the log.</param>
public readonly record struct JsonFileRead<T>(JsonFileStatus Status, T? Value, Exception? Error) where T : class;

/// <summary>
/// Every state file the tracker keeps — config, outbox, captures, today's total, the nudges' and the evening review's state —
/// is written and read through this, so each is written the same durable way and a reader can tell "not there" from
/// "damaged" from "busy".
/// </summary>
public static class JsonFile
{
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Serializes <paramref name="value"/> exactly as <see cref="JsonSerializer.Serialize{TValue}(TValue, JsonSerializerOptions?)"/> would, and <see cref="WriteBytes"/> it.</summary>
    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null) =>
        WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, options));

    /// <summary>
    /// Writes <c>path.tmp</c>, flushes it to the disk, then moves it over <paramref name="path"/>, creating the folder if
    /// needed. A power cut leaves the old file or the new one, never a short or zero-length one: without the flush, the
    /// rename can reach the disk before the data does.
    /// </summary>
    public static void WriteBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>Reads and parses <paramref name="path"/>. Never throws.</summary>
    public static JsonFileRead<T> TryRead<T>(string path, JsonSerializerOptions? options = null) where T : class
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new JsonFileRead<T>(JsonFileStatus.Missing, null, null);
        }
        catch (Exception ex)
        {
            return new JsonFileRead<T>(JsonFileStatus.CouldNotOpen, null, ex);
        }

        try
        {
            // A file saved by hand in Notepad may start with one; what File.ReadAllText used to skip.
            var json = bytes.AsSpan();
            if (json.StartsWith(Utf8Bom)) json = json[Utf8Bom.Length..];

            return JsonSerializer.Deserialize<T>(json, options) is { } value
                ? new JsonFileRead<T>(JsonFileStatus.Read, value, null)
                : new JsonFileRead<T>(JsonFileStatus.Unreadable, null, null);
        }
        catch (Exception ex)
        {
            return new JsonFileRead<T>(JsonFileStatus.Unreadable, null, ex);
        }
    }
}
