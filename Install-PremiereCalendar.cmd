@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-PremiereCalendar.ps1" %*
exit /b %ERRORLEVEL%
