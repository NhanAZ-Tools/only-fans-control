param([switch]$SkipPackage)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'scripts\build.ps1') -SkipPackage:$SkipPackage
