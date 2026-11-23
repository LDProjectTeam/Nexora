[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'VellumMigrator.csproj'
$outputDir = Join-Path $root 'artifacts\migrator'
$stageDir = Join-Path $root ('.migrator-stage-' + $PID)

$candidateDotnet = @(
    (Join-Path $root '.dotnet\dotnet.exe'),
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
)
$dotnet = $candidateDotnet | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $dotnet) {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (-not $command) {
        throw 'The .NET 8 SDK was not found.'
    }
    $dotnet = $command.Source
}

if (Test-Path $stageDir) {
    throw "Temporary staging directory already exists: $stageDir"
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

try {
    $publishArgs = @(
        'publish', $project,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '--output', $stageDir
    )

    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw ".NET publish failed with exit code $LASTEXITCODE."
    }

    $appExe = Join-Path $stageDir 'Vellum-Migrator.exe'
    if (-not (Test-Path $appExe)) {
        throw "Published migrator was not found: $appExe"
    }

    Copy-Item -LiteralPath $appExe -Destination (Join-Path $outputDir 'Vellum-Migrator.exe') -Force
}
finally {
    if (Test-Path $stageDir) {
        Remove-Item -LiteralPath $stageDir -Recurse -Force
    }
}

Write-Host "Migrator created: $outputDir\Vellum-Migrator.exe"
