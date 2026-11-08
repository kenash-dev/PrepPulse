# PrepPulse — brainstorm

A Windows background tracker for interview prep. It records **sessions** (start/stop), **which program has focus** and for how long, and **pauses** where there is no keyboard or mouse input for 2+ minutes, then charts daily progress against what was planned.

Design templates for the dashboard: [`design/dashboard-templates.html`](design/dashboard-templates.html) (open it in a browser; it runs on sample data).

---

## 1. The big constraint: a Windows Service can't see your desktop

Since Windows Vista, services run in **Session 0**, isolated from the logged-in user's desktop. From a service:

- `GetForegroundWindow` returns nothing useful (Session 0 has no interactive desktop),
- `GetLastInputInfo` reports input for Session 0, not for your session,
- there's no UI.

So "a Windows service that records which programs are focused" has to be split: the part that **watches** must run inside the user's session. Options:

| Option | Shape | Pros | Cons |
|---|---|---|---|
| **A. Tray agent only** *(recommended for MVP)* | One per-user background app (tray icon), starts at logon via a scheduled task or `HKCU\…\Run`. Captures, stores to SQLite, serves the dashboard. | Simplest; one process; no admin rights or installer needed | Stops if the app is closed or crashes |
| **B. Service + tray agent** | Windows Service owns the database, aggregation and a localhost API. Tray agent captures and sends events over a named pipe. | Matches "Windows service"; storage survives agent crashes; easy to add other clients later | Two processes, IPC, needs admin to install the service |
| C. Service only | Service launches a helper into the user session (`WTSQueryUserToken` + `CreateProcessAsUser`) | Single install unit | Fiddly, needs LocalSystem; more security surface than it's worth |

**Proposal:** build A as a .NET Generic Host so the capture, storage and web pieces are separate hosted services. Moving storage + API into a real Windows Service later (B) is then a packaging change (`UseWindowsService()`), not a rewrite.

## 2. Stack (proposed)

- **.NET 10 (LTS)**, C#.
- **Capture:** Win32 via P/Invoke (or CsWin32 source generator).
- **Storage:** SQLite (`Microsoft.Data.Sqlite` + Dapper) at `%LOCALAPPDATA%\PrepPulse\preppulse.db`.
- **Dashboard UI** — two candidates:
  - **Local web dashboard** *(leaning this way)*: ASP.NET Core minimal API on `127.0.0.1`, HTML/JS charts (the design templates become the real UI). Can be shown in a WebView2 window from the tray, or in any browser. Same pattern as Loop Prep.
  - WPF + ScottPlot/LiveCharts2: fully native, but charts take more effort to make look good.
- **Tray:** WinForms `NotifyIcon` (lightest) with a small flyout window.

## 3. What to capture and how

| Signal | API | Notes |
|---|---|---|
| Foreground window changes | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` + a 1 s poll as a backstop | Poll also catches **title** changes (browser tab switches don't change the foreground window) |
| Window title | `GetWindowText` | Browser titles carry the useful bit: "LeetCode – 3Sum", "ByteByteGo – Rate limiter" |
| Process | `GetWindowThreadProcessId` → `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` → `QueryFullProcessImageName` | UWP apps sit under `ApplicationFrameHost.exe` — resolve the child window. Elevated apps may refuse; record as "unknown (elevated)". |
| Last input time | `GetLastInputInfo` polled every 1 s | Keyboard **and** mouse; gives a timestamp only, never key contents. `dwTime` is a 32-bit tick count that wraps after ~49.7 days — subtract with unsigned arithmetic. |
| Lock / unlock, sleep / resume | `SystemEvents.SessionSwitch`, `SystemEvents.PowerModeChanged` | Close the current segment; mark as "away", not idle |
| Start / stop | Tray menu, global hotkey (`RegisterHotKey`, e.g. Ctrl+Alt+P) | Optional: auto-start when a prep app gets focus; auto-stop after N minutes idle |

### Idle rules

- A gap opens when there's been no input for **≥ 2 min** (configurable) and is **backdated to the last input**, so a 7-minute pause records as 7 minutes, not 5.
- It closes on the next input.
- **Passive work problem:** watching a system-design video or reading a PDF produces no input but is real prep. Two complementary fixes:
  1. Per-rule `passiveAllowed: true` (e.g. YouTube, PDF readers) — no idle gap while that window has focus.
  2. **Return prompt**: after a gap ≥ N min, a toast asks *Break / Reading / Thinking / Skip*. Reading and Thinking count as prep; ignored prompts stay idle.

## 4. Categorising time

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

Anything unmatched is `uncategorized` — shown in the dashboard with a one-click "assign to…" that writes a new rule.

**Prep categories (count toward targets):** Coding practice · System design · AI / ML study · Mock & behavioral.
**Non-prep:** Distraction · Uncategorized · Idle.

## 5. Metrics

| Metric | Definition |
|---|---|
| Session length | stop − start |
| Active prep time | time in prep categories with input in the last 2 min, plus pauses labelled Reading/Thinking |
| Idle gaps | count, total, longest, distribution by length |
| Distraction time | time in distraction-category apps during a session |
| Focus ratio | active prep ÷ session length |
| Longest focus block | longest run of prep categories without an idle gap or distraction |
| Switch rate | foreground-app changes ÷ session hours |
| Goal attainment | active prep ÷ daily target |
| Streak | consecutive days with ≥ 1 session (or ≥ target — pick one) |
| Plan pace | cumulative actual ÷ cumulative planned; projection = 7-day average × days left |
| Topic hours | active prep per topic vs topic plan |
| Best hours | average active prep by hour × weekday |

## 6. "Expected" — where the plan comes from

- **Daily target** per weekday (e.g. 2 h weekdays, 4 h weekends) with per-date overrides (travel, family days).
- **Prep window**: start date → interview date.
- **Topic plan** (optional): hours per topic over the window.
- Could import the week-by-week plan from **Loop Prep** later, so "expected" follows the plan you already work through there.

## 7. Data model (SQLite, first cut)

```sql
CREATE TABLE sessions (
  id INTEGER PRIMARY KEY,
  started_at TEXT NOT NULL,          -- ISO 8601, local time + offset
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
  title      TEXT,                   -- optional, see privacy
  category   TEXT NOT NULL
);

CREATE TABLE idle_gaps (
  id INTEGER PRIMARY KEY,
  session_id INTEGER NOT NULL REFERENCES sessions(id),
  started_at TEXT NOT NULL,          -- backdated to last input
  ended_at   TEXT NOT NULL,
  label      TEXT                    -- NULL | break | reading | thinking | away
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

Daily rollups are computed views/queries, not stored, until performance says otherwise.

## 8. Privacy

- Never record keystroke content — `GetLastInputInfo` only gives a timestamp.
- Window titles can hold private things (email subjects, chat names). Options: store titles only for rule-matched prep apps; or store the matched rule name instead of the raw title. Default: **titles only for prep categories**.
- Data stays on the machine. The repo is public, so `*.db` and personal `rules.local.json` are git-ignored.
- "Pause tracking" in the tray menu, and a delete-a-day option.

## 9. Dashboard (see design templates)

**MVP:** today tiles · session timeline · where today's time went · daily progress vs expected · cumulative burn-up · focus ratio by day · tray flyout + return prompt.
**Later:** prep calendar · topic coverage vs plan · app switches per hour · idle pause histogram · best-hours heatmap · weekly review export.

## 10. Roadmap

1. **M0 – Design** *(this)*: metrics, charts, architecture choice.
2. **M1 – Capture**: tray app, start/stop + hotkey, foreground + idle + lock tracking, SQLite. Verify with a `--dump` CLI.
3. **M2 – Dashboard**: localhost API + MVP charts, rules editor for uncategorised apps.
4. **M3 – Plan & goals**: targets, prep window, burn-up, return prompt.
5. **M4 – Hardening**: optional Windows Service split, installer (MSIX/winget), auto-start, tests.
6. **M5 – Integrations**: Loop Prep plan import, weekly summary.

## 11. Open questions

1. Option A (tray agent) first, or straight to B (real Windows Service + agent)?
2. Dashboard as local web page (WebView2/browser) or native WPF?
3. Should window titles be stored, or only for prep apps?
4. Streak = any session, or target met?
5. Link "expected" to Loop Prep's plan now, or keep a simple targets screen first?
