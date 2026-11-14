@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo Install .NET 10 SDK x64 first: winget install Microsoft.DotNet.SDK.10 & exit /b 1)

echo [1/2] Building SoundFlow...
dotnet publish "SoundFlow.App\SoundFlow.App.csproj" -c Release -r win-x64 --self-contained true -o "native-release" || goto :failed
copy /y "native-release\SoundFlow.exe" "SoundFlow.exe" >nul

if not exist "installer-assets\EqualizerAPO-x64-1.4.2.exe" (
  echo Missing installer-assets\EqualizerAPO-x64-1.4.2.exe
  exit /b 1
)

echo [2/2] Building combined installer...
dotnet publish "SoundFlow.Setup\SoundFlow.Setup.csproj" -c Release -r win-x64 --self-contained true -o "installer-release" || goto :failed
copy /y "installer-release\SoundFlow-Setup.exe" "SoundFlow-Setup.exe" >nul
echo Done: %~dp0SoundFlow-Setup.exe
exit /b 0

:failed
echo Build failed.
exit /b 1
