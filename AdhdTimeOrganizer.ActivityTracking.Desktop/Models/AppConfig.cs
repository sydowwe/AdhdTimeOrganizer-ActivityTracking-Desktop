using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services.Matching;
using Serilog;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

public enum LocalRuleAction
{
    /// <summary>Not recorded at all — system noise, or an app the user wants no trace of.</summary>
    Ignore,
    /// <summary>Recorded, but the title never leaves the machine (see <see cref="PrivateWindowHandling"/>).</summary>
    HideTitle
}

/// <summary>Which planned blocks the start ramp asks about. The phone's choice, with the same default.</summary>
public enum RampBlocks
{
    /// <summary>Every planned block.</summary>
    All,

    /// <summary>All but the Optional ones: those the user already said they may skip.</summary>
    NotOptional,

    /// <summary>Critical, or a level the user ranked above it.</summary>
    Critical
}

/// <summary>What a private browser window or a <see cref="LocalRuleAction.HideTitle"/> window turns into.</summary>
public enum PrivateWindowHandling
{
    /// <summary>Sent with the title replaced by "&lt;app&gt; - PRIVATE", so the time still adds up.</summary>
    KeepTimeAsAppName,
    /// <summary>Not recorded, like an ignored window.</summary>
    Drop
}

/// <summary>
/// One of the user's own rules, applied when a window is recorded — before anything is written to the outbox.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProcessName"/> null or empty → any process. <see cref="WindowTitle"/> null → any title; <c>""</c> →
/// only an empty title; otherwise matched with <see cref="WindowTitleMatchType"/> against the raw title <em>and</em>
/// the normalized one, so a rule written from what the dashboards show still catches the window, and a rule
/// written against a part normalization strips does too. A rule that names neither matches nothing.
/// </para>
/// <para>
/// The JSON is the old ignore rule's (<c>processName</c>, <c>windowTitle</c>, exact) plus three optional fields,
/// so a config.json written before them reads unchanged.
/// </para>
/// </remarks>
public sealed class LocalRule
{
    [JsonPropertyName("action")]
    [JsonConverter(typeof(LenientEnumConverter<LocalRuleAction>))]
    public LocalRuleAction Action { get; set; } = LocalRuleAction.Ignore;

    [JsonPropertyName("processName")]
    public string? ProcessName { get; set; }

    [JsonPropertyName("processNameMatchType")]
    [JsonConverter(typeof(LenientEnumConverter<PatternMatchType>))]
    public PatternMatchType ProcessNameMatchType { get; set; } = PatternMatchType.Exact;

    [JsonPropertyName("windowTitle")]
    public string? WindowTitle { get; set; }

    [JsonPropertyName("windowTitleMatchType")]
    [JsonConverter(typeof(LenientEnumConverter<PatternMatchType>))]
    public PatternMatchType WindowTitleMatchType { get; set; } = PatternMatchType.Exact;
}

/// <summary>
/// The time chime set on, or off, by hand for one activity or role · category. <see cref="Key"/> is the today breakdown's
/// (<c>activity:12</c>, <c>label:Leisure · Video</c>); <see cref="Name"/> is shown in Settings and is user-authored, so the
/// diagnostics report counts choices and never names them.
/// </summary>
/// <param name="Chime">True: ticked. False: a distraction the user does not want chimed.</param>
public sealed record ChimeChoice(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("chime")] bool Chime);

public sealed class AppConfig
{
    public static string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdhdTimeOrganizer.ActivityTracking.Desktop");

    public static string ConfigPath { get; } = Path.Combine(ConfigDirectory, "config.json");

    /// <summary>
    /// Where <see cref="Save"/> writes: <see cref="ConfigPath"/>, or the path it was <see cref="Load(string)"/>ed from — in
    /// tests, which must never write over the config of the tracker the user is running, a temp one.
    /// </summary>
    [JsonIgnore]
    public string FilePath { get; internal set; } = ConfigPath;

    // Ties the DPAPI blob to this app as well as to the Windows user.
    private static readonly byte[] TokenEntropy = "AdhdTimeOrganizer.ActivityTracking.Desktop"u8.ToArray();

    private static readonly Lock SaveGate = new();

#if DEBUG
    public string ApiBaseUrl { get; set; } = "https://localhost:8080";

    /// <summary>The web app, for the links the tracker opens (the planner, the mappings). Its own setting: in development it is a separate server.</summary>
    public string WebAppUrl { get; set; } = "https://localhost:5173";
#else
    public string ApiBaseUrl { get; set; } = "https://adhdtimeorganizer.com";

    /// <summary>The web app, for the links the tracker opens (the planner, the mappings). Its own setting: in development it is a separate server.</summary>
    public string WebAppUrl { get; set; } = "https://adhdtimeorganizer.com";
#endif
    /// <summary>
    /// How often idleness, background windows, sound and the microphone are checked. Not the foreground: that is
    /// followed from events as it changes, so this can be slower than the 2 s it was when it sampled the foreground too.
    /// </summary>
    public int PollIntervalMs { get; set; } = 5000;
    public int IdleThresholdSeconds { get; set; } = 30;
    public int MinBackgroundWindowPercent { get; set; } = 15;
    public int MaxBackgroundWindowsPerMonitor { get; set; } = 2;

    /// <summary>
    /// How long a foreground window that is playing sound keeps the user "present" without keyboard or
    /// mouse input — a film, a talk, a call. Past this, autoplay left running counts as idle.
    /// </summary>
    public int MaxPassiveIdleMinutes { get; set; } = 150;

    // ── Hyperfocus nudges (see HyperfocusPolicy) ──────────────────────────────

    /// <summary>Off from a nudge's own "Turn off" button too. A break timer already running still ends with its nudge.</summary>
    public bool HyperfocusNudgesEnabled { get; set; } = true;

    /// <summary>At the computer this long without a break → "you've been at it for 1 h 30 m".</summary>
    public int HyperfocusNudgeMinutes { get; set; } = 90;

    /// <summary>Foreground time in one app without a break from it → "2 h straight in Rider". 0 turns it off.</summary>
    public int AppHyperfocusNudgeMinutes { get; set; } = 120;

    /// <summary>Away this long — no input, no sound in the foreground, no call — ends a stretch.</summary>
    public int BreakMinutes { get; set; } = 5;

    /// <summary>How long "Taking a break" lasts before the nudge that says the break is over.</summary>
    public int BreakTimerMinutes { get; set; } = 10;

    /// <summary>Hold nudges while an app is full screen or presenting (Windows' own definition).</summary>
    public bool QuietWhenFullscreen { get; set; } = true;

    /// <summary>Hold nudges while any app has the microphone open — a call, even a muted one.</summary>
    public bool QuietWhenMicrophoneInUse { get; set; } = true;

    // ── Drift nudges (see DriftPolicy) ────────────────────────────────────────

    /// <summary>
    /// "You planned X, you've been on Y for 10 minutes." Off until the user turns it on: it needs a plan and mapped apps,
    /// and an unasked-for "you drifted" is the nagging the tracker must not do.
    /// </summary>
    public bool DriftNudgesEnabled { get; set; }

    /// <summary>Off-plan minutes inside one planned block before the nudge. At least 10.</summary>
    public int DriftNudgeMinutes { get; set; } = 10;

    // ── Start ramp (see StartRampPolicy) ──────────────────────────────────────

    /// <summary>
    /// "Thesis starts now (until 11:30). Start the timer?" at each planned block's start. On by default, unlike drift
    /// nudges: it needs only a plan, not mapped apps, it helps with the hard part rather than reproaching a slip, and it
    /// asks once per block.
    /// </summary>
    public bool StartRampEnabled { get; set; } = true;

    /// <summary>Which planned blocks it asks about, by the importance the planner already gives them.</summary>
    [JsonConverter(typeof(LenientEnumConverter<RampBlocks>))]
    public RampBlocks StartRampBlocks { get; set; } = RampBlocks.NotOptional;

    // ── Time chime (see ChimeSchedule) ────────────────────────────────────────

    /// <summary>
    /// "15 min on YouTube", silently, every <see cref="TimeChimeMinutes"/> of continuous time in a chosen activity. Off by
    /// default, like the phone's: ticking an activity in the tray's today menu turns it on.
    /// </summary>
    public bool TimeChimeEnabled { get; set; }

    /// <summary>One interval for every chosen activity: 5, 10, 15, 20 or 30 (<see cref="Services.ChimeSettings.IntervalChoices"/>).</summary>
    public int TimeChimeMinutes { get; set; } = 15;

    /// <summary>Chime on what the server says the user marked a distraction, unless unticked in <see cref="TimeChimeChoices"/>.</summary>
    public bool TimeChimeDistractions { get; set; } = true;

    /// <summary>
    /// Activities (or role · category) ticked or unticked by hand, by the today breakdown's key. Replaced as a whole on
    /// change, never mutated: the send loop reads it on its own thread.
    /// </summary>
    public List<ChimeChoice> TimeChimeChoices { get; set; } = [];

    // ── Quick capture (see CaptureQueue) ──────────────────────────────────────

    /// <summary>
    /// The global hotkey that opens the capture box, as <see cref="Services.HotkeyChord"/> writes it ("Win+Alt+N"). Empty
    /// switches the hotkey off; the tray's "Capture a thought…" stays either way.
    /// </summary>
    public string CaptureHotkey { get; set; } = Services.HotkeyChord.Default;

    // ── Upload problems (see UploadHealthPolicy) ──────────────────────────────

    /// <summary>
    /// Tell me when tracked time isn't arriving: signed out, uploads stuck for a day, windows the server refused.
    /// A switch of its own: operational notices are not nudges, and "Turn off" on a nudge must not silence them.
    /// </summary>
    public bool UploadNoticesEnabled { get; set; } = true;

    // ── Evening review (see UnmappedReviewPolicy) ─────────────────────────────

    /// <summary>
    /// One toast at the user's evening: how much of today on this PC no rule maps, and the top two apps, with a button
    /// that opens the page to map them. On by default — it is how coverage grows — and independent of drift nudges.
    /// </summary>
    public bool UnmappedReviewEnabled { get; set; } = true;

    // ── Pause reminder (see PauseReminderPolicy) ──────────────────────────────

    /// <summary>
    /// "Tracking has been paused for 4 h": a toast while a pause "until I resume" lasts, with Resume and Keep paused. Not a
    /// nudge: a switch of its own, no quiet conditions.
    /// </summary>
    public bool PauseRemindersEnabled { get; set; } = true;

    // ── Privacy (see PrivacyFilter) ───────────────────────────────────────────

    /// <summary>Firefox Private Browsing and Edge InPrivate windows are handled like a hide-title rule.</summary>
    public bool HidePrivateWindows { get; set; } = true;

    [JsonConverter(typeof(LenientEnumConverter<PrivateWindowHandling>))]
    public PrivateWindowHandling PrivateWindowHandling { get; set; } = PrivateWindowHandling.KeepTimeAsAppName;

    /// <summary>
    /// Every window is recorded as if a hide-title rule matched it, until the user saves the Privacy form. Set by
    /// <see cref="Load(string)"/> when the user's rules could not be recovered — a damaged file with no usable backup, or a
    /// rule this build cannot evaluate — so a title the user had hidden is not uploaded from the next poll. Written only
    /// while true: a normal config.json is the same as before it existed, and a build that does not know it ignores it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HideAllTitlesUntilReviewed { get; set; }

    /// <summary>
    /// Tracking is paused until then: a time from "Pause for…", <see cref="DateTime.MaxValue"/> for "until I
    /// resume", null when not paused. Saved, so a restart during a pause does not quietly start recording again.
    /// </summary>
    public DateTime? PausedUntilUtc { get; set; }

    /// <summary>
    /// When recording stopped for the pause in <see cref="PausedUntilUtc"/>; null when not paused, and in a config written
    /// before it was saved. A new end for a pause in force keeps it: the pause reminder counts from when recording stopped.
    /// </summary>
    public DateTime? PausedSinceUtc { get; set; }

    /// <summary>
    /// The refresh token, encrypted with DPAPI for the current Windows user. It used to sit in this file
    /// in plain text, readable by anything that can read the user's profile.
    /// </summary>
    public string? ProtectedRefreshToken
    {
        get => _protectedRefreshToken;
        set
        {
            _protectedRefreshToken = value;
            _isRefreshTokenDecrypted = false;
        }
    }

    /// <summary>Read once from a config written before the token was encrypted, then cleared.</summary>
    [JsonPropertyName("RefreshToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyRefreshToken { get; set; }

    /// <summary>The decrypted token, cached — the tray reads it every couple of seconds.</summary>
    [JsonIgnore]
    public string? RefreshToken
    {
        get
        {
            if (!_isRefreshTokenDecrypted)
            {
                _refreshToken = Unprotect(_protectedRefreshToken);
                _isRefreshTokenDecrypted = true;
            }
            return _refreshToken;
        }
        set
        {
            _protectedRefreshToken = Protect(value);
            _refreshToken = string.IsNullOrEmpty(value) ? null : value;
            _isRefreshTokenDecrypted = true;
        }
    }

    private string? _protectedRefreshToken;
    private string? _refreshToken;
    private bool _isRefreshTokenDecrypted;

    /// <summary>
    /// The user's own rules (see <see cref="LocalRule"/>), edited in the Privacy form. Still stored under the old
    /// <c>IgnoreRules</c> key, which configs written before hide-title rules carry. Replaced as a whole on save,
    /// never mutated, because the poll loop reads it on its own thread.
    /// </summary>
    [JsonPropertyName("IgnoreRules")]
    public List<LocalRule> LocalRules { get; set; } =
    [
        // ── Explorer shell noise ──────────────────────────────────────────────
        // "Program Manager" is the hidden desktop host window; empty title = taskbar focus.
        // File Explorer windows (folder titles) are NOT matched and will still be tracked.
        new() { ProcessName = "explorer",                  WindowTitle = "Program Manager" },
        new() { ProcessName = "explorer",                  WindowTitle = "" },

        // ── Windows Search ────────────────────────────────────────────────────
        new() { ProcessName = "SearchHost" },        // Win 11 search overlay
        new() { ProcessName = "SearchApp" },          // Win 10 search overlay

        // ── Start menu / shell chrome ─────────────────────────────────────────
        new() { ProcessName = "StartMenuExperienceHost" }, // Win 10/11 Start menu
        new() { ProcessName = "ShellExperienceHost" },     // Notification centre, older Start
        new() { ProcessName = "ActionCenter" },
        new() { ProcessName = "Taskmgr" },

        // ── Lock / login screen ───────────────────────────────────────────────
        new() { ProcessName = "LockApp" },
        new() { ProcessName = "LogonUI" },

        // ── Input infrastructure ──────────────────────────────────────────────
        new() { ProcessName = "TextInputHost" },     // Touch keyboard / voice input panel
        new() { ProcessName = "ctfmon" },            // IME/input method manager

        // ── Other shell plumbing ──────────────────────────────────────────────
        new() { ProcessName = "ScreenClippingHost" }, // Snipping Tool host (momentary)
        new() { ProcessName = "PaintStudio" },        // Win 11 Snipping Tool itself is brief
    ];

    /// <summary>A page of the web app, for <c>Process.Start</c>. <paramref name="pathAndQuery"/> starts with <c>/</c>.</summary>
    public Uri WebAppUri(string pathAndQuery) => new(new Uri(WebAppUrl.TrimEnd('/') + "/"), pathAndQuery.TrimStart('/'));

    public static ConfigLoad Load() => Load(ConfigPath);

    /// <summary>
    /// The config at <paramref name="path"/>, <see cref="Normalize"/>d, and how it was obtained. Never falls back to
    /// defaults silently: the first thing a tray without a session does is save, which would overwrite the user's rules.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Read: a legacy plain-text token moves to DPAPI, and the file — as it is now on disk — is copied to
    /// <c>.bak</c>, the last file that loaded.</item>
    /// <item>Damaged (empty, cut short, not JSON): moved to <c>.bad</c> (one kept), then <c>.bak</c> is used, then defaults
    /// with every title hidden; either is saved at once, so a crash right after cannot come back as a clean first start.</item>
    /// <item>Could not be opened, even after a short wait: the same fall-back, but the file is left where it is and nothing
    /// is saved over it here.</item>
    /// <item>From <c>.bak</c>, the refresh token is dropped: it has almost certainly been rotated since, and the server
    /// answers an old one as a stolen token — by revoking every session of the account.</item>
    /// </list>
    /// </remarks>
    public static ConfigLoad Load(string path)
    {
        var read = ReadWaitingForLock(path);
        switch (read.Status)
        {
            case JsonFileStatus.Missing:
                return new ConfigLoad(new AppConfig { FilePath = path }, ConfigOrigin.Created);

            case JsonFileStatus.Read:
            {
                var config = read.Value!;
                config.FilePath = path;
                var reset = config.Normalize();

                if (config.LegacyRefreshToken is { } legacy)
                {
                    config.RefreshToken = legacy;
                    config.LegacyRefreshToken = null;
                    config.Save();
                }

                WriteBackup(path);
                return new ConfigLoad(config, ConfigOrigin.Loaded, reset);
            }

            case JsonFileStatus.Unreadable:
                Log.Warning(read.Error, "{File} is damaged — moving it to .bad and falling back", Path.GetFileName(path));
                try
                {
                    File.Move(path, path + ".bad", overwrite: true);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not move the damaged {File} aside", Path.GetFileName(path));
                }
                return FallBack(path, couldNotOpen: false);

            default:
                Log.Warning(read.Error, "{File} could not be opened — falling back, leaving it in place", Path.GetFileName(path));
                return FallBack(path, couldNotOpen: true);
        }
    }

    /// <summary>A virus scan or a backup tool can hold the file for a moment at logon.</summary>
    private static JsonFileRead<AppConfig> ReadWaitingForLock(string path)
    {
        var read = JsonFile.TryRead<AppConfig>(path);
        for (var attempt = 0; attempt < 5 && read.Status == JsonFileStatus.CouldNotOpen; attempt++)
        {
            Thread.Sleep(400);
            read = JsonFile.TryRead<AppConfig>(path);
        }
        return read;
    }

    private static void WriteBackup(string path)
    {
        try
        {
            JsonFile.WriteBytes(path + ".bak", File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not write the config backup");
        }
    }

    private static ConfigLoad FallBack(string path, bool couldNotOpen)
    {
        var backup = JsonFile.TryRead<AppConfig>(path + ".bak");
        ConfigLoad load;
        if (backup.Value is { } config)
        {
            config.FilePath = path;
            var reset = config.Normalize();
            config.RefreshToken = null;
            config.LegacyRefreshToken = null;
            Log.Warning("Config restored from the backup, without its refresh token");
            load = new ConfigLoad(config, ConfigOrigin.FromBackup, reset, couldNotOpen);
        }
        else
        {
            Log.Warning(backup.Error, "No usable config backup ({Status}) — defaults, with every title hidden", backup.Status);
            load = new ConfigLoad(new AppConfig { FilePath = path, HideAllTitlesUntilReviewed = true }, ConfigOrigin.Defaults,
                CouldNotOpen: couldNotOpen);
        }

        if (!couldNotOpen)
        {
            try
            {
                load.Config.Save();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not save the recovered config");
            }
        }
        return load;
    }

    /// <summary>
    /// Makes a config read from disk safe to run on: a value this build does not know goes back to its default, a missing
    /// list to its default, and each number into its <see cref="ConfigLimits"/> range. A privacy rule with a match type this
    /// build does not know loses that condition, so it hides or ignores more, never less; one left naming nothing turns on
    /// <see cref="HideAllTitlesUntilReviewed"/>. Returns how many values were unknown or missing; clamped numbers are not
    /// counted, only logged.
    /// </summary>
    internal int Normalize()
    {
        var defaults = new AppConfig();
        var reset = 0;

        if (string.IsNullOrWhiteSpace(ApiBaseUrl)) { ApiBaseUrl = defaults.ApiBaseUrl; reset++; }
        if (string.IsNullOrWhiteSpace(WebAppUrl)) { WebAppUrl = defaults.WebAppUrl; reset++; }
        if (CaptureHotkey is null) { CaptureHotkey = defaults.CaptureHotkey; reset++; }

        if (!Enum.IsDefined(StartRampBlocks)) { StartRampBlocks = defaults.StartRampBlocks; reset++; }
        if (!Enum.IsDefined(PrivateWindowHandling)) { PrivateWindowHandling = defaults.PrivateWindowHandling; reset++; }

        if (TimeChimeChoices is null) { TimeChimeChoices = defaults.TimeChimeChoices; reset++; }
        else if (TimeChimeChoices.Any(c => c?.Key is null || c.Name is null))
        {
            reset++;
            TimeChimeChoices = TimeChimeChoices.Where(c => c?.Key is not null).Select(c => c with { Name = c.Name ?? "" }).ToList();
        }

        if (LocalRules is null) { LocalRules = defaults.LocalRules; reset++; }
        else
        {
            var rules = new List<LocalRule>(LocalRules.Count);
            foreach (var rule in LocalRules)
            {
                if (rule is null) { reset++; continue; }
                reset += NormalizeRule(rule);
                rules.Add(rule);
            }
            LocalRules = rules; // replaced, never mutated: see LocalRules
        }

        if (!ChimeSettings.IntervalChoices.Contains(TimeChimeMinutes)) TimeChimeMinutes = ChimeSettings.DefaultMinutes;
        PollIntervalMs = Clamp(PollIntervalMs, ConfigLimits.PollIntervalMs, nameof(PollIntervalMs));
        IdleThresholdSeconds = Clamp(IdleThresholdSeconds, ConfigLimits.IdleThresholdSeconds, nameof(IdleThresholdSeconds));
        MaxPassiveIdleMinutes = Clamp(MaxPassiveIdleMinutes, ConfigLimits.MaxPassiveIdleMinutes, nameof(MaxPassiveIdleMinutes));
        MinBackgroundWindowPercent = Clamp(MinBackgroundWindowPercent, ConfigLimits.MinBackgroundWindowPercent, nameof(MinBackgroundWindowPercent));
        MaxBackgroundWindowsPerMonitor = Clamp(MaxBackgroundWindowsPerMonitor, ConfigLimits.MaxBackgroundWindowsPerMonitor, nameof(MaxBackgroundWindowsPerMonitor));
        HyperfocusNudgeMinutes = Clamp(HyperfocusNudgeMinutes, ConfigLimits.HyperfocusNudgeMinutes, nameof(HyperfocusNudgeMinutes));
        AppHyperfocusNudgeMinutes = Clamp(AppHyperfocusNudgeMinutes, ConfigLimits.AppHyperfocusNudgeMinutes, nameof(AppHyperfocusNudgeMinutes));
        BreakMinutes = Clamp(BreakMinutes, ConfigLimits.BreakMinutes, nameof(BreakMinutes));
        BreakTimerMinutes = Clamp(BreakTimerMinutes, ConfigLimits.BreakTimerMinutes, nameof(BreakTimerMinutes));
        DriftNudgeMinutes = Clamp(DriftNudgeMinutes, ConfigLimits.DriftNudgeMinutes, nameof(DriftNudgeMinutes));

        if (reset > 0) Log.Warning("Config: {Count} unknown or missing values went back to their defaults", reset);
        return reset;
    }

    private int NormalizeRule(LocalRule rule)
    {
        var reset = 0;
        var lostCondition = false;

        // Ignore records nothing: the most private reading of an action this build does not know.
        if (!Enum.IsDefined(rule.Action)) { rule.Action = LocalRuleAction.Ignore; reset++; }

        if (!Enum.IsDefined(rule.ProcessNameMatchType))
        {
            rule.ProcessNameMatchType = PatternMatchType.Exact;
            if (!string.IsNullOrEmpty(rule.ProcessName)) { rule.ProcessName = null; lostCondition = true; }
            reset++;
        }

        if (!Enum.IsDefined(rule.WindowTitleMatchType))
        {
            rule.WindowTitleMatchType = PatternMatchType.Exact;
            // "" is "only an empty title" whatever the match type, so it survives.
            if (!string.IsNullOrEmpty(rule.WindowTitle)) { rule.WindowTitle = null; lostCondition = true; }
            reset++;
        }

        // A rule left naming nothing matches nothing (PrivacyFilter.IsUsable); it can't be widened, so hide everything.
        if (lostCondition && !PrivacyFilter.IsUsable(rule)) HideAllTitlesUntilReviewed = true;
        return reset;
    }

    private static int Clamp(int value, IntRange range, string name)
    {
        var clamped = range.Clamp(value);
        if (clamped != value) Log.Information("Config: {Setting} {Value} is outside {Min}–{Max}, using {Clamped}", name, value, range.Min, range.Max, clamped);
        return clamped;
    }

    public void Save()
    {
        lock (SaveGate) JsonFile.Write(FilePath, this, WriteOptions);
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static string? Protect(string? plaintext) =>
        string.IsNullOrEmpty(plaintext)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), TokenEntropy, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), TokenEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            // Another Windows user's blob, or a profile restored on a different machine: sign in again.
            Log.Warning(ex, "Saved refresh token could not be decrypted");
            return null;
        }
    }
}
