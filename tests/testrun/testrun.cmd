@echo off
rem Live test run with isolated data (.artifacts\testrun) and fast thresholds (see settings.json here).
rem   testrun.cmd          fresh data, start the app (plan picker shows)
rem   testrun.cmd start    start a session on the smoke-test plan
rem   testrun.cmd status   write status.json
rem   testrun.cmd stop     stop the session, write report.json, open the dashboard
rem   testrun.cmd exit     close the test instance
setlocal
cd /d "%~dp0..\.."
set "ROOT=%CD%\.artifacts\testrun"
set "EXE=%CD%\src\PrepPulse.App\bin\Release\net10.0-windows\PrepPulse.exe"
if not exist "%EXE%" (
  echo Build first: run build.cmd
  exit /b 1
)

if /i "%~1"=="start" ( "%EXE%" --data-dir "%ROOT%" --start smoke-test --out "%ROOT%\start.json" & goto end )
if /i "%~1"=="status" ( "%EXE%" --data-dir "%ROOT%" --status --out "%ROOT%\status.json" & goto end )
if /i "%~1"=="exit" ( "%EXE%" --data-dir "%ROOT%" --exit --out "%ROOT%\exit.json" & goto end )
if /i "%~1"=="stop" (
  "%EXE%" --data-dir "%ROOT%" --stop --out "%ROOT%\stop.json"
  "%EXE%" --data-dir "%ROOT%" --report "%ROOT%\report.json" --plan smoke-test
  "%EXE%" --data-dir "%ROOT%" --dashboard --out "%ROOT%\dashboard.json"
  goto end
)

rem Fresh start: close any previous test instance, reset data, seed config, launch.
"%EXE%" --data-dir "%ROOT%" --exit --out "%TEMP%\preppulse-exit.json" >nul 2>&1
ping -n 3 127.0.0.1 >nul
if exist "%ROOT%" rmdir /s /q "%ROOT%"
mkdir "%ROOT%\config\plans"
copy /y tests\testrun\settings.json "%ROOT%\config\settings.json" >nul
copy /y tests\testrun\distractions.yaml "%ROOT%\config\distractions.yaml" >nul
copy /y tests\testrun\smoke-test.yaml "%ROOT%\config\plans\smoke-test.yaml" >nul
copy /y docs\examples\plans\*.yaml "%ROOT%\config\plans\" >nul
echo seeded> "%ROOT%\config\.seeded"
start "" "%EXE%" --data-dir "%ROOT%"

:end
endlocal
