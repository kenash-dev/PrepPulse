@echo off
rem Removes the PrepPulse that install.cmd installed: stops it, takes it out of startup, deletes the program folder.
rem Keeps your plans and settings, and your history in %LOCALAPPDATA%\PrepPulse.
setlocal
set "TARGET=%LOCALAPPDATA%\Programs\PrepPulse"
if exist "%TARGET%\PrepPulse.exe" "%TARGET%\PrepPulse.exe" --exit >nul 2>&1
ping -n 3 127.0.0.1 >nul
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v PrepPulse /f >nul 2>&1
if exist "%TARGET%" rmdir /s /q "%TARGET%"
echo PrepPulse removed. Your plans, settings and history were kept.
pause
endlocal
