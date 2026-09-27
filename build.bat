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
rem Service files live next to the exe; drop publish leftovers it never uses.
copy /y "%~dp0service\DiscordProxyService.exe" "%~dp0artifacts\publish\" >nul
copy /y "%~dp0service\DiscordProxyService.xml" "%~dp0artifacts\publish\" >nul
copy /y "%~dp0service\install_service.bat" "%~dp0artifacts\publish\" >nul
copy /y "%~dp0service\uninstall_service.bat" "%~dp0artifacts\publish\" >nul
del /q "%~dp0artifacts\publish\web.config" "%~dp0artifacts\publish\DiscordProxy.staticwebassets.endpoints.json" 2>nul
echo OK: %~dp0artifacts\publish\DiscordProxy.exe
