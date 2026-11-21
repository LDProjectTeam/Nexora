#Requires -Version 5.1
<#
.SYNOPSIS
    Reports (and optionally removes) build artifacts that make the Nexora repository huge.

.DESCRIPTION
    The repository accumulates one publish folder and one installer per fix iteration.
    This script keeps only the newest builds and removes regenerable bin/obj folders.

    By default it only prints what it would delete. Pass -Apply to actually delete.

.PARAMETER Apply
    Delete the listed files and folders. Without it the script is read-only.

.PARAMETER KeepInstallers
    How many newest artifacts\installer\Nexora-Setup-*.exe files to keep. Default 3.

.PARAMETER KeepPortableBuilds
    How many newest publish-portable* folders to keep. Default 2.

.PARAMETER KeepBackups
    How many newest backup-installed-* folders to keep. Default 1.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Clean-DevArtifacts.ps1
    powershell -ExecutionPolicy Bypass -File tools\Clean-DevArtifacts.ps1 -Apply
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [int]$KeepInstallers = 3,
    [int]$KeepPortableBuilds = 2,
    [int]$KeepBackups = 1
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root 'Nexora.sln'))) {
    throw "Nexora.sln not found next to tools\; run this script from the repository."
}

function Get-PathSize {
    param([string]$Path)
    try {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            return (Get-Item -LiteralPath $Path).Length
        }
        $sum = 0
        foreach ($file in Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue) {
            $sum += $file.Length
        }
        return $sum
    }
    catch {
        return 0
    }
}

function Add-Candidate {
    param(
        [System.Collections.Generic.List[object]]$List,
        [string]$Path,
        [string]$Reason
    )
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $List.Add([pscustomobject]@{
        Path   = $Path
        Reason = $Reason
        Bytes  = Get-PathSize $Path
    })
}

$candidates = [System.Collections.Generic.List[object]]::new()

# 1. Installer builds: one executable per version, tens of megabytes each.
$installerDir = Join-Path $root 'artifacts\installer'
if (Test-Path $installerDir) {
    Get-ChildItem $installerDir -Filter 'Nexora-Setup-*.exe' -File |
        Sort-Object LastWriteTime -Descending |
        Select-Object -Skip $KeepInstallers |
        ForEach-Object { Add-Candidate $candidates $_.FullName 'old installer build' }
}

# 2. Per-fix portable publish folders (~200 MB each).
Get-ChildItem $root -Directory -Filter 'publish-portable*' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -Skip $KeepPortableBuilds |
    ForEach-Object { Add-Candidate $candidates $_.FullName 'old publish-portable build' }

# 3. Manual backups of an installed profile.
Get-ChildItem $root -Directory -Filter 'backup-installed-*' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -Skip $KeepBackups |
    ForEach-Object { Add-Candidate $candidates $_.FullName 'old installed-profile backup' }

# 4. Artwork debugging dumps.
Get-ChildItem $root -Directory -Filter 'artwork-debug*' |
    ForEach-Object { Add-Candidate $candidates $_.FullName 'artwork debug dump' }

# 5. Regenerable .NET build output.
foreach ($sourceRoot in @((Join-Path $root 'src'), (Join-Path $root 'tests'))) {
    if (-not (Test-Path $sourceRoot)) { continue }
    Get-ChildItem $sourceRoot -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -in @('bin', 'obj') } |
        ForEach-Object { Add-Candidate $candidates $_.FullName '.NET build output' }
}

# 6. Standalone log/console dumps in the repository root.
foreach ($fileName in @('stdout.txt', 'stderr.txt', 'lifecycle.log')) {
    $path = Join-Path $root $fileName
    if (Test-Path $path) { Add-Candidate $candidates $path 'stray console/log dump' }
}

if ($candidates.Count -eq 0) {
    Write-Host 'Nothing to clean. The repository is already tidy.' -ForegroundColor Green
    exit 0
}

# Deduplicate (bin/obj folders may be nested inside a removed publish folder).
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$unique = foreach ($item in $candidates | Sort-Object Bytes -Descending) {
    $insideRemoved = $false
    foreach ($parent in $seen) {
        if ($item.Path.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            $insideRemoved = $true
            break
        }
    }
    if ($insideRemoved) { continue }
    [void]$seen.Add($item.Path)
    $item
}

$totalBytes = ($unique | Measure-Object Bytes -Sum).Sum
$unique |
    Sort-Object Bytes -Descending |
    Select-Object @{n='Size';e={ '{0,8:N1} MB' -f ($_.Bytes / 1MB) }}, Reason, Path |
    Format-Table -AutoSize

Write-Host ''
Write-Host ("Reclaimable: {0:N2} GB in {1} item(s)" -f ($totalBytes / 1GB), $unique.Count) -ForegroundColor Cyan

if (-not $Apply) {
    Write-Host 'Dry run only. Re-run with -Apply to delete these items.' -ForegroundColor Yellow
    exit 0
}

if (Get-Process -Name 'Nexora', 'Nexora.TorrentEngine' -ErrorAction SilentlyContinue) {
    throw 'Close Nexora and its torrent engine before cleaning publish folders.'
}

$freed = 0
$failed = 0
foreach ($item in $unique) {
    try {
        Remove-Item -LiteralPath $item.Path -Recurse -Force -ErrorAction Stop
        $freed += $item.Bytes
    }
    catch {
        $failed++
        Write-Warning ("Could not delete {0}: {1}" -f $item.Path, $_.Exception.Message)
    }
}

Write-Host ''
Write-Host ("Freed {0:N2} GB. Failed: {1}." -f ($freed / 1GB), $failed) -ForegroundColor Green
