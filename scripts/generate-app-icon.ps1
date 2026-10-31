# Rebuild the Windows PNG/ICO directly from the authored SVG geometry.
# Run on Windows: powershell -NoProfile -STA -File scripts/generate-app-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$assetDirectory = Join-Path $PSScriptRoot '..\src\OpenUninstaller.App\Assets'
[xml]$svg = Get-Content -LiteralPath (Join-Path $assetDirectory 'app.svg') -Raw
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
$brushConverter = New-Object System.Windows.Media.BrushConverter

foreach ($size in @($iconSizes) + @(1024)) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    $scale = $size / 256.0
    $drawing.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
    foreach ($shape in $svg.DocumentElement.ChildNodes) {
        if ($shape.LocalName -notin @('rect', 'circle')) { continue }
        $fill = $null
        $pen = $null
        if ($shape.GetAttribute('fill') -ne 'none') {
            $fill = $brushConverter.ConvertFromInvariantString($shape.GetAttribute('fill'))
        }
        if ($shape.HasAttribute('stroke')) {
            $stroke = $brushConverter.ConvertFromInvariantString($shape.GetAttribute('stroke'))
            $pen = New-Object System.Windows.Media.Pen($stroke, [double]$shape.GetAttribute('stroke-width'))
        }
        if ($shape.LocalName -eq 'rect') {
            $rect = New-Object System.Windows.Rect([double]$shape.x, [double]$shape.y, [double]$shape.width, [double]$shape.height)
            $drawing.DrawRoundedRectangle($fill, $pen, $rect, [double]$shape.rx, [double]$shape.rx)
        } else {
            $center = New-Object System.Windows.Point([double]$shape.cx, [double]$shape.cy)
            $drawing.DrawEllipse($fill, $pen, $center, [double]$shape.r, [double]$shape.r)
        }
    }
    $drawing.Pop()
    $drawing.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $metadata = New-Object System.Windows.Media.Imaging.BitmapMetadata('png')
    $metadata.SetQuery('/tEXt/{str=impeccable:prompt}', 'Origin: authored vector src/OpenUninstaller.App/Assets/app.svg; rendered locally by scripts/generate-app-icon.ps1 using WPF. OpenUninstaller zinc theme redesign, 2026-10-31. No AI image generation.')
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap, $null, $metadata, $null))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    if ($size -eq 1024) {
        [System.IO.File]::WriteAllBytes((Join-Path $assetDirectory 'app.png'), $bytes)
    } else {
        $frames += [pscustomobject]@{ Size = $size; Bytes = $bytes }
    }
}

# Windows 10/11 accept PNG-compressed entries in ICO containers.
$file = [System.IO.File]::Create((Join-Path $assetDirectory 'app.ico'))
$writer = New-Object System.IO.BinaryWriter($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally {
    $writer.Dispose()
    $file.Dispose()
}
Write-Output 'Created app.png (1024px) and app.ico (16,20,24,32,40,48,64,128,256px) from app.svg.'
