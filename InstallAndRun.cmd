@echo off
setlocal
rem Double-click to install the V15.1 desktop GUI and open it.
rem Named PowerShell options can follow, e.g. -TiaPortalVersion V19.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Install-Desktop.ps1" -Run %*
set "SIEMENS_INSTALL_EXIT=%ERRORLEVEL%"
if not "%SIEMENS_INSTALL_EXIT%"=="0" (
    echo.
    echo Installation failed. Review the message above.
    pause
)
exit /b %SIEMENS_INSTALL_EXIT%
