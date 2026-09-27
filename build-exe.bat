@echo off
rem Builds a self-contained single-file Windows exe into publish\.
rem Requires .NET 8 SDK or newer. Run the exe with PORT and DB_PATH env vars.
setlocal
cd /d "%~dp0"
dotnet publish -c Release -r win-x64 --self-contained --nologo -v q ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o publish
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo OK: %~dp0publish\DiscordProxy.exe
