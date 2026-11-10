using System.Text.Json.Serialization;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

/// <summary>
/// <c>POST /api/tracker/desktop/break</c> (servers from 2026-11-10): the break the user started, as it stands — the server
/// hands it to the phone through <c>/now</c>. The whole break each time, so the latest report mends any lost one. Instants
/// are UTC and go out with their <c>Z</c>.
/// </summary>
public sealed record BreakRequest(
    [property: JsonPropertyName("startedAt")] DateTime StartedAt,
    [property: JsonPropertyName("plannedEndAt")] DateTime PlannedEndAt,
    [property: JsonPropertyName("endedAt")] DateTime? EndedAt)
{
    public static BreakRequest From(BreakReport report) => new(
        DateTime.SpecifyKind(report.StartedUtc, DateTimeKind.Utc),
        DateTime.SpecifyKind(report.PlannedEndUtc, DateTimeKind.Utc),
        report.EndedUtc is { } ended ? DateTime.SpecifyKind(ended, DateTimeKind.Utc) : null);
}
