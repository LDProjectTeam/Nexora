@echo off
setlocal
title Nexora Launcher
cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-Launcher.ps1" %*
exit /b %ERRORLEVEL%
