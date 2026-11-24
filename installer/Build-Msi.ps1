[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Vellum.App\Vellum.App.csproj'
$wxs = Join-Path $PSScriptRoot 'Vellum.wxs'
$outputDir = Join-Path $root 'artifacts\msi'
$stageDir = Join-Path $root ('.msi-stage-' + $PID)
$appStage = Join-Path $stageDir 'app'
$objDir = Join-Path $stageDir 'obj'
$harvestFile = Join-Path $stageDir 'harvest.wxs'

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

$wixBin = $env:WIX_BIN
if (-not $wixBin -or -not (Test-Path (Join-Path $wixBin 'candle.exe'))) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'WiX Toolset v3.14\bin'),
        (Join-Path $env:ProgramFiles 'WiX Toolset v3.14\bin'),
        (Join-Path ${env:ProgramFiles(x86)} 'WiX Toolset v3.11\bin'),
        (Join-Path $env:ProgramFiles 'WiX Toolset v3.11\bin')
    )
    $wixBin = $candidates | Where-Object { Test-Path (Join-Path $_ 'candle.exe') } | Select-Object -First 1
}
if (-not $wixBin) {
    throw 'WiX Toolset v3 was not found. Install it or set WIX_BIN to the folder that contains candle.exe and light.exe.'
}

$heat = Join-Path $wixBin 'heat.exe'
$candle = Join-Path $wixBin 'candle.exe'
$light = Join-Path $wixBin 'light.exe'
foreach ($tool in @($heat, $candle, $light)) {
    if (-not (Test-Path $tool)) { throw "WiX tool not found: $tool" }
}

$projectXml = Get-Content -LiteralPath $project -Raw
$versionMatch = [regex]::Match($projectXml, '<Version>\s*([^<]+?)\s*</Version>')
if (-not $versionMatch.Success) { throw "Could not read <Version> from $project." }
$version = $versionMatch.Groups[1].Value

if (Test-Path $stageDir) { throw "Temporary staging directory already exists: $stageDir" }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
New-Item -ItemType Directory -Path $objDir -Force | Out-Null

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

    & $heat dir $appStage -cg AppFiles -dr INSTALLFOLDER -gg -g1 -srd -sfrag -var var.PublishDir -out $harvestFile
    if ($LASTEXITCODE -ne 0) { throw "heat.exe failed with exit code $LASTEXITCODE." }

    $candleArgs = @(
        '-arch', 'x64',
        "-dPublishDir=$appStage",
        "-dAppVersion=$version",
        "-dSourceRoot=$root",
        '-out', ($objDir + '\'),
        $wxs,
        $harvestFile
    )
    & $candle @candleArgs
    if ($LASTEXITCODE -ne 0) { throw "candle.exe failed with exit code $LASTEXITCODE." }

    $msiPath = Join-Path $outputDir "Vellum-$version-win-x64.msi"
    if (Test-Path $msiPath) { Remove-Item -LiteralPath $msiPath -Force }

    & $light -sval -out $msiPath (Join-Path $objDir 'Vellum.wixobj') (Join-Path $objDir 'harvest.wixobj')
    if ($LASTEXITCODE -ne 0) { throw "light.exe failed with exit code $LASTEXITCODE." }

    Write-Host "MSI created: $msiPath"
}
finally {
    if (Test-Path $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
}
