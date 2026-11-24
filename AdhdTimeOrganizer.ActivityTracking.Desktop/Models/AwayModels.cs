using System.Text.Json.Serialization;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

/// <summary>
/// An answered "Away 47 min — what was it?" (<c>POST /api/tracker/desktop/away</c>, servers from 2026-11-24): the span the user
/// was away and the activity they said it was. Pinned as <c>awayRequest</c> in <c>DesktopTrackerWire.samples.json</c>. Instants
/// are UTC and go out with their <c>Z</c>.
/// </summary>
public sealed record AwayRequest(
    [property: JsonPropertyName("from")] DateTime From,
    [property: JsonPropertyName("to")] DateTime To,
    [property: JsonPropertyName("activityId")] long ActivityId)
{
    public static AwayRequest Of(AwayAnswered answered) => new(
        DateTime.SpecifyKind(answered.FromUtc, DateTimeKind.Utc),
        DateTime.SpecifyKind(answered.ToUtc, DateTimeKind.Utc),
        answered.ActivityId);
}

/// <summary>What the answer added to History: less than the span when the activity already covered part of it.</summary>
public sealed record AwayRecorded
{
    [JsonPropertyName("recordedSeconds")]
    public int RecordedSeconds { get; init; }
}

/// <summary>The caller's activities (<c>GET /api/tracker/desktop/activities</c>): what Settings offers as away answers.</summary>
public sealed record TrackerActivities
{
    [JsonPropertyName("activities")]
    public List<TrackerActivity> Activities { get; init; } = [];
}

/// <param name="Name">User-authored: shown on a toast button and in Settings, never logged.</param>
public sealed record TrackerActivity(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name);
