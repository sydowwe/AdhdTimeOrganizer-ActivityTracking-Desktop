using System.Diagnostics;
using Microsoft.Win32;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Native;

/// <summary>The apps holding the microphone: desktop apps by executable path, packaged ones by package family name.</summary>
internal sealed record MicrophoneHolders(IReadOnlySet<string> ExecutablePaths, IReadOnlySet<string> PackageFamilyNames)
{
    public static MicrophoneHolders None { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool Any => ExecutablePaths.Count > 0 || PackageFamilyNames.Count > 0;

    /// <summary>Whether <paramref name="process"/> is one of them.</summary>
    public bool Holds(ProcessInfo process) =>
        (process.ExecutablePath is { } path && ExecutablePaths.Contains(path))
        || (process.PackageFamilyName is { } family && PackageFamilyNames.Contains(family));

    /// <summary>
    /// The running processes that hold it, for an app that is in a call without being on screen or audible — nothing
    /// else would put it in front of the tracker. Desktop apps are looked up by name; a packaged one needs every
    /// process's package identity, so that scan only runs while a packaged app holds the microphone.
    /// </summary>
    public List<int> FindProcessIds()
    {
        var pids = new List<int>();
        if (!Any) return pids;

        if (PackageFamilyNames.Count > 0)
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                    if (ProcessInfoCache.Get(process.Id) is { } info && Holds(info)) pids.Add(process.Id);
            }
            return pids;
        }

        foreach (var path in ExecutablePaths)
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            using (process)
                if (ProcessInfoCache.Get(process.Id) is { } info && Holds(info)) pids.Add(process.Id);
        }

        return pids;
    }
}

/// <summary>
/// Whether any app is using the microphone right now — a call or a meeting, even a muted one — and which.
/// </summary>
/// <remarks>
/// <para>
/// Windows records microphone use per app under the capability consent store: one subkey per packaged
/// app, and one per executable under <c>NonPackaged</c> (the path with <c>#</c> for <c>\</c>). An entry
/// whose <c>LastUsedTimeStop</c> is <c>0</c> has the microphone open now. The layout is undocumented but
/// has been stable since Windows 10 1903.
/// </para>
/// <para>
/// Fails closed: any surprise in the layout means "not in use", never an exception. A desktop app that
/// crashed with the microphone open can leave its stop time at 0 for good, which would hold every nudge
/// back without a trace; so a non-packaged entry only counts while a process of that name is running.
/// </para>
/// </remarks>
internal static class MicrophoneUsage
{
    private const string ConsentStoreKey =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private const string NonPackagedKey = "NonPackaged";

    private static readonly ILogger _log = Log.ForContext(typeof(MicrophoneUsage));
    private static bool _hasLoggedFailure;

    public static bool IsInUse() => GetHolders().Any;

    /// <summary>Every app holding the microphone now; <see cref="MicrophoneHolders.None"/> when none is, or the store can't be read.</summary>
    public static MicrophoneHolders GetHolders()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentStoreKey);
            if (root is null) return MicrophoneHolders.None;

            var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in root.GetSubKeyNames())
            {
                if (name.Equals(NonPackagedKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsHoldingMicrophone(root, name)) families.Add(name);
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var nonPackaged = root.OpenSubKey(NonPackagedKey);
            if (nonPackaged is not null)
            {
                foreach (var name in nonPackaged.GetSubKeyNames())
                {
                    var path = ExecutablePathFromKeyName(name);
                    if (IsHoldingMicrophone(nonPackaged, name) && IsRunning(path)) paths.Add(path);
                }
            }

            return paths.Count == 0 && families.Count == 0 ? MicrophoneHolders.None : new MicrophoneHolders(paths, families);
        }
        catch (Exception ex)
        {
            if (!_hasLoggedFailure)
            {
                _log.Warning(ex, "Could not read microphone use — calls will not hold nudges back or be marked");
                _hasLoggedFailure = true;
            }
        }

        return MicrophoneHolders.None;
    }

    /// <summary>In use = started at some point and not stopped since. Anything else, including a missing value, is not.</summary>
    internal static bool IsHolding(object? lastUsedStart, object? lastUsedStop) =>
        lastUsedStart is long start && start > 0 && lastUsedStop is long stop && stop == 0;

    internal static string ExecutablePathFromKeyName(string keyName) => keyName.Replace('#', '\\');

    private static bool IsHoldingMicrophone(RegistryKey parent, string name)
    {
        using var app = parent.OpenSubKey(name);
        return app is not null && IsHolding(app.GetValue("LastUsedTimeStart"), app.GetValue("LastUsedTimeStop"));
    }

    private static bool IsRunning(string executablePath)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath));
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }
}
