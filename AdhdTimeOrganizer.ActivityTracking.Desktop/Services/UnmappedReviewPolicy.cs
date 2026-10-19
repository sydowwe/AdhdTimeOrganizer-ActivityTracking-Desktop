using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

public enum ReviewTiming
{
    /// <summary>The evening has not come yet.</summary>
    NotYet,

    /// <summary>The evening is here and has not been looked at: ask the server what today left unmapped.</summary>
    Due,

    /// <summary>The evening came and went while the agent could not ask (asleep, off): too late to ring now.</summary>
    Missed,

    /// <summary>This evening was already invited, or found nothing worth asking.</summary>
    Handled
}

/// <summary>One line of the invite. <see cref="Name"/> is a product or process name, never a window title.</summary>
public sealed record InviteApp(string Name, TimeSpan Length, bool IsBrowser);

/// <param name="Unmapped">All of today's unmapped time, not only the named apps'.</param>
/// <param name="Apps">The busiest few, busiest first.</param>
/// <param name="More">How many more the server named, past <see cref="Apps"/>.</param>
public sealed record UnmappedInvite(TimeSpan Unmapped, IReadOnlyList<InviteApp> Apps, int More);

/// <summary>
/// Decides when the evening invite is due and what it says: "2 h 10 m on this PC today isn't mapped — Discord 50 m,
/// Chrome 30 m". Pure — no clock, no server, no toast — like <see cref="DriftPolicy"/>: the caller passes the time and
/// what the server answered.
/// </summary>
/// <remarks>
/// <para>
/// <b>The server decides whether today is worth asking about.</b> Its thresholds (an app needs five minutes, the day
/// fifteen) are <c>DesktopUnmappedTime</c>'s, and an empty app list is its "don't ask". Repeating them here would put the
/// same number in two repos to drift apart, so the only rule on this side is that an empty list is no invite.
/// </para>
/// <para>
/// <b>One look per evening.</b> The evening moment is the server's (the same instant the phone's review and the
/// evening-shutdown push use); an evening is handled once, whether it ended in an invite or in nothing to say, so a
/// restart or a second pass never asks again. A moment more than <see cref="Window"/> old is missed rather than late:
/// a laptop opened at 2 a.m. is not reminded of last night's ritual.
/// </para>
/// <para>
/// The invite names apps, not what the user was doing in them: nothing here is a window title.
/// </para>
/// </remarks>
public static class UnmappedReviewPolicy
{
    /// <summary>How long after the moment an invite is still welcome.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(3);

    /// <summary>The apps the toast names; the page lists the rest.</summary>
    public const int NamedApps = 2;

    public static ReviewTiming Timing(DateTime nowUtc, DateTime momentUtc, DateTime? handledMomentUtc)
    {
        if (handledMomentUtc is { } handled && handled >= momentUtc) return ReviewTiming.Handled;
        if (nowUtc < momentUtc) return ReviewTiming.NotYet;
        return nowUtc - momentUtc > Window ? ReviewTiming.Missed : ReviewTiming.Due;
    }

    /// <summary>The invite for the server's answer, or null when it named no app.</summary>
    public static UnmappedInvite? Invite(UnmappedToday today)
    {
        if (today.Apps.Count == 0) return null;

        var named = today.Apps
            .Take(NamedApps)
            .Select(a => new InviteApp(a.ProductName.Length > 0 ? a.ProductName : a.ProcessName, TimeSpan.FromSeconds(a.ActiveSeconds), a.IsBrowser))
            .ToList();
        return new UnmappedInvite(TimeSpan.FromSeconds(today.UnmappedSeconds), named, today.Apps.Count - named.Count);
    }

    /// <summary>"2 h 10 m on this PC today isn't mapped" / "Discord 50 m, Chrome 30 m (by page) and 2 more".</summary>
    public static (string Title, string Body) Text(UnmappedInvite invite)
    {
        var apps = string.Join(", ", invite.Apps.Select(a => $"{a.Name} {NudgeToast.FormatLength(a.Length)}{(a.IsBrowser ? " (by page)" : "")}"));
        var more = invite.More > 0 ? $" and {invite.More} more" : "";
        return ($"{NudgeToast.FormatLength(invite.Unmapped)} on this PC today isn’t mapped", $"{apps}{more}. Map them?");
    }
}

/// <summary>
/// Which evening was last handled, kept across restarts so a restart at 22:05 does not invite a second time. A missing
/// or unreadable file is "nothing handled", which costs at most one repeated invite. With no path it is kept in memory
/// only — what tests use, so none of them touches the user's real file.
/// </summary>
public sealed class UnmappedReviewStore(string? path = null)
{
    public static string DefaultPath { get; } = Path.Combine(AppConfig.ConfigDirectory, "unmapped-review.json");

    private readonly ILogger _log = Log.ForContext<UnmappedReviewStore>();
    private readonly Lock _lock = new();
    private DateTime? _handledMomentUtc;
    private bool _loaded;

    public DateTime? HandledMomentUtc
    {
        get
        {
            lock (_lock)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _handledMomentUtc = Load();
                }
                return _handledMomentUtc;
            }
        }
    }

    public void MarkHandled(DateTime momentUtc)
    {
        lock (_lock)
        {
            _loaded = true;
            _handledMomentUtc = momentUtc;
            Save(momentUtc);
        }
    }

    private DateTime? Load()
    {
        if (path is null) return null;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))?.HandledMomentUtc : null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not read the evening review's saved state — starting afresh");
            return null;
        }
    }

    /// <summary>Never throws: a failed save costs at most a repeated invite after a restart.</summary>
    private void Save(DateTime momentUtc)
    {
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new Saved(momentUtc)));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not save the evening review's state");
        }
    }

    private sealed record Saved(DateTime HandledMomentUtc);
}
