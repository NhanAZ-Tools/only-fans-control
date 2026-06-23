$ErrorActionPreference = "Stop"

$localExe = Join-Path $PSScriptRoot "OnlyFansControl.exe"
$builtExe = Join-Path $PSScriptRoot "dist\OnlyFansControl\OnlyFansControl.exe"

if (Test-Path $localExe) {
    $exe = $localExe
} elseif (Test-Path $builtExe) {
    $exe = $builtExe
} else {
    throw "OnlyFansControl.exe was not found."
}

Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -Verb RunAs
