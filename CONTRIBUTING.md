# Contributing to PrepPulse

## Branches

```
feature/…  fix/…  docs/…  chore/…
      │  pull request (build + all unit tests pass)
      ▼
     dev  ─────────────────────────►  main
            pull request from dev only
            (build + all unit tests pass, owner approves)
```

| Branch | Holds | Changes only by |
|---|---|---|
| `main` | Released, approved code | A pull request **from `dev`**, after every check passes, merged with the owner's approval |
| `dev` | The next release; the default branch | Pull requests **from `feature/`, `fix/`, `docs/` or `chore/` branches**, after the build and all unit tests pass |
| `feature/…`, `fix/…`, `docs/…`, `chore/…` | One change each | Commit and push freely |

## Rules

1. **Nothing is pushed to `dev` or `main`.** Not new commits, not merges, not force-pushes, not deletions. Every change starts on its own branch from an up-to-date `dev`.
2. **Only feature branches merge into `dev`, by pull request.** The branch name must start with `feature/`, `fix/`, `docs/` or `chore/`. The pull request can merge when `build-and-test` (restore, build, run every unit test) and `source-branch` pass, and the branch is up to date with `dev`.
3. **Only `dev` merges into `main`, by pull request.** `build-and-test` and `source-branch` must pass, and the owner (Avinash) approves it by merging it himself or by explicitly saying so.
4. **Merge `dev` → `main` with "Create a merge commit"**, not squash or rebase, so both branches keep the same history. Squash is fine for feature → `dev`.
5. **Fix the cause when a check fails.** Don't skip the hooks (`--no-verify`), and don't edit the rules, the workflows or the hooks to get a change in.

## Making a change

```bat
git switch dev
git pull
git switch -c feature/short-name

rem ...make your change, then build and run all unit tests (they must pass):
build.cmd
git add -A
git commit -m "What changed and why"
git push -u origin feature/short-name
gh pr create --base dev --fill
```

When `build-and-test` and `source-branch` are green, merge the pull request. GitHub deletes the branch automatically. Then:

```bat
git switch dev
git pull
git branch -d feature/short-name
```

Branch names are lower-case with hyphens: `feature/plan-editor`, `fix/idle-overlap`, `docs/readme-setup`, `chore/bump-sqlite`.

## Releasing `dev` to `main`

```bat
gh pr create --base main --head dev --title "Release: <summary>" --body "<what's in it>"
```

Wait for `build-and-test` and `source-branch` to pass. The owner then reviews the pull request and merges it with **Create a merge commit**.

## How the rules are enforced

| Where | What | File |
|---|---|---|
| GitHub ruleset on `dev` | Pull request required; `build-and-test` and `source-branch` must pass on an up-to-date branch; no force-push or deletion; nobody can bypass, including admins | [`.github/rulesets/dev.json`](.github/rulesets/dev.json) |
| GitHub ruleset on `main` | Pull request required; `build-and-test` and `source-branch` must pass; no force-push or deletion; nobody can bypass | [`.github/rulesets/main.json`](.github/rulesets/main.json) |
| GitHub Actions `build-and-test` | Restore, build and run all unit tests on Windows, for every PR and push to `dev`/`main` | [`.github/workflows/ci.yml`](.github/workflows/ci.yml) |
| GitHub Actions `source-branch` | PRs into `main` must come from this repo's `dev`; PRs into `dev` must come from `feature/`, `fix/`, `docs/` or `chore/` branches | [`.github/workflows/branch-guard.yml`](.github/workflows/branch-guard.yml) |
| Git hooks on your PC | Block commits and merge commits on `dev`/`main`, any push to them, and pushes of branches outside the four prefixes | [`.githooks/`](.githooks) |

**Why a pull request can't change its own check.** `source-branch` runs on `pull_request_target`, so GitHub always takes it from the default branch (`dev`), never from the pull request. Changing it means a reviewed pull request into `dev` first. The workflow never runs pull-request code, has no permissions and uses no secrets, which is what makes that trigger safe.

**Known limit.** `build-and-test` has to run the pull request's own code, so it runs from the pull request (`pull_request`). A pull request that edits `ci.yml` to weaken the tests would still pass that check. Such an edit shows in the diff, and rule 5 forbids it.

**About approval.** GitHub doesn't let you approve your own pull request. With a single maintainer, a "required approvals" rule would block every merge. So approval for `main` means the owner merges the release pull request himself, or explicitly tells Claude to. Green checks alone aren't approval.

**About "up to date".** `dev` requires feature branches to be up to date before merging, so tests run against the latest `dev`. `main` doesn't, because GitHub's "Update branch" button would have to push to `dev`, which the rules forbid.

## One-time setup

- **After cloning:** run `tools\setup-hooks.cmd`. It sets `git config core.hooksPath .githooks`.
- **GitHub, once:** sign in with `gh auth login`, then run `tools\setup-github.cmd`. It runs `tools/setup-github.sh` in Git Bash.
  1. **First run.** It turns on the hooks, creates the public repo and publishes `main` as the starting point. That's the only direct push `main` ever gets, made before any rules exist. It then creates `dev` on GitHub as a copy of `main`: a new branch, with nothing pushed to it. It makes `dev` the default branch, pushes `chore/branch-policy` and opens that branch's pull request into `dev`.
  2. **Merge that pull request** once `build-and-test` is green. This is how the rules themselves reach `dev`.
  3. **Second run.** It sees the guard on `dev` and switches on both rulesets. It's safe to rerun after editing `.github/rulesets/*.json`, because rulesets are matched by name.
- **Renaming a check.** The job names `build-and-test` and `source-branch` are referenced by the rulesets. Rename them in both places, then rerun the setup script.
