using System.Globalization;
using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

public sealed record OutboxItem(string Path, ActivityWindow Window, DateTime LastWriteUtc);

/// <param name="OldestStartUtc">When the oldest waiting window began (UTC); null when none, or none with a readable name.</param>
public sealed record OutboxSummary(int Count, DateTime? OldestStartUtc);

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
    /// and goes again; the server skips the rows it already holds. True when this deleted it, i.e. the window is
    /// settled; false when it will come round again as a whole.
    /// </summary>
    public bool Remove(OutboxItem item)
    {
        lock (_gate)
        {
            if (!File.Exists(item.Path) || File.GetLastWriteTimeUtc(item.Path) != item.LastWriteUtc) return false;

            File.Delete(item.Path);
            return true;
        }
    }

    public int Count => Summary().Count;

    /// <summary>
    /// How many windows are waiting and when the oldest began, from the file names alone — no file is opened, so the
    /// tray can ask every couple of seconds.
    /// </summary>
    public OutboxSummary Summary()
    {
        lock (_gate)
        {
            if (!Directory.Exists(directory)) return new OutboxSummary(0, null);

            var count = 0;
            DateTime? oldest = null;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                count++;
                if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), FileNameFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)
                    && (oldest is null || start < oldest))
                    oldest = start;
            }
            return new OutboxSummary(count, oldest);
        }
    }

    private const string FileNameFormat = "yyyyMMdd'T'HHmm";

    private string PathFor(DateTime windowStart) =>
        Path.Combine(directory, $"{windowStart.ToString(FileNameFormat, CultureInfo.InvariantCulture)}.json");

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
                    IsFullscreen = group.Any(e => e.IsFullscreen),
                    SwitchCount = group.Any(e => e.SwitchCount.HasValue) ? group.Sum(e => e.SwitchCount ?? 0) : null,
                    IsInCall = group.Any(e => e.IsInCall)
                };
            }))
    };
}
