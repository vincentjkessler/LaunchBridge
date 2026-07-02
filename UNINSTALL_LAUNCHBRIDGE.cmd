@echo off
setlocal EnableExtensions
set "INSTALL=%LOCALAPPDATA%\DevMind\LaunchBridge"
set "EXE=%INSTALL%\LaunchBridge.exe"
if not exist "%EXE%" set "EXE=%INSTALL%\DevMindLaunchBridge.exe"

echo This removes LaunchBridge and its file associations.
echo Installed products in D:\Work are NOT deleted.
echo.
set /p "ANSWER=Type REMOVE to continue: "
if /i not "%ANSWER%"=="REMOVE" exit /b 0

if exist "%EXE%" "%EXE%" --remove-associations

del /q "%USERPROFILE%\Desktop\LaunchBridge.lnk" >nul 2>&1
del /q "%USERPROFILE%\Desktop\DevMind LaunchBridge.lnk" >nul 2>&1
del /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\LaunchBridge\LaunchBridge.lnk" >nul 2>&1
del /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\DevMind\DevMind LaunchBridge.lnk" >nul 2>&1

if exist "%INSTALL%" rmdir /s /q "%INSTALL%"
echo LaunchBridge removed. Installed products were left untouched.
pause
endlocal
