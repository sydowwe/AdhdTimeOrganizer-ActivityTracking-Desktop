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
}
