@echo off
rem Double-click to build PUBLISH\CamDock.exe
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
pause
