param([switch]$RequireVerified)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $taskRoot 'build\OnlyFansControl'
$executablePath = Join-Path $outputPath 'OnlyFansControl.exe'
if (-not (Test-Path -LiteralPath $executablePath)) { throw 'Build the application first.' }
$executableHash = (Get-FileHash -LiteralPath $executablePath).Hash.ToLowerInvariant()
$verified = $true
foreach ($name in @('hardware-test.json','ui-test.json','watchdog-crash-test.json')) {
    $testPath = Join-Path $taskRoot ('artifacts\'+$name)
    if (-not (Test-Path -LiteralPath $testPath)) { $verified = $false; continue }
    $testReport = Get-Content -LiteralPath $testPath -Raw | ConvertFrom-Json
    if (-not $testReport.success -or $testReport.executable_sha256 -ne $executableHash) { $verified = $false }
}
if ($RequireVerified -and -not $verified) { throw 'The current executable has not passed all hardware, GUI and crash checks.' }
foreach ($name in @('README.md','THIRD-PARTY-NOTICES.md','LICENSE','VERSION','CONTRIBUTING.md','CHANGELOG.md')) {
    Copy-Item -LiteralPath (Join-Path $taskRoot $name) -Destination $outputPath -Force
}
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $outputPath -Recurse -Force
$packagePaths = @('OnlyFansControl.exe','OnlyFansControl.exe.config','only_fans_config.json','README.md','THIRD-PARTY-NOTICES.md','LICENSE','VERSION','CONTRIBUTING.md','CHANGELOG.md','docs','Start-OnlyFansControl.cmd','module-source','driver','assets') | ForEach-Object { Join-Path $outputPath $_ }
if ($verified) {
    $reportsPath = Join-Path $outputPath 'verification'
    New-Item -ItemType Directory -Force -Path $reportsPath | Out-Null
    foreach ($name in @('verification-report.md','hardware-test.json','hardware-samples.csv','ui-test.json','ui-test.png','self-test.json','ec-probe-final.json','watchdog-crash-test.json','cpu-temperature-crosscheck.json','icon-verification.json','lucide-ui.png','lucide-tray-menu.png','exe-lucide-icon.png','light-dialog.png')) {
        $reportSource = Join-Path $taskRoot ('artifacts\'+$name)
        if (Test-Path -LiteralPath $reportSource) { Copy-Item -LiteralPath $reportSource -Destination $reportsPath -Force }
    }
    $packagePaths += $reportsPath
}
$zipPath = Join-Path $taskRoot 'OnlyFansControl-Windows-x64.zip'
Compress-Archive -LiteralPath $packagePaths -DestinationPath $zipPath -Force
$sourceZip = Join-Path $taskRoot 'OnlyFansControl-Source.zip'
$sourcePaths = @('src','scripts','docs','installer','.github','build.ps1','build-installer.ps1','run.ps1','run-exe-admin.ps1','only_fans_config.json','OnlyFansControl.exe.config','Start-OnlyFansControl.cmd','README.md','THIRD-PARTY-NOTICES.md','LICENSE','VERSION','CONTRIBUTING.md','CHANGELOG.md','.gitignore','.gitattributes') | ForEach-Object { Join-Path $taskRoot $_ }
Compress-Archive -LiteralPath $sourcePaths -DestinationPath $sourceZip -Force
$checksumLines = @($zipPath,$sourceZip) | ForEach-Object { $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256; $hash.Hash.ToLowerInvariant()+'  '+(Split-Path $_ -Leaf) }
$checksumLines | Set-Content -LiteralPath (Join-Path $taskRoot 'SHA256SUMS.txt') -Encoding ASCII
Get-Item -LiteralPath $zipPath,$sourceZip | Select-Object FullName,Length
Get-FileHash -LiteralPath $executablePath -Algorithm SHA256 | Select-Object Path,Hash
