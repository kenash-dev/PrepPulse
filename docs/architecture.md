# PrepPulse — architecture, patterns and decisions

This document covers:

- what was built in v0.1 and how it's put together;
- the patterns used, and the decisions that shaped them;
- what the live test on Windows found.

Every pattern and decision links to the file and line where it's implemented.

> **Reading the links.** Links are relative to this file and use GitHub's `#L` line anchors, so on GitHub (or in VS Code with a Markdown preview) they jump straight to the line. Line numbers are for v0.1 (commit `aa1caff`). If code moves later, search for the member name given next to each link.

---

## 1. Shape of the solution

```
PrepPulse.slnx
├─ src/PrepPulse.Core     net10.0          Plans, matching, the session engine, dashboard model. No Windows or I/O code.
├─ src/PrepPulse.Data     net10.0          SQLite store (Microsoft.Data.Sqlite + Dapper), schema migrations.
├─ src/PrepPulse.App      net10.0-windows  PrepPulse.exe: Win32 probes, tray, taskbar, popups, plan picker, hotkey,
│                                          command pipe, local dashboard (Kestrel). Composition root.
├─ web/index.html                          Dashboard page (plain HTML/SVG, no chart library).
└─ tests/PrepPulse.Tests  net10.0          77 xUnit tests; tests/testrun has the live-test scripts and config.
```

```
 Windows (1 s)                     PrepPulse.Core                         Outputs
 ─────────────                     ──────────────                         ───────
 WindowProbe ───────┐
 BrowserAddressProbe├─► SamplerService ─► FocusEngine ──► ISessionStore ─► SqliteStore ─► preppulse.db
 GetLastInputInfo ──┘                     │   ▲                                              │
 SystemEvents (lock/sleep/shutdown) ──────┤   │ Func<AppSettings>, Func<PlanCatalogSnapshot> │
 Tray / hotkey / pipe commands ───────────┘   └── CatalogService (plans/*.yaml, settings.json)│
                                          │                                                  ▼
                                          └─► Changed / Event ─► Shell (WPF thread) ─► tray ring, taskbar, popup, sound
                                                                                   DashboardBuilder ─► /api/dashboard ─► web/index.html
```

**Key files**

| File | Role |
|---|---|
| [`Program.cs`](../src/PrepPulse.App/Program.cs) | Entry point, single-instance check, composition root |
| [`FocusEngine.cs`](../src/PrepPulse.Core/Engine/FocusEngine.cs) | The session state machine: segments, pauses, alerts |
| [`EngineTypes.cs`](../src/PrepPulse.Core/Engine/EngineTypes.cs) | States, samples, snapshot, events, storage keys |
| [`Matching.cs`](../src/PrepPulse.Core/Matching/Matching.cs) | Focus / distraction / neither classification |
| [`PlanLoader.cs`](../src/PrepPulse.Core/Plans/PlanLoader.cs) · [`Plan.cs`](../src/PrepPulse.Core/Plans/Plan.cs) | Plan files → validated plan objects; daily targets |
| [`DashboardBuilder.cs`](../src/PrepPulse.Core/Reporting/DashboardBuilder.cs) | Stored records → dashboard model |
| [`SqliteStore.cs`](../src/PrepPulse.Data/SqliteStore.cs) | Schema, writes, reads |
| [`SamplerService.cs`](../src/PrepPulse.App/Services/SamplerService.cs) | The 1-second loop that feeds the engine |
| [`Shell.cs`](../src/PrepPulse.App/Ui/Shell.cs) | UI controller: tray, taskbar window, popup, picker, Windows events |
| [`Commands.cs`](../src/PrepPulse.App/Services/Commands.cs) | Command router and named pipe |

---

## 2. Patterns used

### P1. Composition root + Generic Host
One method creates every object and wires them together. The Generic Host gives DI, logging, background services and Kestrel with clean start/stop. WPF runs its message loop next to it on the main thread.

**In the code**
- [`Program.cs:87-155`](../src/PrepPulse.App/Program.cs#L87-L155): `Run`, the composition root, top to bottom.
- [`Program.cs:105-122`](../src/PrepPulse.App/Program.cs#L105-L122): `WebApplication.CreateSlimBuilder` plus singleton and hosted-service registration.
- [`Program.cs:126`](../src/PrepPulse.App/Program.cs#L126): host started *before* the UI, so the pipe and dashboard are ready when the tray appears.
- [`Program.cs:132-148`](../src/PrepPulse.App/Program.cs#L132-L148): the WPF `Application` (`OnExplicitShutdown`) and `Shell` are created, then `wpf.Run()` runs the message loop.
- [`Program.cs:150-154`](../src/PrepPulse.App/Program.cs#L150-L154): orderly shutdown: end the session, dispose the UI, stop the host.
- [`PrepPulse.App.csproj:18`](../src/PrepPulse.App/PrepPulse.App.csproj#L18): the host comes from the shared framework (`Microsoft.AspNetCore.App`), not NuGet.

### P2. Ports and adapters (hexagonal)
The core defines the interfaces it needs, and the outer projects implement them. The engine and reports never reference Windows, SQLite or WPF.

**In the code**
- [`Time.cs:4-8`](../src/PrepPulse.Core/Time.cs#L4-L8): `IClock`, the port for "now". [`Time.cs:10-14`](../src/PrepPulse.Core/Time.cs#L10-L14): `SystemClock`, the adapter.
- [`Records.cs:28-40`](../src/PrepPulse.Core/Records.cs#L28-L40): `ISessionStore`, the write port used by the engine.
- [`Records.cs:43-49`](../src/PrepPulse.Core/Records.cs#L43-L49): `IReportSource`, the read port used by the dashboard.
- [`SqliteStore.cs:11`](../src/PrepPulse.Data/SqliteStore.cs#L11): `SqliteStore : ISessionStore, IReportSource`, the adapter.
- [`FocusEngine.cs:26-32`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L26-L32): the engine takes only ports: clock, store, settings and catalog providers.
- [`TestSupport.cs:9`](../tests/PrepPulse.Tests/TestSupport.cs#L9) and [`TestSupport.cs:25`](../tests/PrepPulse.Tests/TestSupport.cs#L25): test adapters `FakeClock` and `FakeStore`.

### P3. State machine
A session is in one state at a time (Running / Paused / Locked / OnBreak). The UI's focus state (Focused / Drifting / Idle / FocusLost / Paused / OnBreak) is derived from it. Every transition goes through a small set of methods, so pausing, locking, breaks, alerts and ending always close what's open in the same way.

**In the code**
- [`EngineTypes.cs:5`](../src/PrepPulse.Core/Engine/EngineTypes.cs#L5): `SessionState`. [`EngineTypes.cs:8`](../src/PrepPulse.Core/Engine/EngineTypes.cs#L8): `FocusState`.
- [`FocusEngine.cs:237-345`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L237-L345): `TickSession`, ten numbered steps in a fixed order: sleep check → plan reload → heartbeat → paused/locked → break → classify → idle → segments → alerts → suggestions.
- [`FocusEngine.cs:458-479`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L458-L479): `Suspend` / `Unsuspend` (pause and lock).
- [`FocusEngine.cs:446-456`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L446-L456): `BeginBreak`.
- [`FocusEngine.cs:481-496`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L481-L496): `End`, the single exit path for every end reason.
- [`FocusEngine.cs:542-551`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L542-L551): focus state derived from session state, alert, pause and list.

### P4. Functional core, imperative shell
The engine and the dashboard builder do no I/O of their own. Given the same samples and clock, they produce the same result. The shell (sampler, Windows events, HTTP) does the I/O and hands plain data in.

**In the code**
- [`EngineTypes.cs:17`](../src/PrepPulse.Core/Engine/EngineTypes.cs#L17): `Sample`, one second of desktop state as an immutable record.
- [`FocusEngine.cs:182-186`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L182-L186): `Tick(Sample)`, the only input from sampling.
- [`SamplerService.cs:47-80`](../src/PrepPulse.App/Services/SamplerService.cs#L47-L80): `TickOnce`, the imperative shell that reads Windows and builds the `Sample`.
- [`DashboardBuilder.cs:29-33`](../src/PrepPulse.Core/Reporting/DashboardBuilder.cs#L29-L33): `Build(catalog, data, selected, now, zone)`, a pure function of its arguments.
- [`DashboardApi.cs:40-58`](../src/PrepPulse.App/Services/DashboardApi.cs#L40-L58): the shell side: loads from SQLite, merges the live segment, calls the builder.

### P5. Immutable snapshots, swapped on change (+ null objects)
Shared data is published as an immutable object and replaced, never edited in place. Readers always see a complete, consistent version. `Empty` / `Neither` instances stand in for "nothing", so callers don't need null checks.

**In the code**
- [`EngineTypes.cs:21-47`](../src/PrepPulse.Core/Engine/EngineTypes.cs#L21-L47): `EngineSnapshot` record with `Empty`.
- [`PlanLoader.cs:13-23`](../src/PrepPulse.Core/Plans/PlanLoader.cs#L13-L23): `PlanCatalogSnapshot` with `Empty`.
- [`CatalogService.cs:22-23`](../src/PrepPulse.App/Services/CatalogService.cs#L22-L23): `volatile` fields swapped whole in `Load` ([`:43-54`](../src/PrepPulse.App/Services/CatalogService.cs#L43-L54)).
- [`FocusEngine.cs:251`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L251): a running session picks up an edited plan by reference swap.
- [`Matching.cs:18`](../src/PrepPulse.Core/Matching/Matching.cs#L18): `Classification.Neither`.

### P6. Observer, with events raised outside the lock and marshalled to the UI thread
The engine changes state under one lock. It collects events while it does, then raises them after releasing the lock. The UI subscribes and re-posts each event to the WPF dispatcher. So the 1-second sampler never waits on the UI, and UI handlers can call back into the engine without deadlocking.

**In the code**
- [`FocusEngine.cs:34-38`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L34-L38): `Event` (one-off things: alerts, beeps, start/end) and `Changed` (latest snapshot).
- [`FocusEngine.cs:606-620`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L606-L620): `Run<T>`: body under `lock`, then `Event?.Invoke` / `Changed?.Invoke` outside it.
- [`EngineTypes.cs:49-57`](../src/PrepPulse.Core/Engine/EngineTypes.cs#L49-L57): the event types.
- [`Shell.cs:60-62`](../src/PrepPulse.App/Ui/Shell.cs#L60-L62): subscriptions re-posted with `Dispatcher.InvokeAsync`.
- [`Shell.cs:195`](../src/PrepPulse.App/Ui/Shell.cs#L195) and [`Shell.cs:257`](../src/PrepPulse.App/Ui/Shell.cs#L257): `Show` / `Exit` called from the pipe thread hop to the UI thread with `CheckAccess`.

### P7. Command pattern + single-instance IPC
Every action is a short text command handled in one place. A second `PrepPulse.exe` doesn't start another tracker. It sends its command to the running one over a named pipe and writes the JSON reply to the `--out` file. The tray, hotkey, scripts and tests all share this path.

**In the code**
- [`Program.cs:51-57`](../src/PrepPulse.App/Program.cs#L51-L57): named mutex. The first instance runs; later ones forward with `PipeClient.Send`.
- [`AppPaths.cs:20-21`](../src/PrepPulse.App/AppPaths.cs#L20-L21) and [`AppPaths.cs:34-35`](../src/PrepPulse.App/AppPaths.cs#L34-L35): mutex and pipe names derived from a hash of the data folder, so a test instance and your real one never collide.
- [`Commands.cs:35-70`](../src/PrepPulse.App/Services/Commands.cs#L35-L70): `CommandRouter.Handle`, the verb dispatch.
- [`Commands.cs:15-20`](../src/PrepPulse.App/Services/Commands.cs#L15-L20): `IShellCommands`, the UI actions the router may call. They are attached once the UI is up ([`:87-91`](../src/PrepPulse.App/Services/Commands.cs#L87-L91)).
- [`Commands.cs:115-140`](../src/PrepPulse.App/Services/Commands.cs#L115-L140): `PipeServer` (a `BackgroundService`). [`Commands.cs:142-161`](../src/PrepPulse.App/Services/Commands.cs#L142-L161): `PipeClient`.

### P8. Rule matching by specificity score
Each list entry is scored against what's on screen, and the highest score wins. This one rule removes the need for exceptions such as "YouTube is a distraction except system-design videos".

**In the code**
- [`Plan.cs:41-59`](../src/PrepPulse.Core/Plans/Plan.cs#L41-L59): `FocusEntry.Parse`: `x.exe` → app, otherwise site; `site | words` adds a title condition.
- [`Matching.cs:64-94`](../src/PrepPulse.Core/Matching/Matching.cs#L64-L94): `EntryMatcher.Score`:

  | Match | Score |
  |---|---|
  | App | 1 |
  | Site by title | 1.5 |
  | Site by domain | 2 |
  | Title words required too | +4 |
- [`Matching.cs:126-161`](../src/PrepPulse.Core/Matching/Matching.cs#L126-L161): `Classifier.Classify`, tie rules:
  - this week's topic beats an unscheduled one ([`:142-143`](../src/PrepPulse.Core/Matching/Matching.cs#L142-L143));
  - focus beats distraction, because a distraction must score strictly higher ([`:153`](../src/PrepPulse.Core/Matching/Matching.cs#L153)).
- [`MatchingTests.cs:55`](../tests/PrepPulse.Tests/MatchingTests.cs#L55): `Most_specific_entry_wins_across_lists`.

### P9. Repository / data mapper with versioned migrations
`SqliteStore` turns domain records into rows and back. Times are stored as epoch milliseconds and keys as text, and none of that leaks out. The schema carries a version number, so later upgrades run once at startup.

**In the code**
- [`SqliteStore.cs:42-53`](../src/PrepPulse.Data/SqliteStore.cs#L42-L53): `Migrate`: WAL mode, `PRAGMA user_version`, schema applied in a transaction.
- [`SqliteStore.cs:55-112`](../src/PrepPulse.Data/SqliteStore.cs#L55-L112): `SchemaV1`, five tables.
- [`SqliteStore.cs:114-117`](../src/PrepPulse.Data/SqliteStore.cs#L114-L117): `Ms` / `At`, the only place times are converted.
- [`SqliteStore.cs:226-275`](../src/PrepPulse.Data/SqliteStore.cs#L226-L275): `Load`: Dapper rows → domain records. Row classes start at [`:290`](../src/PrepPulse.Data/SqliteStore.cs#L290).
- [`SqliteStore.cs:190-208`](../src/PrepPulse.Data/SqliteStore.cs#L190-L208): `UpsertSessionApps` with `ON CONFLICT … DO UPDATE`.

### P10. Polling sampler with cached slow lookups
A background service ticks once a second. The cheap calls (foreground window, last input) run every tick. The slow one (reading the browser's address bar) runs off-thread with a time limit, and is cached until the window or title changes.

**In the code**
- [`SamplerService.cs:26-45`](../src/PrepPulse.App/Services/SamplerService.cs#L26-L45): `PeriodicTimer(1 s)` loop. Repeated errors are logged at most 5 times ([`:40`](../src/PrepPulse.App/Services/SamplerService.cs#L40)).
- [`SamplerService.cs:74-79`](../src/PrepPulse.App/Services/SamplerService.cs#L74-L79): open-window list every 30 s.
- [`BrowserAddressProbe.cs:26-53`](../src/PrepPulse.App/Probes/BrowserAddressProbe.cs#L26-L53): `Domain`:
  - cache hit on the same window and title ([`:28`](../src/PrepPulse.App/Probes/BrowserAddressProbe.cs#L28));
  - otherwise `Task.Run` with an 800 ms wait ([`:15`](../src/PrepPulse.App/Probes/BrowserAddressProbe.cs#L15), [`:47-48`](../src/PrepPulse.App/Probes/BrowserAddressProbe.cs#L47-L48)).
- [`Native.cs:130-137`](../src/PrepPulse.App/Interop/Native.cs#L130-L137): `IdleFor` via `GetLastInputInfo`, with 32-bit tick wrap handled.

### P11. Heartbeat + crash recovery
A running session writes a heartbeat every 15 s. At startup, any session without an end is closed at its last heartbeat and marked "unexpected". A crash or power cut then costs at most 15 s of accuracy.

**In the code**
- [`FocusEngine.cs:15`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L15): `HeartbeatEvery`. [`FocusEngine.cs:259-264`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L259-L264): written from the tick.
- [`SqliteStore.cs:283-287`](../src/PrepPulse.Data/SqliteStore.cs#L283-L287): `CloseUnfinishedSessions` → `ended_at = COALESCE(last_heartbeat, started_at)`.
- [`Program.cs:91`](../src/PrepPulse.App/Program.cs#L91): runs before anything else. [`Shell.cs:70-71`](../src/PrepPulse.App/Ui/Shell.cs#L70-L71): tells you it happened.
- [`ReportAndStoreTests.cs:175`](../tests/PrepPulse.Tests/ReportAndStoreTests.cs#L175): `Sessions_left_open_by_a_crash_are_closed_at_their_last_heartbeat`.

### P12. Debounced file watcher (hot reload)
Editors save a file in several writes, so a change to the config folder restarts a 600 ms timer, and the reload happens only once it fires. A bad file is reported, not fatal: the other plans keep working.

**In the code**
- [`CatalogService.cs:56-68`](../src/PrepPulse.App/Services/CatalogService.cs#L56-L68): `FileSystemWatcher` on the config folder.
- [`CatalogService.cs:70-74`](../src/PrepPulse.App/Services/CatalogService.cs#L70-L74): `.yaml` / `.json` changes restart the timer (`_debounce.Change(600, …)`).
- [`CatalogService.cs:76-87`](../src/PrepPulse.App/Services/CatalogService.cs#L76-L87): `Reload` → `Changed` event → tray and picker refresh.
- [`PlanLoader.cs:44-48`](../src/PrepPulse.Core/Plans/PlanLoader.cs#L44-L48): one broken file becomes a `PlanError`; the rest still load.

### P13. Collect-all validation (Notification) + DTO → domain mapping
YAML is read into loose DTOs where every value is a string. Then each field is checked, *all* problems are collected with the field named, and they're reported together. The domain `Plan` is built only from valid data.

**In the code**
- [`PlanLoader.cs:231`](../src/PrepPulse.Core/Plans/PlanLoader.cs#L231): `PlanDto` and friends (all strings).
- [`PlanLoader.cs:80`](../src/PrepPulse.Core/Plans/PlanLoader.cs#L80): `errors` list, filled field by field. Thrown once at [`:158`](../src/PrepPulse.Core/Plans/PlanLoader.cs#L158).
- [`PlanLoader.cs:191-198`](../src/PrepPulse.Core/Plans/PlanLoader.cs#L191-L198): `Flatten`: checklist lines that YAML read as maps (`"Timed design: …"`) are kept as text.
- [`PlanTests.cs:96`](../tests/PrepPulse.Tests/PlanTests.cs#L96): `Missing_pieces_are_reported_together`. [`PlanTests.cs:133`](../tests/PrepPulse.Tests/PlanTests.cs#L133): invalid YAML is an error, not a crash.

### P14. Test doubles and a time harness
Tests drive the real engine with a hand-moved clock and an in-memory store, one simulated second per tick. A whole session runs in milliseconds and reads like the behaviour being checked.

**In the code**
- [`TestSupport.cs:8-22`](../tests/PrepPulse.Tests/TestSupport.cs#L8-L22): `FakeClock`, fixed at +11:00 so day boundaries are tested off-UTC.
- [`TestSupport.cs:25`](../tests/PrepPulse.Tests/TestSupport.cs#L25): `FakeStore`, an in-memory `ISessionStore`.
- [`TestSupport.cs:145-177`](../tests/PrepPulse.Tests/TestSupport.cs#L145-L177): `EngineHarness`. `Spend(seconds, what, typing)` is at [`:163-172`](../tests/PrepPulse.Tests/TestSupport.cs#L163-L172).
- [`EngineTests.cs:55`](../tests/PrepPulse.Tests/EngineTests.cs#L55): a typical test, `Idle_gap_is_backdated_to_the_last_input`.

---

## 3. Critical decisions

Each decision gives what was chosen, what it was weighed against, and why, then where it lives in the code.

### D1. A per-user tray app, not a Windows Service
Services run in Session 0 and can't see the foreground window, last input, taskbar or notifications of your session. Everything runs in one process that starts at logon. The Generic Host keeps a later split possible: storage and API in a service, capture in the app.

- [`PrepPulse.App.csproj:4`](../src/PrepPulse.App/PrepPulse.App.csproj#L4): `WinExe`, a desktop process in your session.
- [`Helpers.cs:112-134`](../src/PrepPulse.App/Ui/Helpers.cs#L112-L134): `StartWithWindows` via the per-user `Run` key, so no admin rights are needed.
- [`Program.cs:105-122`](../src/PrepPulse.App/Program.cs#L105-L122): hosted services that could move into a service later.

### D2. You pick the plan at session start, and all the session's time goes to it
Considered: auto-assigning time from whatever is in focus. That's ambiguous when an app or site is in several plans (`Code.exe`, `arxiv.org`). Instead, switching is one click. PrepPulse *suggests* a switch after a few minutes on another plan's site, but never switches on its own.

- [`Shell.cs:76-77`](../src/PrepPulse.App/Ui/Shell.cs#L76-L77): the picker opens at launch. [`Shell.cs:200-213`](../src/PrepPulse.App/Ui/Shell.cs#L200-L213): `ShowPicker`, with the last-used plan preselected.
- [`FocusEngine.cs:48-66`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L48-L66): `StartSession(planSlug)`. [`FocusEngine.cs:74-89`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L74-L89): `SwitchPlan`.
- [`FocusEngine.cs:396-416`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L396-L416): `EvaluateSuggestion`. It uses `Classifier.OtherPlanFor` ([`Matching.cs:164-172`](../src/PrepPulse.Core/Matching/Matching.cs#L164-L172)) and only raises `PlanSuggested`.
- [`Commands.cs:51-52`](../src/PrepPulse.App/Services/Commands.cs#L51-L52): `start X` during a session switches instead of starting a second session.

### D3. Plans are YAML files in a folder, and that folder is the source of truth
Considered: plans in the database behind an editor. Files are diff-able, editable in any editor, easy to back up, and need no UI to start. History stores the plan's slug and name as text on each record, with no foreign key to a plan table. So editing, renaming or deleting a plan never breaks old data.

- [`PlanLoader.cs:32-68`](../src/PrepPulse.Core/Plans/PlanLoader.cs#L32-L68): `LoadFolder`. Each `*.yaml` is a plan, and its file name is the slug ([`:70-71`](../src/PrepPulse.Core/Plans/PlanLoader.cs#L70-L71)).
- [`SqliteStore.cs:56-64`](../src/PrepPulse.Data/SqliteStore.cs#L56-L64): `sessions.plan_slug` / `plan_name` are plain `TEXT`.
- [`AppPaths.cs:60-70`](../src/PrepPulse.App/AppPaths.cs#L60-L70): example plans are copied in once (`.seeded` marker), then never overwritten.

### D4. Poll once a second instead of WinEvent hooks
Polling also catches browser tab switches, where the title changes but the window doesn't. It's trivial to test. One `GetForegroundWindow` + `GetLastInputInfo` a second costs nothing measurable.

- [`SamplerService.cs:28`](../src/PrepPulse.App/Services/SamplerService.cs#L28): `PeriodicTimer(TimeSpan.FromSeconds(1))`.
- [`WindowProbe.cs:31`](../src/PrepPulse.App/Probes/WindowProbe.cs#L31): `Foreground()`. [`WindowProbe.cs:78`](../src/PrepPulse.App/Probes/WindowProbe.cs#L78): Store apps resolved through `ApplicationFrameHost`.
- [`Native.cs:130-137`](../src/PrepPulse.App/Interop/Native.cs#L130-L137): `IdleFor()`.

### D5. Idle pauses are backdated to the last input, but never across a recorded boundary
A 7-minute pause records as 7 minutes, not 5 (it started 2 minutes before the threshold was crossed). *Live-test finding:* typing in one app, switching without input, then going idle made the pause overlap the previous segment. Fixed with a "last boundary" floor.

- [`FocusEngine.cs:308-316`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L308-L316): gap start = last input, floored at `LastBoundary` ([`:311-313`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L311-L313)).
- [`FocusEngine.cs:505`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L505) and [`:519`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L519): `CloseSegment` / `CloseGap` record the boundary.
- [`FocusEngine.cs:309`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L309): `passive` entries (lectures) never go idle (`m?.Passive != true`).
- Tests:
  - [`EngineTests.cs:55`](../tests/PrepPulse.Tests/EngineTests.cs#L55): backdating.
  - [`EngineTests.cs:74`](../tests/PrepPulse.Tests/EngineTests.cs#L74): `Idle_pause_never_overlaps_the_previous_segment`.
  - [`EngineTests.cs:91`](../tests/PrepPulse.Tests/EngineTests.cs#L91): passive entries.

### D6. Alerts look at what's in front, not at whether you're typing
*Live-test finding:* sitting on Paint without input started an idle pause at 30 s. That reset the distraction timer just before its own 30 s threshold, so the alert never came. In real life, watching Netflix without touching the keyboard would never have alerted.

Distraction and off-plan time now count regardless of input. When thresholds overlap, priority is distraction → no input → not in the plan. Time idle on a distraction is still *recorded* as an idle pause (one rule for all apps), but it *alerts* as a distraction.

- [`FocusEngine.cs:350-356`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L350-L356): `DistractionSince` / `OffTaskSince` depend only on the list, not on idle.
- [`FocusEngine.cs:377-393`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L377-L393): the priority order.
- [`FocusEngine.cs:358-375`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L358-L375): an open alert clears on refocus, and beeps repeat up to `MaxRepeats`.
- [`EngineTests.cs:134`](../tests/PrepPulse.Tests/EngineTests.cs#L134): `Distraction_alert_fires_even_with_no_input`.

### D7. Sleep, restart and shutdown end the session; a lock pauses it
A lock pauses the session, then ends it after 30 minutes (configurable).

Sleep is detected two ways, because Modern Standby laptops don't always send the event:
- the suspend event;
- a gap of more than 60 s between ticks.

A crash is closed at the last heartbeat (P11). Sessions never restart on their own; after waking you get a "start a new one" notification.

- [`Shell.cs:165-187`](../src/PrepPulse.App/Ui/Shell.cs#L165-L187): `SystemEvents` power, lock and shutdown handlers → engine.
- [`FocusEngine.cs:158-178`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L158-L178): `OnLock`, `OnUnlock`, `OnSuspend`, `OnShutdown`.
- [`FocusEngine.cs:242-247`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L242-L247): clock-jump sleep detection (`SleepGap` at [`:14`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L14)).
- [`FocusEngine.cs:266-277`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L266-L277): lock timeout ends the session *at the moment of locking*.
- [`Shell.cs:107-118`](../src/PrepPulse.App/Ui/Shell.cs#L107-L118): "Click to start a new one" balloon.
- Tests:
  - [`EngineTests.cs:204`](../tests/PrepPulse.Tests/EngineTests.cs#L204): lock.
  - [`EngineTests.cs:228`](../tests/PrepPulse.Tests/EngineTests.cs#L228): clock jump.
  - [`EngineTests.cs:246`](../tests/PrepPulse.Tests/EngineTests.cs#L246): sleep and shutdown events.

### D8. Websites come from the browser's address bar via UI Automation, domain only
Considered: a browser extension, which is more robust but another install. UI Automation needs nothing extra.

If the address bar can't be read (an Edge/Chrome app window, an unusual browser), the window title is matched against the site's name. That's how the Reddit app window was recognised in the live test. Private/incognito windows are never matched to sites.

- [`BrowserAddressProbe.cs:63-91`](../src/PrepPulse.App/Probes/BrowserAddressProbe.cs#L63-L91): `Read`: Firefox `urlbar-input` → Chromium "Address and search bar" → first edit box ([`:71-80`](../src/PrepPulse.App/Probes/BrowserAddressProbe.cs#L71-L80)).
- [`Matching.cs:43`](../src/PrepPulse.Core/Matching/Matching.cs#L43): `DomainExtractor.FromAddress` keeps only the host (and port).
- [`Matching.cs:108-116`](../src/PrepPulse.Core/Matching/Matching.cs#L108-L116): `TitleMentionsSite`, the title fallback (scored 1.5).
- [`Matching.cs:31-35`](../src/PrepPulse.Core/Matching/Matching.cs#L31-L35): `IsPrivateTitle`. [`SamplerService.cs:65-67`](../src/PrepPulse.App/Services/SamplerService.cs#L65-L67): private windows aren't even probed. [`Matching.cs:78`](../src/PrepPulse.Core/Matching/Matching.cs#L78): never site-matched.
- [`DashboardBuilder.cs:446`](../src/PrepPulse.Core/Reporting/DashboardBuilder.cs#L446): `Label`: a title-matched browser segment shows as the site, not "msedge" (live-test fix; test at [`ReportAndStoreTests.cs:97`](../tests/PrepPulse.Tests/ReportAndStoreTests.cs#L97)).

### D9. Privacy by default
- No keystroke content is read: `GetLastInputInfo` only gives a timestamp.
- Window titles are stored only for focus-list matches.
- Websites are stored as the domain only.
- Data stays on the PC:
  - the dashboard binds to 127.0.0.1 and rejects other Host headers (DNS-rebinding guard);
  - the command pipe is current-user-only.

In the code:
- [`FocusEngine.cs:527-529`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L527-L529): titles only for focus matches; private windows stored as `"private"`.
- [`Program.cs:112`](../src/PrepPulse.App/Program.cs#L112): `Listen(IPAddress.Loopback, port)`.
- [`DashboardApi.cs:20-31`](../src/PrepPulse.App/Services/DashboardApi.cs#L20-L31): Host header check.
- [`Commands.cs:123-124`](../src/PrepPulse.App/Services/Commands.cs#L123-L124): `PipeOptions.CurrentUserOnly`.

### D10. Few dependencies, all replaceable
The only NuGet packages are YamlDotNet, Dapper and Microsoft.Data.Sqlite, plus the test libraries. Built-in alternatives were chosen over:

| Planned | Used instead | Why |
|---|---|---|
| H.NotifyIcon | WinForms `NotifyIcon` | Built into .NET |
| Windows App SDK toasts | PrepPulse's own topmost popup + tray balloons | Do Not Disturb can't hide the popup, and there's no packaging/bootstrapper work |
| WebView2 | The default browser | No extra runtime to install |
| CsWin32 | Hand-written P/Invoke | About 20 functions |
| Serilog | A small file logger | Plain log files are enough here |

All of these can be swapped later without touching Core.

- [`PrepPulse.Core.csproj:10`](../src/PrepPulse.Core/PrepPulse.Core.csproj#L10) and [`PrepPulse.Data.csproj:11`](../src/PrepPulse.Data/PrepPulse.Data.csproj#L11): the package references.
- [`TrayIcon.cs:30`](../src/PrepPulse.App/Ui/TrayIcon.cs#L30): WinForms `NotifyIcon`.
- [`AlertWindow.cs:23-25`](../src/PrepPulse.App/Ui/AlertWindow.cs#L23-L25): own popup, topmost, doesn't steal focus.
- [`SessionWindow.cs:72-75`](../src/PrepPulse.App/Ui/SessionWindow.cs#L72-L75): taskbar progress and thumbnail buttons via WPF `TaskbarItemInfo`. [`Native.cs:139-151`](../src/PrepPulse.App/Interop/Native.cs#L139-L151): `FlashWindowEx`.
- [`AppLog.cs:8`](../src/PrepPulse.App/AppLog.cs#L8): `FileLoggerProvider`.

### D11. Time is stored as UTC epoch milliseconds and bucketed into local days in C#
SQLite's date functions convert offsets to UTC, which would put a 10 pm session on the wrong day in Sydney. So SQL only compares and clips numbers, and calendar days are worked out in C#. Segments that cross midnight are split between the two days.

- [`Time.cs:46-49`](../src/PrepPulse.Core/Time.cs#L46-L49): `ToMs` / `FromMs`. [`Time.cs:40-41`](../src/PrepPulse.Core/Time.cs#L40-L41): `OverlapDay`.
- [`SqliteStore.cs:210-222`](../src/PrepPulse.Data/SqliteStore.cs#L210-L222): `SumPrep` clips with scalar `MIN`/`MAX`, with no date functions.
- [`DashboardBuilder.cs:295`](../src/PrepPulse.Core/Reporting/DashboardBuilder.cs#L295): each day takes only its overlap of a segment.
- [`ReportAndStoreTests.cs:111`](../tests/PrepPulse.Tests/ReportAndStoreTests.cs#L111): `A_segment_across_midnight_is_split_between_days`.

### D12. Readable storage
Lists, triggers, responses and end reasons are short text keys (`focus`, `no_input`, `lock_timeout`), so the database makes sense in any SQLite browser.

- [`EngineTypes.cs:60-125`](../src/PrepPulse.Core/Engine/EngineTypes.cs#L60-L125): `Keys`, enum → key and key → label, in one place.

### D13. Security patch picked up from the first build
NuGet audit flagged `SQLitePCLRaw.lib.e_sqlite3 2.1.11` (CVE-2025-6965, fixed in SQLite 3.50.2). Moving to Microsoft.Data.Sqlite 10.0.14 brings SQLitePCLRaw 2.1.13. The build now has zero warnings.

- [`PrepPulse.Data.csproj:11`](../src/PrepPulse.Data/PrepPulse.Data.csproj#L11).

### D14. WPF windows built in code, explicit usings in the App project
WPF and WinForms both define `Application`, `Button`, `Color`…, so the App project turns implicit usings off. The windows are small enough that code is clearer than XAML plus code-behind.

- [`PrepPulse.App.csproj:8-9`](../src/PrepPulse.App/PrepPulse.App.csproj#L8-L9): `ImplicitUsings` disabled, with the reason.
- [`AlertWindow.cs:42-67`](../src/PrepPulse.App/Ui/AlertWindow.cs#L42-L67): a window's layout built in code.

### D15. Test isolation
- `--data-dir` gives a test run its own config, plans, database and instance (pipe/mutex).
- `tests/testrun/settings.json` makes every threshold fast.
- `build.cmd` asks a running test instance to exit first, so rebuilding never fails on a locked exe.

In the code:
- [`AppPaths.cs:37-50`](../src/PrepPulse.App/AppPaths.cs#L37-L50): `Resolve`: `--data-dir root` → `root\config` + `root\data`.
- [`tests/testrun/settings.json:3-14`](../tests/testrun/settings.json#L3-L14): 30 s idle, 30 s distraction, 90 s no input, 1 min break.
- [`build.cmd:13-15`](../build.cmd#L13-L15): `--exit` sent to the test instance before building.

### D16. Stay light in the background (v0.2)
Measured on the installed v0.1 after 3.7 hours idle: 103 MB private memory, 0% CPU, a 34 MB install folder, and log files that were never deleted. What changed:

- **The dashboard's web server starts only when you open the dashboard**, and stops after `DashboardIdleMinutes` (default 10) without a request. The background services now run on a plain Generic Host, so Kestrel and routing aren't running most of the day. This supersedes the "Kestrel in the composition root" part of P1.
  [`DashboardServer.cs`](../src/PrepPulse.App/Services/DashboardServer.cs), [`Program.cs`](../src/PrepPulse.App/Program.cs).
- **Idle-aware sampling.** Once you're idle past the idle threshold, the sampler checks every 5 seconds instead of every second. Any input brings back 1-second sampling, and idle pauses are still dated from your last input.
  [`Housekeeping.ShouldSample`](../src/PrepPulse.Core/Housekeeping.cs), [`SamplerService.cs`](../src/PrepPulse.App/Services/SamplerService.cs).
- **Log retention.** Daily logs older than 30 days are deleted at startup. Only files named `preppulse-yyyyMMdd.log` are ever touched. [`Housekeeping.ExpiredLogs`](../src/PrepPulse.Core/Housekeeping.cs).
- **The session window is closed when a session ends**, not hidden, so nothing stays in memory between sessions. [`Shell.cs`](../src/PrepPulse.App/Ui/Shell.cs).
- **Smaller GC footprint:** no background GC thread, and `System.GC.ConserveMemory=5`. [`PrepPulse.App.csproj`](../src/PrepPulse.App/PrepPulse.App.csproj).
- **The install is Windows x64 only and pre-compiled (ReadyToRun).** It no longer ships SQLite for Linux and macOS, and the program folder is emptied before each install. [`install.cmd`](../install.cmd).

Not done: replacing the WinForms tray icon with direct Win32 calls, which would remove WinForms from the process. That's a bigger change, kept separate.

---

## 4. Where it differs from the design

| Design | v0.1 | Note |
|---|---|---|
| Plan editor in the app | Plan files only (+ README in the plans folder) | As asked: "add the study plan to the folder" |
| Return prompt after a pause | Folded into the alert popup ("I'm reading" marks the pause as study) | One popup instead of two |
| Windows toasts with buttons | Own popup + tray balloons | Not hidden by Do Not Disturb |
| Screen-edge flash | Not built | Tray blink, taskbar flash, beep and popup are in |
| Calendar, best-hours heatmap, switch rate | Not built | Daily vs expected, burn-up/weekly, focus ratio, topics, timeline, apps open, alerts are in |
| Dashboard in a WebView2 window | Opens in your browser | |

## 5. How it was verified

**Unit tests (77, all passing).** They cover:
- plan parsing and validation, and duration parsing;
- domain extraction, matching and specificity;
- the engine second by second: focus, idle backdating, passive entries, every alert type, beeps and repeats, "I'm reading", break, lock, lock timeout, sleep by event and by clock jump, shutdown, open apps, heartbeat, own windows;
- the dashboard model: tiles, timeline, burn-up, all-plans view, midnight split, browser labels;
- the SQLite store: round trip, clipping, crash recovery, and engine → SQLite → report end to end ([`ReportAndStoreTests.cs:191`](../tests/PrepPulse.Tests/ReportAndStoreTests.cs#L191)).

**Live test on Windows 11 (isolated data, fast thresholds):**

| Step | Expected | Seen |
|---|---|---|
| Launch | Plan picker with 3 plans and today's targets | ✓ |
| Start "Smoke test", type in Notepad | Focus segment, topic "Writing notes", title kept | ✓ 09:23:04–09:23:27 |
| Paint in front, no typing | Distraction alert at 30 s, beep, red flashing taskbar | ✓ fired 09:23:57 (since 09:23:27), repeat beep at 30 s *(failed before fix D6)* |
| Reddit app window | Matched as a distraction by title | ✓ `reddit.com`, 25 s |
| Back to Notepad | Popup closes, alert marked refocused | ✓ 09:24:36 |
| Idle in Notepad | Pause from last input; no-input alert at 90 s | ✓ alert 09:26:08, "No input for 91 seconds" |
| Click "Break 1 min" | Pause labelled break; "break over" after 60 s | ✓ 09:26:29–09:27:29 |
| Earlier run: "I'm reading", "End session", off-plan alert | Recorded answers; session ends; "1 minute off your plan" | ✓ |
| Apps open | All windowed apps with open vs focus time; open distraction flagged at start | ✓ 8 apps; Paint flagged |
| Stop + `--report` | Session ended "manual"; dashboard JSON from the database | ✓ rendered in the dashboard |
| Browser probe | Domain read from Edge's address bar | ✓ `learn.microsoft.com` |
| `--exit` via pipe | Running instance shuts down cleanly | ✓ |

**Bugs the live test found, and their fixes:**

| Bug | Fix | Test |
|---|---|---|
| No distraction alert while idle on a distraction | D6, [`FocusEngine.cs:350-356`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L350-L356) | [`EngineTests.cs:134`](../tests/PrepPulse.Tests/EngineTests.cs#L134) |
| A pause overlapped the previous app's segment | D5, [`FocusEngine.cs:311-313`](../src/PrepPulse.Core/Engine/FocusEngine.cs#L311-L313) | [`EngineTests.cs:74`](../tests/PrepPulse.Tests/EngineTests.cs#L74) |
| "No input for 2 minutes" shown for a 90 s threshold | [`Durations.cs:49-54`](../src/PrepPulse.Core/Durations.cs#L49-L54) `Spoken` uses seconds under 2 min | [`PlanTests.cs:33`](../tests/PrepPulse.Tests/PlanTests.cs#L33) |
| A title-matched browser app showed as "msedge" | D8, [`DashboardBuilder.cs:446`](../src/PrepPulse.Core/Reporting/DashboardBuilder.cs#L446) | [`ReportAndStoreTests.cs:97`](../tests/PrepPulse.Tests/ReportAndStoreTests.cs#L97) |

**Not tested live:** real sleep, lock and restart, because they would have interrupted the remote session. Unit tests cover them by driving the same engine methods that the Windows events call ([`Shell.cs:165-187`](../src/PrepPulse.App/Ui/Shell.cs#L165-L187)).

## 6. Known limitations

- The Chrome/Edge address bar is found by its English name first, then falls back to "the first edit box" ([`BrowserAddressProbe.cs:74-80`](../src/PrepPulse.App/Probes/BrowserAddressProbe.cs#L74-L80)). Other UI languages are likely fine, but haven't been checked.
- New Windows 11 tray icons start hidden behind the `^` overflow. Drag PrepPulse out once, or turn it on in Settings › Personalization › Taskbar.
- If the hotkey (default Ctrl+Alt+P) is taken by another app, the conflict is logged and the tray still works. Change it in `settings.json`.
- Single machine, single user, by design.
