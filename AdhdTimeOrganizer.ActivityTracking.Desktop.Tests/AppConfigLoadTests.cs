using System.Text.Json;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Models;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services.Matching;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// A damaged or newer config must not silently reset the user's privacy rules: the tray saves straight after start, so a
/// quiet fall-back to defaults overwrote them, and titles the user had hidden were uploaded from the next poll.
/// </summary>
public sealed class AppConfigLoadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "config-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public AppConfigLoadTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "config.json");
    }

    private string Bak => _path + ".bak";
    private string Bad => _path + ".bad";

    /// <summary>A config the user has made their own: a hide-title rule, a pause, a chime choice, a session.</summary>
    private AppConfig Customized() => new()
    {
        FilePath = _path,
        RefreshToken = "refresh-SECRET",
        PausedUntilUtc = DateTime.MaxValue,
        TimeChimeChoices = [new ChimeChoice("activity:12", "Thesis", true)],
        LocalRules =
        [
            new LocalRule { Action = LocalRuleAction.HideTitle, ProcessName = "firefox", WindowTitle = "bank", WindowTitleMatchType = PatternMatchType.Contains },
            new LocalRule { ProcessName = "LockApp" }
        ]
    };

    [Fact]
    public void MissingFile_IsAFirstStart_AndNothingIsWritten()
    {
        var load = AppConfig.Load(_path);

        Assert.Equal(ConfigOrigin.Created, load.Origin);
        Assert.Equal(_path, load.Config.FilePath);
        Assert.False(load.Config.HideAllTitlesUntilReviewed);
        Assert.Null(load.Notice());
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void AFileThatLoads_IsCopiedToTheBackup_ByteForByte()
    {
        Customized().Save();

        var load = AppConfig.Load(_path);

        Assert.Equal(ConfigOrigin.Loaded, load.Origin);
        Assert.Equal(0, load.ResetValues);
        Assert.Null(load.Notice());
        Assert.Equal(File.ReadAllBytes(_path), File.ReadAllBytes(Bak));
    }

    /// <summary>What a power cut during an unflushed save left: the backup's rules come back, its old token does not.</summary>
    [Fact]
    public void ZeroLengthFile_GoesToBad_AndTheBackupIsUsed_WithoutItsRefreshToken()
    {
        Customized().Save();
        AppConfig.Load(_path); // writes the backup
        File.WriteAllBytes(_path, []);

        var load = AppConfig.Load(_path);

        Assert.Equal(ConfigOrigin.FromBackup, load.Origin);
        Assert.Equal(["firefox", "LockApp"], load.Config.LocalRules.Select(r => r.ProcessName));
        Assert.Equal(DateTime.MaxValue, load.Config.PausedUntilUtc);
        Assert.Equal("activity:12", Assert.Single(load.Config.TimeChimeChoices).Key);
        // Rotated since the backup was written: replaying it would make the server revoke every session of the account.
        Assert.Null(load.Config.RefreshToken);
        Assert.False(load.Config.HideAllTitlesUntilReviewed);

        Assert.Equal(0, new FileInfo(Bad).Length);
        // Saved at once: the next start reads the recovered rules, not a missing file and a clean first start.
        Assert.Equal(ConfigOrigin.Loaded, AppConfig.Load(_path).Origin);
        Assert.Equal(2, AppConfig.Load(_path).Config.LocalRules.Count);
    }

    [Fact]
    public void BadJson_WithoutABackup_FallsBackToDefaults_WithEveryTitleHidden_AndSaysSo()
    {
        File.WriteAllText(_path, """{ "IgnoreRules": [ { "processName": "firefox", """);

        var load = AppConfig.Load(_path);

        Assert.Equal(ConfigOrigin.Defaults, load.Origin);
        Assert.True(load.Config.HideAllTitlesUntilReviewed);
        Assert.Equal(new AppConfig().LocalRules.Count, load.Config.LocalRules.Count);
        Assert.True(File.Exists(Bad));
        var notice = load.Notice();
        Assert.NotNull(notice);
        Assert.Contains("hidden", notice.Value.Text);

        // Survives a restart until the Privacy form is saved, and keeps saying so.
        var next = AppConfig.Load(_path);
        Assert.Equal(ConfigOrigin.Loaded, next.Origin);
        Assert.True(next.Config.HideAllTitlesUntilReviewed);
        Assert.NotNull(next.Notice());
    }

    [Fact]
    public void ADamagedBackup_IsNoBackup()
    {
        File.WriteAllText(_path, "not json");
        File.WriteAllText(Bak, "");

        Assert.Equal(ConfigOrigin.Defaults, AppConfig.Load(_path).Origin);
    }

    [Fact]
    public void OnlyOneBadFileIsKept_TheLatest()
    {
        File.WriteAllText(_path, "first");
        AppConfig.Load(_path);
        File.WriteAllText(_path, "second");
        AppConfig.Load(_path);

        Assert.Equal("second", File.ReadAllText(Bad));
        Assert.Single(Directory.GetFiles(_directory, "*.bad"));
    }

    /// <summary>Locked at logon by a scanner, say: not damaged. Nothing is moved or saved over it.</summary>
    [Fact]
    public void AFileThatCannotBeOpened_IsLeftInPlace_AndTheBackupIsUsedForNow()
    {
        Customized().Save();
        AppConfig.Load(_path);
        var before = File.ReadAllBytes(_path);

        ConfigLoad load;
        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
            load = AppConfig.Load(_path);

        Assert.Equal(ConfigOrigin.FromBackup, load.Origin);
        Assert.True(load.CouldNotOpen);
        Assert.Contains("couldn't be opened", load.Notice()!.Value.Text);
        Assert.False(File.Exists(Bad));
        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    /// <summary>A newer build's value must cost that one setting, not the file: that would take the rules with it.</summary>
    [Fact]
    public void UnknownEnumValues_GoBackToTheirDefaults_AndTheRestLoads()
    {
        File.WriteAllText(_path, """
            {
              "StartRampBlocks": "Someday",
              "PrivateWindowHandling": "Blur",
              "DriftNudgesEnabled": true,
              "IgnoreRules": [
                { "action": "Redact", "processName": "slack", "processNameMatchType": "Exact", "windowTitle": null, "windowTitleMatchType": "Exact" },
                { "action": "HideTitle", "processName": "firefox", "processNameMatchType": "exact", "windowTitle": "bank", "windowTitleMatchType": "contains" }
              ]
            }
            """);

        var load = AppConfig.Load(_path);

        Assert.Equal(ConfigOrigin.Loaded, load.Origin);
        Assert.Equal(3, load.ResetValues);
        Assert.Equal(RampBlocks.NotOptional, load.Config.StartRampBlocks);
        Assert.Equal(PrivateWindowHandling.KeepTimeAsAppName, load.Config.PrivateWindowHandling);
        Assert.True(load.Config.DriftNudgesEnabled);
        // Ignore records nothing: the most private reading of an action this build does not know.
        Assert.Equal(LocalRuleAction.Ignore, load.Config.LocalRules[0].Action);
        // Read in any case, like JsonStringEnumConverter.
        Assert.Equal(PatternMatchType.Contains, load.Config.LocalRules[1].WindowTitleMatchType);
        Assert.False(load.Config.HideAllTitlesUntilReviewed);
        Assert.Contains("3 settings", load.Notice()!.Value.Text);
        // The file as it was is the backup, unknown values and all.
        Assert.Contains("Someday", File.ReadAllText(Bak));
    }

    /// <summary>A pattern this build cannot evaluate would match nothing as Exact, and the titles it hid would be uploaded.</summary>
    [Fact]
    public void AnUnknownMatchType_DropsThatCondition_SoTheRuleHidesMoreNeverLess()
    {
        File.WriteAllText(_path, """
            { "IgnoreRules": [
                { "action": "HideTitle", "processName": "firefox", "processNameMatchType": "Exact", "windowTitle": "bank", "windowTitleMatchType": "Fuzzy" },
                { "action": "HideTitle", "processName": "chrome", "processNameMatchType": "Glob", "windowTitle": "bank", "windowTitleMatchType": "Contains" }
            ] }
            """);

        var load = AppConfig.Load(_path);

        var (firefox, chrome) = (load.Config.LocalRules[0], load.Config.LocalRules[1]);
        Assert.Equal(("firefox", (string?)null, PatternMatchType.Exact), (firefox.ProcessName, firefox.WindowTitle, firefox.WindowTitleMatchType));
        Assert.Equal(((string?)null, "bank", PatternMatchType.Exact), (chrome.ProcessName, chrome.WindowTitle, chrome.ProcessNameMatchType));
        Assert.False(load.Config.HideAllTitlesUntilReviewed);
    }

    [Fact]
    public void AnUnknownMatchType_LeavingARuleNamingNothing_HidesEveryTitle()
    {
        File.WriteAllText(_path, """
            { "IgnoreRules": [
                { "action": "HideTitle", "processName": null, "processNameMatchType": "Exact", "windowTitle": "diagnosis", "windowTitleMatchType": "Fuzzy" }
            ] }
            """);

        var load = AppConfig.Load(_path);

        Assert.True(load.Config.HideAllTitlesUntilReviewed);
        var notice = load.Notice()!.Value;
        Assert.Contains("hidden", notice.Text);
        Assert.DoesNotContain("diagnosis", notice.Text); // counts only
    }

    [Fact]
    public void NullLists_BecomeTheirDefaults_AndNullItemsAreDropped()
    {
        File.WriteAllText(_path, """{ "IgnoreRules": null, "TimeChimeChoices": null, "ApiBaseUrl": null }""");

        var load = AppConfig.Load(_path);

        Assert.Equal(new AppConfig().LocalRules.Select(r => (r.ProcessName, r.WindowTitle)), load.Config.LocalRules.Select(r => (r.ProcessName, r.WindowTitle)));
        Assert.Empty(load.Config.TimeChimeChoices);
        Assert.Equal(new AppConfig().ApiBaseUrl, load.Config.ApiBaseUrl);
        Assert.Equal(3, load.ResetValues);

        File.WriteAllText(_path, """{ "IgnoreRules": [ null, { "processName": "LockApp" } ], "TimeChimeChoices": [ null ] }""");

        load = AppConfig.Load(_path);

        Assert.Equal("LockApp", Assert.Single(load.Config.LocalRules).ProcessName);
        Assert.Empty(load.Config.TimeChimeChoices);
    }

    /// <summary>One range per number (ConfigLimits), the one Settings offers; they used to disagree.</summary>
    [Fact]
    public void Numbers_AreClampedToTheirRanges_Once_OnLoad()
    {
        File.WriteAllText(_path, """
            { "PollIntervalMs": 100, "IdleThresholdSeconds": 99999, "BreakMinutes": 0, "BreakTimerMinutes": 500,
              "DriftNudgeMinutes": 2, "TimeChimeMinutes": 7, "MaxBackgroundWindowsPerMonitor": -3 }
            """);

        var config = AppConfig.Load(_path).Config;

        Assert.Equal((500, 600, 1, 120, 10, 15, 0),
            (config.PollIntervalMs, config.IdleThresholdSeconds, config.BreakMinutes, config.BreakTimerMinutes,
             config.DriftNudgeMinutes, config.TimeChimeMinutes, config.MaxBackgroundWindowsPerMonitor));
    }

    [Fact]
    public void ALegacyPlainTextToken_MovesToDpapi_AndIsInNeitherFile()
    {
        File.WriteAllText(_path, """{ "RefreshToken": "plain-SECRET", "IgnoreRules": [] }""");

        var load = AppConfig.Load(_path);

        Assert.Equal("plain-SECRET", load.Config.RefreshToken);
        Assert.NotNull(load.Config.ProtectedRefreshToken);
        Assert.Null(load.Config.LegacyRefreshToken);
        foreach (var file in new[] { _path, Bak })
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("plain-SECRET", text);
            Assert.DoesNotContain("\"RefreshToken\"", text);
        }
        Assert.Equal("plain-SECRET", AppConfig.Load(_path).Config.RefreshToken);
    }

    /// <summary>
    /// The shape 1.2.1 writes (keys and kinds from a real one; the values here are made up). It must read unchanged, and
    /// what this build writes back must hold no key 1.2.1 has not met except the two the build before this one added.
    /// </summary>
    [Fact]
    public void AConfigWrittenBy121_LoadsUnchanged_AndIsWrittenBackWithoutNewKeys()
    {
        var protectedToken = new AppConfig { FilePath = _path, RefreshToken = "token-121" }.ProtectedRefreshToken;
        var json = $$"""
            {
              "ApiBaseUrl": "https://adhdtimeorganizer.com",
              "WebAppUrl": "https://adhdtimeorganizer.com",
              "PollIntervalMs": 5000,
              "IdleThresholdSeconds": 30,
              "MinBackgroundWindowPercent": 15,
              "MaxBackgroundWindowsPerMonitor": 2,
              "MaxPassiveIdleMinutes": 150,
              "HyperfocusNudgesEnabled": true,
              "HyperfocusNudgeMinutes": 90,
              "AppHyperfocusNudgeMinutes": 120,
              "BreakMinutes": 5,
              "BreakTimerMinutes": 10,
              "QuietWhenFullscreen": true,
              "QuietWhenMicrophoneInUse": false,
              "DriftNudgesEnabled": true,
              "DriftNudgeMinutes": 15,
              "StartRampEnabled": true,
              "StartRampBlocks": "Critical",
              "TimeChimeEnabled": false,
              "TimeChimeMinutes": 20,
              "TimeChimeDistractions": true,
              "TimeChimeChoices": [],
              "CaptureHotkey": "Win+Alt+N",
              "UploadNoticesEnabled": true,
              "UnmappedReviewEnabled": true,
              "HidePrivateWindows": true,
              "PrivateWindowHandling": "Drop",
              "PausedUntilUtc": "9999-12-31T23:59:59.9999999",
              "ProtectedRefreshToken": "{{protectedToken}}",
              "IgnoreRules": [
                { "action": "Ignore", "processName": "explorer", "processNameMatchType": "Exact", "windowTitle": "Program Manager", "windowTitleMatchType": "Exact" },
                { "action": "Ignore", "processName": "explorer", "processNameMatchType": "Exact", "windowTitle": "", "windowTitleMatchType": "Exact" },
                { "action": "HideTitle", "processName": "firefox", "processNameMatchType": "Exact", "windowTitle": "bank", "windowTitleMatchType": "Contains" }
              ]
            }
            """;
        File.WriteAllText(_path, json);
        var keys121 = JsonDocument.Parse(json).RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        var load = AppConfig.Load(_path);
        var config = load.Config;

        Assert.Equal((ConfigOrigin.Loaded, 0), (load.Origin, load.ResetValues));
        Assert.Null(load.Notice());
        Assert.Equal("token-121", config.RefreshToken);
        Assert.Equal((5000, 30, 15, 2, 150), (config.PollIntervalMs, config.IdleThresholdSeconds, config.MinBackgroundWindowPercent,
            config.MaxBackgroundWindowsPerMonitor, config.MaxPassiveIdleMinutes));
        Assert.Equal((90, 120, 5, 10, 15, 20), (config.HyperfocusNudgeMinutes, config.AppHyperfocusNudgeMinutes, config.BreakMinutes,
            config.BreakTimerMinutes, config.DriftNudgeMinutes, config.TimeChimeMinutes));
        Assert.False(config.QuietWhenMicrophoneInUse);
        Assert.Equal(RampBlocks.Critical, config.StartRampBlocks);
        Assert.Equal(PrivateWindowHandling.Drop, config.PrivateWindowHandling);
        Assert.Equal(DateTime.MaxValue, config.PausedUntilUtc);
        Assert.Equal(
            [(LocalRuleAction.Ignore, "explorer", "Program Manager", PatternMatchType.Exact),
             (LocalRuleAction.Ignore, "explorer", "", PatternMatchType.Exact),
             (LocalRuleAction.HideTitle, "firefox", "bank", PatternMatchType.Contains)],
            config.LocalRules.Select(r => (r.Action, r.ProcessName!, r.WindowTitle!, r.WindowTitleMatchType)));
        Assert.False(config.HideAllTitlesUntilReviewed);
        Assert.Equal(json, File.ReadAllText(_path)); // loading alone writes nothing over it

        config.Save();
        var written = JsonDocument.Parse(File.ReadAllText(_path)).RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(["PauseRemindersEnabled", "PausedSinceUtc"], written.Except(keys121).Order(StringComparer.Ordinal));
        Assert.Empty(keys121.Except(written));
    }

    [Fact]
    public void EveryNotice_FitsABalloon()
    {
        Customized().Save();
        AppConfig.Load(_path);
        File.WriteAllText(_path, "");
        var notices = new[]
        {
            AppConfig.Load(_path).Notice(),
            new ConfigLoad(new AppConfig { HideAllTitlesUntilReviewed = true }, ConfigOrigin.Defaults, CouldNotOpen: true).Notice(),
            new ConfigLoad(new AppConfig { HideAllTitlesUntilReviewed = true }, ConfigOrigin.Loaded, ResetValues: 12).Notice()
        };

        Assert.All(notices, notice => Assert.InRange(notice!.Value.Text.Length, 1, 255));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
