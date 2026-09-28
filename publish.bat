@echo off
:: Bikin 1 file exe mandiri (tanpa perlu install .NET) di folder dist\.
:: Yang dibagikan ke user cukup dist\SealDpsMeter.exe. User tetap perlu Npcap
:: (https://npcap.com) - lisensi Npcap gratis nggak mengizinkan ikut dibundel.

cd /d "%~dp0"
dotnet publish src\DpsMeterUI\DpsMeterUI.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none -o dist\publish
if errorlevel 1 (
    echo.
    echo Publish GAGAL.
    pause
    exit /b 1
)
copy /y dist\publish\DpsMeterUI.exe dist\SealDpsMeter.exe >nul
echo.
echo Selesai: dist\SealDpsMeter.exe
pause
