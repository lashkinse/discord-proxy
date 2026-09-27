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

rem NSSM next to the scripts takes precedence over PATH.
if exist "%~dp0nssm.exe" set "PATH=%~dp0;%PATH%"

where nssm >nul 2>&1
if errorlevel 1 (
  echo NSSM not found. Put nssm.exe next to this script or download it from https://nssm.cc/download and put it on PATH.
  pause
  exit /b 1
)

nssm stop %SERVICE_NAME%
nssm remove %SERVICE_NAME% confirm

echo.
echo Service %SERVICE_NAME% stopped and removed.
pause
