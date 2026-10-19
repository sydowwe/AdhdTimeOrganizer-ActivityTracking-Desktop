using Microsoft.Win32;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Helpers;

/// <summary>What "Start with Windows" really amounts to.</summary>
public enum AutoStartState
{
    /// <summary>No entry: Windows will not start the tracker.</summary>
    Off,

    /// <summary>An entry, and Windows is honouring it.</summary>
    On,

    /// <summary>An entry exists, but the user turned it off in Task Manager or Settings → Startup apps. Windows skips it.</summary>
    DisabledInWindows
}

/// <summary>The two registry keys autostart lives in. The real one is <see cref="RegistryAutoStartStore"/>; tests pass a dictionary.</summary>
internal interface IAutoStartStore
{
    /// <summary><c>HKCU\…\Run</c>: the command Windows runs at sign-in.</summary>
    string? GetRunValue(string name);
    void SetRunValue(string name, string command);
    void DeleteRunValue(string name);

    /// <summary><c>HKCU\…\Explorer\StartupApproved\Run</c>: what Task Manager and Settings write when the user flips the switch.</summary>
    byte[]? GetApproved(string name);
    void SetApproved(string name, byte[] data);
    void DeleteApproved(string name);
}

/// <summary>
/// "Start with Windows": an entry under the user's <c>Run</c> key, under a name that is this tracker's alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not just "does the value exist".</b> That stayed ticked when the entry pointed at an exe that had moved (the
/// output folder changed when the build became RID-specific) and when the user switched the entry off in Task Manager —
/// the entry is still in <c>Run</c>, and Windows keeps its verdict next to it in <c>StartupApproved\Run</c>: 12 bytes
/// whose first byte is even when enabled and odd when disabled (the rest is the disable time).
/// </para>
/// <para>
/// <b>The name.</b> It used to be <c>AdhdTimeOrganizer</c>, which the separate desktop app of that name also uses, so the
/// checkbox reflected — and toggling it overwrote or deleted — that app's entry. The name before that,
/// <c>DesktopActivityTracker</c>, is left over on machines that ran an older build; <see cref="Reconcile"/> removes it.
/// Neither of the other two names is ever written or deleted by this class except that one.
/// </para>
/// </remarks>
public sealed class AutoStartHelper
{
    public const string AppName = "AdhdTimeOrganizer.ActivityTracking.Desktop";
    internal const string LegacyName = "DesktopActivityTracker";

    /// <summary>What Windows writes for "enabled": state 2, then no disable time.</summary>
    private static readonly byte[] EnabledMarker = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    public static AutoStartHelper Current { get; } = new(new RegistryAutoStartStore(), Environment.ProcessPath, File.Exists);

    private readonly IAutoStartStore _store;
    private readonly string? _exePath;
    private readonly Func<string, bool> _fileExists;
    private readonly ILogger _log = Log.ForContext<AutoStartHelper>();

    internal AutoStartHelper(IAutoStartStore store, string? exePath, Func<string, bool> fileExists)
    {
        _store = store;
        _exePath = exePath;
        _fileExists = fileExists;
    }

    public AutoStartState GetState()
    {
        try
        {
            if (_store.GetRunValue(AppName) is null) return AutoStartState.Off;
            return IsDisabled(_store.GetApproved(AppName)) ? AutoStartState.DisabledInWindows : AutoStartState.On;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not read the autostart entry");
            return AutoStartState.Off;
        }
    }

    /// <summary>Turning it on also undoes a switch-off in Windows' own settings: ticking the box is the user asking for it.</summary>
    public void Enable()
    {
        if (_exePath is null) return;

        try
        {
            _store.SetRunValue(AppName, Command(_exePath));
            if (IsDisabled(_store.GetApproved(AppName)))
                _store.SetApproved(AppName, EnabledMarker);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not turn autostart on");
        }
    }

    public void Disable()
    {
        try
        {
            _store.DeleteRunValue(AppName);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not turn autostart off");
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }

    /// <summary>
    /// At start: carries the pre-rename entry over and removes it, and repoints an entry whose exe is gone at this one.
    /// An entry whose exe still exists is left alone even if it is another build's — a Debug run from the IDE must not
    /// redirect a Release autostart — and so is Windows' verdict on it.
    /// </summary>
    public void Reconcile()
    {
        try
        {
            if (_store.GetRunValue(LegacyName) is not null)
            {
                var wasOn = !IsDisabled(_store.GetApproved(LegacyName));
                _store.DeleteRunValue(LegacyName);
                _store.DeleteApproved(LegacyName);
                _log.Information("Removed the autostart entry an older build left under its previous name");

                if (wasOn && _store.GetRunValue(AppName) is null) Enable();
            }

            if (_exePath is not null
                && _store.GetRunValue(AppName) is { } command
                && ExecutablePath(command) is { } target
                && !_fileExists(target))
            {
                _store.SetRunValue(AppName, Command(_exePath));
                _log.Information("The autostart entry pointed at a file that no longer exists — repointed at this one");
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not reconcile the autostart entry");
        }
    }

    private static string Command(string exePath) => $"\"{exePath}\" --minimized";

    /// <summary>Windows' verdict is "disabled" when the first byte is odd (3, 7); an absent value means enabled.</summary>
    internal static bool IsDisabled(byte[]? approved) => approved is { Length: > 0 } && (approved[0] & 1) == 1;

    /// <summary>The exe a Run command starts: the quoted path, or an unquoted one up to its <c>.exe</c>.</summary>
    internal static string? ExecutablePath(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.Length == 0) return null;

        if (command[0] == '"')
        {
            var close = command.IndexOf('"', 1);
            return close > 1 ? command[1..close] : null;
        }

        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? command[..(exe + 4)] : command.Split(' ')[0];
    }
}

internal sealed class RegistryAutoStartStore : IAutoStartStore
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public string? GetRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(name) as string;
    }

    public void SetRunValue(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(name, command);
    }

    public void DeleteRunValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        key?.DeleteValue(name, false);
    }

    public byte[]? GetApproved(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, false);
        return key?.GetValue(name) as byte[];
    }

    public void SetApproved(string name, byte[] data)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        key.SetValue(name, data, RegistryValueKind.Binary);
    }

    public void DeleteApproved(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);
        key?.DeleteValue(name, false);
    }
}
