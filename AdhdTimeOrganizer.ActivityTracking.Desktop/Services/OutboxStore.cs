using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

public sealed record OutboxItem(string Path, ActivityWindow Window, DateTime LastWriteUtc);

/// <summary>
/// Finished windows waiting to be sent, one JSON file per window under LocalAppData. They used to live
/// in an in-memory queue, so every exit, crash, reboot and Windows update lost whatever had not been
/// sent yet — all of an offline afternoon included.
/// </summary>
public sealed class OutboxStore(string directory, TimeSpan retention)
{
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdhdTimeOrganizer.ActivityTracking.Desktop",
        "outbox");

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly ILogger _log = Log.ForContext<OutboxStore>();
    private readonly Lock _gate = new();

    /// <summary>
    /// Persists <paramref name="window"/>. If an unsent file for the same minute exists — the minute was
    /// flushed early and then kept going — the two are merged, since the server keys a window's rows on
    /// (process, title) and would skip the second copy's.
    /// </summary>
    public void Save(ActivityWindow window)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(window.WindowStart);

            if (File.Exists(path) && TryRead(path) is { } existing)
                window = Merge(existing, window);

            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(window, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
    }

    /// <summary>
    /// Up to <paramref name="max"/> pending windows, oldest first. Unreadable files and windows past the
    /// retention are deleted on the way. Bounded so a long offline stretch is not re-read in full on
    /// every send attempt.
    /// </summary>
    public List<OutboxItem> Pending(DateTime nowUtc, int max)
    {
        lock (_gate)
        {
            if (!Directory.Exists(directory)) return [];

            var items = new List<OutboxItem>();
            foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                if (items.Count >= max) break;

                var window = TryRead(path);
                if (window is null)
                {
                    _log.Warning("Deleting unreadable outbox file {File}", Path.GetFileName(path));
                    File.Delete(path);
                    continue;
                }

                if (window.WindowStart < nowUtc - retention)
                {
                    _log.Warning("Dropping window {WindowStart} — older than the {Days}-day outbox retention",
                        window.WindowStart, retention.TotalDays);
                    File.Delete(path);
                    continue;
                }

                items.Add(new OutboxItem(path, window, File.GetLastWriteTimeUtc(path)));
            }
            return items;
        }
    }

    /// <summary>
    /// Deletes a sent window — unless it was rewritten (merged) while in flight, in which case it stays
    /// and goes again; the server skips the rows it already holds.
    /// </summary>
    public void Remove(OutboxItem item)
    {
        lock (_gate)
        {
            if (File.Exists(item.Path) && File.GetLastWriteTimeUtc(item.Path) == item.LastWriteUtc)
                File.Delete(item.Path);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json").Length : 0;
        }
    }

    private string PathFor(DateTime windowStart) =>
        Path.Combine(directory, $"{windowStart:yyyyMMdd'T'HHmm}.json");

    private static ActivityWindow? TryRead(string path)
    {
        try { return JsonSerializer.Deserialize<ActivityWindow>(File.ReadAllText(path), JsonOptions); }
        catch { return null; }
    }

    private static ActivityWindow Merge(ActivityWindow first, ActivityWindow second) => new()
    {
        WindowStart = first.WindowStart,
        Entries = WindowLimits.Apply(first.Entries
            .Concat(second.Entries)
            .GroupBy(e => (e.ProcessName, e.WindowTitle))
            .Select(group =>
            {
                var busiest = group.MaxBy(e => e.ActiveSeconds + e.BackgroundSeconds)!;
                return new ActivityEntry
                {
                    ProcessName = busiest.ProcessName,
                    WindowTitle = busiest.WindowTitle,
                    ExecutablePath = busiest.ExecutablePath,
                    ProductName = busiest.ProductName,
                    ActiveSeconds = group.Sum(e => e.ActiveSeconds),
                    BackgroundSeconds = group.Sum(e => e.BackgroundSeconds),
                    ActiveMonitor = busiest.ActiveMonitor,
                    IsPlayingSound = group.Any(e => e.IsPlayingSound),
                    IsFullscreen = group.Any(e => e.IsFullscreen)
                };
            }))
    };
}
