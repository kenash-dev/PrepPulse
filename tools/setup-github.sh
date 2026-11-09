#!/usr/bin/env bash
# One-time GitHub setup for PrepPulse (CONTRIBUTING.md). Safe to rerun: each run picks up where the last one stopped.
#   1. turns on the local git hooks (.githooks)
#   2. first run: creates the public repo and publishes main as the repository's starting point
#      (the only time main is pushed directly, before any rules exist)
#   3. creates dev on GitHub as a copy of main (a new branch: no commits are pushed to it),
#      makes dev the default branch, and has GitHub delete merged feature branches
#   4. while dev doesn't have the branch rules yet: pushes chore/branch-policy, opens its pull request
#      into dev and stops. Merge that pull request once build-and-test is green, then run this again.
#   5. once dev has them: creates or updates the rulesets in .github/rulesets (matched by name)
# Needs git and the GitHub CLI, signed in (gh auth login). On Windows, run tools\setup-github.cmd.
set -euo pipefail
cd "$(dirname "$0")/.."

POLICY_BRANCH=chore/branch-policy
GUARD=.github/workflows/branch-guard.yml
REPO_NAME=PrepPulse
DESCRIPTION='Windows tray app that tracks focus during study sessions and charts progress against your study plan'

step() { printf '\n== %s\n' "$1"; }
die() { printf '\nStopped: %s\n' "$1" >&2; exit 1; }

command -v gh >/dev/null 2>&1 || die "GitHub CLI (gh) not found. Install it (winget install GitHub.cli), then run: gh auth login"
gh auth status >/dev/null 2>&1 || die "gh isn't signed in. Run: gh auth login"

step "Local git hooks"
git config core.hooksPath .githooks
echo "on: commits on dev/main and pushes to them are blocked"

step "Repository"
if ! git remote | grep -qx origin; then
  [ "$(git rev-parse dev)" = "$(git rev-parse main)" ] ||
    die "local dev has commits that aren't on main. Everything reaches dev through a pull request, so move them to a feature branch first (CONTRIBUTING.md)."
  gh repo create "$REPO_NAME" --public --source . --remote origin --description "$DESCRIPTION"
fi
git fetch --quiet origin
if [ -z "$(git ls-remote --heads origin main)" ]; then
  # The repository's starting point. --no-verify skips our own pre-push hook for this one push.
  git push --no-verify -u origin main
  git fetch --quiet origin
fi
if [ -z "$(git ls-remote --heads origin dev)" ]; then
  # dev starts as a copy of main, created on GitHub: a new branch, no commits pushed to it.
  gh api -X POST 'repos/{owner}/{repo}/git/refs' -f ref=refs/heads/dev -f sha="$(git rev-parse origin/main)" >/dev/null
  git fetch --quiet origin
  echo "created dev on GitHub from main"
fi
git branch --quiet --set-upstream-to=origin/dev dev
gh repo edit --default-branch dev --delete-branch-on-merge >/dev/null
echo "default branch: dev; merged feature branches are deleted automatically"

step "Branch rules"
if ! gh api "repos/{owner}/{repo}/contents/$GUARD?ref=dev" --silent >/dev/null 2>&1; then
  # The rules require the branch guard, which runs from dev. Until dev has it, the rules would block every
  # pull request, so the policy itself goes in first, through a normal pull request into dev.
  git rev-parse --verify --quiet "refs/heads/$POLICY_BRANCH" >/dev/null ||
    die "dev on GitHub doesn't have $GUARD yet, and there's no local $POLICY_BRANCH branch to propose it from."
  git push -u origin "$POLICY_BRANCH"
  if [ "$(gh pr list --head "$POLICY_BRANCH" --base dev --state open --json number --jq length)" = "0" ]; then
    gh pr create --base dev --head "$POLICY_BRANCH" \
      --title "Branch policy: feature branches -> dev -> main" \
      --body "Adds the branch policy (CONTRIBUTING.md), CI, the branch guard and the local git hooks. Once this is merged, run tools\\setup-github.cmd again to switch on the branch rules."
  fi
  echo
  echo "Next: merge that pull request once build-and-test is green, then run this again to switch the rules on."
  exit 0
fi

existing=$(gh api 'repos/{owner}/{repo}/rulesets' --jq '.[] | "\(.id)\t\(.name)"')
for file in .github/rulesets/dev.json .github/rulesets/main.json; do
  # The first "name" in each file is the ruleset's own name (rules inside don't have one).
  name=$(grep -m1 -o '"name": *"[^"]*"' "$file" | sed 's/^"name": *"\(.*\)"$/\1/')
  id=$(printf '%s\n' "$existing" | awk -F'\t' -v n="$name" '$2 == n { print $1; exit }')
  if [ -n "$id" ]; then
    gh api -X PUT "repos/{owner}/{repo}/rulesets/$id" --input "$file" >/dev/null
    echo "updated: $name"
  else
    gh api -X POST 'repos/{owner}/{repo}/rulesets' --input "$file" >/dev/null
    echo "created: $name"
  fi
done

echo
echo "Done. dev and main now only change through pull requests (CONTRIBUTING.md)."
