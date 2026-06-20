@echo off
cd /d "%~dp0"
dotnet run --project GoldSrcProbe -c Release -- --no-pause --verify --host 179.61.132.147:27015
pause
