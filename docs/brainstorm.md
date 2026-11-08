# PrepPulse — brainstorm

A Windows background tracker for interview prep. It records **sessions** (start/stop), **which program has focus** and for how long, and **pauses** with no keyboard or mouse input, alerts you after **5 minutes off-task**, and charts daily progress against what was planned.

Design templates: [`design/dashboard-templates.html`](design/dashboard-templates.html) (open it in a browser; it runs on sample data).

---

## 1. Two parts, one engine

| | **A. Current session — live focus tracking** | **B. Overall progress** |
|---|---|---|
| Question it answers | "Am I focused right now?" | "Am I on track for the interview?" |
| Where it lives | Tray icon in the notification area (bottom-right, by the clock), taskbar button while a session runs, flyout, toast/flash/beep alerts | Dashboard window opened from the tray |
| Shows | Session clock, app in focus now, no-input timer, today's % of target, focus-lost alerts, return prompt, today's session timeline | Today at a glance, daily progress vs expected, burn-up to interview day, calendar, topic coverage, focus-quality trends |
| Updates | Every second | On open / every minute |

Both read from the same **capture engine** (foreground window + input + lock events) and the same **SQLite database**, using the same **category rules** and **plan/targets**.

## 2. The Windows Service constraint

Since Windows Vista, services run in **Session 0**, isolated from the logged-in user's desktop. From a service, `GetForegroundWindow` and `GetLastInputInfo` don't see your session, and there's no UI, taskbar button or toast. So the part that watches has to run **inside your session**:

| Option | Shape | Verdict |
|---|---|---|
| **A. Per-user app only** | One app (tray + taskbar + dashboard), starts at logon. Capture, storage and alerts in-process. | **Start here** |
| B. Service + per-user app | Windows Service owns the database and a localhost API; the app captures and shows UI, talking over a named pipe. | Later, if wanted. Same code, different hosting. |
| C. Service only | Service launches a helper into your session with `CreateProcessAsUser`. | Not worth the complexity or LocalSystem surface. |

The app is built on the .NET Generic Host, so moving storage + API into a real Windows Service later is `UseWindowsService()` plus IPC — not a rewrite.

## 3. Technology stack

| Layer | Choice | Why |
|---|---|---|
| Language / runtime | **C# on .NET 10 (LTS)**, `net10.0-windows` | Your main stack; first-class Win32 interop |
| App shell | **WPF** | Built-in `TaskbarItemInfo`: taskbar progress bar, green/yellow/red state, overlay badge, and thumbnail buttons (Pause · Break · Stop) with no extra interop |
| Tray icon | **H.NotifyIcon.Wpf** | Notification-area icon with context menu, live tooltip and a popup flyout; icon redrawn at runtime (progress ring + state colour) from a WPF `DrawingVisual` |
| Alerts — toast | **Windows App SDK app notifications** (`Microsoft.Windows.AppNotifications`) | Actionable toasts with buttons that call back into the app; work for unpackaged apps |
| Alerts — flash | Tray icon blink (swap icons on a timer), `FlashWindowEx` for the taskbar button, optional click-through topmost WPF window for a red screen-edge border | All still work when Do Not Disturb hides toasts |
| Alerts — sound | `System.Media.SystemSounds` or `SoundPlayer` for a custom `.wav` | Plays regardless of Do Not Disturb; repeat interval and cap are configurable |
| Capture | **Win32 via CsWin32** source generator | `SetWinEventHook`, `GetForegroundWindow`, `GetWindowText`, `QueryFullProcessImageName`, `GetLastInputInfo`, `RegisterHotKey`; `SystemEvents` for lock/sleep |
| Background work | **Microsoft.Extensions.Hosting** inside the WPF app | DI, logging, config, `BackgroundService`s: Capture, Session, Alerts, Api |
| Storage | **SQLite** via `Microsoft.Data.Sqlite` + **Dapper** | Single local file, zero setup, small schema |
| Dashboard | **ASP.NET Core minimal API** on `127.0.0.1` + HTML/JS with **Apache ECharts** (bundled, no CDN), shown in **WebView2** | The design templates become the real UI; also opens in any browser |
| Config | `appsettings.json` + `rules.json` (categories) | Hand-editable; rules editable from the dashboard |
| Tests | **xUnit** with a fake clock and a fake Win32 probe | Idle, focus-lost and alert logic tested without a desktop |
| Start at logon | Scheduled task at logon (or Startup folder) | No admin rights needed |
| Packaging | Self-contained single-folder publish; MSIX / winget later | |

**Why not WinUI 3:** taskbar progress, overlay and thumbnail buttons need COM interop there, and tray support is no better. **Why not Electron/Python:** heavier, and the Win32 pieces (foreground window, input, taskbar) are more natural in .NET.

```
PrepPulse.sln
├─ src/PrepPulse.Core        domain: sessions, segments, idle/alert rules, metrics (no Windows deps)
├─ src/PrepPulse.Capture     Win32 probes: foreground window, last input, lock/sleep, hotkey
├─ src/PrepPulse.Data        SQLite schema, migrations, queries (Dapper)
├─ src/PrepPulse.Api         minimal API for the dashboard
├─ src/PrepPulse.App         WPF: tray, taskbar, flyout, toasts, WebView2 dashboard; hosts everything
├─ web/                      dashboard HTML/JS/CSS (ECharts)
└─ tests/PrepPulse.Core.Tests
```

## 4. Part A — live focus tracking

### What gets captured

| Signal | API | Notes |
|---|---|---|
| Foreground window changes | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` + 1 s poll as a backstop | Poll also catches **title** changes (browser tab switches don't change the foreground window) |
| Window title | `GetWindowText` | Browser titles carry the useful part: "LeetCode – 3Sum", "ByteByteGo – Rate limiter" |
| Process | `GetWindowThreadProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` → `QueryFullProcessImageName` | UWP apps sit under `ApplicationFrameHost.exe` — resolve the child window. Elevated apps may refuse; record "unknown (elevated)". |
| Last input time | `GetLastInputInfo` polled every 1 s | Keyboard **and** mouse; a timestamp only, never key contents. `dwTime` is a 32-bit tick count that wraps after ~49.7 days — subtract with unsigned arithmetic. |
| Lock / unlock, sleep / resume | `SystemEvents.SessionSwitch`, `SystemEvents.PowerModeChanged` | Pause the session; mark the time as "away", not idle |
| Start / stop | Tray menu, flyout, thumbnail button, global hotkey (`RegisterHotKey`, e.g. Ctrl+Alt+P) | Optional later: auto-start when a prep app gets focus; auto-stop after a long idle |

### Taskbar and tray

- **Tray icon** — in the notification area at the bottom-right, next to the clock — is always present. It's drawn as a ring: how full = today's progress, colour = focus state. The tooltip shows `Session 3 · 47:12 · Focused: Loop Prep · Today 3h 05m / 4h`. Click opens the flyout (clock, app in focus, no-input timer, Pause / Stop); right-click has Start/Stop, Pause tracking, Open dashboard, Settings, Exit.
- **Windows 11 overflow:** new tray icons start hidden behind the `^` chevron, and Windows gives apps no supported way to pin themselves. On first run PrepPulse shows a one-time tip and opens `ms-settings:taskbar` (Personalization › Taskbar › Other system tray icons) so you can switch it on, or you can drag it out of the overflow.
- **Taskbar button** appears only while a session runs (the small session window has `ShowInTaskbar=true`; it hides when the session stops).
  - **Progress bar length** = today's prep time ÷ today's target.
  - **Progress bar colour** = current focus state: green *Focused* · yellow *Idle* (no input ≥ 2 min) · red *Focus lost* (off-task past the alert threshold, also flashes).
  - **Thumbnail buttons** on hover: Pause · Break 10 min · Stop.
- Status colours are always paired with a shape or word (●/◆/▲, tooltip text), never colour alone.

### Idle and focus-lost rules

Two thresholds, both configurable:

| Threshold | Default | Effect |
|---|---|---|
| Idle gap recorded | **2 min** without input | Gap is stored, **backdated to the last input**; taskbar turns yellow |
| Focus-lost alert | **5 min** off-task | Toast, flashing tray icon and taskbar button, beep (each can be turned off) |

"Off-task" means either:
1. **No input for 5 min**, unless the focused app is on the passive list (videos, PDF readers) — those count as prep while focused; or
2. **5 continuous minutes in a distraction or uncategorised app** (WhatsApp, Reddit, an app no rule matches).

Alert behaviour:
- One alert per gap; nothing repeats until you're back on task.
- Toast buttons: **Back to it** · **Break 10 min** (snoozes alerts, pings when the break ends) · **End session**. The no-input toast swaps "Back to it" for **I'm reading**, which records the gap as prep.
- No alerts while the PC is locked or asleep — the session pauses instead.
- If Windows Do Not Disturb suppresses the toast, the taskbar still turns red and flashes.
- On return from a gap, the **return prompt** (Break / Reading / Thinking / Skip) labels it; ignored prompts stay idle.

### Getting your attention: flash and beep

Everything here is configurable from the Settings window (and `appsettings.json`):

```json
"Alerts": {
  "FocusLostMinutes": 5,              // how long focus can be lost before reacting
  "IdleAfterMinutes": 2,              // when no input starts counting as idle
  "Triggers": { "NoInput": true, "OffTaskApp": true },
  "Flash": { "TrayIcon": true, "TaskbarButton": true, "ScreenEdge": false },
  "Sound": {
    "Enabled": true,
    "File": "SystemExclamation",      // or a path to a .wav
    "RepeatEveryMinutes": 2,
    "MaxRepeats": 3,
    "OffTaskAppOnly": false           // true = no beep for no-input gaps
  },
  "Toast": true
}
```

Escalation: at the threshold → toast + flashing + one beep. Still off-task → beep again every `RepeatEveryMinutes`, up to `MaxRepeats`, then keep flashing silently until you're back. Any input in a prep app stops it immediately.

- **Tray icon**: blinks red (two icons swapped every 500 ms).
- **Taskbar button**: `FlashWindowEx(FLASHW_TRAY | FLASHW_TIMERNOFG)` plus red progress state; stops when you return.
- **Screen edge** (off by default): a thin red border around every monitor, click-through, so it's noticed even in a full-screen app.
- **Beep**: a Windows system sound or your own `.wav`, with a **Test** button in settings.

## 5. Categorising time

Rules file (`rules.json`), first match wins:

```json
[
  { "category": "coding",   "process": "chrome|msedge", "title": "LeetCode|NeetCode|HackerRank" },
  { "category": "coding",   "process": "Code|devenv|rider64" },
  { "category": "design",   "title": "System Design|ByteByteGo|Excalidraw" },
  { "category": "ai",       "title": "arXiv|Hugging Face|RAG|LLM" },
  { "category": "mock",     "title": "Loop Prep" },
  { "category": "ai",       "title": "YouTube", "passiveAllowed": true },
  { "category": "distract", "process": "WhatsApp|Discord", "title": "Reddit|Facebook|Instagram|X$" }
]
```

Unmatched apps are `uncategorized` — they count as off-task for alerts, and the dashboard offers one-click "assign to…" that writes a new rule.

**Prep categories (count toward targets):** Coding practice · System design · AI / ML study · Mock & behavioral.
**Non-prep:** Distraction · Uncategorized · Idle.

## 6. Part B — overall progress

**MVP:** today at a glance (prep time vs target, focus ratio, longest focus block, idle pauses, plan pace) · daily progress vs expected · cumulative burn-up to interview day · focus ratio by day.
**Later:** prep calendar · topic coverage vs plan · app switches per hour · idle-pause histogram · best-hours heatmap · alerts review · weekly summary.

### Where "expected" comes from

- **Daily target** per weekday (e.g. 2 h weekdays, 4 h weekends) with per-date overrides.
- **Prep window**: start date → interview date.
- **Topic plan** (optional): hours per topic over the window.
- Later: import the week-by-week plan from **Loop Prep**.

## 7. Metrics

| Metric | Definition | Part |
|---|---|---|
| Session length | stop − start | A, B |
| Active prep time | time in prep categories with input in the last 2 min, plus gaps labelled Reading/Thinking | A, B |
| Idle gaps | count, total, longest, distribution by length | A, B |
| Distraction time | time in distraction/uncategorised apps during a session | A, B |
| Focus-lost alerts | alerts per session, by trigger (no input / off-task app) | A, B |
| Time to refocus | alert → next input in a prep-category app | B |
| Focus ratio | active prep ÷ session length | B |
| Longest focus block | longest run of prep categories without an idle gap or distraction | A, B |
| Switch rate | foreground-app changes ÷ session hours | B |
| Goal attainment | active prep ÷ daily target | A (taskbar), B |
| Streak | consecutive days with ≥ 1 session (or target met — to decide) | B |
| Plan pace | cumulative actual ÷ cumulative planned; projection = 7-day average × days left | B |
| Topic hours | active prep per topic vs topic plan | B |
| Best hours | average active prep by hour × weekday | B |

## 8. Data model (SQLite, first cut)

```sql
CREATE TABLE sessions (
  id INTEGER PRIMARY KEY,
  started_at TEXT NOT NULL,          -- ISO 8601 with offset
  ended_at   TEXT,                   -- NULL while running
  topic      TEXT,
  notes      TEXT,
  end_reason TEXT                    -- manual | auto_idle | lock | shutdown
);

CREATE TABLE focus_segments (
  id INTEGER PRIMARY KEY,
  session_id INTEGER NOT NULL REFERENCES sessions(id),
  started_at TEXT NOT NULL,
  ended_at   TEXT NOT NULL,
  process    TEXT NOT NULL,          -- e.g. chrome.exe
  title      TEXT,                   -- see privacy
  category   TEXT NOT NULL
);

CREATE TABLE idle_gaps (
  id INTEGER PRIMARY KEY,
  session_id INTEGER NOT NULL REFERENCES sessions(id),
  started_at TEXT NOT NULL,          -- backdated to last input
  ended_at   TEXT NOT NULL,
  label      TEXT                    -- NULL | break | reading | thinking | away
);

CREATE TABLE alerts (
  id INTEGER PRIMARY KEY,
  session_id   INTEGER NOT NULL REFERENCES sessions(id),
  fired_at     TEXT NOT NULL,
  trigger      TEXT NOT NULL,        -- no_input | off_task_app
  gap_started  TEXT NOT NULL,
  process      TEXT,
  response     TEXT,                 -- back | reading | break | end | ignored
  responded_at TEXT,
  refocused_at TEXT                  -- next input in a prep-category app
);

CREATE TABLE daily_goals (
  date           TEXT PRIMARY KEY,   -- YYYY-MM-DD
  target_minutes INTEGER NOT NULL
);

CREATE TABLE plan (
  id INTEGER PRIMARY KEY,
  starts_on TEXT NOT NULL,
  ends_on   TEXT NOT NULL,           -- interview date
  weekday_minutes INTEGER NOT NULL,
  weekend_minutes INTEGER NOT NULL
);
```

Daily rollups are queries, not stored, until performance says otherwise.

## 9. Privacy

- Never record keystroke content — `GetLastInputInfo` only gives a timestamp.
- Window titles can hold private things (email subjects, chat names). Default: **store titles only for prep-category apps**; other apps keep the process name only.
- Data stays on the machine. The repo is public, so `*.db` and `rules.local.json` are git-ignored.
- Tray menu has "Pause tracking"; the dashboard can delete a day.

## 10. Roadmap

1. **M0 – Design** *(this)*: two parts, metrics, charts, stack.
2. **M1 – Part A core**: solution skeleton, capture probes, session start/stop + hotkey, idle gaps, SQLite, tray icon. Verify with a `--dump` CLI.
3. **M2 – Part A presence**: taskbar progress/state + thumbnail buttons, flyout, focus-lost alerts (toast, flash, beep) with settings, return prompt, alert logging.
4. **M3 – Part B dashboard**: localhost API, WebView2 window, MVP charts, rules editor for uncategorised apps.
5. **M4 – Plan & goals**: targets, prep window, burn-up, topic plan.
6. **M5 – Hardening**: start at logon, installer (MSIX/winget), optional Windows Service split, tests in CI.
7. **M6 – Integrations**: Loop Prep plan import, weekly summary.

## 11. Open questions

1. Store window titles for all apps, or only prep apps (current default)?
2. Should uncategorised apps trigger the 5-minute alert, or only apps explicitly marked as distraction?
3. Streak = any session, or daily target met?
4. Link "expected" to Loop Prep's plan now, or start with a simple targets screen?
