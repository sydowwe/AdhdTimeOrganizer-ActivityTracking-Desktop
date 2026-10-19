using Serilog;
using Velopack;
using Velopack.Sources;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// Looks for a newer release on GitHub, downloads it, and on request hands it to Velopack to apply. The decisions are
/// <see cref="UpdatePolicy"/>'s; this only runs them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only an installed build updates.</b> A build run from <c>bin/</c> or <c>artifacts-cli/</c> is not Velopack's to
/// replace: <see cref="IsInstalled"/> is false there and nothing is checked.
/// </para>
/// <para>
/// <b>Restart for an update goes through the normal exit.</b> <see cref="PrepareRestart"/> starts Velopack's updater,
/// which waits (up to a minute) for this process to end; the caller then exits the usual way — the minute in progress
/// flushed to the outbox, toasts removed, notifications unregistered — and the mutex is released before the updater
/// starts the new build. <c>ApplyUpdatesAndRestart</c> is not used: it ends the process itself and skips all of that.
/// </para>
/// </remarks>
public sealed class UpdateService : IDisposable
{
    /// <summary>The release feed: this repo's GitHub Releases. Public, so no token travels with the app.</summary>
    internal const string RepositoryUrl = "https://github.com/sydowwe/AdhdTimeOrganizer-ActivityTracking-Desktop";

    private static readonly Lazy<bool> Installed = new(ReadInstalled);

    /// <summary>This process is the Velopack-installed build.</summary>
    public static bool IsInstalled => Installed.Value;

    private readonly ILogger _log = Log.ForContext<UpdateService>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private UpdateManager? _manager;
    private VelopackAsset? _pending;

    /// <summary>The downloaded release waiting for a restart, or null. Read by the tray's timer.</summary>
    public string? PendingVersion
    {
        get
        {
            lock (_gate) return _pending?.Version.ToString();
        }
    }

    public void Start()
    {
        if (!IsInstalled) return;

        _manager = CreateManager();
        lock (_gate) _pending = _manager.UpdatePendingRestart;
        _ = RunAsync(_stop.Token);
    }

    /// <summary>
    /// Hands the waiting release to Velopack's updater, which applies it once this process has exited and starts the new
    /// build. False when there is nothing to apply or the updater could not be started — the caller then does not exit.
    /// </summary>
    public bool PrepareRestart()
    {
        VelopackAsset? pending;
        lock (_gate) pending = _pending;
        if (_manager is null || pending is null) return false;

        try
        {
            _manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true, restartArgs: ["--minimized"]);
            _log.Information("Restarting to apply update {Version}", pending.Version);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not start the updater for {Version}", pending.Version);
            return false;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(UpdatePolicy.FirstCheckAfter, ct);
            while (!ct.IsCancellationRequested)
            {
                var outcome = await CheckOnceAsync(ct);
                await Task.Delay(UpdatePolicy.NextCheckAfter(outcome), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task<UpdateCheckOutcome> CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            var update = await _manager!.CheckForUpdatesAsync();
            if (update is null) return UpdateCheckOutcome.UpToDate;

            await _manager.DownloadUpdatesAsync(update, cancelToken: ct);
            lock (_gate) _pending = update.TargetFullRelease;
            _log.Information("Update {Version} downloaded; it applies at the next start", update.TargetFullRelease.Version);
            return UpdateCheckOutcome.Downloaded;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Update check failed; trying again in {Delay}", UpdatePolicy.RetryAfterFailure);
            return UpdateCheckOutcome.Failed;
        }
    }

    private static UpdateManager CreateManager() => new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));

    private static bool ReadInstalled()
    {
        try
        {
            return CreateManager().IsInstalled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
