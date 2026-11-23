# AdhdTimeOrganizer.ActivityTracking.Desktop

The Windows tray tracker that records which windows the user has in front of them and sends one
**minute window** per minute to the AdhdTimeOrganizer server
(`POST /api/tracker/desktop/heartbeat`). WinForms, .NET 10, no DI container.

The server is the sibling repo `../AdhdTimeOrganizer` (Tracking slice:
`AdhdTimeOrganizer.Tracking/`, start with its `docs/summary.md`). Ideas for what to build next are in
`../AdhdTimeOrganizer/review/ideas.md`.

## Layout

| Path | What lives there |
|---|---|
| `Program.cs` | Velopack's hooks (first line — see "Releasing"), logging (Serilog → `%LOCALAPPDATA%\AdhdTimeOrganizer\logs`), single-instance mutex, tray startup |
| `TrayApplicationContext.cs` | Tray icon and menu (incl. "Pause for…", "Quiet nudges for…", the today/now glance and the "map these apps" prompt); pulls status from the tracker on a UI timer, which also swaps the icon and feeds the upload notices and the pause reminder. Double-click never pauses: it opens the menu, resumes a pause, or signs in |
| `Services/SessionStartup.cs` | The tray's start-up order: start tracking, the config notice, restore the session, the sign-in form if none |
| `TrayIcons.cs`, `Services/TrayIconState.cs` | The three tray icons — normal, paused (greyed, pause bars), attention (amber dot: signed out or a poll error) — drawn once from the embedded icon and disposed on exit; and the **pure** choice between them |
| `LoginForm.cs`, `SettingsForm.cs`, `PrivacyForm.cs` | Sign-in (the browser by default; password + 2FA behind "Use a password instead"); settings editor; the user's ignore / hide-title rules and private-window handling |
| `Services/BrowserSignIn.cs` | **Pure** browser sign-in: PKCE (S256), state, the `/connect/tracker` URL, and whether a request reaching the listener is our redirect |
| `Services/LoopbackRedirectListener.cs`, `Services/BrowserSignInFlow.cs` | The loopback listener (`TcpListener` on `127.0.0.1`, free port, one redirect, the handler testable on a stream) and the run: listen → open the browser → wait → exchange |
| `Helpers/AutoStartHelper.cs` | "Start with Windows": the tracker's own `Run` entry (`AdhdTimeOrganizer.ActivityTracking.Desktop` — never `AdhdTimeOrganizer`, which the separate desktop app uses), Windows' `StartupApproved` verdict, repair of a moved exe, the installed build taking the entry over and the uninstaller removing it; an entry whose exe is gone reads as off; the registry sits behind `IAutoStartStore` so tests never touch it |
| `Services/AppVersion.cs` | The csproj's `<Version>` and the `X-Client-Version: desktop/<version>` header `ApiClient` puts on every request |
| `Services/UpdatePolicy.cs`, `Services/UpdateService.cs` | **Pure** update decisions (when to look, the tray's version line and restart item) and the Velopack side: check GitHub Releases, download, hand a waiting release to the updater |
| `scripts/release-tag.ps1`, `scripts/release.ps1`, `.github/workflows/release.yml` | Bump `<Version>`, commit and tag; publish self-contained, check the files, `vpk pack` with release notes (and upload on a `v*` tag; a manual run is a dry run) — see "Releasing" |
| `.github/workflows/ci.yml` | Tests and the Release build on every push to `main` and every pull request, read-only; `release.yml` runs it first |
| `Services/ActivityTrackerService.cs` | Poll loop (foreground changes as they happen, plus a poll for the rest of the desktop), send loop (drains the outbox), lock/suspend/session-end handling |
| `Services/ActivityRecorder.cs` | **Pure** minute accounting: measured elapsed time, split at minute boundaries, gap cap, switch counts, the hold-back that idle backdating retracts from |
| `Services/ScreenState.cs` | **Pure** "what is on screen now": which observations a span is credited to, what counts as a switch, when idleness starts a backdate |
| `Services/WindowLimits.cs` | The server's validation rules, applied before a window is queued |
| `Services/WindowTitleNormalizer.cs` | Strips counters, unsaved markers and app-name suffixes from titles |
| `Services/OutboxStore.cs` | Finished windows as JSON files under `%LOCALAPPDATA%\…Desktop\outbox` until sent; a damaged one as `.json.bad` |
| `Services/ApiClient.cs` | Login, 2FA, token refresh, sign-out (and its revoke on the server), heartbeat (and its answer), the server-rules and mapping-coverage downloads, the start ramp's Start now and Not this one; `Classify` decides retry vs. drop per status code |
| `Services/RefreshPolicy.cs` | **Pure** refresh decisions: save / sign out / keep, whether an attempt left the token possibly rotated, how long to hold it back |
| `Services/TodayTotal.cs` | Today's recorded foreground time on this PC, for the tray glance; saved to `today.json` |
| `Services/PrivacyFilter.cs` | Recording-time privacy: local rules and private browser windows → keep, hide the title, or drop |
| `Services/TrackerRules.cs` | Send-time privacy: the user's server rules (in memory) and the filter that drops entries an ignore rule wins |
| `Services/Matching/` | **Port of the server's matcher** (`PatternMatcher`, `MappingRuleSet`) — see "Rule matching" below |
| `Services/HyperfocusPolicy.cs` | **Pure** nudge decisions: stretches, breaks, app stretches, snooze / repeat / "not now", the break timer, and the `BreakReport`s of a break the user started |
| `Services/BreakReporter.cs` | Hands the server the break the user started (`POST …/desktop/break`): the latest report only, retried, in memory; **pure** `Classify` and `IsWorthSending` |
| `Services/DriftPolicy.cs` | **Pure** drift-nudge decisions over the server's per-minute plan verdicts (`MinuteVerdict`) |
| `Services/StartRampPolicy.cs` | **Pure** start-ramp decisions: offer a planned block at its start, from the heartbeat's plan (`PlanView`) |
| `Services/ChimeSchedule.cs` | **Pure** time-chime decisions over the server's per-minute resolution, and which lines are chosen (`ChimeSettings`, `ChimeChoices`) |
| `Services/UploadHealthPolicy.cs` | **Pure** "is tracked time arriving?" decisions: signed out, stalled a day, windows the server refused → show / withdraw a notice |
| `Services/RecordingHealthPolicy.cs` | **Pure** "is recording itself working?" decisions: the poll failing, the poll loop silent, recordable time with nothing stored → show / withdraw a notice |
| `Services/UnmappedReviewPolicy.cs` | **Pure** evening-invite decisions: is the evening due / missed / handled, the invite's text from the server's answer; plus `UnmappedReviewStore` (the last handled evening, `unmapped-review.json`) |
| `Services/UnmappedReviewService.cs` | The send loop's evening tick: holds the server's evening moment, asks the server for today's unmapped time when it comes, hands the invite on |
| `Services/PauseKeepAlive.cs` | **Pure** when a paused tracker sends its empty heartbeat, and what that heartbeat is |
| `Services/PauseReminderPolicy.cs` | **Pure** "still paused" decisions: when a pause "until I resume" gets its "Tracking has been paused for 4 h" toast |
| `Services/QuietNudges.cs` | **Pure** "Quiet nudges for…": whether nudges are quiet by choice, when each choice ends, the tray's "Nudges quiet until 14:30" |
| `Services/CaptureQueue.cs`, `Services/CaptureDelivery.cs` | Quick capture: parked thoughts as JSON files under `%LOCALAPPDATA%\…Desktop\captures` (**pure** rules: trim, cut, what each answer means) and the send loop's tick that delivers them |
| `Services/CaptureFocus.cs`, `Services/HotkeyChord.cs` | **Pure** capture-box rules (where focus goes back, what the tracker credits while it is open, the plan line) and the hotkey as written in Settings ("Win+Alt+N") |
| `CaptureForm.cs`, `Native/GlobalHotkey.cs`, `Native/CaptureBoxHandles.cs` | The one-line capture box, the `RegisterHotKey` window that opens it, and the handles the poll loop reads to stand the box in for the work |
| `Services/DiagnosticsReport.cs` | **Pure** text of the tray's "Copy diagnostics" (state, settings as counts, scrubbed log tail) and the log-tail reader |
| `Services/NudgeService.cs` | Feeds polls to the policy, checks quiet conditions, shows and withdraws toasts, handles button presses |
| `Services/NudgeNotifier.cs`, `NudgeToast.cs` | Windows App SDK notifications: registration, toast text and buttons, parsing a press |
| `Native/` | P/Invoke (`Win32`, `MonitorHelper`), `ProcessInfoCache`, `AudioSessionHelper` (NAudio), `MicrophoneUsage` (registry: whether, and which apps), `ForegroundEvents` (the WinEvent hook) |
| `Models/` | Wire models (`ActivityModels.cs`, `TrackerRule.cs`, `ServerResolution.cs` — the heartbeat's answer, the coverage, today's unmapped time and the evening moment) and `AppConfig` (`%LOCALAPPDATA%\…Desktop\config.json`; `Load` answers a `ConfigLoad`, with `ConfigLimits`, the one range per number, and `LenientEnumConverter`) |
| `Services/JsonFile.cs` | Every state file's write (temp, flush to disk, move over) and read (read / missing / unreadable / could not open) |
| `AdhdTimeOrganizer.ActivityTracking.Desktop.Tests/` | xunit v3 tests, including `NativeSmokeTests` against the real machine |

## Building and testing — always through `scripts/test.ps1`

```powershell
./scripts/test.ps1                                   # build + run all tests
./scripts/test.ps1 -Class '*OutboxStoreTests'        # comma-separated, xunit wildcard syntax
./scripts/test.ps1 -NotTrait 'Needs=Desktop'         # what CI runs: everything a build runner can
./scripts/test.ps1 -BuildOnly                        # compile check, Debug and Release
```

`-Class`, `-Method` and `-Trait` include; `-NotClass` and `-NotTrait` exclude.

- It builds into `artifacts-cli/` (gitignored). The user usually has the tracker **running from
  `bin/`** or open in Rider, so a plain `dotnet build` fails with `MSB3021 … being used by another
  process` even though the code compiled.
- `dotnet test` is not used; the test project is an xunit v3 executable and the script runs it.
- **Tests never touch the user's real files, notifications or network.** Give every `AppConfig` a temp `FilePath`
  (anything that signs in, refreshes a token or turns a nudge off calls `Save()`); pass `ApiClient` a fake
  `HttpMessageHandler`; build `NudgeService` through its internal constructor with a fake `INudgeNotifier`, a
  browser stand-in and a temp drift-state path; put `OutboxStore` and `TodayTotal` in a temp folder.
  `NudgeServiceTests` and `SendLoopTests` show the shape.
- **Wait on something observable, never a fixed sleep.** To assert the send loop did *not* do something, wait for a whole
  pass since the request (`SendLoopTests.SendAndWaitForAWholePass`, on `ActivityTrackerService.SendPassesStarted` /
  `SendPassesFinished`). Pass `TestContext.Current.CancellationToken` to anything that takes a token (xUnit1051 is a
  warning, and warnings fail CI).
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
- **A refused heartbeat is a lost minute.** The tracker cannot repair a batch the server calls
  malformed: a 4xx other than 401/403/408/409/425/429 is dropped (`ApiClient.Classify`). So every server
  bound is mirrored in `WindowLimits` and applied before queuing: a non-blank process name (the entry is dropped),
  ≤ 60 s active + background per entry,
  ≤ 60 s active per window, ≤ 200 entries, column widths, the 2000-byte (process, title) index limit, and
  ≤ 120 switches per entry (clamped, not refused).
  When the server's `DesktopActivityHeartbeatValidator` or `TrackingIngestLimits` change, change
  `WindowLimits` with them.
- **Every request names the build**: `X-Client-Version: desktop/<csproj Version>`, set once on the `HttpClient`
  (`AppVersion`). The server keeps the last one per tracker kind with "last seen" and calls a silent tracker "out of date"
  below its configured minimum. Keep the `desktop/` prefix and a plain SemVer after it — the server parses it.
- **A paused tracker still says it's alive** (servers from 2026-11-05). The server calls a tracker silent after 48 h
  without a report, and a pause reports nothing. So while paused and signed in, the send loop posts a heartbeat with
  `entries: []` at most every 6 h (`PauseKeepAlive`), the same as the phone's empty sync; a window stored in that time
  counts instead. It is not a minute: it skips the outbox, and its outcome reaches neither `LastSendResult`,
  `RejectedWindows` nor today's total. A server from before it answers 400, which waits the full 6 h and costs nothing.
  The newest outcome is shown only in "Copy diagnostics" (`PauseKeepAlive.LastAnswer`), so a "went silent while
  paused" report says whether the keep-alive went out and whether the server was too old for it.
- **Never send null or a blank title.** Missing product names and paths go out as `""`, and an untitled
  window takes its product or process name (`ActivityRecorder`). The current server accepts nulls, but
  servers from before 2026-10-18 answer a null with a 500 and a blank title with a 400, and a user's
  tracker may meet either server.
- **Process names must stay identical to `Process.ProcessName`** (file name minus a trailing `.exe`).
  Users' mapping rules on the server match on them. Changing a process name or the title
  normalization changes which rules match and splits the distinct-entries grid. Do it deliberately and
  say so.
- **Rule matching: the server is authoritative; `Services/Matching/` is its one sanctioned port.** The
  tracker downloads the user's active rules (`GET /api/tracker/desktop/rules`, ETag → 304,
  at most every 5 min, memory only, forgotten on sign-out) and, just before sending a window, drops the
  entries whose winning rule ignores — so an ignored title never leaves the machine. It needs *every*
  rule, not only the ignore ones: first match wins after the specificity sort, so a narrower activity rule
  shadows a broad ignore rule. The heartbeat still applies the same rules (stale cache, old trackers).
  - **Must answer exactly what the server answers.** `Tests/DesktopMappingRuleSet.vectors.json` is a copy
    of the server's `AdhdTimeOrganizer.Tracking/domain/helper/mapping/DesktopMappingRuleSet.vectors.json`;
    both test suites resolve it and each fails when the other repo's copy differs. Matching changes start
    on the server: change it, its vectors, copy the file here, port. Never edit only this copy.
  - Resolve on what the heartbeat resolves on: a missing product or blank title is `""`, not null.
  - If nothing is known yet and the download fails, the send pass **waits** rather than upload unfiltered.
    A 404 (server older than the route) counts as "no rules": it filters on arrival, and waiting would
    hold the outbox forever.
  - The port only decides drop / keep. Which *activity* a window maps to comes from the server — see the
    next point. Never derive it from the port.
- **The heartbeat answers with what the minute resolved to** (servers from 2026-10-18; `Models/ServerResolution.cs`):
  the minute's active seconds by target, each with the server's `onPlan` verdict, and the planner blocks covering
  it (with `inProgress`); from 2026-10-19 also the next block today (`next`) and whether a timer runs (`timerRunning`).
  The tray's "now", the drift nudge and the start ramp read it, and nothing else may decide any of them. An older server answers a bare
  count: `ApiClient.ReadHeartbeatResponse` reads that (or anything unreadable) as "no answer" — the window is still
  sent, only the glance and the nudge go without. `target` stays a string so a target the server adds later can't fail
  the parse. `GET …/coverage` (404 = older server: no prompt) is fetched hourly, only while drift nudges are on.
  - **Both answers are pinned by `Tests/DesktopTrackerWire.samples.json`**, a copy of the server's
    `AdhdTimeOrganizer.Tracking/application/dto/response/desktop/DesktopTrackerWire.samples.json`, the same arrangement as
    the matcher vectors: the server checks it against its DTOs and live routes, `WireSampleTests` read it through
    these models, and each side fails when the other's copy differs. A wire change starts on the server.

- **Browser sign-in is the RFC 8252 loopback flow** (servers from 2026-10-19). The tracker listens on
  `http://127.0.0.1:<free port>/cb` and opens `<WebAppUrl>/connect/tracker?redirect_uri=…&code_challenge=…&state=…`.
  The user confirms there, the browser comes back with `?code=…&state=…`, and `POST /api/auth/tracker/token`
  `{ code, codeVerifier, redirectUri }` answers with the same token pair as `/auth/tracker/login`. The server
  accepts exactly that redirect shape: the IPv4 literal (never `localhost`), any port from 1024 up, path `/cb`,
  nothing after it. The challenge is S256 only, the state is 16–128 base64url characters, and a code lives a minute
  and is spent by its first exchange, even a failed one. Change `BrowserSignIn` and the server's `TrackerSignInRules`
  together. A 404 from the exchange means an older server: the password form still works there. A stray request — a
  favicon, a wrong state, a connection reset before its request arrived — is not ours and the wait goes on; only the
  listener's own socket failing ends the sign-in, with its own message (`BrowserSignIn.DescribeFailure`).
- **Tracking starts on `ApiClient.SignedIn`, not on the sign-in form's answer** (review L13). A password login or a code
  exchange takes no cancellation, so one still in flight when the form is closed may save a session afterwards; the event
  (raised by every sign-in that saves one, never by a refresh) is what the tray starts tracking on, posted to the UI thread.

### Time accounting
- All of it lives in `ActivityRecorder` and `ScreenState`, which have no clock and no Win32, so they're
  unit-testable. Keep it that way: the service passes `DateTime.UtcNow` and the measured elapsed time in.
- Credit **measured** elapsed time, never the configured interval. A gap longer than `MaxCreditedGap`
  (sleep, a hang) credits nothing.
- **The foreground is followed from events, everything else is polled.** `ForegroundEvents` (a WinEvent hook on
  foreground changes, and on title changes of the foreground process only) wakes the poll loop the moment the window
  in front changes; the poll (`PollIntervalMs`, 5 s by default) covers idleness, background windows, sound and the
  microphone. Each span is credited to what was on screen **during** it — the state set by the last change or poll —
  so a span ends exactly where the window changed. The hook needs the UI thread (see "Native code"); if it can't be
  set, the poll alone still records, only less precisely.
- The state holds at most one non-background observation, and each process (name + path) is observed at most once at
  a time — a window coming to the front drops its own background observation. That's what keeps entries within the
  60-second rules without clamping.
- **A switch** is the entry in front changing: another window, or another (normalized) title of the same one. It is
  counted once, in the minute it happened (`SwitchCount`, sent on every entry — a measured zero is not the server's
  "unknown"). Coming back to the same window from idle or a lock is not one; a window that may not be recorded still
  is something in front, so switching back from it counts.
- **Idle backdating.** Idleness is noticed only once the threshold has run out, and those seconds were credited as if
  someone were there. The first poll that finds the user gone retracts everything after the last input (or after the
  last poll that found them present by sound — passive presence stays credited). It works because credited time is
  **held back** for `IdleThresholdSeconds` + one credited gap before it is committed to its minute, so a minute window
  closes that long after its end, and nothing already queued or sent is ever touched. Flush (exit, suspend) commits
  early and can close two windows. A locked screen is not idleness and takes nothing back.
  - The hold-back is **capped at 50 s** (`MaxHoldBack`): the tray's "now" and the drift nudge only use a minute
    younger than `DriftPolicy.FreshFor`, and a later window would arrive too old for them. A longer idle threshold is
    backdated only that far. Lengthen one and you must re-check the other. Settings still offers up to 600 s (reading without
    input is a reason for a long one) and its hint says the real figure — the cap less one credited gap, 35 s at the default
    poll (`ActivityTrackerService.FullyTakenBackSeconds`, `SettingsForm.IdleHint`; review L3, decided 2026-11-23).
- Idle = no input for `IdleThresholdSeconds`, unless the **foreground** window is audibly playing
  (`IsPassivelyPresent`), up to `MaxPassiveIdleMinutes`. Fullscreen alone deliberately doesn't count:
  a paused fullscreen video looks like a watched one. A locked session records nothing.

### Durability and threads
- Every finished window goes to the **outbox on disk** before anything tries to send it. Exit, suspend,
  logoff, session end and **pause** call `ActivityTrackerService.Flush()` (pause after giving the poll loop up to 1 s to
  finish its poll). Restarting the poll loop for a new poll interval (`Stop`, then `Start`) does not flush: recording
  carries on. Don't add an in-memory-only queue.
- **Every state file goes through `JsonFile`** — config, outbox, captures, `today.json`, the nudges' and the evening
  review's state. `Write` writes `.tmp`, **flushes it to the disk**, then moves it over: without the flush a power cut can
  leave a zero-length file. Don't hand-write "temp, then move" again. `TryRead` tells **Missing**, **Unreadable** (empty,
  cut short, not JSON, `null`) and **CouldNotOpen** (locked, denied) apart: only an unreadable file may be set aside; one
  that could not be opened is left for the next try.
- **The outbox keeps what it can't read.** `OutboxStore.Pending` renames a file that isn't a window to `.json.bad` (logged
  once, by the rename; out of every pass and the count) and deletes `.bad` files once they are older than the outbox
  retention, by last write. A file it can't open is skipped and logged once, and goes on a later pass. `Save` sets a
  damaged copy of the same minute aside before writing, and never writes over one it can't open: it throws, and the poll
  loop logs the minute as lost.
- **Snapshots taken under a lock and written outside it carry their order.** `NudgeService.Evaluate` numbers each
  evaluation's drift / ramp / chime snapshots under `_lock`, and `OrderedStateFiles` drops one older than what its file
  already holds — a poll and a button press could otherwise write the older state last.
- **`config.json` never falls back to defaults silently** (`AppConfig.Load(path)` → `ConfigLoad`): the tray's first act
  without a session is a save, which used to overwrite a damaged file — rules, pause, chime choices and all. A file that
  loads is copied to `config.json.bak`. A damaged one goes to `config.json.bad` (one kept), then `.bak` is used, **without its
  refresh token** — it has been rotated since, and the server answers a rotated token by revoking every session of the
  account — then defaults with `HideAllTitlesUntilReviewed` (see "Privacy before upload"); either is saved at once. One that
  can't be opened (after ~2 s of retries) gets the same fall-back but is neither moved nor saved over. The tray says what
  happened once, in a balloon, in counts (`ConfigLoad.Notice`).
- **A config value this build doesn't know costs that value, not the file.** Config enums use `LenientEnumConverter`
  (writes the member name like `JsonStringEnumConverter`; an unknown name reads as an undefined value), and
  `AppConfig.Normalize` resets those, `null` lists and blank URLs to their defaults and clamps each number to its
  `ConfigLimits` range — the same ranges the Settings form offers. A new config enum or number gets both. The file format
  stays what 1.2.1 reads: a new field is written only when it differs from its default (`WhenWritingDefault`), unless an
  older build ignoring it is harmless.
- The send loop runs for the app's lifetime, independent of pausing, and stops a pass at the first
  window that has to wait (offline, signed out, server rules not known yet). The poll loop stops when
  tracking is paused.
- **Today's total (`TodayTotal`) counts a window when it is queued**, so unsent time is in it, and takes back
  what never got stored (entries a server rule drops, a window the server refuses) only once the outbox reports the window settled (`OutboxStore.Remove` returns true): a
  window rewritten in flight goes again as a whole, and subtracting on both sends would count its drops twice.
- **Never touch WinForms objects from the poll or send loop.** The tray pulls `GetStatus()` on a UI
  timer. A status event that set `NotifyIcon.Text` from the poll thread once threw out of the loop's own
  catch and silently ended tracking. (`NotifyIcon.Text` also throws past 127 characters, so it's
  truncated.)
- A session ends only when the server refuses the refresh token (400/401/403). Being offline, a throttle,
  a 409 or a 5xx keeps it. While signed out, tracking continues into the outbox. **At start, with a saved token, recording
  starts at once and the session is restored in the background** (`SessionStartup`, review L8): recording needs no network,
  and a restore on a network that swallows the request takes the 30 s HTTP timeout. The sign-in form comes only after the
  restore says there is no session. The decisions are the
  **pure** `Services/RefreshPolicy.cs`; `ApiClient` only sends and keeps the clock.
- **Signing out revokes the token on the server, best-effort** (review M5). `ApiClient.SignOut` clears the session at once
  and then, in the background, posts the refresh token to `POST /api/auth/tracker/logout` with the access token held. A 401
  (the access token expired through a pause) or no access token yet gets one refresh — that pair is used for the revoke
  only, never saved — and one more logout. 10 s for all of it, no retry; a 404 is a server from before the route. A changed
  server URL in Settings revokes against the old URL. When the server itself refused the token nothing is sent
  (`EndSession`). The tray never waits for it.
- **A refresh whose answer may have been lost is not sent again for 6 s** (`RefreshPolicy.ReplayDelay`). That
  covers a refresh that failed after it was sent, a timeout, a 409, a 5xx or a 2xx without both tokens; a
  request that never left (DNS, connect, TLS) can go again at once. The server rotates on every refresh. For a
  token client it exchanges a replay of the rotated token again when the successor was never used and the
  latest rotation is 5 s – 72 h old (`RefreshTokenService`, lost-response recovery). Inside the 5 s it answers
  409, "rotated moments ago" — a server from before 409 answers 401 there, which ends the session — and any
  caller (send loop, rules, coverage) could be the one replaying. While held back, a refresh reports
  `Unavailable` without sending. A rotated pair is saved (`config.Save()`) before it is used.
- **Exit doesn't abandon a refresh on the wire.** Once sent, it may finish for `ApiClient.ExitGrace` (3 s) after
  its caller is cancelled, and `ActivityTrackerService.Dispose` waits longer than that for the send loop.
  Abandoning it throws away the pair the server already rotated to.
- **Deploy the server before a client build that relies on the recovery.** A server without it revokes every
  session of the account on a replay of a rotated token, so against one a lost answer still signs everything out.

### Native code
- `[LibraryImport]` with `StringMarshalling.Utf16` for anything taking `char[]`. Enumeration callbacks
  are `[UnmanagedCallersOnly]` and only append a handle. Do the managed work after the enumeration
  returns.
- Process info comes from `ProcessInfoCache` (`QueryFullProcessImageName` on a limited handle, keyed by
  pid + creation time). `Process.MainModule` fails on elevated processes and is slow, so don't
  reintroduce it.
- A wrong P/Invoke signature compiles and fails silently at runtime. Add a case to `NativeSmokeTests`
  for any new native call. Give it `[Trait("Needs", "Desktop")]` if it needs a signed-in desktop (a monitor, an audio
  device, a hotkey, a WinEvent hook): CI runs every case without that trait, on a runner that has none of those.
- **`ForegroundEvents` is installed and removed on the UI thread** (`Start`, `Stop`, `Dispose`): an out-of-context
  WinEvent hook is delivered through the setting thread's message loop, and only that thread may remove it. The
  callback is `[UnmanagedCallersOnly]` and static (WinEvent callbacks carry no context), stamps a `Stopwatch`
  timestamp, queues, and returns; the one other thing it does is move the title-change hook to the new foreground
  process — hooked system-wide, every accessible-name change on the machine would be a message on the UI thread.
- A packaged app is named in the microphone's consent store by its **package family name**, read per process with
  `GetPackageFamilyName` on the limited handle (`ProcessInfo.PackageFamilyName`); a desktop app by its path.

### Nudges and notifications
- Nudges are **client-side**: their decisions never involve the server. A nudge sends three things: the start ramp's
  Start now and Not this one (below), and the break the user started (the next point). The policies live in `HyperfocusPolicy`, `DriftPolicy`, `StartRampPolicy` and `ChimeSchedule`, pure like
  `ActivityRecorder` — keep clocks, Win32 and toasts out of them.
- **A break the user started is said where they are** (prompt 10, servers from 2026-11-10). "Taking a break" starts the
  timer (`BreakTimerMinutes`). The phone may be where the break is spent, so:
  - **Back at the desk ends it, silently.** A poll that finds the user present, after one found them away (or a gap the
    poll loop didn't see), ends the break with no break-over toast and starts a new stretch. This holds before the timer's
    end and after it.
  - **At the timer's end the toast shows only to someone at the desk.** Someone away is the phone's to tell, so the toast
    is held. Their return ends the break without it. The break timer is not re-armed for a held break: the next poll decides.
  - **Reports.** Each change leaves a `BreakReport` (start, planned end, end) for `TakeBreakReport`: the press, "5 more
    minutes" (a later end), "I'm back", and the return. The toast itself is not an end, because "5 more minutes" may still
    follow.
  - **Delivery.** `NudgeService` hands each report to `BreakReporter`, which sends `POST /api/tracker/desktop/break`
    (`BreakRequest`, pinned as `breakRequest` in `DesktopTrackerWire.samples.json`). It keeps only the latest report, in
    memory, and retries it each minute (offline, 5xx, 401, 429). It drops the report on a 404 (an older server), a 403 or a
    400, and lets it go unsent 2 h past the planned end. It never touches the outbox or the heartbeat, so a server without
    the route costs one 404 per break and no minute.
  - **What is never reported.** A break the poll loop only *detects* (away for `BreakMinutes`) is not reported: the server
    reads it off the ledger. The tray's test toast makes believe a break ran and reports nothing.
  - The phone's side (android `BreakPolicy`) speaks only while the desk is idle. Decisions D10-1 to D10-3 are in the server
    repo's `review/androidTrackerPrompts/decisions.md`.
- **Drift nudges** are fed from the **send** loop, a minute at a time, with the server's verdicts; the policy only
  counts them. Off by default, ≥ 10 min, one per planned block ("Snooze" asks for exactly one more after 30 min).
  Unmapped and ignored minutes neither count nor reset — never treat "unmapped" as off-plan. A minute older than 3
  minutes when sent (an outbox backlog) is skipped: it says nothing about now. Toast ids are counted per policy, so a
  toast carries its `kind` and `NudgeService` routes a press by kind first; "Turn off" switches off only the kind it
  was pressed on. "Switch plan" opens `WebAppUrl` + `/day-planner` — `WebAppUrl` is its own setting because in
  development the web app is not on the API's origin.
- The drift policy's state is saved to `drift.json` after each minute and each answer, so a restart mid-block neither
  repeats the block's nudge nor loses the streak or a snooze. The hyperfocus policy keeps no such file.
- **The start ramp** (`StartRampPolicy`, `NudgeKind.StartRamp`, on by default, its own Settings switch
  `StartRampEnabled`) helps with *starting*.
  - **When.** At a planned block's start, while someone is at the computer (a poll found them present within the last
    minute): "Thesis starts now (until 11:30) · 45 min planned. Start the timer?". It is fed by the send loop with the
    heartbeat's plan (`PlanView`: `planned`, `next`, `timerRunning`, the minute's verdict), trusted for 3 minutes like a
    drift minute; it fires from the poll loop.
  - **Which blocks.** `StartRampBlocks`, a Settings choice: Every block / All but Optional (default) / Critical only, from
    the heartbeat's `isOptional` / `isCritical` (server-computed, the phone's rule and default, since 2026-11-08). A block left
    out isn't settled, so widening the choice mid-block asks about it. A server without the flags reads every block as
    neither, so "Critical only" stays quiet against it.
  - **Late.** "Started 20 min ago", but only while ≥ 10 min are left; a block nearly over is settled without a word.
  - **Skipped** when the block is `inProgress` or the minute was on plan. **Held** while a countdown, pomodoro or
    stopwatch runs (`timerRunning`; a paused one has no alarm rows, so the server can't see it), under the quiet conditions,
    and within the gap to the other families. A server from before 2026-10-19 sends no `timerRunning`, so the ramp stays
    quiet against it.
  - **Asks once per block.** A *reminder* toast (stays until answered).
    - **Start now** does two things side by side. It calls `POST …/tracker/desktop/start-block` (the block goes in progress
      in the planner, so the start counts even if the browser never gets there). It also opens
      `WebAppUrl/?track=<plannerTaskId>`, which the web app's home turns into the block's countdown, already running. The
      timer itself lives only in the browser: there is no server timer to start.
    - **In 2 minutes** asks exactly once more, without that button.
    - **Not this one** also silences the drift nudge for that block (`DriftPolicy.QuietBlock`, saved in `drift.json`):
      after an explicit decision, "you slid off it" would only shame. For the same reason it calls
      `POST …/tracker/desktop/skip-block` (not started → cancelled, the phone push's Skip), or the planner would announce the
      block as missed when it ends. It isn't retried: offline, the local half stands and the block stays open on the server.
    - A toast still out leaves when the block is started, worked on, or over (at its latest end, if extended), and when a
      minute inside the block no longer lists it (cancelled or moved, in the planner or by the phone's Not this one). Another
      block's toast replaces it.
  - **State.** Saved to `start-ramp.json` beside `drift.json`: settled blocks by id, kept a day past their end (an
    extended block keeps its id), and a pending "In 2 minutes". A restart neither asks twice nor forgets the repeat.
- **The time chime** (`ChimeSchedule`, `NudgeKind.TimeChime`, off by default, `TimeChimeEnabled`) makes time felt
  *outside* a plan: "15 min on YouTube", silently, at each interval of continuous time in a chosen activity.
  - **What is chosen is the server's answer, never a process list** — YouTube in Chrome chimes, docs in Chrome don't. It
    reads each minute's `resolution` (which `MinuteVerdict.Slices` carries from the send loop, so the chime needs no send-loop
    code of its own), keyed like today's breakdown (`activity:<id>`, `label:<role · category>`; a renamed role or category
    is a new key). Chosen = the server's `isDistraction` (servers from 2026-11-08; `TimeChimeDistractions`, on by default)
    plus `TimeChimeChoices` ticked by hand, minus distractions unticked by hand. A choice that only repeats the default is
    dropped (`ChimeChoices.With`), so a flag set later on the server still decides every line the user never touched.
    Unmapped time can't be chosen. Picked from the tray's today submenu ("Chime every 15 min", which also turns the chime
    on) or in Settings (today's lines, the known distractions, earlier choices).
  - **The stretch.** The chosen activities are one stretch, named by the busiest of them in the latest minute. A minute
    counts when chosen time holds at least half of its tracked time. A gap over 3 min (another activity, idle, locked)
    starts it over; a shorter one only pauses it. A minute older than `FreshFor` when sent is skipped, like drift's.
  - **When.** At each multiple of `TimeChimeMinutes` (5/10/15/20/30, default 15), the same interval every time — no
    escalation. A mark is passed once: one due under a quiet condition is **skipped, not held** (a late "15 min" is wrong),
    and one inside a planned block while drift nudges are on is passed silently — one voice per block — though the minutes
    still count. "Stop for today" lasts to local midnight; the stretch keeps counting through it.
  - **Not in the gap between families**: it holds none of them and none holds it (a chime held 10 min says the wrong
    number). It only waits for an evaluation in which no other toast was shown — the next poll.
  - **The toast**: `MuteAudio`, the short duration, `Expiration` 2 min (`ChimeSchedule.ToastLifetime`), one button. The
    next chime replaces it by its tag. State in `time-chime.json` beside `drift.json`, so a restart neither repeats a mark
    nor forgets the stop; it also keeps the keys the server last flagged, which the tray and Settings show as chimed.
    Logs carry minutes only; diagnostics count the choices and never name them.
  - The phone's own chime (android prompt 11) made the same choices; its decisions are in the server repo's
    `review/androidTrackerPrompts/decisions.md` (11-2, 11-3, 11-5, 11-6, 11-7).
- The three families keep `HyperfocusPolicy.MinGapBetweenKinds` (10 min) apart: a nudge due within it of another
  family's is held like under a quiet condition, never dropped by it. The start ramp is evaluated first (a block's start
  doesn't come again). The break-over nudge and the time chime are exempt.
- Today's total and its top-5 breakdown (`TodayTotal`, `today.json`) count a window **once, when settled**: the total
  at queue time less what was never stored, the breakdown from the heartbeat's answer. The breakdown is the tray
  glance's submenu, not a left-click flyout — a single click is half of every double-click, which opens the menu.
- **Upload notices are not nudges** (`UploadHealthPolicy`, toast kind `NudgeKind.UploadHealth`, switch
  `UploadNoticesEnabled`). Three problems, one toast at a time, in this order: signed out (minutes waiting for 10 min —
  it replaced the old "signed out" balloon), stalled (the **oldest unsent minute** is a day old and stays so for 10 min),
  dropped (3+ windows refused since the last notice). A stall is measured by `OutboxStore.Summary().OldestStartUtc`, never
  by `LastSentUtc`: the outbox is on disk, "last sent" is not. The 10 min (`StallGrace`) are for a PC waking from a long
  sleep: its backlog is days old before anything has tried to send it. They are fed by the tray's 2 s timer (`NudgeService.OnUploadStatus`), not
  the poll loop, so they keep running through a pause — the policy itself shows nothing while paused. No quiet
  conditions (Windows holds toasts itself). Once per episode, again at most daily, withdrawn when the condition clears;
  a dropped-windows toast is not withdrawn (those minutes are gone) and leaves on exit. **Why minutes wait** comes from the
  last heartbeat's status code (`TrackerStatus.LastStatusCode`, `UploadHealthPolicy.WhyWaiting`): "offline" only when there was
  no answer; a 403 reads "the server refuses this account (403)" in the tray and in the stalled toast, which says "Check the
  connection" only when offline (review L2). State is in memory: a restart
  while still stalled shows the notice again. Counts and durations only in the text and the log. "Turn off" on one
  switches off only these, and a nudge's "Turn off" leaves them on.
- **Recording notices** (prompt 25; `RecordingHealthPolicy`, toast kind `NudgeKind.RecordingHealth`, the same switch
  `UploadNoticesEnabled`). The upload notices watch the send side only; a poll loop failing on every pass showed nothing but
  "Error:" in the tooltip, and a dead poll loop once ended tracking without a word. Three problems, one toast at a time, in
  this order: **the poll failing** for 10 min (T; `TrackerStatus.PollFailingSinceUtc`, set by the first failed pass and
  cleared only by a poll that completes — a pass that only handled a window switch proves nothing); **the poll loop silent**,
  no poll completed for 5 min with no failure to say why, held 1 min more (a PC waking finds the last poll hours old until the
  loop's next pass; `LastPollUtc`, stamped at start too); **nothing stored** — 30 min (U) of *recordable* time with no window
  queued (`UnrecordedFor`: time `Credit` gave to a non-empty observation list within the credited gap, reset by `Queue`). Only
  what the user's own local rules let through counts, so someone working in ignored apps never trips it; server rules drop
  windows after they are queued and don't matter (T, U and this decided 2026-11-23). Fed by the tray's 2 s timer with the
  idle time read there (`Win32.GetIdleTime`), never from the poll loop, whose health is the question; shown only to someone at
  the computer (input within a minute, unlocked), nothing while paused, no quiet conditions. Once per episode, again at most
  daily, withdrawn when it clears; its own toast tag, so it never replaces an upload notice. **Open log folder** and **Turn
  off these notices** (which removes both kinds). Durations only. "Copy diagnostics" says how long the poll has been failing,
  when it last completed and the unstored minutes — never the error's text, which can carry a path.
- **A paused tracker must look paused** (prompt 16, review M1). The installed tracker's log had nine "until I resume"
  pauses in two days, every one undone within seconds: double-click was being used as "open".
  - **Double-click never stops recording.** Signed out it signs in; paused it resumes (the start-up balloon says so);
    otherwise it opens the right-click menu, through `NotifyIcon`'s private `ShowContextMenu` so a click elsewhere closes
    it (`TrayStateTests` pins that the method exists; without it a plain `Show` still opens the menu). "Pause Tracking" in
    the menu still pauses until resumed.
  - **The icon shows the state** (`TrayIconStates.For`): paused wins; then signed out or a poll error is "attention" — signed
    out still records, so it must not look paused; locked is normal. Three icons, drawn once (`TrayIcons`, each a PNG
    inside a one-image ICO so the `Icon` owns its handle), swapped only by `RefreshStatus` when the state changes,
    disposed on exit after the `NotifyIcon`.
  - **"Still paused" reminder** (`PauseReminderPolicy`, toast kind `NudgeKind.PauseReminder`, switch `PauseRemindersEnabled`,
    on by default). Only for a pause "until I resume" — a timed one ends by itself. "Tracking has been paused for 4 h"
    once it is 3 h old, then at most every 6 h, with **Resume** and **Keep paused** (which only closes it). Each waits for
    input within the last minute (`Win32.GetIdleTime`, passed in): a toast to an empty desk is buried by morning, so there
    is no night window. Fed by the tray's 2 s timer (`NudgeService.OnPauseStatus`) because the poll loop is stopped; not a
    nudge, so no quiet conditions. Withdrawn when the pause ends, turns timed, or the switch goes off.
  - **Its age outlives a restart**: `Pause` saves `PausedSinceUtc` beside `PausedUntilUtc` (a new end for a pause in force
    keeps it), `Resume` clears both. A config from before it has none, and the pause counts from when the policy first
    sees it. The reminder's own clock is in memory; the start-up "paused" balloon counts as a reminder
    (`OnPauseAnnounced`), so a restart doesn't add a toast. "Copy diagnostics" says `Pause reminders: on/off` and how long
    the pause has lasted.
- **"Quiet nudges for…"** (prompt 25; `QuietNudges`, `AppConfig.NudgesQuietUntilUtc`): 30 min, 1 h, until tomorrow morning
  (the next 06:00, the same as a pause's) or until turned back on — toasts off, **recording on**, where a pause would lose the
  hour's record. Saved like `PausedUntilUtc` (written only while set), so a restart keeps it; the tray's timer clears one that
  has run out. It is one more quiet condition, the first `NudgeService.IsQuiet` asks: a due ramp, hyperfocus or drift nudge is
  held and shows when the quiet ends **only if still true** — each family re-checks itself (stretch still running, still off
  plan in a block not over, ≥ 10 min of the block left), so there is no staleness cut-off (decided 2026-11-23). A chime mark is
  skipped. The **evening review is held** too, inside its own 3 h window, and missed past it (decided 2026-11-23). The
  break-over toast, the upload and recording notices and the "still paused" reminder ignore it. The tray says it in the status
  line and tooltip ("Tracking · nudges quiet until 14:30") and the submenu's title; **no icon of its own** — prompt 16 kept the
  icon to three looks, a fourth not being told apart at 16 px. "Copy diagnostics" says it on the "Quiet while" line.
- Nudges are fed by the poll loop, so pausing tracking pauses them. A paused, asleep or hung gap as long as a
  break counts as a break.
- A call (any app holding the microphone) counts as presence **for nudges only**. It must not change what
  the tracker credits; passive presence stays the only idle exception there. What the tracker does with a call is
  mark it: the mic-holding app's entry carries `IsInCall`, and the app is observed as a background window (like an
  audio-playing one) so a muted, hidden call app still has an entry to carry it. It is credited active time only while
  it was really in front.
- Quiet conditions hold a due nudge until they end; they never drop it. So does absence for the stretch and app-stretch
  nudges: one due while the last poll found nobody waits for the first poll that finds the user back (review L7). "Full screen" means
  `SHQueryUserNotificationState`, Windows' own definition. A rectangle check also catches a maximised
  window behind an auto-hiding taskbar, and the desktop. The break-over nudge ignores quiet conditions
  and the off switch, because the user set it. It does not ignore absence: it shows only to someone at the desk (prompt 10).
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

### The evening review of unmapped time
- **Why:** drift, the tray's "now" and History read the server's mapping, and time no rule maps is invisible to all of
  them. Once, at the user's evening, one toast: how much of today on this PC is unmapped and the top two apps, with
  **Review** (opens the web app's evening review page, `/activity-tracking/desktop/review?date=<the evening's day>`:
  one card per unmapped app, busiest first, answered Map / Ignore / Skip; the rule grid is its "Edit rules" link — the
  page since 2026-11-10, the distinct-entries grid before) and **Not
  today**. `NudgeKind.UnmappedReview` shares the toast plumbing but is not a nudge: no quiet conditions (Windows holds
  toasts itself), its own Settings switch (`UnmappedReviewEnabled`, on by default), independent of drift nudges.
- **The tracker decides nothing about mapping.** The evening moment is the server's (`GET /api/tracker/evening-shutdown-moment`,
  the same instant the phone uses and the evening-shutdown push sweeps with); what is unmapped, which apps are worth
  naming and whether to ask at all is `GET …/desktop/unmapped`'s, and **an empty `apps` list is the server's "don't
  ask"** — no threshold is repeated here. The review itself is the web app's: the tracker's token must not write rules, and a
  browser (`isBrowser`) is mapped by title there, not by process.
- **One look per evening.** `UnmappedReviewStore` keeps the last handled moment, marked whether the evening ended in an
  invite or in nothing to say, so neither a restart nor a second pass asks again. A moment more than 3 h old
  (`UnmappedReviewPolicy.Window`) is missed, not late. **The server names the evening just gone beside the next**
  (`previousMomentUtc`): once a moment passes it answers tomorrow's as the next, so without it a PC that slept through
  22:00 — or only refreshed after it — lost tonight's invite. `UnmappedReviewPolicy.Pick` takes the previous one while it
  is due, else the next. **Each evening carries its own day** (`date`): bed at 01:30 sends at 00:30 the next date, so
  `unmapped?date=` and Review's `?date=` name the day that is ending. A server from before these fields sends neither,
  and the tracker behaves as it did. Stays quiet while tracking is paused, while nudges are quiet by choice (held, see
  "Quiet nudges for…") and while signed out; a server
  from before the routes ends it for the session; a failed read is retried in 5 min, not every pass.
- The toast names **apps** (product or process name), never a window title, and the log carries counts only.
- Both answers are pinned by `DesktopTrackerWire.samples.json` (`unmapped`, `eveningMoment`) like the others. The tray's
  "Map apps for drift nudges…" stays: it is the week's view, tied to drift nudges, and opens the distinct-entries grid.

### Quick capture
- **Why:** a stray thought mid-focus either derails the block or is lost. **Win+Alt+N** (the default, `HotkeyChord.Default`;
  `AppConfig.CaptureHotkey`, empty = off), the tray's "Capture a thought…" or a drift toast's **Capture a thought** opens a
  one-line box: Enter parks it, Esc cancels, and the keyboard goes back to the window that had it. The thought becomes a
  to-do on the user's capture list ("Inbox" unless the web app's To-do settings point elsewhere).
- **The route is shared with the phone**: `POST /api/tracker/capture` (servers from 2026-11-08), `{ "text": … }` only — the
  phone's share sheet also sends `url`, the desktop never has one. Pinned by `captureRequest` / `captured` in
  `DesktopTrackerWire.samples.json` like the other answers. The server makes the item (`ITodoCaptureSink`); the tracker reads
  no to-dos and decides nothing about them.
- **Never lost, never logged.** The box hands the text to `ActivityTrackerService.Capture`, which writes a file to
  `CaptureQueue` before anything sends it; paused, offline and signed out it waits there. The send loop delivers captures
  first in each pass (`CaptureDelivery`), oldest first, stopping at the first that must wait. Only a 2xx removes a file; a
  400/413/422 keeps it as refused and never sends it again, and the tray shows a count of them in an item of its own ("1 thought
  the server refused — open folder", which opens `captures\`); an unreadable file is skipped, **never deleted or renamed** (unlike the
  outbox's, which goes to `.bad` — it may be the only copy of a thought), and logged once until it changes. The tray reads the
  queue's `Summary` every 2 s on the UI thread, so `CaptureQueue` keeps each file as last read and parses it again only when its
  write time or length changed — the user may delete or mend files in the folder. A 404 (a server from before the route) or 409 `noPriority` holds the
  whole queue `CaptureQueue.HoldFor` (30 min), and the tray item says why; a new capture asks at once. Text is cut to the
  server's 10 000 characters before queuing. Logs carry capture ids and lengths, **never the text** — and not the server's
  answer body either, which can quote it.
- **The box is not a window the user went to.** `CaptureForm` is a tool window (no taskbar button, not in Alt-Tab, and so
  never one of `MonitorHelper`'s background windows). Before it can come to the front it publishes itself and the window it
  returns to in `CaptureBoxHandles`, and the poll loop credits that window instead (`ForegroundAsCredited` →
  `CaptureFocus.StandIn`): parking a thought is neither a switch nor time of its own. With nowhere to return to it credits
  nothing in front, like the tray's own menu.
- **Focus.** The return target is the foreground at the hotkey or toast press, else the newest of the last four other
  processes' foreground windows that is still a real window (`ForegroundEvents.RecentForeignForegrounds`, filled by the
  WinEvent hook — a tray click's own foreground is the taskbar and then the menu; empty while paused). Windows lets a
  process take the foreground only right after the user spoke to it: the hotkey does, a toast button may not, so
  `Win32.BringToFront` falls back to borrowing the front thread's input (`AttachThreadInput`) for the one call. Clicking away
  from an empty box closes it; from a typed one, it stays. Exit parks what was typed.
- **The hotkey** is `Native/GlobalHotkey`, a message-only window created, registered and disposed **on the UI thread** like
  `ForegroundEvents` (`WM_HOTKEY` comes through its message loop). A chord another app holds is `Taken`: said in the tray
  item ("Win+Alt+N is used by another app") and, at start or after a change in Settings, a balloon. `NativeSmokeTests` pins
  the registration, the "taken" answer, and that the default is free on the machine the tests run on.
- **The plan line** ("Thesis until 11:30 — park it and get back") comes from the newest resolved minute while it is
  `DriftPolicy.FreshFor` old at most — a started block before a merely planned one — and is absent otherwise.
- **The drift toast's fifth button** (the most a toast takes) opens the box and settles the block like Back to it
  (`NudgeResponse.Capture`).

### Privacy before upload
- Two layers. **At recording** (`PrivacyFilter`, poll loop): the user's `LocalRules` (stored under the old
  `IgnoreRules` key — old configs read unchanged) and private browser windows. Ignore beats hide-title. A
  hidden title becomes `"<product or process> - PRIVATE"` or the window is dropped
  (`PrivateWindowHandling`); process and product stay, so process-level server rules still map the time.
  The raw title goes no further than `ToSample`, so it never reaches the outbox. **At sending**
  (`TrackerRules`, send loop): the server's rules, as above.
- A local **process** pattern loses a trailing `.exe` (`PrivacyFilter.WithoutExeSuffix`), on save in the Privacy form and on
  load: process names are `Process.ProcessName`, so `chrome.exe` matched nothing (review L4). Exact, Contains and Wildcard
  only, and never down to bare wildcards (`*.exe` stays); a Regex is left as written.
- A local title rule matches the raw title **and** the normalized one: normalization strips the browser
  suffix, and with it a localized private-browsing marker.
- Private windows are recognised by title only: Firefox ends it with "Private Browsing", Edge writes
  "[InPrivate]". Chrome Incognito has no title marker and is not detected. A localized Firefox needs a
  hide-title rule on its own wording.
- `LocalRules` is **replaced, never mutated**: the poll loop iterates it on its own thread. That is also
  why edits apply from the next poll without a restart.
- **Lost rules hide more, never less.** When the rules can't be recovered (a damaged config and no usable `.bak`),
  `HideAllTitlesUntilReviewed` makes every window a hidden-title one — ignore rules still drop theirs — until the user saves
  the Privacy form, which says why at the top. It is saved (so a restart doesn't lift it) and written only while true. A rule
  with an unknown action becomes Ignore; one with an unknown match type loses that condition (process or title), so it
  matches more; one left naming nothing turns on `HideAllTitlesUntilReviewed`.
- A pause (`PausedUntilUtc`, `DateTime.MaxValue` = until resumed, and its start `PausedSinceUtc`) is saved, so a restart
  in the middle of one doesn't start recording early. The tray's timer resumes a timed one.

### Privacy and logging
- No email, name or other personal data in logs. Log files outlive an account deletion. Ids, process
  names and status codes are fine. Window titles and paths are personal; don't log them either. A server's answer body
  can quote them: the heartbeat logs only a refusal's, cut to 300 characters, never a 5xx's (`ApiClient.BodyWorthLogging`);
  the other routes log none.
- "Copy diagnostics" (tray) goes into bug reports, so `DiagnosticsReport` reads `AppConfig` field by field, never
  serializes it: no token, rules as counts, the poll error as how long it has failed and "see the log", and the log tail has the profile folder and
  e-mail-looking text masked. A new config field stays out of the report until someone decides it is safe;
  `DiagnosticsReportTests` plants a token, rule patterns and a title and asserts none comes out. The logger holds today's
  file open, so the tail is read with `FileShare.ReadWrite`.
- The refresh token is DPAPI-encrypted in `config.json` (`ProtectedRefreshToken`). Never write it in plain
  text.

## Releasing

The tracker is distributed with **Velopack**: a `Setup.exe` that installs per user without admin rights, and quiet
updates from this repo's **GitHub Releases** (public, so no token ships in the app).

**Cutting a release.**
1. Deploy the server first if the release relies on a server change (as with the refresh recovery above).
2. `./scripts/release-tag.ps1 -Bump patch` (or `minor` / `major`, or `-Version x.y.z`; `-DryRun` only checks). On a clean
   `main` it runs the full test suite, raises `<Version>` in the app's csproj, commits `release: <version>` and makes the
   annotated tag `v<version>`. It refuses anything but plain `x.y.z` higher than the current version (Velopack only offers a
   higher one; the server parses the header) and a tag that already exists here or on origin. It pushes nothing.
3. `git push origin main`, then `git push origin v<version>`. The tag push runs `.github/workflows/release.yml`: first the
   CI job (`ci.yml`: every test but the `NativeSmokeTests` cases that need a signed-in desktop, `-NotTrait 'Needs=Desktop'`,
   and the Release build), then, only if it passes, `scripts/release.ps1 -Upload` in a job of its own — the only one
   holding `contents: write` — with the workflow's own `GITHUB_TOKEN`, which refuses a tag that disagrees with the csproj.
   The GitHub release's text is the commit subjects since the previous `v*` tag (the `release:` commit left out).
4. Installed trackers find it within 6 h (`UpdatePolicy.CheckEvery`), or 2 min after their next start, download it quietly,
   and show "Restart to update" in the tray; otherwise it applies at the next start.
5. To make the server's tracker-silence notification say "out of date" for older builds, set the server's
   `TRACKER_MINIMUM_CLIENT_VERSION_DESKTOP` env var (`TrackerMinimumClientVersion:Desktop`). It is advisory only: nothing
   is refused. Not on every release — give installed trackers a few days to pick the new one up first.

**Trying the pipeline without publishing:** Actions → Release → Run workflow (on `main`). It runs the same tests and pack,
uploads nothing (`release.ps1` refuses `-Upload` off a tag in CI), and keeps the packages as the `release-dry-run` artifact
for 3 days. The button appears once the workflow with `workflow_dispatch` is on GitHub's `main`.

**Every push to `main` and every pull request** runs `ci.yml` alone: the same tests and Release build, read-only, nothing
packed. A red run there is a release that would not go out. **A warning fails CI** (`TreatWarningsAsErrors`, set in the
CI job only, so a local build just shows it); NuGet's vulnerability-audit warnings NU1901–NU1904 are the exception, so a
fresh advisory doesn't block a release that has no fix to ship.

**The same tag builds the same dependencies.** Package versions are exact (no `4.*`), and each project has a
`packages.lock.json` pinning every package, transitive ones included, by version and content hash
(`RestorePackagesWithLockFile` in `Directory.Build.props`). CI restores in locked mode (`RestoreLockedMode` when
`CI=true`, plus an explicit `dotnet restore --locked-mode` step), so a lock file that disagrees with the project files fails
the run instead of being quietly rewritten. Locally a restore after a package change rewrites the lock file: commit it with
the change and read its diff. The lock files cover `win-x64` only.

Locally: `dotnet tool restore` (vpk, pinned to the Velopack package's version in `.config/dotnet-tools.json`), then
`./scripts/release.ps1`. Output goes to `artifacts-release/` (gitignored). **Never commit a signing certificate, its password
or a release token.** Releases are unsigned today: SmartScreen warns once, on the downloaded `Setup.exe` ("More info → Run
anyway"). Updates are fetched by Velopack's updater, not a browser, so they carry no Mark of the Web and don't warn.

**What an installed tracker trusts.** Unsigned, it applies whatever this repo's releases offer, so the trust chain is the
GitHub account, the `v*` tags and the release workflow. Each link is held: the packages are locked (above); the actions
are pinned to commit SHAs, the release in a comment beside each (a tag can be moved to other code, a commit cannot —
update both workflow files together from `git ls-remote --tags`); only the pack job can write; and `v*` tags are meant to
be protected by a tag ruleset (Settings → Rules → Rulesets: create, update and delete restricted, repository admins
bypass), a repo setting only the owner can change. Signing stays off by decision (2026-11-23): one user, and updates
don't warn.

**What the installer must not break** — each of these fails without an error:
- **The install folder is not the data folder.** The package id is `AdhdTimeOrganizer.Tracker`, so it installs to
  `%LocalAppData%\AdhdTimeOrganizer.Tracker\` (`current\` holds the app, `Update.exe` beside it). An uninstall deletes that
  folder whole. Config, outbox and state stay in `%LocalAppData%\AdhdTimeOrganizer.ActivityTracking.Desktop\` and the logs in
  `%LocalAppData%\AdhdTimeOrganizer\logs`, so an uninstall keeps unsent minutes and a reinstall resumes. Never give the
  package either of those names.
- **The `Run` entry follows the exe.** `current\AdhdTimeOrganizer.exe` keeps its path across updates, so updates never touch
  the entry. Its command carries `--minimized`, as does "Restart to update": those starts are silent, and any start without
  it (Start menu, the exe, the installer's first run) shows a "running" balloon once recording
  (`TrayApplicationContext.StartedByUser`). `AutoStartHelper` compares the whole command, so changing it rewrites every entry. The installed build takes the entry over from a build run from source at start (`AutoStartHelper.Reconcile`).
  A build from source never moves an entry whose exe exists. The uninstall hook removes the entry if it starts the
  installed exe (`RemoveIfOwn`).
- **An update goes through the normal exit.** "Restart to update" in the tray calls `WaitExitThenApplyUpdates` — the
  updater waits for this process — and then the ordinary exit: flush, toasts removed, notifications unregistered, mutex
  released. Never use `ApplyUpdatesAndRestart`: it ends the process itself and skips all of that.
- **Only the first instance applies an update at start.** Velopack's apply-on-start is off (`SetAutoApplyOnStartup(false)`
  in `Program.cs`): it runs inside `VelopackApp.Run()`, before the single-instance mutex, and the updater stops every
  process in the install folder — so a second launch (Start menu clicked while the tray runs) would end the running tracker
  without its exit. `UpdateService.ApplyPendingAtStart` applies a downloaded release after the mutex, in the first
  instance only and before anything starts (`UpdatePolicy.ApplyAtStart`); a second launch just says "already running".
- **Never apply at a start the updater made.** After a *failed* apply with `restart: true` the updater starts the old build
  again (Velopack 1.2.161's `apply.rs`; its docs: "restarts the application after updates are applied (or if they
  failed)"), and that build finds the same release waiting — applying it would loop. Velopack's own apply-on-start skips
  when `VELOPACK_RESTART` is set; ours skips when `UpdateService.StartedByUpdater`, set by the `OnRestarted` hook in
  `Program.cs`. Don't read `VELOPACK_RESTART` yourself: `VelopackApp.Run()` clears it before anything after it runs. The
  release waits for the next ordinary start or "Restart to update". Recheck this when upgrading Velopack.
- **The published folder must hold the Windows App SDK runtime, the extracted Insights dll and the icon file.**
  `release.ps1` checks them after `dotnet publish` (self-contained: .NET and the Windows App SDK both travel with the app,
  about 165 MB before compression). `NativeSmokeTests` checks the same dll in a build.
- **Uninstall while the tracker runs** may end it without a flush (Velopack stops processes in the install folder). The
  minute in progress is lost, nothing queued is.

**Updates are one-way.** The first run of a newer build may migrate what it saved (the plain-text refresh token became
DPAPI-protected that way), and an older build can't read the migrated form: going back means signing in again. Velopack
doesn't offer a lower version anyway; to roll back, release a higher version with the old code.

A build run from `bin/` or `artifacts-cli/` is not installed: it never checks for updates, and its tray line says
"built from source, no updates".

## Shell

This is a Windows machine: use PowerShell. Never round-trip a file through the shell
(`Get-Content | Set-Content` and the like); edit files with the editor tools.
