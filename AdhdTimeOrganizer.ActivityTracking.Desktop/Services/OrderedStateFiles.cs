namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// Writes state snapshots in the order they were taken. <see cref="NudgeService"/> takes them under its policy lock and
/// writes them outside it, so a poll and a button press can each hold a snapshot and reach the disk in either order;
/// without this the older one could land last and come back after a restart. Each snapshot carries the number it was
/// given under that lock, and one older than what a file already holds is not written.
/// </summary>
internal sealed class OrderedStateFiles
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _newestWritten = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="takenAt">Increasing, handed out under the lock the snapshot was taken under.</param>
    /// <returns>False when a snapshot taken later is already in the file, and this one was dropped.</returns>
    public bool Write<T>(string path, T state, long takenAt)
    {
        lock (_gate)
        {
            if (_newestWritten.TryGetValue(path, out var newest) && newest >= takenAt) return false;

            JsonFile.Write(path, state);
            _newestWritten[path] = takenAt;
            return true;
        }
    }
}
