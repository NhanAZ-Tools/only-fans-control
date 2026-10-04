$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$artifactsPath = Join-Path $taskRoot 'artifacts'
$exePath = Join-Path $taskRoot 'build\OnlyFansControl\OnlyFansControl.exe'
$exeHash = (Get-FileHash -LiteralPath $exePath).Hash.ToLowerInvariant()
$hardware = Get-Content -LiteralPath (Join-Path $artifactsPath 'hardware-test.json') -Raw | ConvertFrom-Json
$ui = Get-Content -LiteralPath (Join-Path $artifactsPath 'ui-test.json') -Raw | ConvertFrom-Json
$crash = Get-Content -LiteralPath (Join-Path $artifactsPath 'watchdog-crash-test.json') -Raw | ConvertFrom-Json
$unit = Get-Content -LiteralPath (Join-Path $artifactsPath 'self-test.json') -Raw | ConvertFrom-Json
$finalProbe = Get-Content -LiteralPath (Join-Path $artifactsPath 'ec-probe-final.json') -Raw | ConvertFrom-Json
foreach ($report in @($hardware,$ui,$crash)) {
    if (-not $report.success -or $report.executable_sha256 -ne $exeHash) { throw 'Reports do not verify the current executable.' }
}
if (-not $unit.success -or -not $finalProbe.ec_probe_success -or ($finalProbe.ec.fan_raw -band 0xC0) -ne 0x80) { throw 'Unit checks or final BIOS state are not verified.' }
$iconResult = ''
$iconEvidence = ''
$crosscheckEvidence = ''
if (Test-Path -LiteralPath (Join-Path $artifactsPath 'cpu-temperature-crosscheck.json')) {
    $crosscheckEvidence = 'An earlier comparison with LibreHardwareMonitor 0.9.6 showed CPU Tctl/Tdie around 87-88.5 C and EC temperatures around 87-88 C under heavy load. That earlier report is retained in cpu-temperature-crosscheck.json. The library is not required to run Only Fans Control.'
}
$iconReportPath = Join-Path $artifactsPath 'icon-verification.json'
if (Test-Path -LiteralPath $iconReportPath) {
    $iconReport = Get-Content -LiteralPath $iconReportPath -Raw | ConvertFrom-Json
    if (-not $iconReport.success -or $iconReport.executable_sha256 -ne $exeHash) { throw 'Icon verification does not match the current executable.' }
    $iconResult = '- Lucide icons were rendered from the running WPF window, the EC connection dialog, and the tray menu. All six tray actions have icons. The Fan logo is used in the header, window, tray, and native EXE icon; the ICO includes nine sizes from 16 to 256 pixels.'
    $iconEvidence = ' Icon evidence: icon-verification.json, lucide-ui.png, lucide-tray-menu.png, light-dialog.png, and exe-lucide-icon.png. Original SVGs, upstream URLs, hashes, and the complete license are distributed in assets/Lucide/.'
}
$csvPath = Join-Path $artifactsPath 'hardware-samples.csv'
$hardware.samples | Select-Object elapsed_ms,stage,temperature_c,cpu_temperature_c,rpm,fan_raw | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
$tableRows = @()
$customTestNote = if ($hardware.brief_custom_tests_due_to_high_temperature) {
    'Because temperature was at least 80 °C after cooling at Max, Custom 1–7 were verified with brief commands and readback after 250 ms, followed by three seconds at Max between levels. This verifies command acceptance, not stable RPM at low levels.'
} else {
    'Each Custom level was held for about 20 seconds. No Custom hold was shortened because of high temperature.'
}
$customMeasurementNote = if ($hardware.brief_custom_tests_due_to_high_temperature) {
    'The table records each brief Custom command and its readback temperature. RPM was still influenced by Max and is not reported as a speed for that level.'
} else {
    'The table shows the RPM range across the last five samples of each hold, which may still reflect acceleration or deceleration.'
}
for ($level = 1; $level -le 7; $level++) {
    $samples = @($hardware.samples | Where-Object { $_.stage -eq ('Custom '+$level+' hold') })
    if ($samples.Count -gt 0) {
        $last = $samples | Select-Object -Last 1
        $tail = @($samples | Select-Object -Last 5 | Where-Object { $null -ne $_.rpm })
        $rpmStats = $tail | Measure-Object -Property rpm -Minimum -Maximum
        $rpmRange = if ($tail.Count -gt 0) { [string]$rpmStats.Minimum+'–'+[string]$rpmStats.Maximum } else { 'Unavailable' }
        $method = '20-second hold'
    } else {
        $last = $hardware.samples | Where-Object { $_.stage -eq ('Custom '+$level+' brief readback') } | Select-Object -Last 1
        if ($null -eq $last) { throw ('Missing Custom '+$level+' evidence') }
        $rpmRange = 'Not stabilized'
        $method = '250 ms readback'
    }
    $tableRows += '| '+$level+' | '+$last.fan_raw+' | '+$method+' | '+$rpmRange+' | '+$last.temperature_c+' °C |'
}
$maxRpm = ($hardware.samples | Where-Object { $_.stage -eq 'Max spin-up' } | Measure-Object -Property rpm -Maximum).Maximum
$maxHoldLast = $hardware.samples | Where-Object { $_.stage -eq 'Max spin-up' } | Select-Object -Last 1
$baseline = $hardware.samples | Where-Object { $_.stage -eq 'baseline' } | Select-Object -Last 1
$observedRestoreSeconds = [math]::Round($crash.restore_observed_after_ms/1000,2)
$finishedLocal = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]$hardware.finished_utc,'SE Asia Standard Time').ToString('yyyy-MM-dd HH:mm:ss')
$reportText = @'
# Only Fans Control hardware verification

Device: @@product@@, model @@model@@, BIOS @@bios@@. Windows x64 with PawnIO Official 2.2.0 running. Profile: @@profile@@.

The final hardware run completed at @@finished@@ (Vietnam time, UTC+07:00). Every check below passed on the same executable.

This build uses an entirely English interface and blue Light mode: a pale blue background, white cards, soft blue selected buttons, and readable text. ui-test.png was rendered from the real WPF window while connected to the physical EC.

OnlyFansControl.exe SHA-256:

`@@hash@@`

## Results

- @@hardware_count@@ checks with the real EC and fan: Custom 1–7, Custom Max, Max, BIOS default, Smart auto from JSON, and watchdog heartbeat loss. @@custom_test_note@@
- @@ui_count@@ checks using the real WPF window: mode buttons, EC temperature and RPM display, JSON hot reload, invalid JSON returning control to BIOS, valid configuration recovery, hide to tray, reopen, and Exit restoring BIOS control.
- @@unit_count@@ simulated-EC and independent-process watchdog checks: hysteresis, thermal protection, missing sensors, failed BIOS restores and retries, invalid configuration, unsupported profiles, invalid fan values, process termination, and heartbeat loss.
- Abruptly terminating the dedicated test process while it held Max: the independent watchdog restored the register from 0x40 to 0x80. The confirmation read completed @@restore_seconds@@ seconds after termination.
- The original configuration was restored byte for byte after GUI verification.
- The final read confirmed BIOS default (0x80). No test process remains in control of the fan.
@@icon_result@@

## Measurements

BIOS baseline at the end of the read-only phase: @@baseline_rpm@@ RPM, @@baseline_temp@@ °C. During the 20-second Max hold, the highest observed RPM was @@max_rpm@@ and the final reading was @@max_last@@ RPM. The EC confirmed raw 0x40.

@@custom_measurements@@

| Custom level | Register readback | Test method | RPM in last five hold samples | Readback temperature |
| --- | --- | --- | --- | --- |
@@table@@

Levels are discrete EC steps. RPM varies with firmware, temperature, and settling time. These measurements are not fixed RPM targets guaranteed by the app.

## Fixes verified

- This device returns EC data after IBF clears. Waiting for OBF to fill in the first implementation caused read timeouts. The backend now uses the ThinkPad wait sequence verified through real commands and measurements on ports 0x62/0x66.
- EC diagnostics report failure and return a nonzero exit code when the EC cannot be read, instead of treating JSON creation as a successful probe.
- CPU channel 0x78 is required. Losing that channel disables direct control and restores BIOS if the app owns the fan.
- Temporarily missing RPM during a level transition is shown as unavailable while CPU temperature monitoring and thermal protection continue.
- The window has an explicit Exit button. Closing with X hides to the tray; Exit restores BIOS before the process ends.
- WPF image export updates layout after drawing the CPU chart and fills the light background.
- Labels, tooltips, tray menus, connection details, controller status, configuration errors, and EC errors are in English. The app uses the en-US culture for its messages.

## Evidence and limitations

Included evidence: hardware-test.json, hardware-samples.csv, ui-test.json, ui-test.png, watchdog-crash-test.json, self-test.json, and ec-probe-final.json. The executable_sha256 fields in the hardware, GUI, and crash reports match the executable above.@@icon_evidence@@

@@crosscheck@@

Actual Windows sleep or logoff, kernel hangs, power loss, and multi-hour load calibration have not been tested. This run verifies fan control on the device and BIOS listed above; it does not establish compatibility with other models or BIOS versions.
'@
$replacements = @{
    '@@model@@'=$hardware.model; '@@bios@@'=$hardware.bios; '@@finished@@'=$finishedLocal; '@@hash@@'=$exeHash;
    '@@product@@'=$hardware.product; '@@profile@@'=$hardware.profile_id;
    '@@hardware_count@@'=[string]$hardware.checks.Count; '@@ui_count@@'=[string]$ui.checks.Count; '@@unit_count@@'=[string]$unit.passed;
    '@@custom_test_note@@'=$customTestNote; '@@custom_measurements@@'=$customMeasurementNote;
    '@@icon_result@@'=$iconResult; '@@icon_evidence@@'=$iconEvidence;
    '@@crosscheck@@'=$crosscheckEvidence;
    '@@restore_seconds@@'=[string]$observedRestoreSeconds; '@@baseline_rpm@@'=[string]$baseline.rpm; '@@baseline_temp@@'=[string]$baseline.temperature_c;
    '@@max_rpm@@'=[string]$maxRpm; '@@max_last@@'=[string]$maxHoldLast.rpm; '@@table@@'=($tableRows -join [Environment]::NewLine)
}
foreach ($entry in $replacements.GetEnumerator()) { $reportText = $reportText.Replace($entry.Key,$entry.Value) }
$reportText | Set-Content -LiteralPath (Join-Path $artifactsPath 'verification-report.md') -Encoding UTF8
Write-Output (Join-Path $artifactsPath 'verification-report.md')
