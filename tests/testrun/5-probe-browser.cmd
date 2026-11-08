@echo off
rem Opens a docs page in Edge, then logs what PrepPulse sees for 8 seconds (foreground app, domain, idle).
setlocal
cd /d "%~dp0..\.."
set "EXE=%CD%\src\PrepPulse.App\bin\Release\net10.0-windows\PrepPulse.exe"
start "" msedge "https://learn.microsoft.com/en-us/dotnet/"
ping -n 5 127.0.0.1 >nul
"%EXE%" --data-dir "%CD%\.artifacts\testrun" --probe 8 --out "%CD%\.artifacts\testrun\probe.txt"
endlocal
