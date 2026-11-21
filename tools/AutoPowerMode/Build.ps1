param(
    [switch]$SelfContained,
    [string]$Output = (Join-Path $PSScriptRoot 'publish')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'AutoPowerMode.csproj'

if (Test-Path $Output) {
    Remove-Item $Output -Recurse -Force
}

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
    '-o', $Output
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish завершился с кодом $LASTEXITCODE"
}

# Иконки должны лежать рядом с exe: publish иногда не копирует их сам.
$iconsSource = Join-Path $PSScriptRoot 'icons'
if (Test-Path $iconsSource) {
    Copy-Item $iconsSource (Join-Path $Output 'icons') -Recurse -Force
}

Write-Host ''
Write-Host "Готово: $Output" -ForegroundColor Green
Get-ChildItem $Output | Select-Object Name, Length | Format-Table -AutoSize
