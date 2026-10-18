# AdhdTimeOrganizer.ActivityTracking.Desktop

The Windows tray agent that records which windows the user has in front of them and sends one
**minute window** per minute to the AdhdTimeOrganizer server
(`POST /api/activity-tracking/desktop/heartbeat`). WinForms, .NET 10, no DI container.

The server is the sibling repo `../AdhdTimeOrganizer` (Tracking slice:
`AdhdTimeOrganizer.Tracking/`, start with its `docs/summary.md`). Ideas for what to build next are in
`../AdhdTimeOrganizer/review/ideas.md`.

## Layout

| Path | What lives there |
|---|---|
| `Program.cs` | Logging (Serilog → `%LOCALAPPDATA%\AdhdTimeOrganizer\logs`), single-instance mutex, tray startup |
| `TrayApplicationContext.cs` | Tray icon and menu; pulls status from the tracker on a UI timer |
| `LoginForm.cs`, `SettingsForm.cs` | Password + 2FA sign-in; settings editor (ignore rules stay in `config.json`) |
| `Services/ActivityTrackerService.cs` | Poll loop (observes the desktop), send loop (drains the outbox), lock/suspend/session-end handling |
| `Services/ActivityRecorder.cs` | **Pure** minute accounting: measured elapsed time, split at minute boundaries, gap cap |
| `Services/WindowLimits.cs` | The server's validation rules, applied before a window is queued |
| `Services/WindowTitleNormalizer.cs` | Strips counters, unsaved markers and app-name suffixes from titles |
| `Services/OutboxStore.cs` | Finished windows as JSON files under `%LOCALAPPDATA%\…Desktop\outbox` until sent |
| `Services/ApiClient.cs` | Login, 2FA, token refresh, heartbeat; `Classify` decides retry vs. drop per status code |
| `Services/HyperfocusPolicy.cs` | **Pure** nudge decisions: stretches, breaks, app stretches, snooze / repeat / "not now", the break timer |
| `Services/NudgeService.cs` | Feeds polls to the policy, checks quiet conditions, shows and withdraws toasts, handles button presses |
| `Services/NudgeNotifier.cs`, `NudgeToast.cs` | Windows App SDK notifications: registration, toast text and buttons, parsing a press |
| `Native/` | P/Invoke (`Win32`, `MonitorHelper`), `ProcessInfoCache`, `AudioSessionHelper` (NAudio), `MicrophoneUsage` (registry) |
| `Models/` | Wire models (`ActivityModels.cs`) and `AppConfig` (`%LOCALAPPDATA%\…Desktop\config.json`) |
| `AdhdTimeOrganizer.ActivityTracking.Desktop.Tests/` | xunit v3 tests, including `NativeSmokeTests` against the real machine |

## Building and testing — always through `scripts/test.ps1`

```powershell
./scripts/test.ps1                                   # build + run all tests
./scripts/test.ps1 -Class '*OutboxStoreTests'        # comma-separated, xunit wildcard syntax
./scripts/test.ps1 -BuildOnly                        # compile check, Debug and Release
```

- It builds into `artifacts-cli/` (gitignored). The user usually has the tracker **running from
  `bin/`** or open in Rider, so a plain `dotnet build` fails with `MSB3021 … being used by another
  process` even though the code compiled.
- `dotnet test` is not used; the test project is an xunit v3 executable and the script runs it.
- `-BuildOnly` also builds Release: `AppConfig.ApiBaseUrl` defaults to `localhost` under `#if DEBUG`
  and to production otherwise, so Release is a different compile.
- The build is architecture-specific (the Windows App SDK is self-contained; see below): the exe lands in
  `bin/Debug/net10.0-windows10.0.19041.0/win-x64/`, not `bin/Debug/net10.0-windows/`. The default RID is
  the building SDK's; `-r win-arm64` cross-builds.
- **Don't launch the app to "try it" without asking.** It shares the single-instance mutex and
  `config.json` with the tracker the user actually runs, and the first run of a newer build migrates the
  saved token, so an older build afterwards has to sign in again.

## Rules that hold everywhere

### The wire contract
- **A refused heartbeat is a lost minute.** The agent cannot repair a batch the server calls
  malformed: a 4xx other than 401/403/408/409/425/429 is dropped (`ApiClient.Classify`). So every server
  bound is mirrored in `WindowLimits` and applied before queuing: ≤ 60 s active + background per entry,
  ≤ 60 s active per window, ≤ 200 entries, column widths, and the 2000-byte (process, title) index limit.
  When the server's `DesktopActivityHeartbeatValidator` or `TrackingIngestLimits` change, change
  `WindowLimits` with them.
- **Never send null or a blank title.** Missing product names and paths go out as `""`, and an untitled
  window takes its product or process name (`ActivityRecorder`). The current server accepts nulls, but
  servers from before 2026-10-18 answer a null with a 500 and a blank title with a 400, and a user's
  tracker may meet either server.
- **Process names must stay identical to `Process.ProcessName`** (file name minus a trailing `.exe`).
  Users' mapping rules on the server match on them. Changing a process name or the title
  normalization changes which rules match and splits the distinct-entries grid. Do it deliberately and
  say so.
- **Rule matching belongs to the server.** `DesktopMappingRuleSet` is the only matcher, and the slice
  docs forbid a second one. If the agent needs to know which activity a window maps to (nudges, tray
  glance), the server has to tell it, e.g. in the heartbeat response. Don't port the matcher here.
  The local `IgnoreRules` in `config.json` are a separate, deliberately simple list (exact process,
  optional exact title).

### Time accounting
- All of it lives in `ActivityRecorder`, which has no clock and no Win32, so it's unit-testable. Keep it
  that way: the service passes `DateTime.UtcNow` and the measured elapsed time in.
- Credit **measured** elapsed time, never the configured interval. A gap longer than `MaxCreditedGap`
  (sleep, a hang) credits nothing.
- A poll yields at most one non-background observation, and each process (name + path) is observed at
  most once per poll. That's what keeps entries within the 60-second rules without clamping.
- Idle = no input for `IdleThresholdSeconds`, unless the **foreground** window is audibly playing
  (`IsPassivelyPresent`), up to `MaxPassiveIdleMinutes`. Fullscreen alone deliberately doesn't count:
  a paused fullscreen video looks like a watched one. A locked session records nothing.

### Durability and threads
- Every finished window goes to the **outbox on disk** before anything tries to send it. Exit, suspend,
  logoff and session end call `ActivityTrackerService.Flush()`. Don't add an in-memory-only queue.
- The send loop runs for the app's lifetime, independent of pausing, and stops a pass at the first
  window that has to wait (offline, signed out). The poll loop stops when tracking is paused.
- **Never touch WinForms objects from the poll or send loop.** The tray pulls `GetStatus()` on a UI
  timer. A status event that set `NotifyIcon.Text` from the poll thread once threw out of the loop's own
  catch and silently ended tracking. (`NotifyIcon.Text` also throws past 127 characters, so it's
  truncated.)
- A session ends only when the server refuses the refresh token (400/401/403). Being offline or getting
  a 5xx keeps it. While signed out, tracking continues into the outbox.

### Native code
- `[LibraryImport]` with `StringMarshalling.Utf16` for anything taking `char[]`. Enumeration callbacks
  are `[UnmanagedCallersOnly]` and only append a handle. Do the managed work after the enumeration
  returns.
- Process info comes from `ProcessInfoCache` (`QueryFullProcessImageName` on a limited handle, keyed by
  pid + creation time). `Process.MainModule` fails on elevated processes and is slow, so don't
  reintroduce it.
- A wrong P/Invoke signature compiles and fails silently at runtime. Add a case to `NativeSmokeTests`
  for any new native call.

### Nudges and notifications
- Nudges are **client-side only**: nothing about them reaches the server. The policy lives in
  `HyperfocusPolicy`, pure like `ActivityRecorder` — keep clocks, Win32 and toasts out of it.
- They're fed by the poll loop, so pausing tracking pauses them. A paused, asleep or hung gap as long as a
  break counts as a break.
- A call (any app holding the microphone) counts as presence **for nudges only**. It must not change what
  the tracker records or sends; passive presence stays the only idle exception there.
- Quiet conditions hold a due nudge until they end; they never drop it. "Full screen" means
  `SHQueryUserNotificationState`, Windows' own definition. A rectangle check also catches a maximised
  window behind an auto-hiding taskbar, and the desktop. The break-over nudge ignores quiet conditions
  and the off switch, because the user set it.
- Notifications use the **Windows App SDK, unpackaged and self-contained** (`Microsoft.WindowsAppSDK.Foundation`
  only; the metapackage adds ~100 MB of WinUI/AI/ML). Three things break it silently or at runtime only:
  - SDK 2.x self-contained omits `Microsoft.WindowsAppRuntime.Insights.Resource.dll`, and `Register` throws
    without it. A target in the csproj extracts it from the runtime MSIX; `NativeSmokeTests` checks it's
    deployed. Remove the target when a newer SDK ships the file.
  - `Register` takes the icon as a **file**: `timeorganizer-icon.ico` is copied to the output as well as
    embedded.
  - The builder doesn't escape **button labels**: an ASCII apostrophe makes `BuildNotification` throw.
    `NudgeToast.Button` swaps it for `’`; `NudgeToastTests` builds every kind and parses the XML.
- Button presses arrive on a thread-pool thread and are posted to the UI thread ("Turn off" saves the
  config). On exit every toast is removed and the app unregistered, so a stale toast can't relaunch it.

### Privacy and logging
- No email, name or other personal data in logs. Log files outlive an account deletion. Ids, process
  names and status codes are fine. Window titles and paths are personal; don't log them either.
- The refresh token is DPAPI-encrypted in `config.json` (`ProtectedRefreshToken`). Never write it in plain
  text.

## Shell

This is a Windows machine: use PowerShell. Never round-trip a file through the shell
(`Get-Content | Set-Content` and the like); edit files with the editor tools.
