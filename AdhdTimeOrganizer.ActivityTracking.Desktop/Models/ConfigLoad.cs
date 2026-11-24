namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

/// <summary>Where <see cref="AppConfig.Load(string)"/> got the config from.</summary>
public enum ConfigOrigin
{
    /// <summary>From <c>config.json</c>.</summary>
    Loaded,

    /// <summary>No <c>config.json</c> yet: a first start. Defaults, and nothing to say.</summary>
    Created,

    /// <summary><c>config.json</c> was damaged or could not be opened; <c>config.json.bak</c>, the last file that loaded or was saved, was used, without its refresh token.</summary>
    FromBackup,

    /// <summary>Neither file could be used: defaults, with every title hidden until the Privacy form is saved.</summary>
    Defaults
}

/// <param name="ResetValues">Values that were unknown to this build or missing, and went back to their defaults.</param>
/// <param name="CouldNotOpen">
/// The file was there but could not be opened (locked, access denied), as opposed to damaged. The config is then only a
/// stand-in, and never saved over it.
/// </param>
public sealed record ConfigLoad(AppConfig Config, ConfigOrigin Origin, int ResetValues = 0, bool CouldNotOpen = false)
{
    /// <summary>
    /// What the tray says once at start, or null when there is nothing to say. Counts only: the rules are the user's own
    /// words, and a balloon is read over the shoulder.
    /// </summary>
    public (string Title, string Text)? Notice()
    {
        var hidden = Config.HideAllTitlesUntilReviewed
            ? " Every window title is hidden until you check your rules in Privacy and press Save."
            : "";
        var problem = CouldNotOpen ? "couldn't be opened" : "was damaged";
        // The stand-in never saves over a file that is only locked (AppConfig.Save); the next start reads the real one.
        var held = CouldNotOpen ? " Changes made now last only until a restart." : "";

        return Origin switch
        {
            ConfigOrigin.FromBackup => ("Activity Tracker — settings restored",
                $"The settings file {problem}, so the last good copy was used ({Plural(Config.LocalRules.Count, "privacy rule")}). " +
                "Please sign in again." + held + hidden),
            ConfigOrigin.Defaults => ("Activity Tracker — settings reset",
                $"The settings file {problem} and no good copy was left, so settings and privacy rules are back to the defaults." +
                held + hidden),
            ConfigOrigin.Loaded when ResetValues > 0 || Config.HideAllTitlesUntilReviewed => ("Activity Tracker — settings",
                ((ResetValues > 0 ? $"{Plural(ResetValues, "setting")} had a value this version can't use and went back to the default." : "") +
                 hidden).TrimStart()),
            _ => null
        };
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
