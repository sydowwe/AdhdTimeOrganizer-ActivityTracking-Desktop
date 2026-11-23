using System.Text;
using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public sealed class JsonFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "json-file-tests-" + Guid.NewGuid().ToString("N"));

    private string PathOf(string name) => Path.Combine(_directory, name);

    private sealed record Sample(string Name, int Count);

    [Fact]
    public void Write_ThenTryRead_RoundTrips_AndLeavesNoTempFile()
    {
        var path = PathOf("sample.json");

        JsonFile.Write(path, new Sample("a", 2));
        var read = JsonFile.TryRead<Sample>(path);

        Assert.Equal(JsonFileStatus.Read, read.Status);
        Assert.Equal(new Sample("a", 2), read.Value);
        Assert.False(File.Exists(path + ".tmp"));
    }

    /// <summary>Moving the stores onto the helper must not change a byte of what they write: older builds read these files.</summary>
    [Fact]
    public void Write_WritesExactlyWhatSerializeWrites_WithoutABom()
    {
        var path = PathOf("sample.json");
        var options = new JsonSerializerOptions { WriteIndented = true };

        JsonFile.Write(path, new Sample("é — ň", 2), options);

        Assert.Equal(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Sample("é — ň", 2), options)), File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_ReplacesAnExistingFile_AndCreatesTheFolder()
    {
        var path = Path.Combine(_directory, "nested", "sample.json");

        JsonFile.Write(path, new Sample("old", 1));
        JsonFile.Write(path, new Sample("new", 2));

        Assert.Equal(new Sample("new", 2), JsonFile.TryRead<Sample>(path).Value);
    }

    [Fact]
    public void TryRead_TellsAMissingFileOrFolder()
    {
        Assert.Equal(JsonFileStatus.Missing, JsonFile.TryRead<Sample>(PathOf("none.json")).Status);
        Assert.Equal(JsonFileStatus.Missing, JsonFile.TryRead<Sample>(Path.Combine(_directory, "no-folder", "none.json")).Status);
    }

    /// <summary>A zero-length file is what a power cut during an unflushed save leaves; a JSON null is no value either.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{ \"Name\": \"a\", ")]
    [InlineData("not json")]
    [InlineData("null")]
    public void TryRead_CallsADamagedFileUnreadable(string content)
    {
        var path = PathOf("damaged.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, content);

        var read = JsonFile.TryRead<Sample>(path);

        Assert.Equal(JsonFileStatus.Unreadable, read.Status);
        Assert.Null(read.Value);
    }

    /// <summary>Locked for a moment (an antivirus scan, a backup) is not damaged: the caller must leave it for the next try.</summary>
    [Fact]
    public void TryRead_CallsALockedFileCouldNotOpen()
    {
        var path = PathOf("locked.json");
        JsonFile.Write(path, new Sample("a", 1));

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var read = JsonFile.TryRead<Sample>(path);

            Assert.Equal(JsonFileStatus.CouldNotOpen, read.Status);
            Assert.NotNull(read.Error);
        }

        Assert.Equal(JsonFileStatus.Read, JsonFile.TryRead<Sample>(path).Status);
    }

    /// <summary>Notepad saves with a BOM; File.ReadAllText, which the stores used before, skipped it.</summary>
    [Fact]
    public void TryRead_SkipsAUtf8Bom()
    {
        var path = PathOf("bom.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """{"Name":"a","Count":1}""", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(new Sample("a", 1), JsonFile.TryRead<Sample>(path).Value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
