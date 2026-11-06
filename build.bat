@echo off
rem Builds AuraFlyout and publishes a single-file exe to publish\ (needs the .NET 10 SDK).
setlocal
set ROOT=%~dp0

dotnet publish "%ROOT%src\AuraFlyout\AuraFlyout.csproj" -c Release -r win-x64 -p:Platform=x64 ^
  --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true ^
  -o "%ROOT%publish" || exit /b 1

echo.
echo Done: %ROOT%publish\AuraFlyout.exe
