$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$vendorPath = Join-Path $taskRoot 'vendor'
New-Item -ItemType Directory -Force -Path $vendorPath | Out-Null
$archivePath = Join-Path $vendorPath 'PawnIO.Modules-0.2.11.zip'
$moduleDirectory = Join-Path $vendorPath 'PawnIO.Modules-0.2.11'
$moduleHash = '43608CB89BC84247FEF1368A139013F7D043E17DB6D6C8DFC9B46BF0905A81F4'
if (-not (Test-Path -LiteralPath $archivePath)) {
    Invoke-WebRequest 'https://github.com/namazso/PawnIO.Modules/releases/download/0.2.11/release_0_2_11.zip' -OutFile $archivePath
}
if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $moduleHash) { throw 'PawnIO module archive checksum mismatch.' }
Expand-Archive -LiteralPath $archivePath -DestinationPath $moduleDirectory -Force
$sourcePath = Join-Path $moduleDirectory 'source'
New-Item -ItemType Directory -Force -Path $sourcePath | Out-Null
foreach ($sourceName in @('COPYING','LpcACPIEC.p','include/pawnio.inc')) {
    Invoke-WebRequest ('https://raw.githubusercontent.com/namazso/PawnIO.Modules/0.2.11/' + $sourceName) -OutFile (Join-Path $sourcePath (Split-Path $sourceName -Leaf))
}
Invoke-WebRequest 'https://codeload.github.com/namazso/PawnIO.Modules/zip/refs/tags/0.2.11' -OutFile (Join-Path $sourcePath 'PawnIO.Modules-0.2.11-source.zip')
$setupPath = Join-Path $vendorPath 'PawnIO_setup.exe'
if (-not (Test-Path -LiteralPath $setupPath)) {
    Invoke-WebRequest 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe' -OutFile $setupPath
}
if ((Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash -ne '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032') { throw 'PawnIO installer checksum mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $setupPath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=namazso.eu') { throw 'PawnIO installer signature is not valid.' }
$driverCommit = '5cdf470831fdfff3f7f1d06363ca6b230f3bf35a'
Invoke-WebRequest ('https://codeload.github.com/namazso/PawnIO/zip/' + $driverCommit) -OutFile (Join-Path $sourcePath 'PawnIO-driver-source.zip')
Invoke-WebRequest ('https://raw.githubusercontent.com/namazso/PawnIO/' + $driverCommit + '/COPYING') -OutFile (Join-Path $sourcePath 'PawnIO-driver-COPYING')
Invoke-WebRequest 'https://codeload.github.com/namazso/PawnPP/zip/e64e4c37b2d8ba0d8ee57205faf8183aee12c438' -OutFile (Join-Path $sourcePath 'PawnIO-PawnPP-submodule-source.zip')
Write-Output 'Pinned signed EC module and installer ready. No drivers were installed.'
