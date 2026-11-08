@echo off
rem Starts PrepPulse with your real plans (Documents\PrepPulse). Run build.cmd first.
setlocal
cd /d "%~dp0"
set "EXE=%~dp0src\PrepPulse.App\bin\Release\net10.0-windows\PrepPulse.exe"
if not exist "%EXE%" (
  echo PrepPulse isn't built yet. Run build.cmd first.
  pause
  exit /b 1
)
start "" "%EXE%"
endlocal
