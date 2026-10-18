using System.Diagnostics;
using Microsoft.Win32;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Native;

/// <summary>
/// Whether any app is using the microphone right now — a call or a meeting, even a muted one.
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

    public static bool IsInUse()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ConsentStoreKey);
            if (root is null) return false;

            foreach (var name in root.GetSubKeyNames())
            {
                if (name.Equals(NonPackagedKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsHoldingMicrophone(root, name)) return true;
            }

            using var nonPackaged = root.OpenSubKey(NonPackagedKey);
            if (nonPackaged is null) return false;

            foreach (var name in nonPackaged.GetSubKeyNames())
            {
                if (IsHoldingMicrophone(nonPackaged, name) && IsRunning(ExecutablePathFromKeyName(name)))
                    return true;
            }
        }
        catch (Exception ex)
        {
            if (!_hasLoggedFailure)
            {
                _log.Warning(ex, "Could not read microphone use — calls will not hold nudges back");
                _hasLoggedFailure = true;
            }
        }

        return false;
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
