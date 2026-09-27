@echo off
rem Builds a self-contained single-file Windows exe into artifacts\publish.
rem Requires .NET 8 SDK or newer. Run the exe with appsettings.json next to it.
setlocal
cd /d "%~dp0"
dotnet publish src\DiscordProxy -c Release -r win-x64 --self-contained --nologo -v q ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o artifacts\publish
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo OK: %~dp0artifacts\publish\DiscordProxy.exe
