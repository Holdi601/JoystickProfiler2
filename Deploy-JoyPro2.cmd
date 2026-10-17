@echo off
rem Builds JoyPro2 into E:\JoyPro2 (or the folder after -Target) and starts it. Options: see tools\deploy.ps1.
rem Examples:  Deploy-JoyPro2.cmd
rem            Deploy-JoyPro2.cmd -Target D:\Games\JoyPro2 -NoStart
rem            Deploy-JoyPro2.cmd -Package -PackageDir E:\JoyPro2-Packages
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\deploy.ps1" %*
if errorlevel 1 (
    echo.
    echo Build failed. See the messages above.
    pause
    exit /b 1
)
