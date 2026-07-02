@echo off
setlocal
set "EXE=%LOCALAPPDATA%\DevMind\LaunchBridge\LaunchBridge.exe"
if not exist "%EXE%" set "EXE=%LOCALAPPDATA%\DevMind\LaunchBridge\DevMindLaunchBridge.exe"
if not exist "%EXE%" (
  echo LaunchBridge is not installed yet.
  echo Run INSTALL_LAUNCHBRIDGE.cmd first.
  pause
  exit /b 1
)
start "" "%EXE%"
endlocal
