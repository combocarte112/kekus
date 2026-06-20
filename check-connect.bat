@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "HOST=%~1"
if not "%HOST%"=="" goto run

if exist "last-host.txt" (
  set /p HOST=<last-host.txt
  echo Folosesc ultimul server: %HOST%
  echo.
  goto run
)

echo.
echo === GoldSrcProbe - check connect ===
echo.
set /p "HOST=IP:PORT (Enter = 135.125.173.213:27015): "
if "%HOST%"=="" set "HOST=135.125.173.213:27015"

:run
echo %HOST%>last-host.txt
echo.
echo Testez: %HOST%
echo.

dotnet run --project GoldSrcProbe -c Release -- --no-pause --check-connect --host %HOST%
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" echo Exit code: %EXITCODE%
pause
exit /b %EXITCODE%
