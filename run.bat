@echo off
cd /d "%~dp0"
dotnet run --project GoldSrcProbe -c Release -- %*
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" echo Exit code: %EXITCODE%
pause
