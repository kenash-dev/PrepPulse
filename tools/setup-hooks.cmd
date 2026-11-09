@echo off
rem Turns on PrepPulse's git hooks (.githooks) for this clone. Run once after cloning. See CONTRIBUTING.md.
cd /d "%~dp0.."
git config core.hooksPath .githooks
if errorlevel 1 (
  echo Couldn't set core.hooksPath. Is git installed and is this a git clone?
) else (
  echo Git hooks on: commits on dev or main, and pushes to them, are blocked. Use a feature branch.
)
pause
