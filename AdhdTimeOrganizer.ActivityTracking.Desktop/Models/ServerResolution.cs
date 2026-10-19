using System.Text.Json.Serialization;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

/// <summary>
/// The heartbeat's answer: what the minute just sent resolved to on the server, and what the user had planned for it.
/// The tray's "now: Thesis" and the drift nudge both read it — which activity a window maps to is the server's answer,
/// never the agent's (its rule port only decides what is dropped before upload).
/// </summary>
/// <remarks>
/// Servers before 2026-10-18 answer the heartbeat with a bare count; <see cref="Services.ApiClient.ReadHeartbeatResponse"/>
/// reads that as "no resolution".
/// </remarks>
public sealed record HeartbeatResponse
{
    [JsonPropertyName("processedCount")]
    public int ProcessedCount { get; init; }

    /// <summary>The minute's active seconds by what they resolved to, busiest first.</summary>
    [JsonPropertyName("resolution")]
    public List<ResolvedTime> Resolution { get; init; } = [];

    /// <summary>The open planner blocks covering the minute, earliest first.</summary>
    [JsonPropertyName("planned")]
    public List<PlannedBlock> Planned { get; init; } = [];
}

/// <summary>One slice of a minute's foreground time. <see cref="Name"/> is user-authored: never log it.</summary>
public sealed record ResolvedTime
{
    /// <summary><c>"Activity"</c>, <c>"RoleOrCategory"</c>, <c>"Ignored"</c>, or null for unmapped time. A string, so a target added later can't fail the parse.</summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("activityId")]
    public long? ActivityId { get; init; }

    /// <summary>The activity, or role · category; null for ignored and unmapped time.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("activeSeconds")]
    public int ActiveSeconds { get; init; }

    /// <summary>True: fits a planned block. False: mapped somewhere else. Null: the server can't tell (nothing planned, unmapped, ignored).</summary>
    [JsonPropertyName("onPlan")]
    public bool? OnPlan { get; init; }

    [JsonIgnore]
    public bool IsUnmapped => Target is null;

    [JsonIgnore]
    public bool IsIgnored => Target == "Ignored";
}

/// <summary>A planner block. <see cref="ActivityName"/> is user-authored: never log it.</summary>
public sealed record PlannedBlock
{
    [JsonPropertyName("plannerTaskId")]
    public long PlannerTaskId { get; init; }

    [JsonPropertyName("activityId")]
    public long ActivityId { get; init; }

    [JsonPropertyName("activityName")]
    public string ActivityName { get; init; } = "";

    [JsonPropertyName("startsAt")]
    public DateTime StartsAtUtc { get; init; }

    [JsonPropertyName("endsAt")]
    public DateTime EndsAtUtc { get; init; }
}

/// <summary>
/// <c>GET /api/activity-tracking/desktop/agent-coverage</c>: how much of the last week the user's rules map, and the
/// unmapped apps holding the most of the rest — the "map these apps to enable drift nudges" prompt.
/// </summary>
public sealed record AgentCoverage
{
    [JsonPropertyName("days")]
    public int Days { get; init; }

    [JsonPropertyName("trackedSeconds")]
    public int TrackedSeconds { get; init; }

    [JsonPropertyName("mappedSeconds")]
    public int MappedSeconds { get; init; }

    [JsonPropertyName("uncovered")]
    public List<UncoveredApp> Uncovered { get; init; } = [];
}

public sealed record UncoveredApp
{
    [JsonPropertyName("processName")]
    public string ProcessName { get; init; } = "";

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = "";

    [JsonPropertyName("activeSeconds")]
    public int ActiveSeconds { get; init; }
}

/// <summary>
/// <c>GET /api/activity-tracking/desktop/agent-unmapped</c>: today's desktop time no active rule maps, for the evening
/// invite. <see cref="Apps"/> empty is the server saying "don't ask" — its thresholds, not this agent's.
/// </summary>
public sealed record UnmappedToday
{
    [JsonPropertyName("date")]
    public string Date { get; init; } = "";

    [JsonPropertyName("trackedSeconds")]
    public long TrackedSeconds { get; init; }

    [JsonPropertyName("unmappedSeconds")]
    public long UnmappedSeconds { get; init; }

    [JsonPropertyName("apps")]
    public List<UnmappedApp> Apps { get; init; } = [];
}

public sealed record UnmappedApp
{
    [JsonPropertyName("processName")]
    public string ProcessName { get; init; } = "";

    /// <summary>The label to show; the process name when the server had none.</summary>
    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = "";

    [JsonPropertyName("activeSeconds")]
    public long ActiveSeconds { get; init; }

    /// <summary>A browser: one process, many pages, so the review is about title rules, not a process rule.</summary>
    [JsonPropertyName("isBrowser")]
    public bool IsBrowser { get; init; }
}

/// <summary>
/// <c>GET /api/user/evening-shutdown-moment</c>: the instant of the user's next evening, the one the phone's review also
/// uses. <see cref="Enabled"/> is whether the evening-shutdown push is on, which this agent does not depend on.
/// </summary>
public sealed record EveningMoment
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("momentUtc")]
    public DateTime MomentUtc { get; init; }
}
