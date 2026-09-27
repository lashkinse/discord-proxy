@echo off
rem Installs DiscordProxy as a Windows service via NSSM.
rem Run from this folder after build.bat (uses artifacts\publish\DiscordProxy.exe).
setlocal

set SERVICE_NAME=DiscordProxy
set APP_DIR=%~dp0
set APP_EXE=%APP_DIR%DiscordProxy.exe
set LOG_DIR=%APP_DIR%logs

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

if not exist "%APP_EXE%" (
  echo %APP_EXE% not found. Run build.bat first.
  pause
  exit /b 1
)

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

nssm install %SERVICE_NAME% "%APP_EXE%"
nssm set %SERVICE_NAME% AppDirectory "%APP_DIR%"
nssm set %SERVICE_NAME% AppStdout "%LOG_DIR%\service-out.log"
nssm set %SERVICE_NAME% AppStderr "%LOG_DIR%\service-err.log"
nssm set %SERVICE_NAME% Start SERVICE_AUTO_START

nssm start %SERVICE_NAME%

echo.
echo Service %SERVICE_NAME% installed and started.
pause
