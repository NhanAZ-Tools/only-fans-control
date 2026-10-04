param([switch]$ReadOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $taskRoot 'build\OnlyFansControl\OnlyFansControl.exe'
$reportPath = Join-Path $taskRoot ('artifacts\' + $(if ($ReadOnly) { 'ec-probe-admin.json' } else { 'hardware-test.json' }))
$checkName = if ($ReadOnly) { '--probe-ec' } else { '--hardware-test' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } finally { $identity.Dispose() }
$startParameters = @{FilePath=$exePath; ArgumentList=@($checkName, ('"'+$reportPath+'"')); WindowStyle='Hidden'; PassThru=$true}
if (-not $isAdmin) { $startParameters.Verb = 'RunAs' }
$process = Start-Process @startParameters
while (-not $process.WaitForExit(1000)) { }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $report.success) { throw "Hardware verification failed. Inspect $reportPath : $($report.error) $($report.ec_error)" }
if ($ReadOnly) { $report.ec | Format-List; Write-Output 'Real EC read succeeded; no fan-register writes.' }
else { Write-Output "Real fan tests passed: $($report.checks.Count) checks; final BIOS restore: $($report.bios_restored)." }
Write-Output $reportPath
