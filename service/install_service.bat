@echo off
rem Installs and starts the DiscordProxy service (WinSW wrapper, no extra tools needed).
rem Expects DiscordProxyService.exe + DiscordProxyService.xml next to the proxy exe:
rem build.bat already copies everything into artifacts\publish.
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
