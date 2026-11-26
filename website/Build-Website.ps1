[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$siteRoot = $PSScriptRoot
$projectRoot = Split-Path $siteRoot -Parent
$distPath = Join-Path $siteRoot 'dist'
$installerDir = Join-Path $projectRoot 'artifacts/installer'
$appProject = Join-Path $projectRoot 'src/Vellum.App/Vellum.App.csproj'
$setupScript = Join-Path $projectRoot 'installer/Vellum.iss'

$projectXml = [xml](Get-Content -LiteralPath $appProject -Raw)
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'App version not found in Vellum.App.csproj.' }
$setupText = Get-Content -LiteralPath $setupScript -Raw
if ($setupText -notmatch '(?m)^\s*#define AppVersion "([^"]+)"') { throw 'Installer version not found in Vellum.iss.' }
if ($Matches[1] -ne $version) { throw "Version mismatch: app=$version, installer=$($Matches[1])." }
$installerName = "Vellum-Setup-$version-win-x64.exe"
$installerPath = Join-Path $installerDir $installerName
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw "Installer not found: $installerPath" }
$installer = Get-Item -LiteralPath $installerPath

$history = @(Get-ChildItem -LiteralPath $installerDir -File -Filter 'Vellum-Setup-*-win-x64.exe' | ForEach-Object {
    if ($_.BaseName -match '^Vellum-Setup-(\d+\.\d+\.\d+)-win-x64$') {
        [pscustomobject]@{ Version = $Matches[1]; File = $_ }
    }
} | Sort-Object { [version]$_.Version } -Descending | Select-Object -First 10 | ForEach-Object {
    [pscustomobject]@{
        version = $_.Version
        filename = $_.File.Name
        sizeBytes = $_.File.Length
        fileModifiedUtc = $_.File.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
})

$artifactSpecs = @(
    [pscustomobject]@{ Key = 'installer'; Name = $installerName; Path = $installerPath },
    [pscustomobject]@{ Key = 'msi'; Name = "Vellum-$version-win-x64.msi"; Path = (Join-Path $projectRoot "artifacts/msi/Vellum-$version-win-x64.msi") },
    [pscustomobject]@{ Key = 'portable'; Name = "Vellum-Portable-$version-win-x64.zip"; Path = (Join-Path $projectRoot "artifacts/portable/Vellum-Portable-$version-win-x64.zip") },
    [pscustomobject]@{ Key = 'migrator'; Name = 'Vellum-Migrator.exe'; Path = (Join-Path $projectRoot 'artifacts/migrator/Vellum-Migrator.exe') },
    [pscustomobject]@{ Key = 'checksums'; Name = 'SHA256SUMS.txt'; Path = (Join-Path $projectRoot 'artifacts/SHA256SUMS.txt') }
)
$downloads = [ordered]@{}
foreach ($spec in $artifactSpecs) {
    if (Test-Path -LiteralPath $spec.Path) {
        $artifact = Get-Item -LiteralPath $spec.Path
        $downloads[$spec.Key] = [pscustomobject]@{
            filename = $spec.Name
            sizeBytes = $artifact.Length
            url = "downloads/$($spec.Name)"
        }
    }
}

$current = [pscustomobject]@{
    version = $version
    installer = "downloads/$installerName"
    filename = $installerName
    sizeBytes = $installer.Length
    fileModifiedUtc = $installer.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ssZ')
    releaseDate = $null
    architecture = 'x64'
    os = 'Windows 10 build 18362 or later; Windows 11'
    downloads = $downloads
}
$manifest = [pscustomobject]@{
    generatedFrom = 'src/Vellum.App/Vellum.App.csproj and artifacts/installer'
    current = $current
    history = $history
    historyCount = @(Get-ChildItem -LiteralPath $installerDir -File -Filter 'Vellum-Setup-*-win-x64.exe').Count
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $siteRoot 'releases.json') -Encoding utf8

if (Test-Path -LiteralPath $distPath) {
    $resolvedDist = (Resolve-Path -LiteralPath $distPath).Path
    $resolvedSite = (Resolve-Path -LiteralPath $siteRoot).Path
    if (-not $resolvedDist.StartsWith($resolvedSite + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear output outside the website directory: $resolvedDist"
    }
    Remove-Item -LiteralPath $resolvedDist -Recurse -Force
}
$siteFiles = @(Get-ChildItem -LiteralPath $siteRoot -Recurse -File | Where-Object {
    $_.Name -notin @('Build-Website.ps1', 'Serve-Website.ps1', 'README.md')
})
New-Item -ItemType Directory -Path $distPath | Out-Null
foreach ($sourceFile in $siteFiles) {
    $relativePath = $sourceFile.FullName.Substring($siteRoot.Length).TrimStart([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    $destinationPath = Join-Path $distPath $relativePath
    New-Item -ItemType Directory -Force -Path (Split-Path $destinationPath -Parent) | Out-Null
    Copy-Item -LiteralPath $sourceFile.FullName -Destination $destinationPath
}
$downloadsPath = Join-Path $distPath 'downloads'
New-Item -ItemType Directory -Path $downloadsPath | Out-Null
foreach ($spec in $artifactSpecs) {
    if (Test-Path -LiteralPath $spec.Path) {
        Copy-Item -LiteralPath $spec.Path -Destination (Join-Path $downloadsPath $spec.Name) -Force
    }
}
$distManifestPath = Join-Path $distPath 'releases.json'
$distManifest = Get-Content -LiteralPath $distManifestPath -Raw | ConvertFrom-Json
$distManifest.current.installer = "downloads/$installerName"
$distManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $distManifestPath -Encoding utf8

$distManifest = Get-Content -LiteralPath $distManifestPath -Raw | ConvertFrom-Json
$builtInstaller = Get-Item -LiteralPath (Join-Path $downloadsPath $installerName)
if ($distManifest.current.version -ne $version -or $builtInstaller.Length -ne $installer.Length) {
    throw 'Production output verification failed.'
}
foreach ($entry in $downloads.Values) {
    $downloaded = Join-Path $downloadsPath $entry.filename
    if (-not (Test-Path -LiteralPath $downloaded)) {
        throw "Missing download in production output: $($entry.filename)"
    }
    if ((Get-Item -LiteralPath $downloaded).Length -ne $entry.sizeBytes) {
        throw "Size mismatch in production output: $($entry.filename)"
    }
}
Write-Host "Website built to $distPath"
Write-Host "Vellum $version installer: $([math]::Round($installer.Length / 1MB, 1)) MiB"
Write-Host "Installer archive entries found: $($manifest.historyCount)"
