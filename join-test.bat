@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "HOST=%~1"
set "HOLD=%~2"

if not "%HOST%"=="" goto have_host

if exist "last-host.txt" (
  set /p HOST=<last-host.txt
  echo Folosesc ultimul server: %HOST%
  echo.
  goto have_host
)

echo.
echo === GoldSrcProbe - join test ===
echo.
set /p "HOST=IP:PORT (Enter = 135.125.173.213:27015): "
if "%HOST%"=="" set "HOST=135.125.173.213:27015"

:have_host
if "%HOLD%"=="" set "HOLD=30"

echo %HOST%>last-host.txt
echo.
echo Join: %HOST% ^| hold %HOLD%s ^| nume din config.json
echo.

dotnet run --project GoldSrcProbe -c Release -- --no-pause --join --host %HOST% --hold %HOLD%
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" echo Exit code: %EXITCODE%
pause
exit /b %EXITCODE%
