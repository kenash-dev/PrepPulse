@echo off
rem One-time GitHub setup: creates the repo, publishes main, creates dev from it, and switches on the branch rules.
rem Safe to rerun; it picks up where it stopped. See CONTRIBUTING.md.
setlocal
cd /d "%~dp0.."
set "GITBASH=%ProgramFiles%\Git\bin\bash.exe"
if not exist "%GITBASH%" (
  echo Git Bash wasn't found at %GITBASH%. Install Git for Windows, or run tools/setup-github.sh from any bash shell.
  pause
  exit /b 1
)
"%GITBASH%" tools/setup-github.sh
pause
endlocal
