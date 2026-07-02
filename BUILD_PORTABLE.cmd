@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo ERROR: Windows C# compiler not found.
  pause
  exit /b 1
)
if not exist "portable" mkdir "portable"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /win32icon:"%~dp0assets\LaunchBridge.ico" /win32manifest:"%~dp0app.manifest" /out:"%~dp0portable\LaunchBridge.exe" ^
 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Management.dll ^
 /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.Web.Extensions.dll ^
 "%~dp0src\Program.cs" "%~dp0src\SingleInstanceCoordinator.cs" "%~dp0src\Models.cs" "%~dp0src\LaunchBridgeCore.cs" "%~dp0src\MainForm.cs"
if errorlevel 1 (
  echo BUILD FAILED.
  pause
  exit /b 1
)
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /out:"%~dp0portable\LaunchBridgeSmartClickHost.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll "%~dp0src\SmartClickNativeHost.cs"
if errorlevel 1 (
  echo SMART CLICK HOST BUILD FAILED.
  pause
  exit /b 1
)
copy /y "%~dp0LaunchBridge.exe.config" "%~dp0portable\LaunchBridge.exe.config" >nul
if exist "%~dp0portable\browser-extension" rmdir /s /q "%~dp0portable\browser-extension"
xcopy /e /i /y "%~dp0browser-extension" "%~dp0portable\browser-extension" >nul
echo Portable build created:
echo %~dp0portable\LaunchBridge.exe
start "" "%~dp0portable\LaunchBridge.exe"
endlocal
