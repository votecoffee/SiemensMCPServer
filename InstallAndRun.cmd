@echo off
setlocal
rem Double-click to install the V15.1 desktop GUI and open it.
rem Named PowerShell options can follow, e.g. -TiaPortalVersion V19.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Invoke-DesktopCommand.ps1" -Command Install %*
exit /b %ERRORLEVEL%
