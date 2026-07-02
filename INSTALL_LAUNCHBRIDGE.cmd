@echo off
setlocal EnableExtensions
cd /d "%~dp0"

echo ============================================================
echo  LaunchBridge v0.3.1 - Build and Install
echo ============================================================
echo.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  for /f "delims=" %%I in ('where csc.exe 2^>nul') do if not defined CSC_FOUND set "CSC_FOUND=%%I"
  if defined CSC_FOUND set "CSC=%CSC_FOUND%"
)

if not exist "%CSC%" (
  echo ERROR: The Windows C# compiler was not found.
  echo This launcher uses the .NET Framework compiler included with most Windows systems.
  echo Install or enable .NET Framework 4.x, then run this file again.
  echo.
  pause
  exit /b 1
)

set "BUILD=%TEMP%\LaunchBridgeBuild"
set "INSTALL=%LOCALAPPDATA%\DevMind\LaunchBridge"
if exist "%BUILD%" rmdir /s /q "%BUILD%"
mkdir "%BUILD%" >nul 2>&1
mkdir "%INSTALL%" >nul 2>&1

echo [1/4] Compiling LaunchBridge locally...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /win32icon:"%~dp0assets\LaunchBridge.ico" /win32manifest:"%~dp0app.manifest" /out:"%BUILD%\LaunchBridge.exe" ^
 /reference:System.dll ^
 /reference:System.Core.dll ^
 /reference:System.Drawing.dll ^
 /reference:System.Windows.Forms.dll ^
 /reference:System.Management.dll ^
 /reference:System.IO.Compression.dll ^
 /reference:System.IO.Compression.FileSystem.dll ^
 /reference:System.Web.Extensions.dll ^
 "%~dp0src\Program.cs" "%~dp0src\SingleInstanceCoordinator.cs" "%~dp0src\Models.cs" "%~dp0src\LaunchBridgeCore.cs" "%~dp0src\MainForm.cs"

if errorlevel 1 (
  echo.
  echo BUILD FAILED. The compiler output above explains the problem.
  echo Nothing was installed.
  pause
  exit /b 1
)

echo [1b/4] Compiling Smart Click helper...
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /out:"%BUILD%\LaunchBridgeSmartClickHost.exe" ^
 /reference:System.dll ^
 /reference:System.Core.dll ^
 /reference:System.Web.Extensions.dll ^
 "%~dp0src\SmartClickNativeHost.cs"

if errorlevel 1 (
  echo.
  echo SMART CLICK HELPER BUILD FAILED. Nothing was installed.
  pause
  exit /b 1
)

echo [2/4] Installing to %INSTALL% ...
taskkill /IM LaunchBridge.exe /F >nul 2>&1
taskkill /IM DevMindLaunchBridge.exe /F >nul 2>&1
copy /y "%BUILD%\LaunchBridge.exe" "%INSTALL%\LaunchBridge.exe" >nul
del /q "%INSTALL%\DevMindLaunchBridge.exe" >nul 2>&1
copy /y "%~dp0assets\LaunchBridge.ico" "%INSTALL%\LaunchBridge.ico" >nul
copy /y "%~dp0LaunchBridge.exe.config" "%INSTALL%\LaunchBridge.exe.config" >nul
copy /y "%~dp0README.md" "%INSTALL%\README.md" >nul
copy /y "%BUILD%\LaunchBridgeSmartClickHost.exe" "%INSTALL%\LaunchBridgeSmartClickHost.exe" >nul
if exist "%INSTALL%\browser-extension" rmdir /s /q "%INSTALL%\browser-extension"
xcopy /e /i /y "%~dp0browser-extension" "%INSTALL%\browser-extension" >nul

if not exist "%INSTALL%\LaunchBridge.exe" (
  echo ERROR: Installation copy failed.
  pause
  exit /b 1
)

echo [3/4] Registering package types and Smart Click...
"%INSTALL%\LaunchBridge.exe" --install-defaults
if errorlevel 1 (
  echo WARNING: Association registration returned an error.
)
"%INSTALL%\LaunchBridge.exe" --install-smart-click
if errorlevel 1 (
  echo WARNING: Smart Click native host registration returned an error.
)

echo [4/4] Creating shortcuts...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$exe='%INSTALL%\LaunchBridge.exe'; $ws=New-Object -ComObject WScript.Shell;" ^
  "$desk=[Environment]::GetFolderPath('Desktop'); Remove-Item -LiteralPath (Join-Path $desk 'DevMind LaunchBridge.lnk') -Force -ErrorAction SilentlyContinue; $s=$ws.CreateShortcut((Join-Path $desk 'LaunchBridge.lnk')); $s.TargetPath=$exe; $s.WorkingDirectory='%INSTALL%'; $s.IconLocation=$exe+',0'; $s.Save();" ^
  "$menu=[Environment]::GetFolderPath('StartMenu'); $legacy=Join-Path $menu 'Programs\DevMind\DevMind LaunchBridge.lnk'; Remove-Item -LiteralPath $legacy -Force -ErrorAction SilentlyContinue; $dir=Join-Path $menu 'Programs\LaunchBridge'; New-Item -ItemType Directory -Force -Path $dir ^| Out-Null; $s2=$ws.CreateShortcut((Join-Path $dir 'LaunchBridge.lnk')); $s2.TargetPath=$exe; $s2.WorkingDirectory='%INSTALL%'; $s2.IconLocation=$exe+',0'; $s2.Save();"

echo.
echo ============================================================
echo  INSTALL COMPLETE
 echo ============================================================
echo  Installed: %INSTALL%\LaunchBridge.exe
echo  Registered: .devmind and Smart Click native host
echo.
echo  Future flow:
echo    Download a .devmind package
 echo    Click Open file in Edge
 echo    LaunchBridge validates, installs, and launches it
 echo.
start "" "%INSTALL%\LaunchBridge.exe"
endlocal
exit /b 0
