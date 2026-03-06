using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Serilog;

namespace DesktopActivityTracker.Native;

internal record ActiveAudioInfo(HashSet<int> Pids, HashSet<string> ExePaths);

internal static class AudioSessionHelper
{
    private static readonly ILogger _log = Log.ForContext(typeof(AudioSessionHelper));

    // Treat anything above this as "actually playing" — filters out paused/silent sessions
    private const float MinPeakValue = 0.001f;

    /// <summary>
    /// Returns the PIDs and executable paths of all processes currently producing audible audio.
    /// Executable paths enable matching browser audio subprocesses back to their parent exe.
    /// Excludes PID 0 (system audio) and silent/paused sessions.
    /// Returns empty sets if no audio device is available.
    /// </summary>
    public static ActiveAudioInfo GetActiveAudioInfo()
    {
        var pids = new HashSet<int>();
        var exePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            var sessions = device.AudioSessionManager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];

                if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                if (session.AudioMeterInformation.MasterPeakValue < MinPeakValue) continue;

                var pid = (int)session.GetProcessID;
                if (pid == 0) continue;

                pids.Add(pid);

                try
                {
                    var exePath = Process.GetProcessById(pid).MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath)) exePaths.Add(exePath);
                }
                catch { /* process may have exited */ }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to enumerate audio sessions");
        }
        return new ActiveAudioInfo(pids, exePaths);
    }
}
