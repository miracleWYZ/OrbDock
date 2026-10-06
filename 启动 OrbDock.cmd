@echo off
setlocal
cd /d "%~dp0"

if exist "app\OrbDock.exe" (
  start "" "app\OrbDock.exe"
  exit /b 0
)

echo.
echo   app\OrbDock.exe not found -- OrbDock has not been built on this machine.
echo.
echo   This repository contains SOURCE CODE ONLY (no compiled binaries).
echo.
echo   Option 1 - use a prebuilt package (no SDK needed):
echo       https://github.com/miracleWYZ/OrbDock/releases
echo     Download the zip, extract it, then run the .cmd inside that folder.
echo.
echo   Option 2 - build from source (needs the .NET 8 SDK):
echo       run:  build.ps1 -Publish
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
  echo   [i] dotnet was not found on this machine, so it cannot build automatically.
  echo.
  pause
  exit /b 1
)

echo   dotnet found. Build now? It takes about half a minute.
set "ANS="
set /p ANS=  Build? [Y/N]:
if /i not "%ANS%"=="Y" (
  echo   Cancelled.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Publish

if exist "app\OrbDock.exe" (
  echo.
  echo   Build OK. Starting OrbDock...
  start "" "app\OrbDock.exe"
) else (
  echo.
  echo   Build failed - please read the messages above.
  pause
)
exit /b 0
