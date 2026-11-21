# ==============================================================================
# Nexora Universal Launcher with Live Background CMD Logs
# Автоматически находит и запускает последнюю версию фиксов
# ==============================================================================

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "              Nexora Universal Launcher                  " -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "[*] Поиск последней версии фиксов..." -ForegroundColor Gray

# 1. Поиск кандидатов в publish-portable* и publish*
$publishCandidates = Get-ChildItem -Path $scriptDir -Filter "Nexora.exe" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { 
        $d = $_.DirectoryName
        $d -match "\\publish-portable" -or $d -match "\\publish($|\\)"
    } |
    Sort-Object LastWriteTime -Descending

# 2. Если в publish ничего нет, поиск в bin\Release / bin\Debug
if (-not $publishCandidates -or $publishCandidates.Count -eq 0) {
    $publishCandidates = Get-ChildItem -Path $scriptDir -Filter "Nexora.exe" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
}

if (-not $publishCandidates -or $publishCandidates.Count -eq 0) {
    Write-Host "`n[ERROR] Исполняемый файл Nexora.exe не найден!" -ForegroundColor Red
    Write-Host "Сначала скомпилируйте или опубликуйте проект (dotnet publish)." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Нажмите любую клавишу для выхода..." -ForegroundColor DarkGray
    try {
        if ([Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
            $null = [Console]::ReadKey($true)
        }
    } catch { }
    exit 1
}

$latestExe = $publishCandidates[0]
$workingDir = $latestExe.DirectoryName
$folderName = $latestExe.Directory.Name
$buildTime = $latestExe.LastWriteTime.ToString("dd.MM.yyyy HH:mm:ss")

try {
    $host.UI.RawUI.WindowTitle = "Nexora Launcher - [$folderName]"
} catch { }

Write-Host "[OK] Найдена последняя версия: " -NoNewline
Write-Host "$folderName" -ForegroundColor Green
Write-Host "     Папка:       $workingDir" -ForegroundColor Gray
Write-Host "     Дата сборки: $buildTime" -ForegroundColor Gray
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

# Подготовка к чтению lifecycle.log
$logPath = Join-Path $workingDir "lifecycle.log"
$initialOffset = 0
if (Test-Path $logPath) {
    $initialOffset = (Get-Item $logPath).Length
}

# Настройка запуска процесса с перехватом stdout / stderr
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $latestExe.FullName
$psi.WorkingDirectory = $workingDir
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

# Передача переданных аргументов (если есть)
foreach ($arg in $args) {
    $psi.ArgumentList.Add($arg)
}

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi

$outEvent = Register-ObjectEvent -InputObject $proc -EventName OutputDataReceived -Action {
    if (-not [string]::IsNullOrEmpty($EventArgs.Data)) {
        $timestamp = (Get-Date).ToString("HH:mm:ss")
        Write-Host "[$timestamp] [STDOUT] $($EventArgs.Data)" -ForegroundColor DarkGray
    }
}
$errEvent = Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action {
    if (-not [string]::IsNullOrEmpty($EventArgs.Data)) {
        $timestamp = (Get-Date).ToString("HH:mm:ss")
        Write-Host "[$timestamp] [STDERR] $($EventArgs.Data)" -ForegroundColor Red
    }
}

$exitCode = 0

try {
    $proc.Start() | Out-Null
    $proc.BeginOutputReadLine()
    $proc.BeginErrorReadLine()

    Write-Host "[LAUNCHER] Nexora запущен (PID: $($proc.Id))" -ForegroundColor Green
    Write-Host "[LAUNCHER] Консоль CMD активна на фоне. Логи приложения в реальном времени:" -ForegroundColor Cyan
    Write-Host "------------------------------------------------------------" -ForegroundColor DarkGray

    $fileStream = $null
    $streamReader = $null
    $opened = $false

    while (-not $proc.HasExited) {
        if (-not $opened -and (Test-Path $logPath)) {
            try {
                $fileStream = [System.IO.File]::Open($logPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                $fileStream.Seek($initialOffset, [System.IO.SeekOrigin]::Begin) | Out-Null
                $streamReader = New-Object System.IO.StreamReader($fileStream, [System.Text.Encoding]::UTF8)
                $opened = $true
            } catch {
                # Файл может быть кратковременно занят другим процессом
            }
        }

        if ($opened -and $streamReader) {
            while (-not $streamReader.EndOfStream) {
                $line = $streamReader.ReadLine()
                if ($line) {
                    $timestamp = (Get-Date).ToString("HH:mm:ss")
                    if ($line -match "\[Error\]|\[AppDomain Error\]|\[Dispatcher Error\]|Exception") {
                        Write-Host "[$timestamp] $line" -ForegroundColor Red
                    } elseif ($line -match "\[Loaded\]|\[ProcessExit\]|\[Closed\]") {
                        Write-Host "[$timestamp] $line" -ForegroundColor Green
                    } else {
                        Write-Host "[$timestamp] $line" -ForegroundColor Cyan
                    }
                }
            }
        }

        Start-Sleep -Milliseconds 150
    }

    # Дочитываем оставшиеся логи после завершения процесса
    Start-Sleep -Milliseconds 250
    if ($opened -and $streamReader) {
        while (-not $streamReader.EndOfStream) {
            $line = $streamReader.ReadLine()
            if ($line) {
                $timestamp = (Get-Date).ToString("HH:mm:ss")
                if ($line -match "\[Error\]|\[AppDomain Error\]|\[Dispatcher Error\]|Exception") {
                    Write-Host "[$timestamp] $line" -ForegroundColor Red
                } else {
                    Write-Host "[$timestamp] $line" -ForegroundColor Green
                }
            }
        }
    }

    $exitCode = $proc.ExitCode
    Write-Host "------------------------------------------------------------" -ForegroundColor DarkGray
    if ($exitCode -eq 0) {
        Write-Host "[LAUNCHER] Приложение завершило работу успешно (код: 0)." -ForegroundColor Green
    } else {
        Write-Host "[LAUNCHER] Приложение завершило работу с кодом ошибки: $exitCode" -ForegroundColor Red
    }
}
catch {
    Write-Host "[EXCEPTION] $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    if ($streamReader) { $streamReader.Dispose() }
    if ($fileStream) { $fileStream.Dispose() }

    Unregister-Event -SourceIdentifier $outEvent.Name -ErrorAction SilentlyContinue
    Unregister-Event -SourceIdentifier $errEvent.Name -ErrorAction SilentlyContinue

    if ($proc -and -not $proc.HasExited) {
        Write-Host "`n[LAUNCHER] Завершение процесса Nexora..." -ForegroundColor Yellow
        try { $proc.Kill() } catch { }
    }
    if ($proc) { $proc.Dispose() }
}

Write-Host ""
Write-Host "Нажмите любую клавишу для закрытия окна..." -ForegroundColor DarkGray
try {
    if ([Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
        $null = [Console]::ReadKey($true)
    }
} catch { }

exit $exitCode