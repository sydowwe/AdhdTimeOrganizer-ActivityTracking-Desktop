using System.Text.Json;
using System.Text.Json.Nodes;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The agent half of the wire contract: <c>DesktopAgentWire.samples.json</c> is what the server's heartbeat and
/// <c>agent-coverage</c> send — the server's tests check that against its DTOs and live endpoints — and here the same
/// file goes through this repo's models (<c>Models/ServerResolution.cs</c>).
/// </summary>
/// <remarks>
/// A model that disagrees fails silently: a renamed field reads as null, so the tray shows no "now" and the drift
/// nudge never comes, while every window is still sent. The file is the server's
/// (<c>AdhdTimeOrganizer.Tracking/application/dto/response/desktop/</c>); change it there and copy it here.
/// </remarks>
public class WireSampleTests
{
    private const string SamplesFileName = "DesktopAgentWire.samples.json";

    private static readonly Lazy<JsonNode> Samples = new(() => JsonNode.Parse(File.ReadAllText(SamplesPath()))!);

    [Fact]
    public void TheHeartbeatSample_ReadsThroughTheAgentsModels()
    {
        var response = ApiClient.ReadHeartbeatResponse(Samples.Value["heartbeat"]!.ToJsonString());

        Assert.NotNull(response);
        Assert.Equal(4, response.ProcessedCount);
        Assert.Equal(
            new (string?, long?, string?, int, bool?)[]
            {
                ("Activity", 12, "Thesis", 25, true),
                ("RoleOrCategory", null, "Leisure · Video", 15, false),
                (null, null, null, 12, null),
                ("Ignored", null, null, 8, null)
            },
            response.Resolution.Select(r => (r.Target, r.ActivityId, r.Name, r.ActiveSeconds, r.OnPlan)));

        var block = Assert.Single(response.Planned);
        Assert.Equal((7L, 12L, "Thesis"), (block.PlannerTaskId, block.ActivityId, block.ActivityName));
        Assert.Equal(new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc), block.StartsAtUtc);
        Assert.Equal(new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc), block.EndsAtUtc);
        Assert.Equal(DateTimeKind.Utc, block.EndsAtUtc.Kind);

        // And it means what the server meant: 25 s on plan against 15 s off, the unmapped 12 s left out.
        var verdict = MinuteVerdict.From(block.StartsAtUtc, response);
        Assert.True(verdict.OnPlan);
        Assert.Equal("Leisure · Video", verdict.OffPlanName);
    }

    [Fact]
    public void TheCoverageSample_ReadsThroughTheAgentsModels()
    {
        var coverage = Samples.Value["coverage"]!.Deserialize<AgentCoverage>();

        Assert.NotNull(coverage);
        Assert.Equal((7, 43200, 16200), (coverage.Days, coverage.TrackedSeconds, coverage.MappedSeconds));
        Assert.Equal(new[] { ("slack", "Slack", 9000), ("chrome", "Google Chrome", 5400) },
            coverage.Uncovered.Select(a => (a.ProcessName, a.ProductName, a.ActiveSeconds)));
    }

    [Fact]
    public void TheUnmappedSample_ReadsThroughTheAgentsModels()
    {
        var unmapped = Samples.Value["unmapped"]!.Deserialize<UnmappedToday>();

        Assert.NotNull(unmapped);
        Assert.Equal(("2026-10-19", 21600L, 7800L), (unmapped.Date, unmapped.TrackedSeconds, unmapped.UnmappedSeconds));
        Assert.Equal(new[] { ("discord", "Discord", 3000L, false), ("chrome", "Google Chrome", 1800L, true) },
            unmapped.Apps.Select(a => (a.ProcessName, a.ProductName, a.ActiveSeconds, a.IsBrowser)));
    }

    /// <summary>A moment read as <see cref="DateTime.MinValue"/> would put the evening in the year 1: an invite at every start.</summary>
    [Fact]
    public void TheEveningMomentSample_ReadsThroughTheAgentsModels()
    {
        var moment = Samples.Value["eveningMoment"]!.Deserialize<EveningMoment>();

        Assert.NotNull(moment);
        Assert.True(moment.Enabled);
        Assert.Equal(new DateTime(2026, 10, 19, 20, 30, 0, DateTimeKind.Utc), moment.MomentUtc);
        Assert.Equal(DateTimeKind.Utc, moment.MomentUtc.Kind);
    }

    /// <summary>A field the server sends and the agent's model has no member for is a field the agent can never read.</summary>
    [Theory]
    [InlineData("heartbeat", typeof(HeartbeatResponse))]
    [InlineData("coverage", typeof(AgentCoverage))]
    [InlineData("unmapped", typeof(UnmappedToday))]
    [InlineData("eveningMoment", typeof(EveningMoment))]
    public void TheAgentsModels_HaveEveryFieldTheServerSends(string sample, Type model)
    {
        var node = Samples.Value[sample]!;
        var read = JsonSerializer.SerializeToNode(node.Deserialize(model), model)!;

        Assert.Equal(FieldsOf(node).Order(), FieldsOf(read).Order());
    }

    /// <summary>
    /// The server's copy must be this file. Skipped where the server repo is not checked out next to this one. Line
    /// endings are normalized because git may check either copy out with CRLF.
    /// </summary>
    [Fact]
    public void ServerCopy_IsIdenticalToThisOne()
    {
        var serverRepo = Path.GetFullPath(Path.Combine(RepositoryRoot(), "..", "AdhdTimeOrganizer"));
        if (!Directory.Exists(serverRepo))
            Assert.Skip($"The server repo is not checked out at {serverRepo}.");

        var serverCopy = Path.Combine(serverRepo, "AdhdTimeOrganizer.Tracking", "application", "dto", "response", "desktop", SamplesFileName);

        Assert.True(File.Exists(serverCopy), $"the server's tests check the same samples at {serverCopy}");
        Assert.True(Normalize(File.ReadAllText(serverCopy)) == Normalize(File.ReadAllText(SamplesPath())),
            $"copy {serverCopy} over {SamplesPath()} and update Models/ServerResolution.cs for whatever changed");
    }

    /// <summary>Every field path in a document, arrays collapsed: <c>resolution[].onPlan</c>.</summary>
    private static HashSet<string> FieldsOf(JsonNode node, string path = "")
    {
        var fields = new HashSet<string>();
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                {
                    var child = path.Length == 0 ? name : $"{path}.{name}";
                    fields.Add(child);
                    if (value is not null) fields.UnionWith(FieldsOf(value, child));
                }
                break;
            case JsonArray array:
                foreach (var item in array.OfType<JsonNode>())
                    fields.UnionWith(FieldsOf(item, path + "[]"));
                break;
        }
        return fields;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string SamplesPath() =>
        Path.Combine(RepositoryRoot(), "AdhdTimeOrganizer.ActivityTracking.Desktop.Tests", SamplesFileName);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AdhdTimeOrganizer.ActivityTracking.Desktop.sln")))
            directory = directory.Parent;

        Assert.True(directory is not null, $"the samples are read from the source tree, above {AppContext.BaseDirectory}");
        return directory!.FullName;
    }
}
