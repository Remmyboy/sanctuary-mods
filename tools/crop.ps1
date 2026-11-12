<#
.SYNOPSIS
  Crops and enlarges a screenshot so a small UI detail can be read.

.DESCRIPTION
  crop.ps1 <png> <x> <y> <w> <h> [-Scale 2] [-Out file.png]

  Pixel coordinates from the top-left of the image. Writes <png>-crop.png
  beside the original unless -Out is given, and prints the path so it can be
  opened with Read. Uses System.Drawing (there is no ffmpeg, ImageMagick or
  PIL here, and the Browser zoom tool only works on web pages).
#>
param(
    [Parameter(Mandatory, Position = 0)][string]$Png,
    [Parameter(Mandatory, Position = 1)][int]$X,
    [Parameter(Mandatory, Position = 2)][int]$Y,
    [Parameter(Mandatory, Position = 3)][int]$W,
    [Parameter(Mandatory, Position = 4)][int]$H,
    [double]$Scale = 2,
    [string]$Out
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$Png = (Resolve-Path $Png).Path
if (-not $Out) { $Out = [IO.Path]::ChangeExtension($Png, $null).TrimEnd('.') + '-crop.png' }
$Out = [IO.Path]::GetFullPath($Out)
$sw = [int]($W * $Scale); $sh = [int]($H * $Scale)
$src = [System.Drawing.Bitmap]::new($Png)
try {
    $dst = [System.Drawing.Bitmap]::new($sw, $sh)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.DrawImage($src, [System.Drawing.Rectangle]::new(0, 0, $sw, $sh), [System.Drawing.Rectangle]::new($X, $Y, $W, $H), [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $dst.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $dst.Dispose()
} finally { $src.Dispose() }
$Out
