# PrepPulse

A Windows background tracker for interview prep. Start a session, and PrepPulse records which program has focus and for how long, notices pauses where there's been no keyboard or mouse input for 2+ minutes, and charts each day's progress against what you planned.

> **Status:** design stage. No code yet — see the brainstorm and the dashboard design templates below.

## What it will do

Three parts sharing one capture engine and database:

**A. Current session — live focus tracking**
- **Sessions** with a start and stop time (tray, taskbar or hotkey). Locking pauses a session; sleep, restart or shutdown ends it.
- **Your lists**: a focus list (apps and websites where your attention should be, each tagged coding, system design, AI/ML or mock & behavioral) and a distraction list. Anything on neither list is "other".
- **Focus tracking**: the app or website in focus (websites by domain only), plus every app open during the session.
- **Tray and taskbar**: an icon in the notification area by the clock shows today's progress and your focus state; while a session runs, the taskbar button's progress bar fills toward today's target and turns green / yellow / red.
- **Idle detection and alerts**: gaps of 2+ minutes with no input are recorded. When focus is lost — no input for 5 minutes, a distraction for 2, or an unlisted app for 5 (all configurable) — the tray icon and taskbar button flash, a beep plays, and a toast offers Back to it / Break / End session — each can be switched off.

**B. Overall progress**
- **Dashboard**: daily progress vs target, a burn-up to interview day, focus ratio and more — per plan or for all plans together.

**C. Study plans**
- Run several plans side by side, e.g. *Interview prep* with an end date and *Learning AI* with a weekly target. Each plan has its own daily or weekly targets, topics with planned hours, and the apps and websites that count as studying it.
- Feed a plan as a YAML file (see [`docs/examples/plans`](docs/examples/plans)) or, later, through the in-app editor, an export from Loop Prep, or by pasting plan text for a local model to structure.

**Local only**: data lives in a SQLite file on your machine. No keystroke contents are ever recorded.

## Design

- [`docs/brainstorm.md`](docs/brainstorm.md) — architecture options, what to capture and how, metrics, data model, privacy, roadmap, open questions.
- [`docs/examples/plans`](docs/examples/plans) — example study plans: interview prep with an end date, and open-ended AI learning.
- [`docs/design/dashboard-templates.html`](docs/design/dashboard-templates.html) — taskbar, tray and alert mockups plus candidate charts on sample data. Download and open it in a browser (hover for details, light/dark toggle).

## Planned stack

C# / .NET 10 · WPF (tray, taskbar progress, thumbnail buttons) · Windows App SDK notifications · Win32 via CsWin32 · SQLite + Dapper · ASP.NET Core minimal API + ECharts dashboard in WebView2 · Generic Host, so storage can move into a Windows Service later.

## License

MIT — see [LICENSE](LICENSE).
