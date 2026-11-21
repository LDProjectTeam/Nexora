#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'AutoPowerMode'),
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$taskName = 'AutoPowerMode'

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-InstallDir', "`"$InstallDir`""
    )
    if ($RemoveData) { $arguments += '-RemoveData' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit
}

$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'SilentlyContinue'
schtasks.exe /End /TN $taskName 2>$null | Out-Null
schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
$ErrorActionPreference = $previousEap
Get-Process AutoPowerMode -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if (Test-Path $InstallDir) {
    if ($RemoveData) {
        Remove-Item $InstallDir -Recurse -Force
        Write-Host "Удалено полностью: $InstallDir" -ForegroundColor Green
    }
    else {
        Get-ChildItem $InstallDir -File | Where-Object { $_.Name -ne 'config.json' } | Remove-Item -Force
        Get-ChildItem $InstallDir -Directory | Where-Object { $_.Name -ne 'logs' } | Remove-Item -Recurse -Force
        Write-Host "Программа удалена. Конфиг и журнал оставлены в $InstallDir" -ForegroundColor Green
        Write-Host 'Чтобы удалить и их: .\Uninstall.ps1 -RemoveData'
    }
}

Write-Host 'Автозапуск удалён.' -ForegroundColor Green
