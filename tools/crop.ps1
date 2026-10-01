<#
.SYNOPSIS
  Crops and enlarges a screenshot so a small UI detail can be read.

.DESCRIPTION
  crop.ps1 <png> <x> <y> <w> <h> [-Scale 2] [-Out file.png]

  Pixel coordinates from the top-left of the image. Writes <png>-crop.png
  beside the original unless -Out is given, and prints the path so it can be
  opened with Read. Uses the WinGet ffmpeg (there is no ImageMagick or PIL
  here, and the Browser zoom tool only works on web pages).
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
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) {
    $ffmpeg = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $ffmpeg) { throw 'ffmpeg not found (winget install Gyan.FFmpeg)' }
if (-not $Out) { $Out = [IO.Path]::ChangeExtension($Png, $null).TrimEnd('.') + '-crop.png' }
$sw = [int]($W * $Scale); $sh = [int]($H * $Scale)
& $ffmpeg -v error -y -i $Png -vf "crop=${W}:${H}:${X}:${Y},scale=${sw}:${sh}:flags=neighbor" $Out
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed' }
$Out
