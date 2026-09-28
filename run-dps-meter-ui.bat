@echo off
:: Seal Online DPS Meter - UI version (WPF overlay)

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Meminta hak Administrator...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
dotnet run --project src\DpsMeterUI\DpsMeterUI.csproj -c Release
