@echo off
rem Stops and removes the DiscordProxy Windows service.
setlocal

set SERVICE_NAME=DiscordProxy

net session >nul 2>&1
if errorlevel 1 (
  echo Run this script as administrator.
  pause
  exit /b 1
)

where nssm >nul 2>&1
if errorlevel 1 (
  echo NSSM not found. Download it from https://nssm.cc/download and put nssm.exe on PATH.
  pause
  exit /b 1
)

nssm stop %SERVICE_NAME%
nssm remove %SERVICE_NAME% confirm

echo.
echo Service %SERVICE_NAME% stopped and removed.
pause
