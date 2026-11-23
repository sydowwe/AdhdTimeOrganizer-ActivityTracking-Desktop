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

    private const string BadSuffix = ".bad";

    /// <summary>Files already logged as locked, so a lock that lasts a while is one line, not one per pass. Under <see cref="_gate"/>.</summary>
    private readonly HashSet<string> _reportedLocked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Persists <paramref name="window"/>. If an unsent file for the same minute exists — the minute was
    /// flushed early and then kept going — the two are merged, since the server keys a window's rows on
    /// (process, title) and would skip the second copy's. A damaged one is set aside first; one that stays locked
    /// is not written over — this throws instead, and the poll loop logs it.
    /// </summary>
    public void Save(ActivityWindow window)
    {
        lock (_gate)
        {
            var path = PathFor(window.WindowStart);

            var existing = JsonFile.TryRead<ActivityWindow>(path, JsonOptions);
            for (var attempt = 0; attempt < 3 && existing.Status == JsonFileStatus.CouldNotOpen; attempt++)
            {
                Thread.Sleep(100);
                existing = JsonFile.TryRead<ActivityWindow>(path, JsonOptions);
            }

            switch (existing.Status)
            {
                case JsonFileStatus.Read:
                    window = Merge(existing.Value!, window);
                    break;
                case JsonFileStatus.Unreadable:
                    SetAside(path, existing.Error);
                    break;
                case JsonFileStatus.CouldNotOpen:
                    throw new IOException($"Outbox file {Path.GetFileName(path)} could not be opened to merge into", existing.Error);
            }

            JsonFile.Write(path, window, JsonOptions);
        }
    }

    /// <summary>
    /// Up to <paramref name="max"/> pending windows, oldest first. Bounded so a long offline stretch is not re-read in
    /// full on every send attempt. On the way: windows past the retention are deleted; a file that is not a window is
    /// renamed to <c>.bad</c> (kept for the retention, then deleted); a file that could not be opened — locked for a
    /// moment by a scanner or a backup — is left for the next pass.
    /// </summary>
    public List<OutboxItem> Pending(DateTime nowUtc, int max)
    {
        lock (_gate)
        {
            if (!Directory.Exists(directory)) return [];

            DeleteExpiredBadFiles(nowUtc);

            var items = new List<OutboxItem>();
            foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                if (items.Count >= max) break;

                var read = JsonFile.TryRead<ActivityWindow>(path, JsonOptions);
                if (read.Status == JsonFileStatus.Unreadable)
                {
                    SetAside(path, read.Error);
                    continue;
                }
                if (read.Status == JsonFileStatus.CouldNotOpen)
                {
                    if (_reportedLocked.Add(path))
                        _log.Information(read.Error, "Outbox file {File} could not be opened — leaving it for the next pass", Path.GetFileName(path));
                    continue;
                }
                if (read.Value is not { } window) continue; // gone since the listing
                _reportedLocked.Remove(path);

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

    /// <summary>Not a window, and reading it again won't make it one: kept beside the outbox, out of every pass.</summary>
    private void SetAside(string path, Exception? why)
    {
        try
        {
            File.Move(path, path + BadSuffix, overwrite: true);
            _log.Warning(why, "Outbox file {File} is not a window — renamed to {Suffix}", Path.GetFileName(path), BadSuffix);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Outbox file {File} is not a window and could not be renamed", Path.GetFileName(path));
        }
    }

    /// <summary>A <c>.bad</c> file is kept as long as a window would have been, by when it was last written.</summary>
    private void DeleteExpiredBadFiles(DateTime nowUtc)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*" + BadSuffix))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < nowUtc - retention) File.Delete(path);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not delete the old outbox file {File}", Path.GetFileName(path));
            }
        }
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
