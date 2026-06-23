$ErrorActionPreference = "Stop"
$env:PYTHONIOENCODING = "utf-8"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectRoot

$IconSvg = "C:\Users\NhanAZ\Downloads\fan.svg"
if (Test-Path $IconSvg) {
    python .\tools\make_icon.py --source $IconSvg
} elseif (Test-Path ".\assets\fan.svg") {
    python .\tools\make_icon.py --source ".\assets\fan.svg"
}

$env:GOOS = "windows"
$env:GOARCH = "386"
$env:CGO_ENABLED = "0"
go build -o .\helper\tvic-ec-helper.exe .\helper\tvic_ec_helper.go
Remove-Item Env:\GOOS, Env:\GOARCH, Env:\CGO_ENABLED -ErrorAction SilentlyContinue

$pyInstallerArgs = @(
    "-m", "PyInstaller",
    "--noconfirm",
    "--clean",
    "--onedir",
    "--windowed",
    "--name", "OnlyFansControl",
    "--hidden-import", "pystray._win32",
    "--add-data", "only_fans_config.json;."
)

if (Test-Path ".\assets\fan.png") {
    $pyInstallerArgs += @("--add-data", "assets\fan.png;assets")
}

if (Test-Path ".\assets\fan.ico") {
    $pyInstallerArgs += @("--icon", ".\assets\fan.ico")
}

$pyInstallerArgs += ".\src\only_fans.py"
python @pyInstallerArgs

$DriverDir = Join-Path $ProjectRoot "dist\OnlyFansControl\drivers"
New-Item -ItemType Directory -Force -Path $DriverDir | Out-Null
Copy-Item -LiteralPath (Join-Path $ProjectRoot "only_fans_config.json") `
    -Destination (Join-Path $ProjectRoot "dist\OnlyFansControl\only_fans_config.json") `
    -Force

$DistAssets = Join-Path $ProjectRoot "dist\OnlyFansControl\assets"
New-Item -ItemType Directory -Force -Path $DistAssets | Out-Null
if (Test-Path ".\assets\fan.png") {
    Copy-Item -LiteralPath ".\assets\fan.png" -Destination $DistAssets -Force
}
if (Test-Path ".\assets\fan.ico") {
    Copy-Item -LiteralPath ".\assets\fan.ico" -Destination $DistAssets -Force
}
if (Test-Path ".\assets\fan.svg") {
    Copy-Item -LiteralPath ".\assets\fan.svg" -Destination $DistAssets -Force
}
$DistHelper = Join-Path $ProjectRoot "dist\OnlyFansControl\helper"
New-Item -ItemType Directory -Force -Path $DistHelper | Out-Null
Copy-Item -LiteralPath ".\helper\tvic-ec-helper.exe" -Destination $DistHelper -Force
Copy-Item -LiteralPath ".\run-exe-admin.ps1" `
    -Destination (Join-Path $ProjectRoot "dist\OnlyFansControl\run-exe-admin.ps1") `
    -Force
Copy-Item -LiteralPath ".\README.md" `
    -Destination (Join-Path $ProjectRoot "dist\OnlyFansControl\README.md") `
    -Force

Write-Host "Built: $(Join-Path $ProjectRoot 'dist\OnlyFansControl\OnlyFansControl.exe')"
