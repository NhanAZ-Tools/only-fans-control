$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$libraryPath = Join-Path $taskRoot 'vendor\LibreHardwareMonitor-0.9.6\LibreHardwareMonitorLib.dll'
if (-not (Test-Path -LiteralPath $libraryPath)) { throw 'Optional cross-check needs LibreHardwareMonitor 0.9.6 extracted under vendor\LibreHardwareMonitor-0.9.6. It is not needed to run or build Only Fans Control.' }
[Reflection.Assembly]::LoadFrom($libraryPath) | Out-Null
$computer = New-Object LibreHardwareMonitor.Hardware.Computer
$computer.IsCpuEnabled = $true
$readings = @()
try {
    $computer.Open()
    for ($sampleIndex = 0; $sampleIndex -lt 10; $sampleIndex++) {
        foreach ($hardware in $computer.Hardware) {
            $hardware.Update()
            foreach ($sensor in $hardware.Sensors) {
                if ($sensor.SensorType.ToString() -eq 'Temperature') {
                    $readings += [pscustomobject]@{utc=[DateTime]::UtcNow.ToString('o'); hardware=$hardware.Name; sensor=$sensor.Name; value=$sensor.Value}
                }
            }
        }
        Start-Sleep -Seconds 1
    }
    [pscustomobject]@{success=$true; readings=$readings; report=$computer.GetReport()} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $taskRoot 'artifacts\cpu-temperature-crosscheck.json') -Encoding UTF8
} catch {
    [pscustomobject]@{success=$false; error=$_.ToString()} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRoot 'artifacts\cpu-temperature-crosscheck.json') -Encoding UTF8
} finally { $computer.Close() }
