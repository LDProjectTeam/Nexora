@echo off
title Nexora Launcher (Debug)
cd /d "%~dp0"

rem Use the local .NET runtime from D:\launcher\.dotnet if no global one is installed.
if exist "%~dp0.dotnet\dotnet.exe" set "DOTNET_ROOT=%~dp0.dotnet"

set "APP=%~dp0src\Nexora.App\bin\Debug\net8.0-windows\win-x64\Nexora.exe"

if exist "%APP%" (
    start "" "%APP%"
) else (
    echo Debug build not found: %APP%
    echo Build it first:
    echo   .dotnet\dotnet.exe build Nexora.sln -c Debug --nologo -v minimal
    pause
)
exit /b 0
