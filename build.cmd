@echo off
rem Restores, builds and runs the unit tests. Output goes to .artifacts\logs\build.log.
setlocal
cd /d "%~dp0"
if not exist .artifacts\logs mkdir .artifacts\logs
set "LOG=.artifacts\logs\build.log"
set BUILD=1
set TEST=1

echo PrepPulse build %date% %time%> "%LOG%"
dotnet --version >> "%LOG%" 2>&1

rem A running test instance locks PrepPulse.exe; ask it to close first.
set "EXE=src\PrepPulse.App\bin\Release\net10.0-windows\PrepPulse.exe"
if exist "%EXE%" "%EXE%" --data-dir "%CD%\.artifacts\testrun" --exit --out "%TEMP%\preppulse-exit.json" >nul 2>&1

echo.>> "%LOG%"
echo === restore + build ===>> "%LOG%"
echo Building...
dotnet build PrepPulse.slnx -c Release -nologo -v minimal >> "%LOG%" 2>&1
set BUILD=%ERRORLEVEL%
>> "%LOG%" echo build exit %BUILD%
if not "%BUILD%"=="0" goto done

echo.>> "%LOG%"
echo === unit tests ===>> "%LOG%"
echo Testing...
dotnet test PrepPulse.slnx -c Release --no-build -nologo -v minimal --logger "console;verbosity=normal" --logger "trx;LogFileName=tests.trx" --results-directory .artifacts\test-results >> "%LOG%" 2>&1
set TEST=%ERRORLEVEL%
>> "%LOG%" echo test exit %TEST%

:done
> .artifacts\logs\build.exit echo %BUILD% %TEST%
echo Done: build=%BUILD% tests=%TEST%. Log: %LOG%
endlocal
