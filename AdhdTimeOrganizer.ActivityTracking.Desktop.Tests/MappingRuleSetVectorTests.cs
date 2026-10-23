using System.Text.Json;
using System.Text.Json.Serialization;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services.Matching;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// The tracker half of the contract with the server's <c>DesktopMappingRuleSet</c>: every case in
/// <c>DesktopMappingRuleSet.vectors.json</c> resolves to the rule it names, here exactly as on the server.
/// </summary>
/// <remarks>
/// A port that disagrees fails silently either way: it uploads a title the user's rule ignores, or drops a minute
/// the server would have attributed. The file is the server's (<c>AdhdTimeOrganizer.Tracking/domain/helper/mapping/</c>);
/// its rules are deserialized through <see cref="TrackerRule"/>, so the wire shape is pinned too.
/// </remarks>
public class MappingRuleSetVectorTests
{
    private const string VectorsFileName = "DesktopMappingRuleSet.vectors.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<VectorFile> Vectors = new(() =>
        JsonSerializer.Deserialize<VectorFile>(File.ReadAllText(VectorsPath()), Json)!);

    public static TheoryData<string> CaseNames => new(Vectors.Value.Cases.Select(c => c.Name));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Resolve_PicksTheRuleTheVectorNames(string caseName)
    {
        var vector = Vectors.Value.Cases.Single(c => c.Name == caseName);
        var rules = new MappingRuleSet(vector.Rules);

        var resolved = rules.Resolve(vector.Entry.ProcessName, vector.Entry.ProductName, vector.Entry.WindowTitle);

        Assert.Equal(vector.ExpectedRuleId, resolved?.Id);
    }

    [Fact]
    public void Vectors_ExerciseEveryMatchType()
    {
        var used = Vectors.Value.Cases
            .SelectMany(c => c.Rules)
            .SelectMany(r => new[] { r.ProcessNameMatchType, r.ProductNameMatchType, r.WindowTitleMatchType })
            .OfType<PatternMatchType>()
            .ToHashSet();

        Assert.Equal(Enum.GetValues<PatternMatchType>().ToHashSet(), used);
    }

    /// <summary>
    /// The server's copy must be this file. Skipped where the server repo is not checked out next to this one;
    /// where it is, a difference is the drift this contract exists to catch. Line endings are normalized because
    /// git may check either copy out with CRLF.
    /// </summary>
    [Fact]
    public void ServerCopy_IsIdenticalToThisOne()
    {
        var serverRepo = Path.GetFullPath(Path.Combine(RepositoryRoot(), "..", "AdhdTimeOrganizer"));
        if (!Directory.Exists(serverRepo))
            Assert.Skip($"The server repo is not checked out at {serverRepo}.");

        var serverCopy = Path.Combine(serverRepo, "AdhdTimeOrganizer.Tracking", "domain", "helper", "mapping", VectorsFileName);

        Assert.True(File.Exists(serverCopy), $"the server's tests resolve the same vectors from {serverCopy}");
        Assert.True(Normalize(File.ReadAllText(serverCopy)) == Normalize(File.ReadAllText(VectorsPath())),
            $"copy {serverCopy} over {VectorsPath()} and port whatever changed in the server's matcher");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string VectorsPath() =>
        Path.Combine(RepositoryRoot(), "AdhdTimeOrganizer.ActivityTracking.Desktop.Tests", VectorsFileName);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AdhdTimeOrganizer.ActivityTracking.Desktop.sln")))
            directory = directory.Parent;

        Assert.True(directory is not null, $"the vectors are read from the source tree, above {AppContext.BaseDirectory}");
        return directory!.FullName;
    }

    private sealed record VectorFile(List<VectorCase> Cases);

    private sealed record VectorCase(string Name, List<TrackerRule> Rules, VectorEntry Entry, long? ExpectedRuleId);

    private sealed record VectorEntry(string ProcessName, string ProductName, string WindowTitle);
}
