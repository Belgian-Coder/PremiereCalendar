@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-PremiereCalendar.ps1" %*
exit /b %ERRORLEVEL%
