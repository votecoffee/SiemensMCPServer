@echo off
setlocal
rem Launch the last desktop installation without rebuilding it.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Start-Desktop.ps1" %*
set "SIEMENS_RUN_EXIT=%ERRORLEVEL%"
if not "%SIEMENS_RUN_EXIT%"=="0" (
    echo.
    echo Launch failed. Run InstallAndRun.cmd first or review the message above.
    pause
)
exit /b %SIEMENS_RUN_EXIT%
