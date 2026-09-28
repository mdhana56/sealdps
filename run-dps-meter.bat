@echo off
:: Seal Online DPS Meter - auto attack (v0)
:: Minta hak admin karena capture packet (Npcap) butuh privilege tinggi.

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Meminta hak Administrator...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
dotnet run --project src\DpsMeter\DpsMeter.csproj -c Release
pause
