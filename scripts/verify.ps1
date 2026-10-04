$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $taskRoot 'build\OnlyFansControl\OnlyFansControl.exe'
$artifactsPath = Join-Path $taskRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifactsPath | Out-Null
foreach ($checkName in @('self-test','diagnose','preview')) {
    $extension = if ($checkName -eq 'preview') { '.png' } else { '.json' }
    $checkOutput = Join-Path $artifactsPath ($checkName + $extension)
    $process = Start-Process -FilePath $exePath -ArgumentList @('--' + $checkName, ('"' + $checkOutput + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(20000)) { $process.Kill(); throw "$checkName timed out." }
    if ($process.ExitCode -ne 0) { throw "$checkName failed; inspect $checkOutput" }
    if ($checkName -ne 'preview') {
        $report = Get-Content -LiteralPath $checkOutput -Raw | ConvertFrom-Json
        if (-not $report.success) { throw "$checkName returned failure: $($report.error)" }
        if ($checkName -eq 'self-test') { Write-Output "$($report.passed) simulated-backend and independent-watchdog checks passed; real fan-register writes: $($report.hardware_register_writes)." }
    }
}
Write-Output "Diagnostics and WPF render saved under $artifactsPath"
Write-Output 'Run scripts\verify-hardware.ps1 for an elevated read-only EC probe and real fan tests.'
