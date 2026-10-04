param([switch]$SkipPackage)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compilerPath = Join-Path $frameworkPath 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 4.8 x64 compiler is required.' }
$outputPath = Join-Path $taskRoot 'build\OnlyFansControl'
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$modulePath = Join-Path $taskRoot 'vendor\PawnIO.Modules-0.2.11\LpcACPIEC.bin'
if (-not (Test-Path -LiteralPath $modulePath)) { throw 'Run scripts\restore-dependencies.ps1 first.' }
$iconBuilderPath = Join-Path $PSScriptRoot 'build-icons.ps1'
& powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File $iconBuilderPath
if ($LASTEXITCODE -ne 0) { throw 'Lucide asset generation failed.' }
$iconPath = Join-Path $taskRoot 'src\Assets\AppIcon.ico'
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output',
    ('/out:' + (Join-Path $outputPath 'OnlyFansControl.exe')),
    ('/win32manifest:' + (Join-Path $taskRoot 'src\app.manifest')),
    ('/win32icon:' + $iconPath),
    ('/resource:' + $iconPath + ',AppIcon.ico'),
    ('/resource:' + (Join-Path $taskRoot 'src\Assets\Lucide\LICENSE') + ',Lucide.LICENSE'),
    ('/resource:' + (Join-Path $taskRoot 'src\Assets\Lucide\provenance.json') + ',Lucide.provenance.json'),
    ('/resource:' + (Join-Path $taskRoot 'src\MainWindow.xaml') + ',MainWindow.xaml'),
    ('/resource:' + $modulePath + ',LpcACPIEC.bin'),
    '/reference:System.dll','/reference:System.Core.dll','/reference:System.Management.dll',
    '/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Xaml.dll','/reference:System.Xml.dll','/reference:System.Xml.Linq.dll',
    ('/reference:' + (Join-Path $frameworkPath 'WPF\WindowsBase.dll')),
    ('/reference:' + (Join-Path $frameworkPath 'WPF\PresentationCore.dll')),
    ('/reference:' + (Join-Path $frameworkPath 'WPF\PresentationFramework.dll')))
Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src\Assets\Lucide') -Filter '*.svg' | Sort-Object Name | ForEach-Object { $compilerArgs += '/resource:'+$_.FullName+',Lucide.'+$_.Name }
$compilerArgs += @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compilerPath @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
foreach ($name in @('only_fans_config.json','OnlyFansControl.exe.config','README.md','THIRD-PARTY-NOTICES.md','Start-OnlyFansControl.cmd','LICENSE','VERSION','CONTRIBUTING.md','CHANGELOG.md')) {
    $sourcePath = Join-Path $taskRoot $name
    if (Test-Path -LiteralPath $sourcePath) { Copy-Item -LiteralPath $sourcePath -Destination $outputPath -Force }
}
if (Test-Path -LiteralPath (Join-Path $taskRoot 'docs')) { Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $outputPath -Recurse -Force }
$assetsOutput = Join-Path $outputPath 'assets'
New-Item -ItemType Directory -Path $assetsOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\Assets\Lucide') -Destination $assetsOutput -Recurse -Force
Copy-Item -LiteralPath $iconPath,(Join-Path $taskRoot 'src\Assets\AppIcon.png') -Destination $assetsOutput -Force
$noticePath = Join-Path $taskRoot 'vendor\PawnIO.Modules-0.2.11\source'
if (Test-Path -LiteralPath $noticePath) {
    $noticeOutput = Join-Path $outputPath 'module-source'
    New-Item -ItemType Directory -Force -Path $noticeOutput | Out-Null
    Get-ChildItem -LiteralPath $noticePath | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $noticeOutput -Recurse -Force }
}
$setupPath = Join-Path $taskRoot 'vendor\PawnIO_setup.exe'
if (Test-Path -LiteralPath $setupPath) { New-Item -ItemType Directory -Force -Path (Join-Path $outputPath 'driver') | Out-Null; Copy-Item -LiteralPath $setupPath -Destination (Join-Path $outputPath 'driver\PawnIO_setup.exe') -Force }
$executablePath = Join-Path $outputPath 'OnlyFansControl.exe'
if (Test-Path -LiteralPath (Join-Path $taskRoot 'VERSION')) {
    $expectedVersion = (Get-Content -LiteralPath (Join-Path $taskRoot 'VERSION') -Raw).Trim()+'.0'
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath).FileVersion -ne $expectedVersion) { throw 'VERSION and executable version disagree.' }
}
Get-Item -LiteralPath $executablePath | Select-Object FullName,Length
if (-not $SkipPackage) {
    & (Join-Path $PSScriptRoot 'package.ps1')
}
