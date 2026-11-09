@echo off
rem Installs PrepPulse for the current Windows user. No admin rights needed.
rem   install.cmd                    plans and settings in Documents\PrepPulse
rem   install.cmd D:\Dev\PrepPlan    plans and settings in that folder instead
rem Publishes a Release build to %LOCALAPPDATA%\Programs\PrepPulse, starts it with Windows
rem (per-user Run key) and starts it now. History stays in %LOCALAPPDATA%\PrepPulse.
rem Run it again to update. Log: .artifacts\logs\install.log. To remove: uninstall.cmd
setlocal
cd /d "%~dp0"
set "TARGET=%LOCALAPPDATA%\Programs\PrepPulse"
set "EXE=%TARGET%\PrepPulse.exe"
set "DEVEXE=%CD%\src\PrepPulse.App\bin\Release\net10.0-windows\PrepPulse.exe"
set "CONFIG="
if not "%~1"=="" set "CONFIG=%~f1"
if not exist .artifacts\logs mkdir .artifacts\logs
set "LOG=%CD%\.artifacts\logs\install.log"
> "%LOG%" echo PrepPulse install %date% %time%
>> "%LOG%" echo target: %TARGET%
>> "%LOG%" echo config: %CONFIG%

if defined CONFIG if not exist "%CONFIG%\" (
  echo The folder %CONFIG% doesn't exist.
  >> "%LOG%" echo result: config folder missing
  exit /b 1
)

rem Close running copies so their files can be replaced: the installed one, and a test run from build output.
if exist "%EXE%" "%EXE%" --exit >nul 2>&1
if exist "%DEVEXE%" "%DEVEXE%" --data-dir "%CD%\.artifacts\testrun" --exit >nul 2>&1
ping -n 3 127.0.0.1 >nul

rem Start from an empty program folder so files from older builds don't linger.
if exist "%TARGET%" rmdir /s /q "%TARGET%"

rem Windows x64 only (no Linux/macOS copies of SQLite), uses the installed .NET runtime, and is compiled ahead of time
rem (ReadyToRun) so it starts faster and spends less memory compiling at runtime.
echo Publishing PrepPulse to %TARGET% ...
dotnet publish src\PrepPulse.App\PrepPulse.App.csproj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o "%TARGET%" -nologo -v minimal >> "%LOG%" 2>&1
if errorlevel 1 (
  echo Publish failed. See .artifacts\logs\install.log
  >> "%LOG%" echo result: publish failed
  exit /b 1
)

rem Start with Windows: the same per-user Run entry the tray menu's "Start with Windows" uses.
set "RUNKEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
if defined CONFIG (
  reg add "%RUNKEY%" /v PrepPulse /t REG_SZ /d "\"%EXE%\" --config-dir \"%CONFIG%\"" /f >> "%LOG%" 2>&1
) else (
  reg add "%RUNKEY%" /v PrepPulse /t REG_SZ /d "\"%EXE%\"" /f >> "%LOG%" 2>&1
)
if errorlevel 1 >> "%LOG%" echo warning: couldn't add PrepPulse to startup

echo Starting PrepPulse...
if defined CONFIG (start "" "%EXE%" --config-dir "%CONFIG%") else (start "" "%EXE%")
ping -n 6 127.0.0.1 >nul
"%EXE%" --status --out "%CD%\.artifacts\logs\install-status.json" >nul 2>&1
>> "%LOG%" echo result: installed

echo.
echo PrepPulse is installed in %TARGET% and starts with Windows.
if defined CONFIG echo Plans and settings: %CONFIG%
echo Its icon is by the clock, possibly under the ^^ arrow.
endlocal
