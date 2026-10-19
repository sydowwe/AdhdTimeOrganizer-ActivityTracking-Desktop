using AdhdTimeOrganizer.ActivityTracking.Desktop;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Helpers;
using Xunit;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Tests;

/// <summary>
/// "Start with Windows" against a dictionary standing in for the two registry keys — the real ones are the user's
/// startup list, which no test may touch. The values mirror what a real machine holds: the other desktop app's entry
/// under <c>AdhdTimeOrganizer</c>, the stale pre-rename entry the user turned off in Task Manager.
/// </summary>
public class AutoStartHelperTests
{
    private const string Exe = @"C:\apps\tracker\AdhdTimeOrganizer.ActivityTracking.Desktop.exe";
    private const string OtherApp = "AdhdTimeOrganizer";
    private const string OtherAppCommand = @"""C:\Users\someone\Desktop\AdhdTimeOrganizer.exe"" --minimized";

    private static readonly byte[] Enabled = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] Disabled = [3, 0, 0, 0, 0x5F, 0x6F, 0x46, 0x28, 0xC7, 0xE3, 0xDC, 0x01];

    private readonly FakeStore _store = new();
    private readonly HashSet<string> _existing = [Exe, @"C:\Users\someone\Desktop\AdhdTimeOrganizer.exe"];

    private AutoStartHelper Helper(string? exe = Exe) => new(_store, exe, _existing.Contains);

    [Fact]
    public void WithoutAnEntry_ItIsOff() =>
        Assert.Equal(AutoStartState.Off, Helper().GetState());

    /// <summary>The other app's entry under the old shared name is not this tracker's, and says nothing about it.</summary>
    [Fact]
    public void AnotherAppsEntry_IsNotThisTrackersAutostart()
    {
        _store.Run[OtherApp] = OtherAppCommand;
        _store.Approved[OtherApp] = Enabled;

        Assert.Equal(AutoStartState.Off, Helper().GetState());
    }

    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, AutoStartState.On)]
    [InlineData(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, AutoStartState.On)]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, AutoStartState.On)]
    [InlineData(new byte[] { 3, 0, 0, 0, 0x5F, 0x6F, 0x46, 0x28, 0xC7, 0xE3, 0xDC, 0x01 }, AutoStartState.DisabledInWindows)]
    [InlineData(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, AutoStartState.DisabledInWindows)]
    public void WindowsOwnVerdict_DecidesWhetherTheEntryCounts(byte[] approved, AutoStartState expected)
    {
        _store.Run[AutoStartHelper.AppName] = $"\"{Exe}\" --minimized";
        _store.Approved[AutoStartHelper.AppName] = approved;

        Assert.Equal(expected, Helper().GetState());
    }

    [Fact]
    public void AnEntryWithNoVerdict_IsOn()
    {
        _store.Run[AutoStartHelper.AppName] = $"\"{Exe}\" --minimized";

        Assert.Equal(AutoStartState.On, Helper().GetState());
    }

    // ---- turning it on and off ------------------------------------------

    [Fact]
    public void Enable_WritesOneEntryOfItsOwn_AndLeavesAnotherAppsAlone()
    {
        _store.Run[OtherApp] = OtherAppCommand;

        Helper().Enable();

        Assert.Equal($"\"{Exe}\" --minimized", _store.Run[AutoStartHelper.AppName]);
        Assert.Equal(OtherAppCommand, _store.Run[OtherApp]);
        Assert.Empty(_store.Approved); // no verdict invented: absent means enabled
        Assert.Equal(AutoStartState.On, Helper().GetState());
    }

    /// <summary>Ticking the box is asking for it, so it undoes a Task Manager switch-off — otherwise it would stay ticked and do nothing.</summary>
    [Fact]
    public void Enable_UndoesASwitchOffInWindowsSettings()
    {
        _store.Run[AutoStartHelper.AppName] = @"""C:\gone\tracker.exe"" --minimized";
        _store.Approved[AutoStartHelper.AppName] = Disabled;

        Helper().Enable();

        Assert.Equal(AutoStartState.On, Helper().GetState());
        Assert.Equal(Enabled, _store.Approved[AutoStartHelper.AppName]);
    }

    [Fact]
    public void Disable_RemovesOnlyThisTrackersEntry()
    {
        _store.Run[OtherApp] = OtherAppCommand;
        Helper().Enable();

        Helper().Disable();

        Assert.Equal(AutoStartState.Off, Helper().GetState());
        Assert.Equal(OtherAppCommand, _store.Run[OtherApp]);
    }

    // ---- reconcile at start ----------------------------------------------

    /// <summary>The state this machine is really in: a stale entry under the pre-rename name, switched off in Task Manager.</summary>
    [Fact]
    public void Reconcile_RemovesTheStalePreRenameEntry_WithoutTurningAnythingOn()
    {
        _store.Run[AutoStartHelper.LegacyName] = @"""C:\old\bin\Debug\net10.0-windows\tracker.exe"" --minimized";
        _store.Approved[AutoStartHelper.LegacyName] = Disabled;
        _store.Run[OtherApp] = OtherAppCommand;

        Helper().Reconcile();

        Assert.DoesNotContain(AutoStartHelper.LegacyName, _store.Run.Keys);
        Assert.DoesNotContain(AutoStartHelper.LegacyName, _store.Approved.Keys);
        Assert.Equal(AutoStartState.Off, Helper().GetState());
        Assert.Equal(OtherAppCommand, _store.Run[OtherApp]);
    }

    [Fact]
    public void Reconcile_CarriesAWorkingPreRenameEntryOver_UnderTheNewName()
    {
        _store.Run[AutoStartHelper.LegacyName] = @"""C:\old\tracker.exe"" --minimized";

        Helper().Reconcile();

        Assert.DoesNotContain(AutoStartHelper.LegacyName, _store.Run.Keys);
        Assert.Equal($"\"{Exe}\" --minimized", _store.Run[AutoStartHelper.AppName]);
    }

    /// <summary>The output folder moved when the build became RID-specific: the entry pointed at nothing.</summary>
    [Fact]
    public void Reconcile_RepointsAnEntryWhoseExeHasMoved_KeepingWindowsVerdict()
    {
        _store.Run[AutoStartHelper.AppName] = @"""C:\old\bin\Debug\net10.0-windows\tracker.exe"" --minimized";
        _store.Approved[AutoStartHelper.AppName] = Disabled;

        Helper().Reconcile();

        Assert.Equal($"\"{Exe}\" --minimized", _store.Run[AutoStartHelper.AppName]);
        Assert.Equal(AutoStartState.DisabledInWindows, Helper().GetState()); // the user's switch-off is theirs to undo
    }

    /// <summary>A Debug run from the IDE must not redirect an autostart that works.</summary>
    [Fact]
    public void Reconcile_LeavesAnEntryAloneWhoseExeStillExists()
    {
        const string release = @"C:\installed\tracker.exe";
        _existing.Add(release);
        _store.Run[AutoStartHelper.AppName] = $"\"{release}\" --minimized";

        Helper().Reconcile();

        Assert.Equal($"\"{release}\" --minimized", _store.Run[AutoStartHelper.AppName]);
    }

    /// <summary>Installing is choosing that copy: the entry a build from source left moves to it, Windows' verdict kept.</summary>
    [Fact]
    public void Reconcile_TheInstalledBuild_TakesTheEntryOverFromABuildFromSource()
    {
        const string fromSource = @"C:\src\tracker\bin\Release\tracker.exe";
        const string installed = @"C:\Users\someone\AppData\Local\AdhdTimeOrganizer.Tracker\current\AdhdTimeOrganizer.exe";
        _existing.Add(fromSource);
        _existing.Add(installed);
        _store.Run[AutoStartHelper.AppName] = $"\"{fromSource}\" --minimized";
        _store.Approved[AutoStartHelper.AppName] = Disabled;

        new AutoStartHelper(_store, installed, _existing.Contains, installed: true).Reconcile();

        Assert.Equal($"\"{installed}\" --minimized", _store.Run[AutoStartHelper.AppName]);
        Assert.Equal(Disabled, _store.Approved[AutoStartHelper.AppName]);
    }

    /// <summary>Its own entry, in another case, is not a takeover — nothing is written.</summary>
    [Fact]
    public void Reconcile_TheInstalledBuild_LeavesItsOwnEntryAlone()
    {
        const string installed = @"C:\Users\someone\AppData\Local\AdhdTimeOrganizer.Tracker\current\AdhdTimeOrganizer.exe";
        _existing.Add(installed);
        _existing.Add(installed.ToUpperInvariant()); // the file system is case-insensitive; the stand-in is not
        var command = $"\"{installed.ToUpperInvariant()}\" --minimized";
        _store.Run[AutoStartHelper.AppName] = command;

        new AutoStartHelper(_store, installed, _existing.Contains, installed: true).Reconcile();

        Assert.Equal(command, _store.Run[AutoStartHelper.AppName]);
    }

    /// <summary>The installer deleted the exe or a build folder was cleaned: Windows will start nothing, so it is off.</summary>
    [Fact]
    public void AnEntryWhoseExeIsGone_IsOff()
    {
        _store.Run[AutoStartHelper.AppName] = @"""C:\gone\tracker.exe"" --minimized";

        Assert.Equal(AutoStartState.Off, Helper().GetState());
    }

    [Fact]
    public void Uninstall_RemovesTheEntry_WhenItStartsTheInstalledExe()
    {
        _store.Run[AutoStartHelper.AppName] = $"\"{Exe}\" --minimized";
        _store.Approved[AutoStartHelper.AppName] = Disabled;
        _store.Run[OtherApp] = OtherAppCommand;

        Helper().RemoveIfOwn();

        Assert.DoesNotContain(AutoStartHelper.AppName, _store.Run.Keys);
        Assert.DoesNotContain(AutoStartHelper.AppName, _store.Approved.Keys);
        Assert.Equal(OtherAppCommand, _store.Run[OtherApp]);
    }

    [Fact]
    public void Uninstall_LeavesAnEntryThatStartsAnotherBuild()
    {
        const string fromSource = @"C:\src\tracker\bin\Release\tracker.exe";
        _store.Run[AutoStartHelper.AppName] = $"\"{fromSource}\" --minimized";

        Helper().RemoveIfOwn();

        Assert.Equal($"\"{fromSource}\" --minimized", _store.Run[AutoStartHelper.AppName]);
    }

    [Fact]
    public void Reconcile_WithNothingThere_DoesNothing()
    {
        _store.Run[OtherApp] = OtherAppCommand;

        Helper().Reconcile();

        Assert.Equal([OtherApp], _store.Run.Keys);
    }

    [Fact]
    public void AStoreThatThrows_NeverTakesTheTrayDown()
    {
        _store.Throws = true;
        var helper = Helper();

        Assert.Equal(AutoStartState.Off, helper.GetState());
        helper.Enable();
        helper.Disable();
        helper.Reconcile();
        helper.RemoveIfOwn();
    }

    // ---- reading the command ---------------------------------------------

    [Theory]
    [InlineData(@"""C:\Program Files\x\tracker.exe"" --minimized", @"C:\Program Files\x\tracker.exe")]
    [InlineData(@"C:\tools\tracker.exe --minimized", @"C:\tools\tracker.exe")]
    [InlineData(@"C:\tools\tracker.EXE", @"C:\tools\tracker.EXE")]
    [InlineData(@"  ""C:\a b\t.exe""  ", @"C:\a b\t.exe")]
    public void ExecutablePath_IsTheProgramNotItsArguments(string command, string expected) =>
        Assert.Equal(expected, AutoStartHelper.ExecutablePath(command));

    [Fact]
    public void ExecutablePath_ExpandsEnvironmentVariables()
    {
        var path = Assert.IsType<string>(AutoStartHelper.ExecutablePath(@"""%SystemRoot%\system32\cmd.exe"" /c"));

        Assert.DoesNotContain('%', path);
        Assert.EndsWith(@"system32\cmd.exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(@"""", null)]
    public void ExecutablePath_OfNothingUsable_IsNull(string command, string? expected) =>
        Assert.Equal(expected, AutoStartHelper.ExecutablePath(command));

    // ---- the menu text -----------------------------------------------------

    [Fact]
    public void TheMenu_SaysWhenWindowsHasItSwitchedOff()
    {
        Assert.Equal("Start with Windows", TrayApplicationContext.DescribeAutoStart(AutoStartState.On));
        Assert.Equal("Start with Windows", TrayApplicationContext.DescribeAutoStart(AutoStartState.Off));
        Assert.Contains("turned off in Windows settings", TrayApplicationContext.DescribeAutoStart(AutoStartState.DisabledInWindows));
    }

    private sealed class FakeStore : IAutoStartStore
    {
        public Dictionary<string, string> Run { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> Approved { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Throws { get; set; }

        public string? GetRunValue(string name) => Check().Run.GetValueOrDefault(name);
        public void SetRunValue(string name, string command) => Check().Run[name] = command;
        public void DeleteRunValue(string name) => Check().Run.Remove(name);
        public byte[]? GetApproved(string name) => Check().Approved.GetValueOrDefault(name);
        public void SetApproved(string name, byte[] data) => Check().Approved[name] = data;
        public void DeleteApproved(string name) => Check().Approved.Remove(name);

        private FakeStore Check() => Throws ? throw new UnauthorizedAccessException() : this;
    }
}
