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
    public void MicrophoneUsage_ReadsTheConsentStore_WithoutThrowing()
    {
        // Fails closed — a wrong layout reads as "not in use", logged once.
        var holders = MicrophoneUsage.GetHolders();
        Assert.Equal(holders.Any, MicrophoneUsage.IsInUse());
        Assert.All(holders.FindProcessIds(), pid => Assert.True(pid > 0));
    }

    /// <summary>
    /// The hook is set and removed on this thread, which has no message loop, so nothing is delivered — what this
    /// pins is the signature: a wrong one returns a null hook, and the tracker silently falls back to polling.
    /// </summary>
    [Fact]
    public void ForegroundEvents_InstallAndUninstall()
    {
        Assert.True(ForegroundEvents.Install());
        Assert.True(ForegroundEvents.IsInstalled);

        ForegroundEvents.Uninstall();
        Assert.False(ForegroundEvents.IsInstalled);
    }

    /// <summary>
    /// A packaged call app (the new Teams) is named by its package family name in the microphone's consent store; a
    /// wrong signature here would leave every packaged app unrecognised, and its calls unmarked.
    /// </summary>
    [Fact]
    public void PackageFamilyName_IsReadForPackagedProcesses_AndNullForThisOne()
    {
        Assert.Null(ProcessInfoCache.Get(Environment.ProcessId)!.PackageFamilyName);

        string[] packagedShellProcesses = ["StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "TextInputHost"];
        var running = packagedShellProcesses.SelectMany(System.Diagnostics.Process.GetProcessesByName).ToList();
        try
        {
            if (running.Count == 0)
                Assert.Skip("None of the packaged shell processes is running here.");

            Assert.Contains(running, p => ProcessInfoCache.Get(p.Id)?.PackageFamilyName?.Contains('_') == true);
        }
        finally
        {
            foreach (var process in running) process.Dispose();
        }
    }

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
