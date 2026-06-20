@echo off
setlocal
cd /d "%~dp0.."
dotnet run --project GoldSrcProbe -c Release -- --no-pause --mode both
exit /b %ERRORLEVEL%
