[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Vellum.App\Vellum.App.csproj'
$outputDir = Join-Path $root 'artifacts\portable'
$stageDir = Join-Path $root ('.portable-stage-' + $PID)
$appStage = Join-Path $stageDir 'Vellum'

$candidateDotnet = @(
    (Join-Path $root '.dotnet\dotnet.exe'),
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
)
$dotnet = $candidateDotnet | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dotnet) {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (-not $command) { throw 'The .NET 8 SDK was not found.' }
    $dotnet = $command.Source
}

$projectXml = Get-Content -LiteralPath $project -Raw
$versionMatch = [regex]::Match($projectXml, '<Version>\s*([^<]+?)\s*</Version>')
if (-not $versionMatch.Success) { throw "Could not read <Version> from $project." }
$version = $versionMatch.Groups[1].Value

if (Test-Path $stageDir) { throw "Temporary staging directory already exists: $stageDir" }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

try {
    $publishArgs = @(
        'publish', $project,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '--output', $appStage
    )
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw ".NET publish failed with exit code $LASTEXITCODE." }

    if (-not (Test-Path (Join-Path $appStage 'Vellum.exe'))) {
        throw "Published application executable was not found in $appStage."
    }

    $zipName = "Vellum-Portable-$version-win-x64.zip"
    $zipPath = Join-Path $outputDir $zipName
    if (Test-Path $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $stageDir,
        $zipPath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)

    Write-Host "Portable build created: $zipPath"
}
finally {
    if (Test-Path $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
}
