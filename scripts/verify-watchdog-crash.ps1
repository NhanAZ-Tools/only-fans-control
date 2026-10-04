$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $taskRoot 'build\OnlyFansControl\OnlyFansControl.exe'
$artifactsPath = Join-Path $taskRoot 'artifacts'
$reportPath = Join-Path $artifactsPath 'watchdog-crash-test.json'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } finally { $identity.Dispose() }
if (-not $isAdmin) {
    $runner = Start-Process 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+$PSCommandPath+'"')) -Verb RunAs -WindowStyle Hidden -PassThru
    while (-not $runner.WaitForExit(1000)) { }
    if ($runner.ExitCode -ne 0) { throw "Crash verification failed: $reportPath" }
    Get-Content -LiteralPath $reportPath
    return
}
$report = [ordered]@{success=$false; real_hardware=$true; parent_terminated=$false; bios_restored=$false; executable_sha256=(Get-FileHash -LiteralPath $exePath).Hash.ToLowerInvariant(); started_utc=[DateTime]::UtcNow.ToString('o'); error=$null}
$child = $null
function Read-EcProbe([string]$name) {
    $probePath = Join-Path $artifactsPath $name
    $probe = Start-Process $exePath -ArgumentList @('--probe-ec',('"'+$probePath+'"')) -WindowStyle Hidden -PassThru
    if (-not $probe.WaitForExit(15000)) { throw 'Read-only probe did not finish.' }
    $reading = Get-Content -LiteralPath $probePath -Raw | ConvertFrom-Json
    if ($probe.ExitCode -ne 0 -or -not $reading.ec_probe_success) { throw $reading.ec_error }
    return $reading.ec
}
try {
    $readyPath = Join-Path $artifactsPath 'watchdog-crash-ready.json'
    $child = Start-Process $exePath -ArgumentList @('--prepare-crash-test',('"'+$readyPath+'"')) -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($child.HasExited) { throw 'Prepared test process exited before becoming ready.' }
        if (Test-Path -LiteralPath $readyPath) {
            try { $prepared = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json; $ready = $prepared.ready -and $prepared.parent_pid -eq $child.Id } catch { }
        }
        if ($ready) { break }; Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw 'Prepared test process did not become ready.' }
    $before = Read-EcProbe 'watchdog-crash-before.json'
    $report.before = $before
    if (($before.fan_raw -band 0xC0) -ne 0x40) { throw 'Expected real fan register in Max before forced exit.' }
    $report.test_parent_pid = $child.Id
    $stopTimer = [Diagnostics.Stopwatch]::StartNew()
    # Only terminate the process that this verification script just created.
    Stop-Process -Id $child.Id -Force
    $report.parent_terminated = $true
    Start-Sleep -Milliseconds 1500
    $after = Read-EcProbe 'watchdog-crash-after.json'
    $report.after = $after
    $report.restore_observed_after_ms = $stopTimer.ElapsedMilliseconds
    $report.bios_restored = ($after.fan_raw -band 0xC0) -eq 0x80
    if (-not $report.bios_restored) { throw 'Independent watchdog did not restore the real fan register to BIOS after parent termination.' }
    $report.success = $true
} catch { $report.error = $_.ToString() }
finally {
    if ($child -and -not $child.HasExited) { $child.WaitForExit(31000) | Out-Null }
    $report.finished_utc = [DateTime]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
if (-not $report.success) { throw $report.error }
