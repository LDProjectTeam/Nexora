#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'AutoPowerMode'),
    [switch]$NoAutoStart,
    [switch]$NoStart
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
    if ($NoAutoStart) { $arguments += '-NoAutoStart' }
    if ($NoStart) { $arguments += '-NoStart' }

    Write-Host 'Нужны права администратора. Запускаю установку с повышением...' -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments
    exit
}

$source = Join-Path $PSScriptRoot 'publish'
if (-not (Test-Path (Join-Path $source 'AutoPowerMode.exe'))) {
    Write-Host 'Сборка не найдена, собираю...' -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot 'Build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }
}

Write-Host "Установка в $InstallDir" -ForegroundColor Cyan

# Останавливаем старую версию.
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'SilentlyContinue'
schtasks.exe /End /TN $taskName 2>$null | Out-Null
schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
$ErrorActionPreference = $previousEap
Get-Process AutoPowerMode -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

# Конфиг пользователя не затираем.
$configTarget = Join-Path $InstallDir 'config.json'
$keepConfig = Test-Path $configTarget
if ($keepConfig) {
    Copy-Item $configTarget (Join-Path $env:TEMP 'AutoPowerMode.config.keep.json') -Force
}

Copy-Item (Join-Path $source '*') $InstallDir -Recurse -Force
if ($keepConfig) {
    Copy-Item (Join-Path $env:TEMP 'AutoPowerMode.config.keep.json') $configTarget -Force
    Remove-Item (Join-Path $env:TEMP 'AutoPowerMode.config.keep.json') -Force
}

if ($NoAutoStart) {
    Write-Host 'Автозапуск отключён (-NoAutoStart).' -ForegroundColor Yellow
}
else {
    $userId = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $exe = [Security.SecurityElement]::Escape((Join-Path $InstallDir 'AutoPowerMode.exe'))
    $workDir = [Security.SecurityElement]::Escape($InstallDir)

    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>AutoPowerMode: игровой режим Lenovo по запуску и выходу из игры.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT10S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$userId</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>"$exe"</Command>
      <WorkingDirectory>$workDir</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@

    $xmlPath = Join-Path $env:TEMP 'AutoPowerMode.task.xml'
    [IO.File]::WriteAllText($xmlPath, $xml, [Text.Encoding]::Unicode)

    schtasks.exe /Create /TN $taskName /XML $xmlPath /F 2>&1 | Out-Null
    $createCode = $LASTEXITCODE
    Remove-Item $xmlPath -Force -ErrorAction SilentlyContinue

    if ($createCode -ne 0) {
        throw "Не удалось создать задачу планировщика (код $createCode)."
    }

    Write-Host "Автозапуск при входе в систему: задача '$taskName'." -ForegroundColor Green
}

if (-not $NoStart) {
    if (Test-Path (Join-Path $InstallDir 'AutoPowerMode.exe')) {
        Start-Process -FilePath (Join-Path $InstallDir 'AutoPowerMode.exe') -WorkingDirectory $InstallDir
        Start-Sleep -Seconds 2
        $process = Get-Process AutoPowerMode -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($process) {
            Write-Host "AutoPowerMode запущен (PID $($process.Id))." -ForegroundColor Green
        }
        else {
            Write-Warning 'Процесс не появился. Проверьте журнал: %LOCALAPPDATA%\AutoPowerMode\logs\auto-power-mode.log'
        }
    }
}

Write-Host ''
Write-Host "Готово. Настройки: $InstallDir\config.json"
Write-Host "Журнал: $env:LOCALAPPDATA\AutoPowerMode\logs\auto-power-mode.log"
