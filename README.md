# PrepPulse

A Windows background tracker for interview prep. Start a session, and PrepPulse records which program has focus and for how long, notices pauses where there's been no keyboard or mouse input for 2+ minutes, and charts each day's progress against what you planned.

> **Status:** design stage. No code yet — see the brainstorm and the dashboard design templates below.

## What it will do

- **Sessions** with a start and stop time (tray icon or hotkey), auto-paused when you lock the PC.
- **Focus tracking**: the foreground app and window title, grouped into categories (coding practice, system design, AI/ML study, mock & behavioral, distraction) by simple rules.
- **Idle detection**: gaps of 2+ minutes with no input, with an optional "what was that pause?" prompt so reading or thinking time still counts.
- **Dashboard**: today's timeline, daily progress vs target, a burn-up to interview day, focus ratio and more.
- **Local only**: data lives in a SQLite file on your machine. No keystroke contents are ever recorded.

## Design

- [`docs/brainstorm.md`](docs/brainstorm.md) — architecture options, what to capture and how, metrics, data model, privacy, roadmap, open questions.
- [`docs/design/dashboard-templates.html`](docs/design/dashboard-templates.html) — candidate charts on sample data. Download and open it in a browser (hover for details, light/dark toggle).

## Planned stack

.NET 10 · Win32 (foreground window, `GetLastInputInfo`) · SQLite · local web dashboard on `127.0.0.1` (or WPF) · tray app, with an optional Windows Service for storage.

## License

MIT — see [LICENSE](LICENSE).
