using System.Text.Json;
using System.Text.Json.Serialization;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

/// <summary>One line of today's breakdown. <see cref="Name"/> is user-authored: never log it.</summary>
public sealed record ActivityTotal(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("activeSeconds")] int ActiveSeconds);

/// <summary>
/// Today's recorded foreground time on this PC, for the tray glance ("today 5 h 12 m"), and what it went to (the
/// top five under it). Saved on every change, so a restart halfway through the day doesn't start either from zero.
/// </summary>
/// <remarks>
/// <para>
/// <b>The total</b> is counted as each window is finished and queued (<see cref="Add"/>), so it includes what is still
/// waiting to be sent; less what never got stored — entries a server rule dropped, a window the server refused.
/// <b>The breakdown</b> can only be counted once the server has said what a window mapped to
/// (<see cref="AddResolved"/>), so it lags the total by whatever is unsent, and covers no time sent to a server too old
/// to say. Neither may count a window twice: the caller adds a sent window's resolution only once the outbox has
/// settled it.
/// </para>
/// <para>
/// This machine only, and labelled so. The server's figure would cover every tracker, but computing it on every
/// heartbeat costs the hottest route a ledger sum; an agent can't add up what it never saw anyway.
/// </para>
/// <para>
/// "Today" is the local date of a window's start. Only the latest day is kept: a window from an earlier day (a
/// flush just after midnight) is ignored, one from a later day starts the count again. Never throws — the poll loop
/// calls it, and a failed save must not cost a recorded minute.
/// </para>
/// </remarks>
public sealed class TodayTotal(string path, TimeZoneInfo zone)
{
    public static string DefaultPath { get; } = Path.Combine(AppConfig.ConfigDirectory, "today.json");

    /// <summary>What unmapped time is listed as.</summary>
    internal const string NotMappedName = "Not mapped";

    private readonly ILogger _log = Log.ForContext<TodayTotal>();
    private readonly Lock _gate = new();
    private State? _state;

    /// <summary>Adds a window's active seconds (negative to take them back).</summary>
    public void Add(DateTime windowStartUtc, int activeSeconds)
    {
        if (activeSeconds == 0) return;

        Update(windowStartUtc, state => state with { ActiveSeconds = Math.Max(0, state.ActiveSeconds + activeSeconds) });
    }

    /// <summary>
    /// Adds what the server resolved a stored window to: each slice to its activity (or role · category), unmapped time
    /// to <see cref="NotMappedName"/>. Ignored time is not tracked time and is left out.
    /// </summary>
    public void AddResolved(DateTime windowStartUtc, HeartbeatResponse response)
    {
        var slices = response.Resolution.Where(r => !r.IsIgnored && r.ActiveSeconds > 0).ToList();
        if (slices.Count == 0) return;

        Update(windowStartUtc, state =>
        {
            var activities = state.Activities.ToDictionary(a => a.Key);
            foreach (var slice in slices)
            {
                var (key, name) = KeyOf(slice);
                activities[key] = activities.TryGetValue(key, out var total)
                    ? total with { Name = name, ActiveSeconds = total.ActiveSeconds + slice.ActiveSeconds }
                    : new ActivityTotal(key, name, slice.ActiveSeconds);
            }
            return state with { Activities = [.. activities.Values] };
        });
    }

    /// <summary>The total for the local day of <paramref name="nowUtc"/>; 0 once that day is over.</summary>
    public TimeSpan On(DateTime nowUtc)
    {
        lock (_gate)
        {
            var state = Load();
            return state.Day == DayOf(nowUtc) ? TimeSpan.FromSeconds(state.ActiveSeconds) : TimeSpan.Zero;
        }
    }

    /// <summary>The <paramref name="count"/> busiest lines of the local day of <paramref name="nowUtc"/>, busiest first.</summary>
    public IReadOnlyList<ActivityTotal> Top(DateTime nowUtc, int count)
    {
        lock (_gate)
        {
            var state = Load();
            return state.Day == DayOf(nowUtc)
                ? state.Activities.OrderByDescending(a => a.ActiveSeconds).ThenBy(a => a.Name, StringComparer.Ordinal).Take(count).ToList()
                : [];
        }
    }

    /// <summary>The same activity is one line even if renamed during the day; role/category and unmapped time go by their label.</summary>
    private static (string Key, string Name) KeyOf(ResolvedTime slice) =>
        slice.ActivityId is { } activityId ? ($"activity:{activityId}", slice.Name ?? NotMappedName)
        : slice.IsUnmapped ? ("unmapped", NotMappedName)
        : ($"label:{slice.Name}", slice.Name ?? NotMappedName);

    private void Update(DateTime windowStartUtc, Func<State, State> change)
    {
        var day = DayOf(windowStartUtc);
        lock (_gate)
        {
            var state = Load();
            if (day < state.Day) return;
            if (day > state.Day) state = State.Empty with { Day = day };

            _state = change(state);
            Save(_state);
        }
    }

    private DateOnly DayOf(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(MinuteVerdict.AsUtc(utc), zone));

    private State Load()
    {
        if (_state is not null) return _state;
        try
        {
            if (File.Exists(path))
                return _state = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? State.Empty;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not read today's total — starting it again");
        }
        return _state = State.Empty;
    }

    private void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not save today's total");
        }
    }

    /// <remarks><see cref="Activities"/> is absent from files written before the breakdown existed, hence the default.</remarks>
    private sealed record State(
        [property: JsonPropertyName("day")] DateOnly Day,
        [property: JsonPropertyName("activeSeconds")] int ActiveSeconds,
        [property: JsonPropertyName("activities")] List<ActivityTotal>? ActivitiesOrNull = null)
    {
        public static State Empty { get; } = new(DateOnly.MinValue, 0);

        [JsonIgnore]
        public List<ActivityTotal> Activities
        {
            get => ActivitiesOrNull ?? [];
            init => ActivitiesOrNull = value;
        }
    }
}
