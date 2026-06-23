param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectRoot

if (-not $Version) {
    $Version = (Get-Content -Raw (Join-Path $ProjectRoot "VERSION")).Trim()
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Installer version must use numeric SemVer, for example 1.0.1. Received: $Version"
}

if (-not (Test-Path ".\dist\OnlyFansControl\OnlyFansControl.exe")) {
    & ".\build.ps1"
}

$IsccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
)
$Iscc = $IsccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $Iscc) {
    throw "Inno Setup 6 is required. Install it with: winget install --id JRSoftware.InnoSetup --exact"
}

New-Item -ItemType Directory -Force -Path ".\release" | Out-Null
& $Iscc "/DAppVersion=$Version" ".\installer\OnlyFansControl.iss"
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

$Installer = Join-Path $ProjectRoot "release\OnlyFansControl-v$Version-windows-setup.exe"
if (-not (Test-Path $Installer)) {
    throw "Installer output was not created: $Installer"
}
Write-Host "Built installer: $Installer"
