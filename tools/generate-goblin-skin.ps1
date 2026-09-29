# Generates the 64x64 goblin skin (OpenGLTest/assets/models/entity/goblin.png). The art is
# deterministic (a fixed hash per texel), so re-running reproduces the same PNG byte for byte.
# Run from the repo root: powershell -File tools/generate-goblin-skin.ps1
#
# The UV rectangles below are the ones goblin.bbmodel's faces point at (pixels, x1 y1 x2 y2):
#   head  0,0 .. 42,7   (north 0 0 7 7, then south, west, east, up, down in 7-wide steps)
#   ears  42,0 .. 44,3  (every face)
#   torso 0,8 .. 40,16  (north 0 8 8 16, south 8 8 16 16, west 16 8 20 16, east 20 8 24 16,
#                        up 24 8 32 12, down 32 8 40 12)
#   arms  0,17 .. 18,25 (north/south/west/east 3x8 each, up 12 17 15 20, down 15 17 18 20)
#   legs  0,26 .. 18,32 (north/south/west/east 3x6 each, up 12 26 15 29, down 15 26 18 29)
# Moving a rectangle here means moving the matching face UVs in the .bbmodel too.
Add-Type -AssemblyName System.Drawing

$outPath = Join-Path $PSScriptRoot '..\OpenGLTest\assets\models\entity\goblin.png'
$outPath = [System.IO.Path]::GetFullPath($outPath)

function Get-Noise([int]$x, [int]$y, [int]$seed) {
    $h = [int64](([int64]$x * 73856093) -bxor ([int64]$y * 19349663) -bxor ([int64]$seed * 83492791))
    $h = $h -band 0x7fffffff
    return [double]($h % 1000) / 1000.0
}

function Clamp([int]$v) { if ($v -lt 0) { return 0 } elseif ($v -gt 255) { return 255 } else { return $v } }

function In-Rect([int]$x, [int]$y, [int]$x1, [int]$y1, [int]$x2, [int]$y2) {
    return ($x -ge $x1 -and $x -lt $x2 -and $y -ge $y1 -and $y -lt $y2)
}

# Mottled goblin green: a fine per-texel speckle plus a coarser blotch so the skin never reads
# as one flat colour.
function Skin([int]$x, [int]$y, [int]$lift) {
    $fine = (Get-Noise $x $y 7) * 16 - 8
    $blotch = if ((Get-Noise ([int][Math]::Floor($x / 2)) ([int][Math]::Floor($y / 2)) 13) -gt 0.7) { -14 } else { 0 }
    $s = $fine + $blotch + $lift
    return @((Clamp (92 + $s)), (Clamp (138 + $s)), (Clamp (58 + $s * 0.6)))
}

# Worn brown hide for the loincloth and the feet.
function Hide([int]$x, [int]$y) {
    $n = (Get-Noise $x $y 29) * 18 - 9
    return @((Clamp (108 + $n)), (Clamp (74 + $n)), (Clamp (44 + $n)))
}

$bmp = New-Object System.Drawing.Bitmap 64, 64
for ($y = 0; $y -lt 64; $y++) {
    for ($x = 0; $x -lt 64; $x++) {
        $a = 255
        if (In-Rect $x $y 0 0 44 7) {
            # Head and ears, a touch lighter than the body.
            $rgb = Skin $x $y 8
        } elseif (In-Rect $x $y 0 8 40 16) {
            $rgb = Skin $x $y 0
            # Loincloth: rows 12..15 of the torso's north and south faces.
            if ($y -ge 12 -and $y -le 15 -and (In-Rect $x $y 0 8 16 16)) { $rgb = Hide $x $y }
        } elseif (In-Rect $x $y 0 17 18 25) {
            $rgb = Skin $x $y -4
        } elseif (In-Rect $x $y 0 26 18 32) {
            $rgb = Skin $x $y -6
            # Feet: rows 30..31 of the four leg sides, plus the soles (the down face).
            if (($y -ge 30 -and $x -lt 12) -or (In-Rect $x $y 15 26 18 29)) { $rgb = Hide $x $y }
        } else {
            # Unused texels stay transparent.
            $rgb = @(0, 0, 0)
            $a = 0
        }
        $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($a, $rgb[0], $rgb[1], $rgb[2]))
    }
}

# Face on the head's north rect: two red eyes and a dark mouth.
$bmp.SetPixel(2, 3, [System.Drawing.Color]::FromArgb(255, 200, 28, 24))
$bmp.SetPixel(4, 3, [System.Drawing.Color]::FromArgb(255, 200, 28, 24))
$bmp.SetPixel(3, 5, [System.Drawing.Color]::FromArgb(255, 34, 22, 16))

$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "wrote $outPath"
