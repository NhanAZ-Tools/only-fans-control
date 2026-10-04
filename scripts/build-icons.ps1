$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$assetsPath = Join-Path $taskRoot 'src\Assets'
$lucidePath = Join-Path $assetsPath 'Lucide'
$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xaml,System.Xml.Linq
$references = @('System.dll','System.Core.dll','System.Xml.dll','System.Xml.Linq.dll','System.Drawing.dll','System.Xaml.dll',
    (Join-Path $frameworkPath 'WPF\WindowsBase.dll'),(Join-Path $frameworkPath 'WPF\PresentationCore.dll'),(Join-Path $frameworkPath 'WPF\PresentationFramework.dll'))
Add-Type -Path (Join-Path $taskRoot 'src\LucideIcons.cs') -ReferencedAssemblies $references
$provenance = Get-Content -LiteralPath (Join-Path $lucidePath 'provenance.json') -Raw | ConvertFrom-Json
foreach ($entry in $provenance.icons) {
    $svgPath = Join-Path $lucidePath $entry.file
    if ((Get-FileHash -LiteralPath $svgPath).Hash.ToLowerInvariant() -ne $entry.sha256) { throw ('Lucide source hash mismatch: '+$entry.file) }
    $svgStream = [IO.File]::OpenRead($svgPath)
    try { [OnlyFansControl.LucideSvg]::Read($svgStream) | Out-Null } finally { $svgStream.Dispose() }
}
if ((Get-FileHash -LiteralPath (Join-Path $lucidePath 'LICENSE')).Hash.ToLowerInvariant() -ne $provenance.license_sha256) { throw 'Lucide license hash mismatch.' }
$fanStream = [IO.File]::OpenRead((Join-Path $lucidePath 'fan.svg'))
try { $fan = [OnlyFansControl.LucideSvg]::Read($fanStream) } finally { $fanStream.Dispose() }
$foreground = [System.Windows.Media.BrushConverter]::new().ConvertFromString('#2472C7')
$frames = @()
foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    try { [OnlyFansControl.LucideSvg]::Draw($drawing,$fan,$foreground,$size,$size) } finally { $drawing.Close() }
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size,$size,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = New-Object IO.MemoryStream
    try { $encoder.Save($memory); $png = $memory.ToArray() } finally { $memory.Dispose() }
    $frames += [pscustomobject]@{Size=$size;Png=$png}
    if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $assetsPath 'AppIcon.png'),$png) }
}
$iconPath = Join-Path $assetsPath 'AppIcon.ico'
$iconStream = [IO.File]::Create($iconPath)
$writer = New-Object IO.BinaryWriter($iconStream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6+16*$frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frame.Png.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Png.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Png) }
} finally { $writer.Dispose(); $iconStream.Dispose() }
Write-Output ('Lucide '+$provenance.version+': '+$provenance.icons.Count+' SVGs verified; app ICO generated at nine sizes.')
