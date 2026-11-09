# PrepPulse — notes for Claude

Windows tray app (.NET 10, WPF) that tracks study-session focus. Architecture, patterns and decisions, with code links: `docs/architecture.md`.

## Git workflow (mandatory; full policy in CONTRIBUTING.md)

- **Never commit on `dev` or `main`, and never push to them** (no commits, merges, force-pushes or deletes). Start every change on a new branch from an up-to-date `dev`: `git switch dev && git pull && git switch -c feature/<name>`. Only `feature/`, `fix/`, `docs/` and `chore/` branches can merge into `dev`.
- **Before pushing, build and run all unit tests** (`build.cmd`, or `dotnet build PrepPulse.slnx -c Release` then `dotnet test PrepPulse.slnx -c Release --no-build`). Everything must pass.
- **Into `dev` only by pull request from a feature branch:** push the branch, then `gh pr create --base dev`. Merge only once `build-and-test` and `source-branch` are green, and only if the user asked for the change to be merged. Otherwise leave the PR open and say so.
- **Only `dev` goes into `main`, and only with the user's approval.** Open the release PR from `dev` (`gh pr create --base main --head dev`). Once `build-and-test` and `source-branch` pass, ask the user. Merge only after their explicit yes, using a merge commit (not squash or rebase). Green checks are not approval.
- **Never get around the rules:** no `--no-verify`, no force-push, no deleting `dev`/`main`. Don't edit `.github/rulesets`, `.github/workflows` or `.githooks` to get a change through. If a check fails, fix the cause or ask the user.
- **If there's no `origin` remote yet** (GitHub not set up), still commit on a feature branch and tell the user the change is waiting for a PR. `tools\setup-github.cmd` does the GitHub setup in two runs: the policy reaches `dev` through its own pull request (`chore/branch-policy`) before the rules are switched on.

## Build and test

- Windows only (the app targets `net10.0-windows`). SDK pinned by `global.json` (10.0.100, latest feature band).
- `build.cmd` restores, builds Release and runs the tests. The log goes to `.artifacts\logs\build.log`, and `.artifacts\logs\build.exit` holds `<build> <test>` exit codes.
- Live test with isolated data and fast thresholds: `tests\testrun\testrun.cmd` (see the comments in it).
