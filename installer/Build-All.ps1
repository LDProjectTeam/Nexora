[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'Build-Installer.ps1')
& (Join-Path $PSScriptRoot 'Build-Portable.ps1')
& (Join-Path $PSScriptRoot 'Build-Msi.ps1')
& (Join-Path (Split-Path $PSScriptRoot -Parent) 'migrator\Build-Migrator.ps1')

Write-Host 'Release artifacts are ready in artifacts\installer, artifacts\portable, artifacts\msi and artifacts\migrator.'
