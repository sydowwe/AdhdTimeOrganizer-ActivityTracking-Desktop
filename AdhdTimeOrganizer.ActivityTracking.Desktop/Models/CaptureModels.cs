using System.Text.Json.Serialization;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

/// <summary>
/// <c>POST /api/tracker/capture</c>: one thought for the user's capture list. The route is shared with the phone, whose share
/// sheet also sends a <c>url</c>; the desktop never has one, so the field is not here. User-authored: never log it.
/// </summary>
public sealed record CaptureRequest([property: JsonPropertyName("text")] string Text);

/// <summary>What the server did with a capture. Every outcome (<c>added</c>, <c>alreadyOpen</c>, <c>reopened</c>) means it is on the list.</summary>
public sealed record CaptureResponse
{
    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }

    [JsonPropertyName("todoListItemId")]
    public long TodoListItemId { get; init; }

    [JsonPropertyName("todoListId")]
    public long TodoListId { get; init; }
}
