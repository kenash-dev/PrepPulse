# PrepPulse

A Windows tray app that tracks study sessions for interview prep (or any study plan). It records:

- which program or website has focus, and for how long
- pauses of 2+ minutes with no keyboard or mouse input

It alerts you when focus is lost and charts each day's progress against what your plan expects.

> **Status:** v0.1 works end to end on Windows 11 with .NET 10. All 77 unit tests pass, plus a live test run. See [`docs/architecture.md`](docs/architecture.md) for the patterns, decisions and test results.

## What it does

**Current session: live focus tracking**
- **Pick a plan on startup.** Sessions start and stop from the tray icon (by the clock), the taskbar button, or Ctrl+Alt+P.
- **Focus and distraction lists.** You list the apps and websites that count as focus for each plan, plus a distraction list. Websites are read from the browser's address bar, domain only.
- **Open apps.** Every app open during a session is recorded, with open time vs focus time.
- **Pauses.** No input for 2+ minutes is recorded as a pause. "I'm reading" turns a pause into study time. Entries marked `passive` (e.g. lecture videos) count without input.
- **Alerts.** These fire when:
  - a distraction stays in front for 2 minutes;
  - there's no input for 5 minutes;
  - an app on neither list stays in front for 5 minutes.

  The tray icon blinks, the taskbar button flashes red and a beep plays (repeats are capped). A popup offers *Back to it / I'm reading / Break / End session*. Every timing and channel is configurable.
- **Taskbar.** The taskbar progress bar fills toward today's target and changes colour with your focus state. Its thumbnail buttons are Pause, Break and Stop.
- **Session end.** A lock pauses the session and ends it after 30 minutes. Sleep, restart and shutdown end it. A crash closes it at the last heartbeat.

**Overall progress**
- **Local dashboard** (opens in your browser) for one plan or all plans, showing:
  - today's tiles and a session timeline;
  - daily progress vs target;
  - a burn-up to the plan's end date, or weekly totals for open-ended plans;
  - focus ratio, time per app/site, alerts, apps open, and topic hours.

**Study plans**
- **One YAML file per plan** in `Documents\PrepPulse\plans`, e.g. *Interview prep* with an end date and *Learning AI* with a weekly target. Each file has the targets, topics with planned hours, and the apps and sites that count as studying.
- **Live reload.** Edits are picked up while the app runs, and any mistakes are shown in the tray menu and on the dashboard. See [`docs/examples/plans`](docs/examples/plans).

**Local only:** data lives in a SQLite file on your PC, and the dashboard only listens on 127.0.0.1. No keystroke contents are ever read, and window titles are kept only for focus-list matches.

## Build and run

Requires Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

| | |
|---|---|
| `build.cmd` | Restore, build (Release) and run the tests. Log in `.artifacts\logs\build.log` |
| `run.cmd` | Start PrepPulse with your real data |
| `tests\testrun\testrun.cmd` | Live test with separate data (`.artifacts\testrun`) and fast thresholds (30 s idle, 30 s distraction, 90 s no input) |

Or from a terminal: `dotnet build -c Release` then `dotnet test`.

**Where things live**

| | Location |
|---|---|
| Plans, distraction list, settings | `Documents\PrepPulse\` (`plans\*.yaml`, `distractions.yaml`, `settings.json`). Example plans are copied in on first run |
| Database and logs | `%LOCALAPPDATA%\PrepPulse\` (`preppulse.db`, `logs\`) |

**Command line:** a running PrepPulse receives these from a second launch:

```
PrepPulse.exe --start interview-prep | --stop | --pause | --resume | --break | --status | --dashboard | --show | --exit
PrepPulse.exe --report report.json [--plan interview-prep]   # dashboard data as JSON, read from the database
PrepPulse.exe --probe 20                                     # print what it sees each second: app, domain, idle
              --data-dir D:\somewhere                        # use a separate config + data folder
              --out result.json                              # write the command's result to a file
```

## Docs

- [`docs/architecture.md`](docs/architecture.md): structure, patterns used, critical decisions, what the live test found, known limitations.
- [`docs/brainstorm.md`](docs/brainstorm.md): the original design covering capture, metrics, data model, privacy and roadmap.
- [`docs/design/dashboard-templates.html`](docs/design/dashboard-templates.html): taskbar, tray, alert and chart mockups (open in a browser).

## Contributing

Work happens on feature branches:

- Only `feature/`, `fix/`, `docs/` and `chore/` branches merge into `dev`, through pull requests, once the build and all unit tests pass.
- Only `dev` merges into `main`, after the same checks and with the owner's approval.
- Nothing is pushed to `dev` or `main`: GitHub rules and local git hooks block it.

See [CONTRIBUTING.md](CONTRIBUTING.md). After cloning, run `tools\setup-hooks.cmd`.

## Stack

C# / .NET 10 · WPF + WinForms tray icon · Win32 P/Invoke + UI Automation · SQLite (Microsoft.Data.Sqlite + Dapper) · YamlDotNet · Generic Host with a Kestrel minimal API serving the dashboard · xUnit.

## License

MIT, see [LICENSE](LICENSE).
