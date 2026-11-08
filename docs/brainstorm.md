# PrepPulse — brainstorm

A Windows background tracker for interview prep. It records **sessions** (start/stop), **which app or website has focus** and for how long, **every app open** during the session, and **pauses** with no keyboard or mouse input. You feed in one or more **study plans** (each with its own targets, topics and focus list) and a shared **distraction list**; PrepPulse alerts you when focus is lost and charts progress against each plan.

Design templates: [`design/dashboard-templates.html`](design/dashboard-templates.html) (open it in a browser; it runs on sample data).

---

## 1. Two parts, one engine

| | **A. Current session — live focus tracking** | **B. Overall progress** |
|---|---|---|
| Question it answers | "Am I focused right now?" | "Am I on track for the interview?" |
| Where it lives | Tray icon in the notification area (bottom-right, by the clock), taskbar button while a session runs, flyout, toast/flash/beep alerts | Dashboard window opened from the tray |
| Shows | Session clock, app or site in focus now, no-input timer, today's % of target, focus-lost alerts, return prompt, apps open during the session, today's session timeline | Today at a glance, daily progress vs expected, burn-up to interview day, calendar, topic coverage, focus-quality trends |
| Updates | Every second | On open / every minute |

Both read from the same **capture engine** (foreground window, browser domain, open apps, input, power events) and the same **SQLite database**, using the same **study plans** (targets, topics, focus lists) and **distraction list**.

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
| Capture | **Win32 via CsWin32** source generator | `SetWinEventHook`, `GetForegroundWindow`, `GetWindowText`, `QueryFullProcessImageName`, `GetLastInputInfo`, `EnumWindows`, `RegisterHotKey` |
| Website detection | **UI Automation** via **FlaUI.UIA3** | Reads the browser address bar (Chrome, Edge, Brave, Firefox); keeps the domain only |
| Power and session events | `SystemEvents` (`SessionSwitch`, `PowerModeChanged`, `SessionEnding`) + a heartbeat | Lock pauses; sleep, restart, shutdown end the session |
| Background work | **Microsoft.Extensions.Hosting** inside the WPF app | DI, logging, config, `BackgroundService`s: Capture, Session, Alerts, Api |
| Storage | **SQLite** via `Microsoft.Data.Sqlite` + **Dapper** | Single local file, zero setup, small schema |
| Dashboard | **ASP.NET Core minimal API** on `127.0.0.1` + HTML/JS with **Apache ECharts** (bundled, no CDN), shown in **WebView2** | The design templates become the real UI; also opens in any browser |
| Config | `appsettings.json` for settings; focus/distraction lists in SQLite, import/export as `lists.json` | Lists edited in the app; JSON for backup and sharing |
| Tests | **xUnit** with a fake clock and a fake Win32 probe | Idle, focus-lost and alert logic tested without a desktop |
| Start at logon | Scheduled task at logon (or Startup folder) | No admin rights needed |
| Packaging | Self-contained single-folder publish; MSIX / winget later | |

**Why not WinUI 3:** taskbar progress, overlay and thumbnail buttons need COM interop there, and tray support is no better. **Why not Electron/Python:** heavier, and the Win32 pieces (foreground window, input, taskbar) are more natural in .NET.

```
PrepPulse.sln
├─ src/PrepPulse.Core        domain: sessions, segments, idle/alert rules, metrics (no Windows deps)
├─ src/PrepPulse.Capture     Win32 probes: foreground window, browser domain, open apps, last input, power events, hotkey
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
| Browser domain | UI Automation on the address bar when a browser has focus | Domain only (`leetcode.com`); private/incognito windows recorded as "Private browsing" with no domain |
| Open apps | `EnumWindows` (visible top-level windows) → process name, every 30 s and on each focus change | See "Apps open during the session" |
| Lock, sleep, restart, shutdown | `SystemEvents.SessionSwitch`, `PowerModeChanged`, `SessionEnding` + heartbeat | See "Session lifecycle" |
| Start / stop | Tray menu, flyout, thumbnail button, global hotkey (`RegisterHotKey`, e.g. Ctrl+Alt+P) | Optional later: auto-start when a prep app gets focus; auto-stop after a long idle |

### Apps open during the session

Besides the app in focus, PrepPulse records **every app with a visible window while the session runs**:
- Snapshot of visible top-level windows at session start, every 30 s, and on each focus change (tool windows and system processes skipped).
- Per app, per session: first seen, last seen, time open, time in focus, and which list it's on. Browsers also get a per-domain breakdown.
- Shown in the flyout and dashboard as "Apps open during the session", with distractions that sat open in the background flagged.
- Optional nudge at session start: "WhatsApp and Discord are open — both are on your distraction list." PrepPulse never closes apps for you.

### Session lifecycle

| Event | What happens | `end_reason` |
|---|---|---|
| Start | Tray, taskbar, flyout or hotkey | – |
| Lock | Session **pauses**; resumes on unlock | – |
| Locked longer than 30 min (configurable) | Session **ends** at the moment it was locked | `lock_timeout` |
| **Sleep / hibernate** | Session **ends** at the moment of suspend | `sleep` |
| **Restart / shut down / sign out** | Session **ends** (`SystemEvents.SessionEnding` / `WM_ENDSESSION`) | `shutdown` |
| Stop | You end it | `manual` |
| Crash or power loss | No event arrives, so a heartbeat is written every 15 s; on next start an unfinished session is closed at its last heartbeat | `unexpected` |

Laptops with Modern Standby don't always raise a clean suspend event, so the 1 s timer also treats a wall-clock jump of more than 60 s as sleep. After waking or restarting, the tray says "Session ended at 16:25 when the PC went to sleep" with a **Start new session** button — sessions never restart on their own.

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

Focus counts as lost when, during a session:
1. **No input for 5 min** — unless the focused entry is marked *passive OK* (videos, PDFs), which counts as prep while in focus;
2. **A distraction-list app or site is in focus for 2 min**; or
3. **An app or site on neither list is in focus for 5 min** (can be switched to "ignore").

Alert behaviour:
- One alert per gap; nothing repeats until you're back on task.
- Toast buttons: **Back to it** · **Break 10 min** (snoozes alerts, pings when the break ends) · **End session**. The no-input toast swaps "Back to it" for **I'm reading**, which records the gap as prep.
- No alerts while the PC is locked (the session pauses). Sleep, restart or shutdown ends the session.
- If Windows Do Not Disturb suppresses the toast, the taskbar still turns red and flashes.
- On return from a gap, the **return prompt** (Break / Reading / Thinking / Skip) labels it; ignored prompts stay idle.

### Getting your attention: flash and beep

Everything here is configurable from the Settings window (and `appsettings.json`):

```json
"Alerts": {
  "IdleAfterMinutes": 2,              // when no input starts counting as idle
  "NoInputMinutes": 5,                // alert after this long with no input (0 = off)
  "DistractionMinutes": 2,            // alert after this long on a distraction (0 = off)
  "NeitherListMinutes": 5,            // alert after this long on an unlisted app/site (0 = off)
  "Flash": { "TrayIcon": true, "TaskbarButton": true, "ScreenEdge": false },
  "Sound": {
    "Enabled": true,
    "File": "SystemExclamation",      // or a path to a .wav
    "RepeatEveryMinutes": 2,
    "MaxRepeats": 3,
    "DistractionsOnly": false         // true = no beep for no-input gaps
  },
  "Toast": true,
  "RemindOpenDistractionsAtStart": true
},
"Session": {
  "EndAfterLockedMinutes": 30         // sleep, restart and shutdown always end the session
}
```

Escalation: at the threshold → toast + flashing + one beep. Still off-task → beep again every `RepeatEveryMinutes`, up to `MaxRepeats`, then keep flashing silently until you're back. Any input in a focus-list app or site stops it immediately.

- **Tray icon**: blinks red (two icons swapped every 500 ms).
- **Taskbar button**: `FlashWindowEx(FLASHW_TRAY | FLASHW_TIMERNOFG)` plus red progress state; stops when you return.
- **Screen edge** (off by default): a thin red border around every monitor, click-through, so it's noticed even in a full-screen app.
- **Beep**: a Windows system sound or your own `.wav`, with a **Test** button in settings.

## 5. Focus list and distraction list

Each **study plan has its own focus list**, built from its topics (section 7), so `huggingface.co` can be focus for *Learning AI* and "neither" during *Interview prep*. The **distraction list is shared** by all plans. Both are stored in SQLite; import/export as `lists.json` or inside a plan file.

| List | Example entries | Effect |
|---|---|---|
| **Focus here** (per plan) | `Code.exe`, `devenv.exe`, `leetcode.com`, `neetcode.io`, `bytebytego.com`, `excalidraw.com`, `arxiv.org`, `localhost:3000` (Loop Prep), `youtube.com` + title has "LLM" | Counts as prep time; never triggers a focus-lost alert |
| **Distractions** (all plans) | `WhatsApp.exe`, `Discord.exe`, `reddit.com`, `x.com`, `instagram.com`, `youtube.com` | Counts as distraction time; alert after 2 min (configurable) |
| *Neither list* | Outlook, File Explorer, a new app | Counts as "other". By default counts toward focus lost after 5 min; can be set to ignore |

Each entry has:
- **Kind** — *app*, matched by program name (`Code.exe`), or *website*, matched by domain with subdomains included (`leetcode.com` also matches `www.leetcode.com`). `localhost:port` works for local tools.
- **Title contains** (optional) — narrows a match, e.g. YouTube only when the title mentions "LLM". The **most specific entry wins**, so a focus entry for `youtube.com` + "LLM" beats the distraction entry for `youtube.com`.
- **Topic and category** (focus list only) — the plan topic it belongs to, and that topic's category (Coding practice · System design · AI / ML study · Mock & behavioral), which colours the charts.
- **Passive OK** (focus list only) — no-input time while it's in focus still counts as prep.

Adding entries should take one click: **Pick from running apps…**, type a program name or domain, or use the **"Seen this session, not on a list"** strip in the flyout and dashboard (+ Focus / + Distraction). A starter set of common prep and distraction sites ships with the app and can be edited freely.

### How websites are recognised

A browser's window title only gives the page title, so when Chrome, Edge, Brave or Firefox has focus PrepPulse reads the **address bar through UI Automation** and keeps **only the domain** — never the full URL or query string.
- If the address bar can't be read (browser update, unusual browser), it falls back to matching the window title.
- Private/incognito windows are recorded as "Private browsing" with no domain.
- Possible later: a small browser extension that reports the active tab's domain to the local API — more robust, but an extra install.

**Prep time** = time on focus-list entries (plus gaps labelled Reading/Thinking). **Not prep**: distractions, neither-list, idle.

## 6. Part B — overall progress

**MVP:** today at a glance (prep time vs target, focus ratio, longest focus block, idle pauses, plan pace) · daily progress vs expected · cumulative burn-up to interview day · focus ratio by day.
**Later:** prep calendar · topic coverage vs plan · app switches per hour · idle-pause histogram · best-hours heatmap · alerts review · weekly summary.

### Plan switcher

A switcher at the top of the dashboard: **All plans** · *Interview prep* · *Learning AI*. Every chart follows it.
- A plan with an end date gets the burn-up to its end date.
- An open-ended plan shows weekly progress against its weekly target instead.
- "All plans" adds the targets together.

## 7. Study plans

A **plan** answers "how much do I expect to study, on what, until when?". You can run several at once — for example *Interview prep* with a hard end date and *Learning AI* with no end date.

| Part of a plan | Example | Used for |
|---|---|---|
| Name, colour | Interview prep — AI engineer, blue | Tray, flyout, dashboard switcher |
| Dates | 26 Oct → 18 Dec (end optional) | Burn-up, days left |
| Expected time | 2h weekdays, 4h weekends — **or** 7h per week; days off and one-off exceptions | Daily target, taskbar bar, "expected" lines |
| Topics | Graphs & trees · coding · 18h · weeks 2–5 | Topic coverage, which topic gets the time |
| Focus entries per topic | `leetcode.com`, `Code.exe`, `youtube.com \| system design` | The plan's focus list |
| Week checklist (optional) | Week 1: two pointers — 6 problems | Ticked off in the dashboard |

### Ways to feed a plan

| Way | How | When |
|---|---|---|
| **Plan file (YAML)** | Write or edit a `.yaml` file (see `docs/examples/plans/`). Import it, or drop it in `Documents\PrepPulse\plans` and PrepPulse reloads it on save. Good for VS Code and git. | **M1** — first, because it needs no UI |
| **Plan editor** | Form in the app: dates, targets, topics table, checklist; Export as YAML | M4 |
| **From Loop Prep** | Loop Prep keeps your plan and progress in the browser's local storage, so the clean route is an **"Export to PrepPulse"** button in Loop Prep that writes a plan file (its topics and weekly tasks map onto topics and the checklist) | Later — a small change in Loop Prep |
| **Paste any plan text** | Paste a plan from a doc, email or chat; a local **Ollama** model turns it into a draft plan that you review before saving | Later |
| CSV | Columns: topic, category, hours, weeks, focus — for plans kept in a spreadsheet | Maybe |

Plan file format, in short (full examples in `docs/examples/plans/`):

```yaml
name: Interview prep — AI engineer
colour: blue
start: 2026-10-26
end: 2026-12-18                # leave out for an open-ended plan
targets:
  weekdays: 2h                 # or:  weekly: 7h
  weekends: 4h
  days_off: [2026-11-29]
  overrides: { 2026-12-13: 6h }
topics:
  - name: Graphs & trees
    category: coding           # coding | design | ai | mock
    hours: 18
    weeks: 2-5                 # optional; week 1 = the week containing start
    focus: [leetcode.com, neetcode.io, Code.exe, "youtube.com | graphs"]
weeks:                         # optional checklist
  1: [Two pointers — 6 problems, Read DDIA chapter 1]
```

### Which plan gets the time

- **You pick the plan when you start a session** (tray menu, flyout; the hotkey starts the last-used plan). All time goes to that plan until you switch.
- **Switch any time** from the flyout; time after the switch counts to the new plan. Each focus segment stores its plan.
- **Suggestion, not auto-switch:** if you spend 3 min on something that's only in another plan's focus list, the flyout offers "Switch to Learning AI?".
- **The active plan's focus list** decides what counts as focus; the shared distraction list always applies. The same entry (`Code.exe`, `arxiv.org`) can be in several plans without conflict.
- **Topic within the plan** comes from the matching focus entry. If an entry belongs to several topics (LeetCode → arrays, graphs, DP), the topic scheduled for the current week wins; ties go to the topic furthest behind its hours.
- **Taskbar bar** = today's time vs the active plan's target. The flyout lists every active plan's progress.
- Plans can be **paused** (holiday) or **finished**; paused days don't count as expected.

## 8. Metrics

| Metric | Definition | Part |
|---|---|---|
| Session length | stop − start | A, B |
| Active prep time | time on focus-list apps/sites with input in the last 2 min (or passive OK), plus gaps labelled Reading/Thinking | A, B |
| Idle gaps | count, total, longest, distribution by length | A, B |
| Distraction time | time with a distraction-list app/site in focus | A, B |
| Other time | time with a neither-list app/site in focus | A, B |
| Background distractions | distraction-list apps open but not in focus, and for how long | A, B |
| Session end reason | manual · sleep · shutdown · lock timeout · unexpected | B |
| Focus-lost alerts | alerts per session, by trigger (no input / distraction / neither list) | A, B |
| Time to refocus | alert → next input on a focus-list app/site | B |
| Focus ratio | active prep ÷ session length | B |
| Longest focus block | longest run on focus-list entries without an idle gap, distraction or neither-list time | A, B |
| Switch rate | foreground-app changes ÷ session hours | B |
| Goal attainment | active prep ÷ daily target | A (taskbar), B |
| Streak | consecutive days with ≥ 1 session (or target met — to decide) | B |
| Plan pace | cumulative actual ÷ cumulative planned; projection = 7-day average × days left | B |
| Topic hours | active prep per topic vs topic plan | B |
| Best hours | average active prep by hour × weekday | B |

## 9. Data model (SQLite, first cut)

```sql
CREATE TABLE sessions (
  id INTEGER PRIMARY KEY,
  plan_id        INTEGER REFERENCES plans(id),  -- plan picked at start
  started_at     TEXT NOT NULL,      -- ISO 8601 with offset
  ended_at       TEXT,               -- NULL while running
  last_heartbeat TEXT,               -- every 15 s; closes sessions after a crash
  topic          TEXT,
  notes          TEXT,
  end_reason     TEXT                -- manual | sleep | shutdown | lock_timeout | unexpected
);

CREATE TABLE list_entries (
  id INTEGER PRIMARY KEY,
  list           TEXT NOT NULL,      -- focus | distraction
  plan_id        INTEGER REFERENCES plans(id),        -- focus entries; NULL for distractions
  topic_id       INTEGER REFERENCES plan_topics(id),  -- focus entries only
  kind           TEXT NOT NULL,      -- app | site
  pattern        TEXT NOT NULL,      -- Code.exe | leetcode.com | localhost:3000
  title_contains TEXT,               -- optional narrowing
  passive_ok     INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE focus_segments (
  id INTEGER PRIMARY KEY,
  session_id INTEGER NOT NULL REFERENCES sessions(id),
  started_at TEXT NOT NULL,
  ended_at   TEXT NOT NULL,
  process    TEXT NOT NULL,          -- chrome.exe
  domain     TEXT,                   -- leetcode.com (browsers only)
  title      TEXT,                   -- focus-list entries only, see privacy
  list       TEXT NOT NULL,          -- focus | distraction | neither | private
  entry_id   INTEGER REFERENCES list_entries(id),
  plan_id    INTEGER REFERENCES plans(id),        -- active plan at the time
  topic_id   INTEGER REFERENCES plan_topics(id)
);

CREATE TABLE session_apps (
  session_id    INTEGER NOT NULL REFERENCES sessions(id),
  process       TEXT NOT NULL,
  first_seen    TEXT NOT NULL,
  last_seen     TEXT NOT NULL,
  open_seconds  INTEGER NOT NULL,
  focus_seconds INTEGER NOT NULL,
  list          TEXT NOT NULL,       -- list it matched when last seen
  PRIMARY KEY (session_id, process)
);

CREATE TABLE idle_gaps (
  id INTEGER PRIMARY KEY,
  session_id INTEGER NOT NULL REFERENCES sessions(id),
  started_at TEXT NOT NULL,          -- backdated to last input
  ended_at   TEXT NOT NULL,
  label      TEXT                    -- NULL | break | reading | thinking | locked
);

CREATE TABLE alerts (
  id INTEGER PRIMARY KEY,
  session_id   INTEGER NOT NULL REFERENCES sessions(id),
  fired_at     TEXT NOT NULL,
  trigger      TEXT NOT NULL,        -- no_input | distraction | neither_list
  gap_started  TEXT NOT NULL,
  process      TEXT,
  domain       TEXT,
  response     TEXT,                 -- back | reading | break | end | ignored
  responded_at TEXT,
  refocused_at TEXT                  -- next input on a focus-list entry
);

CREATE TABLE plans (
  id INTEGER PRIMARY KEY,
  name            TEXT NOT NULL,
  colour          TEXT,
  starts_on       TEXT NOT NULL,
  ends_on         TEXT,              -- NULL = open-ended
  weekday_minutes INTEGER,           -- either weekday/weekend ...
  weekend_minutes INTEGER,
  weekly_minutes  INTEGER,           -- ... or a weekly target
  status          TEXT NOT NULL DEFAULT 'active',  -- active | paused | finished
  source          TEXT,              -- editor | file | loop_prep | pasted
  source_path     TEXT               -- plan file to reload on change
);

CREATE TABLE plan_days (             -- days off and one-off exceptions
  plan_id INTEGER NOT NULL REFERENCES plans(id),
  date    TEXT NOT NULL,
  minutes INTEGER NOT NULL,          -- 0 = day off
  PRIMARY KEY (plan_id, date)
);

CREATE TABLE plan_topics (
  id INTEGER PRIMARY KEY,
  plan_id      INTEGER NOT NULL REFERENCES plans(id),
  name         TEXT NOT NULL,
  category     TEXT NOT NULL,        -- coding | design | ai | mock
  target_hours REAL,
  week_from    INTEGER,
  week_to      INTEGER
);

CREATE TABLE plan_items (            -- optional week checklist
  id INTEGER PRIMARY KEY,
  plan_id INTEGER NOT NULL REFERENCES plans(id),
  week    INTEGER NOT NULL,
  text    TEXT NOT NULL,
  done_at TEXT
);
```

Daily rollups are queries, not stored, until performance says otherwise.

## 10. Privacy

- Never record keystroke content — `GetLastInputInfo` only gives a timestamp.
- Window titles can hold private things (email subjects, chat names). Default: **store titles only for focus-list entries**; everything else keeps the program name (and domain for browsers) only.
- Websites: **domain only**, never full URLs; private/incognito windows store no domain.
- Open-app snapshots store program names only, not window titles.
- Data stays on the machine. The repo is public, so `*.db` and personal `lists.local.json` exports are git-ignored. Plan files are yours to keep wherever you like; only the examples live in the repo.
- Tray menu has "Pause tracking"; the dashboard can delete a day.

## 11. Roadmap

1. **M0 – Design** *(this)*: two parts, metrics, charts, stack.
2. **M1 – Part A core**: solution skeleton, capture probes (focus, browser domain, open apps, input, power events), session lifecycle incl. sleep/restart ending, **plans loaded from YAML files** (with their focus lists) + shared distraction list, matching, idle gaps, SQLite, tray icon with plan picker. Verify with a `--dump` CLI.
3. **M2 – Part A presence**: taskbar progress/state + thumbnail buttons, flyout, lists editor, focus-lost alerts (toast, flash, beep) with settings, return prompt, apps-open view.
4. **M3 – Part B dashboard**: localhost API, WebView2 window, MVP charts, "seen but not on a list" quick-add.
5. **M4 – Plans in the app**: plan editor, several plans side by side, plan switcher on the dashboard, burn-up / weekly views, topic coverage, week checklist, switch suggestions.
6. **M5 – Hardening**: start at logon, installer (MSIX/winget), optional Windows Service split, tests in CI.
7. **M6 – Integrations**: "Export to PrepPulse" in Loop Prep, paste-a-plan via local Ollama, weekly summary.

## 12. Open questions

1. Websites: start with reading the address bar (no install), or go straight to a browser extension?
2. Lock: pause and end after 30 min — or should any lock end the session, like sleep?
3. Apps on neither list: count toward focus lost after 5 min (current default), or ignore?
4. Streak = any session, or daily target met?
5. Plans: pick the plan when starting a session (current design), or let PrepPulse assign time automatically from what's in focus?
6. Add the "Export to PrepPulse" button to Loop Prep early, so your current interview plan comes across without retyping?
