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
| `TrayApplicationContext.cs` | Tray icon and menu (incl. "Pause for…", the today/now glance and the "map these apps" prompt); pulls status from the tracker on a UI timer |
| `LoginForm.cs`, `SettingsForm.cs`, `PrivacyForm.cs` | Sign-in (the browser by default; password + 2FA behind "Use a password instead"); settings editor; the user's ignore / hide-title rules and private-window handling |
| `Services/BrowserSignIn.cs` | **Pure** browser sign-in: PKCE (S256), state, the `/connect/tracker` URL, and whether a request reaching the listener is our redirect |
| `Services/LoopbackRedirectListener.cs`, `Services/BrowserSignInFlow.cs` | The loopback listener (`TcpListener` on `127.0.0.1`, free port, one redirect, the handler testable on a stream) and the run: listen → open the browser → wait → exchange |
| `Helpers/AutoStartHelper.cs` | "Start with Windows": the tracker's own `Run` entry (`AdhdTimeOrganizer.ActivityTracking.Desktop` — never `AdhdTimeOrganizer`, which the separate desktop app uses), Windows' `StartupApproved` verdict, repair of a moved exe, the installed build taking the entry over and the uninstaller removing it; an entry whose exe is gone reads as off; the registry sits behind `IAutoStartStore` so tests never touch it |
| `Services/AppVersion.cs` | The csproj's `<Version>` and the `X-Client-Version: desktop/<version>` header `ApiClient` puts on every request |
| `Services/UpdatePolicy.cs`, `Services/UpdateService.cs` | **Pure** update decisions (when to look, the tray's version line and restart item) and the Velopack side: check GitHub Releases, download, hand a waiting release to the updater |
| `scripts/release.ps1`, `.github/workflows/release.yml` | Publish self-contained, check the files, `vpk pack` (and upload on a `v*` tag) — see "Releasing" |
| `Services/ActivityTrackerService.cs` | Poll loop (foreground changes as they happen, plus a poll for the rest of the desktop), send loop (drains the outbox), lock/suspend/session-end handling |
| `Services/ActivityRecorder.cs` | **Pure** minute accounting: measured elapsed time, split at minute boundaries, gap cap, switch counts, the hold-back that idle backdating retracts from |
| `Services/ScreenState.cs` | **Pure** "what is on screen now": which observations a span is credited to, what counts as a switch, when idleness starts a backdate |
| `Services/WindowLimits.cs` | The server's validation rules, applied before a window is queued |
| `Services/WindowTitleNormalizer.cs` | Strips counters, unsaved markers and app-name suffixes from titles |
| `Services/OutboxStore.cs` | Finished windows as JSON files under `%LOCALAPPDATA%\…Desktop\outbox` until sent |
| `Services/ApiClient.cs` | Login, 2FA, token refresh, heartbeat (and its answer), the server-rules and mapping-coverage downloads, the start ramp's Start now and Not this one; `Classify` decides retry vs. drop per status code |
| `Services/RefreshPolicy.cs` | **Pure** refresh decisions: save / sign out / keep, whether an attempt left the token possibly rotated, how long to hold it back |
| `Services/TodayTotal.cs` | Today's recorded foreground time on this PC, for the tray glance; saved to `today.json` |
| `Services/PrivacyFilter.cs` | Recording-time privacy: local rules and private browser windows → keep, hide the title, or drop |
| `Services/TrackerRules.cs` | Send-time privacy: the user's server rules (in memory) and the filter that drops entries an ignore rule wins |
| `Services/Matching/` | **Port of the server's matcher** (`PatternMatcher`, `MappingRuleSet`) — see "Rule matching" below |
| `Services/HyperfocusPolicy.cs` | **Pure** nudge decisions: stretches, breaks, app stretches, snooze / repeat / "not now", the break timer |
| `Services/DriftPolicy.cs` | **Pure** drift-nudge decisions over the server's per-minute plan verdicts (`MinuteVerdict`) |
| `Services/StartRampPolicy.cs` | **Pure** start-ramp decisions: offer a planned block at its start, from the heartbeat's plan (`PlanView`) |
| `Services/ChimeSchedule.cs` | **Pure** time-chime decisions over the server's per-minute resolution, and which lines are chosen (`ChimeSettings`, `ChimeChoices`) |
| `Services/UploadHealthPolicy.cs` | **Pure** "is tracked time arriving?" decisions: signed out, stalled a day, windows the server refused → show / withdraw a notice |
| `Services/UnmappedReviewPolicy.cs` | **Pure** evening-invite decisions: is the evening due / missed / handled, the invite's text from the server's answer; plus `UnmappedReviewStore` (the last handled evening, `unmapped-review.json`) |
| `Services/UnmappedReviewService.cs` | The send loop's evening tick: holds the server's evening moment, asks the server for today's unmapped time when it comes, hands the invite on |
| `Services/PauseKeepAlive.cs` | **Pure** when a paused tracker sends its empty heartbeat, and what that heartbeat is |
| `Services/DiagnosticsReport.cs` | **Pure** text of the tray's "Copy diagnostics" (state, settings as counts, scrubbed log tail) and the log-tail reader |
| `Services/NudgeService.cs` | Feeds polls to the policy, checks quiet conditions, shows and withdraws toasts, handles button presses |
| `Services/NudgeNotifier.cs`, `NudgeToast.cs` | Windows App SDK notifications: registration, toast text and buttons, parsing a press |
| `Native/` | P/Invoke (`Win32`, `MonitorHelper`), `ProcessInfoCache`, `AudioSessionHelper` (NAudio), `MicrophoneUsage` (registry: whether, and which apps), `ForegroundEvents` (the WinEvent hook) |
| `Models/` | Wire models (`ActivityModels.cs`, `TrackerRule.cs`, `ServerResolution.cs` — the heartbeat's answer, the coverage, today's unmapped time and the evening moment) and `AppConfig` (`%LOCALAPPDATA%\…Desktop\config.json`) |
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
- **Tests never touch the user's real files, notifications or network.** Give every `AppConfig` a temp `FilePath`
  (anything that signs in, refreshes a token or turns a nudge off calls `Save()`); pass `ApiClient` a fake
  `HttpMessageHandler`; build `NudgeService` through its internal constructor with a fake `INudgeNotifier`, a
  browser stand-in and a temp drift-state path; put `OutboxStore` and `TodayTotal` in a temp folder.
  `NudgeServiceTests` and `SendLoopTests` show the shape.
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
  bound is mirrored in `WindowLimits` and applied before queuing: ≤ 60 s active + background per entry,
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
  together. A 404 from the exchange means an older server: the password form still works there.

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
    backdated only that far. Lengthen one and you must re-check the other.
- Idle = no input for `IdleThresholdSeconds`, unless the **foreground** window is audibly playing
  (`IsPassivelyPresent`), up to `MaxPassiveIdleMinutes`. Fullscreen alone deliberately doesn't count:
  a paused fullscreen video looks like a watched one. A locked session records nothing.

### Durability and threads
- Every finished window goes to the **outbox on disk** before anything tries to send it. Exit, suspend,
  logoff and session end call `ActivityTrackerService.Flush()`. Don't add an in-memory-only queue.
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
  a 409 or a 5xx keeps it. While signed out, tracking continues into the outbox. The decisions are the
  **pure** `Services/RefreshPolicy.cs`; `ApiClient` only sends and keeps the clock.
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
  for any new native call.
- **`ForegroundEvents` is installed and removed on the UI thread** (`Start`, `Stop`, `Dispose`): an out-of-context
  WinEvent hook is delivered through the setting thread's message loop, and only that thread may remove it. The
  callback is `[UnmanagedCallersOnly]` and static (WinEvent callbacks carry no context), stamps a `Stopwatch`
  timestamp, queues, and returns; the one other thing it does is move the title-change hook to the new foreground
  process — hooked system-wide, every accessible-name change on the machine would be a message on the UI thread.
- A packaged app is named in the microphone's consent store by its **package family name**, read per process with
  `GetPackageFamilyName` on the limited handle (`ProcessInfo.PackageFamilyName`); a desktop app by its path.

### Nudges and notifications
- Nudges are **client-side**: their decisions never involve the server, and the two things a nudge sends are the start
  ramp's Start now and Not this one (below). The policies live in `HyperfocusPolicy`, `DriftPolicy`, `StartRampPolicy` and `ChimeSchedule`, pure like
  `ActivityRecorder` — keep clocks, Win32 and toasts out of them.
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
  glance's submenu, not a left-click flyout — double-click already pauses.
- **Upload notices are not nudges** (`UploadHealthPolicy`, toast kind `NudgeKind.UploadHealth`, switch
  `UploadNoticesEnabled`). Three problems, one toast at a time, in this order: signed out (minutes waiting for 10 min —
  it replaced the old "signed out" balloon), stalled (the **oldest unsent minute** is a day old and stays so for 10 min),
  dropped (3+ windows refused since the last notice). A stall is measured by `OutboxStore.Summary().OldestStartUtc`, never
  by `LastSentUtc`: the outbox is on disk, "last sent" is not. The 10 min (`StallGrace`) are for a PC waking from a long
  sleep: its backlog is days old before anything has tried to send it. They are fed by the tray's 2 s timer (`NudgeService.OnUploadStatus`), not
  the poll loop, so they keep running through a pause — the policy itself shows nothing while paused. No quiet
  conditions (Windows holds toasts itself). Once per episode, again at most daily, withdrawn when the condition clears;
  a dropped-windows toast is not withdrawn (those minutes are gone) and leaves on exit. State is in memory: a restart
  while still stalled shows the notice again. Counts and durations only in the text and the log. "Turn off" on one
  switches off only these, and a nudge's "Turn off" leaves them on.
- Nudges are fed by the poll loop, so pausing tracking pauses them. A paused, asleep or hung gap as long as a
  break counts as a break.
- A call (any app holding the microphone) counts as presence **for nudges only**. It must not change what
  the tracker credits; passive presence stays the only idle exception there. What the tracker does with a call is
  mark it: the mic-holding app's entry carries `IsInCall`, and the app is observed as a background window (like an
  audio-playing one) so a muted, hidden call app still has an entry to carry it. It is credited active time only while
  it was really in front.
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

### The evening review of unmapped time
- **Why:** drift, the tray's "now" and History read the server's mapping, and time no rule maps is invisible to all of
  them. Once, at the user's evening, one toast: how much of today on this PC is unmapped and the top two apps, with
  **Review** (opens the web app's distinct-entries grid on `coverage=Uncovered&today=true&date=<the evening's day>`,
  busiest first; `date` wins, `today` is for web apps from before it) and **Not
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
  `unmapped?date=` and Review's `&date=` name the day that is ending. A server from before these fields sends neither,
  and the tracker behaves as it did. Stays quiet while tracking is paused and while signed out; a server
  from before the routes ends it for the session; a failed read is retried in 5 min, not every pass.
- The toast names **apps** (product or process name), never a window title, and the log carries counts only.
- Both answers are pinned by `DesktopTrackerWire.samples.json` (`unmapped`, `eveningMoment`) like the others. The tray's
  "Map apps for drift nudges…" stays: it is the week's view, tied to drift nudges, and opens the same grid.

### Privacy before upload
- Two layers. **At recording** (`PrivacyFilter`, poll loop): the user's `LocalRules` (stored under the old
  `IgnoreRules` key — old configs read unchanged) and private browser windows. Ignore beats hide-title. A
  hidden title becomes `"<product or process> - PRIVATE"` or the window is dropped
  (`PrivateWindowHandling`); process and product stay, so process-level server rules still map the time.
  The raw title goes no further than `ToSample`, so it never reaches the outbox. **At sending**
  (`TrackerRules`, send loop): the server's rules, as above.
- A local title rule matches the raw title **and** the normalized one: normalization strips the browser
  suffix, and with it a localized private-browsing marker.
- Private windows are recognised by title only: Firefox ends it with "Private Browsing", Edge writes
  "[InPrivate]". Chrome Incognito has no title marker and is not detected. A localized Firefox needs a
  hide-title rule on its own wording.
- `LocalRules` is **replaced, never mutated**: the poll loop iterates it on its own thread. That is also
  why edits apply from the next poll without a restart.
- A pause (`PausedUntilUtc`, `DateTime.MaxValue` = until resumed) is saved, so a restart in the middle of
  one doesn't start recording early. The tray's timer resumes a timed one.

### Privacy and logging
- No email, name or other personal data in logs. Log files outlive an account deletion. Ids, process
  names and status codes are fine. Window titles and paths are personal; don't log them either.
- "Copy diagnostics" (tray) goes into bug reports, so `DiagnosticsReport` reads `AppConfig` field by field, never
  serializes it: no token, rules as counts, the poll error as "see the log", and the log tail has the profile folder and
  e-mail-looking text masked. A new config field stays out of the report until someone decides it is safe;
  `DiagnosticsReportTests` plants a token, rule patterns and a title and asserts none comes out. The logger holds today's
  file open, so the tail is read with `FileShare.ReadWrite`.
- The refresh token is DPAPI-encrypted in `config.json` (`ProtectedRefreshToken`). Never write it in plain
  text.

## Releasing

The tracker is distributed with **Velopack**: a `Setup.exe` that installs per user without admin rights, and quiet
updates from this repo's **GitHub Releases** (public, so no token ships in the app).

**Cutting a release.**
1. Raise `<Version>` in the app's csproj (SemVer; Velopack only ever offers a higher version).
2. Commit, then tag `v<version>` and push the tag. `.github/workflows/release.yml` runs the pure tests and
   `scripts/release.ps1 -Upload` with the workflow's own `GITHUB_TOKEN`. The script refuses a tag that disagrees with the csproj.
3. Deploy the server first if the release relies on a server change (as with the refresh recovery above).
4. To make the server's tracker-silence notification say "out of date" for older builds, raise
   `TrackerMinimumClientVersion:Desktop` in its config. It is advisory only: nothing is refused.

Locally: `dotnet tool restore` (vpk, pinned to the Velopack package's version in `.config/dotnet-tools.json`), then
`./scripts/release.ps1`. Output goes to `artifacts-release/` (gitignored). **Never commit a signing certificate, its password
or a release token.** Releases are unsigned today: SmartScreen warns once, on the downloaded `Setup.exe` ("More info → Run
anyway"). Updates are fetched by Velopack's updater, not a browser, so they carry no Mark of the Web and don't warn.

**What the installer must not break** — each of these fails without an error:
- **The install folder is not the data folder.** The package id is `AdhdTimeOrganizer.Tracker`, so it installs to
  `%LocalAppData%\AdhdTimeOrganizer.Tracker\` (`current\` holds the app, `Update.exe` beside it). An uninstall deletes that
  folder whole. Config, outbox and state stay in `%LocalAppData%\AdhdTimeOrganizer.ActivityTracking.Desktop\` and the logs in
  `%LocalAppData%\AdhdTimeOrganizer\logs`, so an uninstall keeps unsent minutes and a reinstall resumes. Never give the
  package either of those names.
- **The `Run` entry follows the exe.** `current\AdhdTimeOrganizer.exe` keeps its path across updates, so updates never touch
  the entry. The installed build takes the entry over from a build run from source at start (`AutoStartHelper.Reconcile`).
  A build from source never moves an entry whose exe exists. The uninstall hook removes the entry if it starts the
  installed exe (`RemoveIfOwn`).
- **An update goes through the normal exit.** A downloaded release applies at the next start (`VelopackApp.Run()`, first line
  of `Program.cs`). "Restart to update" in the tray calls `WaitExitThenApplyUpdates` — the updater waits for this process —
  and then the ordinary exit: flush, toasts removed, notifications unregistered, mutex released. Never use
  `ApplyUpdatesAndRestart`: it ends the process itself and skips all of that.
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
