[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\Nexora.App\Nexora.App.csproj'
$scriptPath = Join-Path $PSScriptRoot 'Nexora.iss'
$installerOutput = Join-Path $repoRoot 'artifacts\installer'
# Stage on the repository drive: the system temp drive (usually C:) can be full
# while D: still has plenty of room for the self-contained publish output.
$stageDir = Join-Path $repoRoot ('.installer-stage-' + $PID)

$candidateDotnet = @(
    (Join-Path $repoRoot '.dotnet\dotnet.exe'),
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
)
$dotnet = $candidateDotnet | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dotnet) {
    $dotnetCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (-not $dotnetCommand) {
        throw 'The .NET 8 SDK was not found. Install it or place dotnet.exe in the repository .dotnet folder.'
    }
    $dotnet = $dotnetCommand.Source
}

# The installer version always follows the application version, so the setup file
# name cannot drift away from the build it contains.
$projectXml = Get-Content -LiteralPath $project -Raw
$versionMatch = [regex]::Match($projectXml, '<Version>\s*([^<]+?)\s*</Version>')
if (-not $versionMatch.Success) {
    throw "Could not read <Version> from $project."
}
$appVersion = $versionMatch.Groups[1].Value

$iscc = $env:INNO_SETUP_COMPILER
if (-not $iscc -or -not (Test-Path $iscc)) {
    $isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($isccCommand) {
        $iscc = $isccCommand.Source
    }
}
if (-not $iscc -or -not (Test-Path $iscc)) {
    $possiblePaths = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno\ISCC.exe')
    )
    $iscc = $possiblePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw 'Inno Setup 6 was not found. Install it or set INNO_SETUP_COMPILER to the full path of ISCC.exe.'
}
if (Test-Path $stageDir) {
    throw "Temporary staging directory already exists: $stageDir"
}

New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null

try {
    Push-Location $repoRoot
    try {
        $publishArgs = @(
            'publish', $project,
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'true',
            '-p:DebugType=None',
            '-p:DebugSymbols=false',
            '--output', $stageDir
        )
        & $dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) {
            throw ".NET publish failed with exit code $LASTEXITCODE."
        }

        $appExe = Join-Path $stageDir 'Nexora.exe'
        if (-not (Test-Path $appExe)) {
            throw "Published application executable was not found: $appExe"
        }

        & $iscc "/DPublishDir=$stageDir" "/DInstallerDir=$installerOutput" "/DAppVersion=$appVersion" $scriptPath
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    if (Test-Path $stageDir) {
        Remove-Item -LiteralPath $stageDir -Recurse -Force
    }
}

Write-Host "Installer created in $installerOutput"
