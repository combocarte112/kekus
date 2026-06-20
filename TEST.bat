@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "HOST=%~1"
set "MODE=%~2"

if "%HOST%"=="" (
  if exist "last-host.txt" (
    set /p HOST=<last-host.txt
  ) else if exist "servers.txt" (
    for /f "usebackq tokens=1 delims=# " %%a in ("servers.txt") do (
      if not "%%a"=="" if not "%%a"=="#" set "HOST=%%a" & goto got_host
    )
  )
  if "%HOST%"=="" set "HOST=179.61.132.147:27015"
)

:got_host
echo %HOST%| findstr /C:":" >nul || set "HOST=%HOST%:27015"
echo %HOST%>last-host.txt

if "%MODE%"=="" goto menu

if /i "%MODE%"=="join" goto do_join
if /i "%MODE%"=="verify" goto do_verify
if /i "%MODE%"=="debug" goto do_debug
echo Mod necunoscut: %MODE%
echo Foloseste: TEST.bat [IP:PORT] [join^|verify^|debug]
goto end

:menu
cls
echo.
echo  ============================================
echo   GoldSrc Probe - TEST (un singur launcher)
echo  ============================================
echo.
echo   Server: %HOST%
echo   (schimba: TEST.bat IP:PORT join)
echo.
echo   [1] JOIN      - intra pe server, ramane ~30s (TAB)
echo   [2] VERIFY    - join + status + salveaza JSON
echo   [3] DEBUG     - join cu loguri retea (consistency)
echo   [4] Alt IP:PORT
echo   [Q] Iesire
echo.
choice /c 1234Q /n /m "Alege: "
if errorlevel 5 goto end
if errorlevel 4 goto ask_host
if errorlevel 3 goto do_debug
if errorlevel 2 goto do_verify
goto do_join

:ask_host
echo.
set /p "HOST=IP:PORT: "
if "%HOST%"=="" goto menu
echo %HOST%| findstr /C:":" >nul || set "HOST=%HOST%:27015"
echo %HOST%>last-host.txt
goto menu

:do_join
echo.
echo === JOIN %HOST% (30s) ===
dotnet run --project GoldSrcProbe -c Release -- --no-pause --join --host %HOST% --hold 30
goto done

:do_verify
echo.
echo === VERIFY %HOST% ===
dotnet run --project GoldSrcProbe -c Release -- --no-pause --verify --host %HOST%
echo.
echo Rezultat: output\verify_players.json
goto done

:do_debug
echo.
echo === JOIN DEBUG %HOST% (20s, loguri consistency) ===
dotnet run --project GoldSrcProbe -c Release -- --no-pause --join --host %HOST% --hold 20 --debug-net
goto done

:done
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" echo Exit code: %EXITCODE%
:end
pause
exit /b %EXITCODE%
