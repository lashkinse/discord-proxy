@echo off
rem Stops and removes the DiscordProxy service.
setlocal

set WRAPPER=%~dp0DiscordProxyService.exe

net session >nul 2>&1
if errorlevel 1 (
  echo Run this script as administrator.
  pause
  exit /b 1
)

if not exist "%WRAPPER%" (
  echo Service wrapper not found. Nothing to remove.
  pause
  exit /b 1
)

"%WRAPPER%" stop
"%WRAPPER%" uninstall

echo.
echo Service stopped and removed.
pause
