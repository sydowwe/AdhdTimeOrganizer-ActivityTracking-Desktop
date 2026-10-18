using AdhdTimeOrganizer.ActivityTracking.Desktop.Native;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// Calls the P/Invoke paths against the real machine. A wrong signature compiles fine and fails only
/// here — or, without this, silently in the tray as entries with no path.
/// </summary>
public class NativeSmokeTests
{
    [Fact]
    public void ProcessInfoCache_ResolvesTheCurrentProcess_WithItsPath()
    {
        var info = ProcessInfoCache.Get(Environment.ProcessId);

        Assert.NotNull(info);
        Assert.Equal(Environment.ProcessPath, info.ExecutablePath, ignoreCase: true);
        Assert.Equal(Path.GetFileNameWithoutExtension(Environment.ProcessPath), info.ProcessName, ignoreCase: true);
        Assert.Same(info, ProcessInfoCache.Get(Environment.ProcessId));
    }

    [Fact]
    public void Monitors_AreEnumerated_PrimaryFirst()
    {
        var monitors = MonitorHelper.GetMonitors();

        Assert.NotEmpty(monitors);
        Assert.Equal((0, 0), (monitors[0].Bounds.Left, monitors[0].Bounds.Top));
    }

    [Fact]
    public void BackgroundWindowsAndAudio_DoNotThrow()
    {
        var windows = MonitorHelper.GetBackgroundWindows(Win32.GetForegroundWindow(), MonitorHelper.GetMonitors(), 0, 5);
        var audio = AudioSessionHelper.GetActiveAudioInfo();

        Assert.All(windows, w => Assert.False(string.IsNullOrEmpty(w.Process.ProcessName)));
        Assert.NotNull(audio);
    }

    [Fact]
    public void ShellNotificationState_IsReadable()
    {
        Assert.Equal(0, Win32.SHQueryUserNotificationState(out var state));
        Assert.True(Enum.IsDefined(state), $"unknown state {(int)state}");
    }

    [Fact]
    public void MicrophoneUsage_ReadsTheConsentStore_WithoutThrowing() =>
        _ = MicrophoneUsage.IsInUse(); // fails closed — a wrong layout reads as "not in use", logged once

    /// <summary>
    /// The project extracts this from the runtime MSIX because the self-contained SDK 2.x leaves it out, and
    /// <c>AppNotificationManager.Register</c> throws without it: no toast could ever be shown. The test
    /// output gets it through the same project reference the app's output does.
    /// </summary>
    [Fact]
    public void WindowsAppRuntimeInsightsResource_AndTheToastIcon_AreDeployed()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.Insights.Resource.dll")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "timeorganizer-icon.ico")));
    }
}
