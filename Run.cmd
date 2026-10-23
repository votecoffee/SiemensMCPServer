@echo off
setlocal
rem Launch the last desktop installation without rebuilding it.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Invoke-DesktopCommand.ps1" -Command Run %*
exit /b %ERRORLEVEL%
