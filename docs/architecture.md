# PrepPulse — architecture, patterns and decisions

What was built in v0.1, how it's put together, the decisions that shaped it, and what the live test on Windows found.

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

## 2. Patterns used

| Pattern | Where | Why |
|---|---|---|
| **Composition root + Generic Host** | `Program.Run` builds one `WebApplication` that owns DI, logging, the background services and Kestrel; WPF runs the message loop beside it | One place wires everything; services get lifetimes and clean shutdown for free |
| **Ports and adapters (hexagonal)** | Core defines `IClock`, `ISessionStore`, `IReportSource`; Data and App provide the adapters | The engine and reports never touch Windows, SQLite or the UI, so they're tested with fakes |
| **State machine** | `FocusEngine`: session states `Running / Paused / Locked / OnBreak` and focus states `Focused / Drifting / Idle / FocusLost` | Lock, sleep, break, alerts and refocus interact; explicit states keep transitions predictable |
| **Functional core, imperative shell** | `FocusEngine` and `DashboardBuilder` are deterministic given samples + an injected clock; I/O sits at the edges | Whole scenarios (an hour of focus, a sleep, a lock) replay in milliseconds in tests |
| **Immutable snapshots, swap on reload** | `PlanCatalogSnapshot`, `EngineSnapshot`, `AppSettings` are replaced, never mutated | A plan-file edit mid-tick can't change data under the engine; the UI gets a consistent picture |
| **Observer + UI-thread marshalling** | Engine raises `Changed` / `Event` after releasing its lock; `Shell` re-posts to the WPF dispatcher | The sampler thread never blocks on the UI, and the UI never sees half-updated state |
| **Command + single-instance IPC** | `CommandRouter` handles `start/stop/pause/break/status/dashboard/exit`; a second `PrepPulse.exe` forwards its arguments over a per-user named pipe | One code path for tray, hotkey, scripts and tests |
| **Specificity scoring (rule matching)** | `EntryMatcher`: app = 1, site by title = 1.5, site by domain = 2, +4 when title words are required; highest wins, ties go to the focus list | "youtube.com \| system design" (focus) beats "youtube.com" (distraction) without special cases |
| **Repository / data mapper** | `SqliteStore` maps Dapper row classes to domain records; schema versioned with `PRAGMA user_version` | Storage details (epoch ms, column names) stay out of the domain |
| **Polling sampler with cached slow lookups** | `SamplerService` ticks every second; UI Automation lookups run off-thread with an 800 ms limit, cached per window + title | Cheap per tick; the slow call happens only on a tab or window switch |
| **Heartbeat + recovery** | Session heartbeat every 15 s; on start, unfinished sessions are closed at their last heartbeat | Crashes and power cuts don't leave open sessions or inflated totals |
| **Debounced file watcher** | `CatalogService` reloads plans/settings 600 ms after the last change | Editors write files several times per save |
| **Test doubles and a time harness** | `FakeClock`, `FakeStore`, `EngineHarness.Spend(seconds, what, typing)` | Tests read like the behaviour they check |

## 3. Critical decisions

Each one lists what was chosen, what it was weighed against, and why.

**D1. A per-user tray app, not a Windows Service.** Services run in Session 0 and can't see the foreground window, last input, taskbar or notifications of your session. Everything runs in one process that starts at logon. The Generic Host keeps a later split possible (storage and API in a service, capture in the app) without a rewrite.

**D2. You pick the plan when a session starts; all time goes to that plan.** Considered: auto-assigning time from whatever is in focus. That's ambiguous when an app or site is in several plans (`Code.exe`, `arxiv.org`). Instead, switching is one click, and PrepPulse *suggests* a switch after a few minutes on another plan's site, but never switches on its own.

**D3. Plans are YAML files in a folder, and that folder is the source of truth.** Considered: storing plans in the database behind an editor. Files are diff-able, editable in VS Code, easy to back up, and need no UI to start. The database stores the plan's slug and name as text on each record (no foreign keys to plan rows), so history survives editing, renaming or deleting a plan.

**D4. Poll once a second instead of WinEvent hooks.** Polling also catches browser tab switches (the title changes, the window doesn't) and is trivial to test. One `GetForegroundWindow` + `GetLastInputInfo` a second costs nothing measurable.

**D5. Idle pauses are backdated to the last input, but never across a recorded boundary.** A 7-minute pause records as 7 minutes, not 5. *Live-test finding:* typing in one app, switching without input, then going idle made the pause overlap the previous segment. Fixed with a "last boundary" floor, and covered by a test.

**D6. Alerts look at what's in front, not at whether you're typing.** *Live-test finding:* sitting on Paint without input started an idle pause at 30 s, which reset the distraction timer just before its own 30 s threshold, so the alert never came. In real life, watching Netflix without touching the keyboard never alerted. Now distraction and off-plan time count regardless of input. Priority when thresholds overlap: distraction → no input → not in the plan. Time spent idle on a distraction is still *recorded* as an idle pause (one rule for all apps), but it *alerts* as a distraction.

**D7. Sleep, restart and shutdown end the session; a lock pauses it.** A lock pauses the session and ends it after 30 minutes (configurable). Sleep is detected two ways: the suspend event, and a >60 s jump between ticks, because Modern Standby laptops don't always send the event. A crash is closed at the last heartbeat. Sessions never restart on their own; after waking you get a "start a new one" notification.

**D8. Websites come from the browser's address bar via UI Automation, domain only.** Considered: a browser extension (more robust, but another install). UI Automation needs nothing extra. If the address bar can't be read (an Edge/Chrome app window, an unusual browser), the window title is matched against the site's name. That's how the Reddit app window was recognised in the live test. Private/incognito windows are never matched to sites.

**D9. Privacy by default.**
- No keystroke content: `GetLastInputInfo` gives only a timestamp.
- Window titles are stored only for focus-list matches.
- Websites are stored as the domain only.
- Data stays on the PC: the dashboard binds to 127.0.0.1 and rejects other Host headers (DNS-rebinding guard).
- The command pipe is current-user-only.

**D10. Few dependencies, all replaceable.** The only NuGet packages are YamlDotNet, Dapper and Microsoft.Data.Sqlite, plus the test libraries. Built-in alternatives were chosen over:

| Planned | Used instead | Why |
|---|---|---|
| H.NotifyIcon | WinForms `NotifyIcon` | Built into .NET |
| Windows App SDK toasts | PrepPulse's own topmost popup + tray balloons | Do Not Disturb can't hide the popup, and there's no packaging/bootstrapper work |
| WebView2 | The default browser | No extra runtime to install |
| CsWin32 | Hand-written P/Invoke | About 20 functions |
| Serilog | A 60-line file logger | Plain log files are enough here |

All of these can be swapped later without touching Core.

**D11. Time is stored as UTC epoch milliseconds and bucketed into local days in C#.** SQLite's date functions convert offsets to UTC, which would put a 10 pm session on the wrong day in Sydney. Segments that cross midnight are split between the two days.

**D12. Readable storage.** Lists, triggers, responses and end reasons are short text keys (`focus`, `no_input`, `lock_timeout`), so the database makes sense in any SQLite browser.

**D13. Security patch picked up from the first build.** NuGet audit flagged `SQLitePCLRaw.lib.e_sqlite3 2.1.11` (CVE-2025-6965, fixed in SQLite 3.50.2). Moving to Microsoft.Data.Sqlite 10.0.14 brings SQLitePCLRaw 2.1.13. The build now has zero warnings.

**D14. WPF windows built in code, explicit usings in the App project.** WPF and WinForms both define `Application`, `Button`, `Color`…, so the App project turns implicit usings off. The windows are small enough that code is clearer than XAML plus code-behind.

**D15. Test isolation.**
- `--data-dir` gives a test run its own config, plans and database.
- `tests/testrun/settings.json` makes every threshold fast (idle 30 s, distraction 30 s, no input 90 s).
- `build.cmd` first asks a running test instance to exit, so rebuilding never fails on a locked exe.

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

**Unit tests (77, all passing):** plan parsing and validation, duration parsing, domain extraction, matching and specificity, the engine second by second (focus, idle backdating, passive entries, every alert type, beeps and repeats, "I'm reading", break, lock, lock timeout, sleep by event and by clock jump, shutdown, open apps, heartbeat, own windows), the dashboard model (tiles, timeline, burn-up, all-plans, midnight split, browser labels), and the SQLite store (round trip, clipping, crash recovery, engine → SQLite → report end to end).

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

**Not tested live:** real sleep, lock and restart (they would have interrupted the remote session). These are covered by unit tests that drive the same engine methods the Windows events call.

## 6. Known limitations

- The Chrome/Edge address bar is found by its English name first, then falls back to "the first edit box". Other UI languages are likely fine, but haven't been checked.
- New Windows 11 tray icons start hidden behind the `^` overflow. Drag PrepPulse out once, or turn it on in Settings › Personalization › Taskbar.
- If the hotkey (default Ctrl+Alt+P) is taken by another app, it's logged and the tray still works. Change it in `settings.json`.
- Single machine, single user, by design.
