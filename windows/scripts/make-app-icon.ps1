# Makes windows/src/OpenClicky.Windows/Assets/OpenClicky.ico (16-256 px, PNG-compressed entries)
# from the Mac app icon. The Mac artwork sits inside Apple's icon grid with a transparent margin
# around the rounded square; Windows icons fill their square, so the margin is cropped first
# (to the bounds of the non-transparent pixels) and the crop is scaled to each size.
#
#   pwsh windows/scripts/make-app-icon.ps1
#
# Uses System.Drawing only (no packages).

param(
    [string]$SourcePng = (Join-Path $PSScriptRoot "..\..\macos\OpenClicky\OpenClicky\Assets.xcassets\AppIcon.appiconset\1024-mac.png"),
    [string]$OutputIco = (Join-Path $PSScriptRoot "..\src\OpenClicky.Windows\Assets\OpenClicky.ico")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$source = [System.Drawing.Bitmap]::new((Resolve-Path $SourcePng).Path)

# The bounds of the artwork: every pixel with some opacity.
$left = $source.Width; $top = $source.Height; $right = -1; $bottom = -1
for ($y = 0; $y -lt $source.Height; $y += 2) {
    for ($x = 0; $x -lt $source.Width; $x += 2) {
        if ($source.GetPixel($x, $y).A -gt 24) {
            if ($x -lt $left) { $left = $x }
            if ($x -gt $right) { $right = $x }
            if ($y -lt $top) { $top = $y }
            if ($y -gt $bottom) { $bottom = $y }
        }
    }
}
# A square crop centred on the artwork (it is square, but the shadow can make it uneven).
$side = [Math]::Max($right - $left, $bottom - $top) + 2
$centerX = ($left + $right) / 2; $centerY = ($top + $bottom) / 2
$cropRect = [System.Drawing.Rectangle]::new([int]($centerX - $side / 2), [int]($centerY - $side / 2), [int]$side, [int]$side)
Write-Host "artwork bounds: $cropRect"

$pngImages = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.DrawImage($source, [System.Drawing.Rectangle]::new(0, 0, $size, $size), $cropRect, [System.Drawing.GraphicsUnit]::Pixel)
    $graphics.Dispose()
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    $pngImages += , $stream.ToArray()
}
$source.Dispose()

# ICONDIR, one ICONDIRENTRY per size, then the PNG payloads.
$output = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($index = 0; $index -lt $sizes.Count; $index++) {
    $size = $sizes[$index]
    $sizeByte = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([byte]$sizeByte); $writer.Write([byte]$sizeByte)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$pngImages[$index].Length); $writer.Write([UInt32]$offset)
    $offset += $pngImages[$index].Length
}
foreach ($png in $pngImages) { $writer.Write($png) }
$writer.Flush()
New-Item -ItemType Directory -Force (Split-Path $OutputIco) | Out-Null
[System.IO.File]::WriteAllBytes($OutputIco, $output.ToArray())
Write-Host "wrote $OutputIco ($($output.Length) bytes, sizes $($sizes -join ', '))"
