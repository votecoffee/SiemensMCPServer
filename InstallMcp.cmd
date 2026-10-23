@echo off
setlocal DisableDelayedExpansion
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Install-StableMcp.ps1" %*
exit /b %ERRORLEVEL%
