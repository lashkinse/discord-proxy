@echo off
rem Installs and starts the DiscordProxy service (WinSW wrapper, no extra tools needed).
rem Run from any folder containing DiscordProxyService.exe/.xml.
setlocal

set WRAPPER=%~dp0DiscordProxyService.exe

net session >nul 2>&1
if errorlevel 1 (
  echo Run this script as administrator.
  pause
  exit /b 1
)

if not exist "%WRAPPER%" (
  echo Service wrapper not found. Run build.bat first.
  pause
  exit /b 1
)

"%WRAPPER%" install
"%WRAPPER%" start

echo.
echo Service installed and started. Check http://localhost:7070/health/ready
pause
