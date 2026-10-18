using Microsoft.Windows.AppNotifications;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>
/// Windows notifications through the Windows App SDK, unpackaged: <see cref="AppNotificationManager.Register(string, Uri)"/>
/// gives the exe an identity of its own, so no Start-menu shortcut or installer is needed.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here throws. If notifications can't work — an old Windows, a broken runtime, notifications
/// turned off for the app — nudges are logged once and silently dropped; tracking is unaffected.
/// </para>
/// <para>
/// A button press arrives on a thread-pool thread, through <c>NotificationInvoked</c> while the tracker
/// runs. On exit every toast is removed and the registration undone, so a stale toast can't relaunch the
/// tracker into a nudge from yesterday.
/// </para>
/// </remarks>
internal sealed class NudgeNotifier : IDisposable
{
    private const string DisplayName = "Activity Tracker";

    private readonly ILogger _log = Log.ForContext<NudgeNotifier>();
    private readonly Action<NudgeActivation> _onActivated;
    private readonly AppNotificationManager? _manager;

    public NudgeNotifier(Action<NudgeActivation> onActivated)
    {
        _onActivated = onActivated;

        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                _log.Warning("This Windows can't show app notifications — nudges are off");
                return;
            }

            var manager = AppNotificationManager.Default;
            // Subscribed before Register, as the SDK requires; otherwise the first press can be lost.
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register(DisplayName, new Uri(Path.Combine(AppContext.BaseDirectory, "timeorganizer-icon.ico")));
            _manager = manager;

            if (manager.Setting != AppNotificationSetting.Enabled)
                _log.Warning("Windows has notifications for the tracker turned off ({Setting}) — nudges won't be seen", manager.Setting);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not register for notifications — nudges are off");
        }
    }

    public void Show(Nudge nudge)
    {
        if (_manager is null) return;
        try
        {
            _manager.Show(NudgeToast.Build(nudge));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not show a {Kind} nudge", nudge.Kind);
        }
    }

    public void Remove(NudgeKind kind)
    {
        if (_manager is null) return;
        _ = RemoveAsync(_manager, kind);
    }

    private async Task RemoveAsync(AppNotificationManager manager, NudgeKind kind)
    {
        try
        {
            await manager.RemoveByTagAndGroupAsync(NudgeToast.Tag(kind), NudgeToast.Group);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not withdraw a {Kind} nudge", kind);
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            if (NudgeToast.Parse(args.Arguments) is { } activation)
                _onActivated(activation);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Handling a nudge button failed");
        }
    }

    public void Dispose()
    {
        if (_manager is null) return;
        try
        {
            _manager.RemoveAllAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not clear nudges on exit");
        }

        try
        {
            _manager.NotificationInvoked -= OnNotificationInvoked;
            _manager.Unregister();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not unregister notifications on exit");
        }
    }
}
