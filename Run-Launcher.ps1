# ==============================================================================
# Vellum Launcher — запуск актуальной сборки с живыми логами в консоли.
#
# Порядок выбора:
#   1. publish\Vellum.exe
#   2. publish-portable\Vellum.exe
#   3. самая свежая сборка в src\Vellum.App\bin\Release|Debug (dev-режим)
#
# Явный путь можно передать параметром: .\Run-Launcher.ps1 -ExePath <путь>
# ==============================================================================
[CmdletBinding()]
param(
    [string]$ExePath
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }

function Write-Banner {
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host "              Vellum Universal Launcher                     " -ForegroundColor Yellow
    Write-Host "============================================================" -ForegroundColor Cyan
}

function Wait-ForKey {
    try {
        if ([Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
            Write-Host ""
            Write-Host "Нажмите любую клавишу для закрытия окна..." -ForegroundColor DarkGray
            $null = [Console]::ReadKey($true)
        }
    }
    catch { }
}

Write-Banner

$candidates = [System.Collections.Generic.List[System.IO.FileInfo]]::new()

if ($ExePath) {
    if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
        Write-Host "[ERROR] Файл не найден: $ExePath" -ForegroundColor Red
        Wait-ForKey
        exit 1
    }
    $candidates.Add((Get-Item -LiteralPath $ExePath))
}
else {
    foreach ($relative in @('publish\Vellum.exe', 'publish-portable\Vellum.exe')) {
        $path = Join-Path $scriptDir $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $candidates.Add((Get-Item -LiteralPath $path))
        }
    }

    # Dev fallback: the newest build produced by `dotnet build`.
    if ($candidates.Count -eq 0) {
        Get-ChildItem -Path $scriptDir -Filter 'Vellum.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\bin\\(Release|Debug)\\' } |
            ForEach-Object { $candidates.Add($_) }
    }
}

$exe = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) {
    Write-Host "[ERROR] Vellum.exe не найден." -ForegroundColor Red
    Write-Host "Сначала опубликуйте проект: .\installer\Build-Installer.ps1 или dotnet publish." -ForegroundColor Yellow
    Wait-ForKey
    exit 1
}

$workingDir = $exe.DirectoryName
Write-Host "[OK] Сборка:  " -NoNewline
Write-Host $exe.Directory.Name -ForegroundColor Green
Write-Host "     Папка:   $workingDir" -ForegroundColor Gray
Write-Host "     Изменён: $($exe.LastWriteTime.ToString('dd.MM.yyyy HH:mm:ss'))" -ForegroundColor Gray
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$bootstrapper = Join-Path $workingDir 'Bootstrapper\Vellum.Bootstrapper.exe'
if (Test-Path -LiteralPath $bootstrapper -PathType Leaf) {
    $psi.FileName = $bootstrapper
}
else {
    $psi.FileName = $exe.FullName
}
$psi.WorkingDirectory = $workingDir
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
foreach ($arg in $args) { $psi.ArgumentList.Add($arg) }

$proc = [System.Diagnostics.Process]::new()
$proc.StartInfo = $psi

$outEvent = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action {
    if (-not [string]::IsNullOrEmpty($EventArgs.Data)) {
        $stamp = (Get-Date).ToString('HH:mm:ss')
        Write-Host "[$stamp] [OUT] $($EventArgs.Data)" -ForegroundColor DarkGray
    }
}
$errEvent = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action {
    if (-not [string]::IsNullOrEmpty($EventArgs.Data)) {
        $stamp = (Get-Date).ToString('HH:mm:ss')
        Write-Host "[$stamp] [ERR] $($EventArgs.Data)" -ForegroundColor Red
    }
}

$exitCode = 0
try {
    $proc.Start() | Out-Null
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()

    Write-Host "[LAUNCHER] Vellum запущен (PID: $($proc.Id))" -ForegroundColor Green
    Write-Host "[LAUNCHER] Логи приложения идут ниже. Закройте лаунчер, чтобы выйти." -ForegroundColor Cyan
    Write-Host "------------------------------------------------------------" -ForegroundColor DarkGray

    while (-not $proc.HasExited) {
        Start-Sleep -Milliseconds 200
    }

    $exitCode = $proc.ExitCode
    Write-Host "------------------------------------------------------------" -ForegroundColor DarkGray
    if ($exitCode -eq 0) {
        Write-Host "[LAUNCHER] Приложение завершилось успешно." -ForegroundColor Green
    }
    else {
        Write-Host "[LAUNCHER] Приложение завершилось с кодом: $exitCode" -ForegroundColor Red
    }
}
catch {
    Write-Host "[EXCEPTION] $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    Unregister-Event -SourceIdentifier $outEvent.Name -ErrorAction SilentlyContinue
    Unregister-Event -SourceIdentifier $errEvent.Name -ErrorAction SilentlyContinue
    if ($proc) { $proc.Dispose() }
}

Write-Host ""
Write-Host "Полный лог: $env:APPDATA\Vellum\logs" -ForegroundColor DarkGray
Wait-ForKey
exit $exitCode
