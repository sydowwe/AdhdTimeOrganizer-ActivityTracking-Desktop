using System.Text.Json.Serialization;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services.Matching;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

/// <summary>
/// One of the user's active server rules, as <c>GET /api/tracker/desktop/rules</c> sends it: the
/// pattern half and whether it ignores. The target activity is not sent — the tracker only decides what leaves the
/// machine. Also the rule shape of <c>DesktopMappingRuleSet.vectors.json</c>.
/// </summary>
public sealed record TrackerRule
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("processName")]
    public string? ProcessName { get; init; }

    [JsonPropertyName("processNameMatchType")]
    [JsonConverter(typeof(JsonStringEnumConverter<PatternMatchType>))]
    public PatternMatchType? ProcessNameMatchType { get; init; }

    [JsonPropertyName("productName")]
    public string? ProductName { get; init; }

    [JsonPropertyName("productNameMatchType")]
    [JsonConverter(typeof(JsonStringEnumConverter<PatternMatchType>))]
    public PatternMatchType? ProductNameMatchType { get; init; }

    [JsonPropertyName("windowTitle")]
    public string? WindowTitle { get; init; }

    [JsonPropertyName("windowTitleMatchType")]
    [JsonConverter(typeof(JsonStringEnumConverter<PatternMatchType>))]
    public PatternMatchType? WindowTitleMatchType { get; init; }

    [JsonPropertyName("isIgnored")]
    public bool IsIgnored { get; init; }
}
