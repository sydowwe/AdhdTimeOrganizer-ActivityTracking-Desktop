using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

public class WindowTitleNormalizerTests
{
    [Theory]
    // Unread counters and unsaved markers
    [InlineData("chrome", "Google Chrome", "(3) Inbox - Gmail - Google Chrome", "Inbox - Gmail")]
    [InlineData("chrome", "Google Chrome", "(99+) Feed - Google Chrome", "Feed")]
    [InlineData("Code", "Visual Studio Code", "● app.ts - repo - Visual Studio Code", "app.ts - repo")]
    [InlineData("notepad++", "Notepad++", "*new 1 - Notepad++", "new 1")]
    // Product suffix, matched against the executable's product name
    [InlineData("firefox", "Firefox", "Docs — Mozilla Firefox", "Docs")]
    [InlineData("msedge", "Microsoft Edge", "Docs - Profile 1 - Microsoft​ Edge", "Docs - Profile 1")]
    [InlineData("slack", "Slack", "general - Team - Slack", "general - Team")]
    // JetBrains: drop the file, keep the project
    [InlineData("rider64", "JetBrains Rider", "AdhdTimeOrganizer – ActivityTrackerService.cs", "AdhdTimeOrganizer")]
    [InlineData("pycharm64", "PyCharm", "scraper – main.py", "scraper")]
    // The last separator wins, whatever its kind
    [InlineData("chrome", "Google Chrome", "Foo – Bar - Google Chrome", "Foo – Bar")]
    // Left alone
    [InlineData("notepad", "Microsoft® Windows® Operating System", "notes - Notepad", "notes - Notepad")]
    [InlineData("explorer", null, "Program Manager", "Program Manager")]
    [InlineData("game", "Game", "Game", "Game")]
    public void Normalize(string process, string? product, string raw, string expected) =>
        Assert.Equal(expected, WindowTitleNormalizer.Normalize(process, product, raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_BlankTitleStaysEmpty(string? raw) =>
        Assert.Equal("", WindowTitleNormalizer.Normalize("explorer", null, raw));

    [Fact]
    public void Normalize_DoesNotStripATitleDownToNothing() =>
        Assert.Equal("(3)", WindowTitleNormalizer.Normalize("app", null, "(3)"));
}
