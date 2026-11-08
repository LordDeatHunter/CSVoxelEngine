# Generates the placeholder 32x32 block tiles for the desert/ocean biomes. The art is
# deterministic (a fixed hash per texel), so re-running reproduces the same PNG byte for byte.
# Run from the repo root: powershell -File tools/generate-biome-textures.ps1
Add-Type -AssemblyName System.Drawing

$outDir = Join-Path $PSScriptRoot '..\OpenGLTest\assets\textures\blocks'
$outDir = [System.IO.Path]::GetFullPath($outDir)

function Get-Noise([int]$x, [int]$y, [int]$seed) {
    $h = [int64](([int64]$x * 73856093) -bxor ([int64]$y * 19349663) -bxor ([int64]$seed * 83492791))
    $h = $h -band 0x7fffffff
    return [double]($h % 1000) / 1000.0
}

function New-Tile([string]$name, [scriptblock]$paint) {
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    for ($y = 0; $y -lt 32; $y++) {
        for ($x = 0; $x -lt 32; $x++) {
            $rgb = & $paint $x $y
            $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $rgb[0], $rgb[1], $rgb[2]))
        }
    }
    $path = Join-Path $outDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "wrote $path"
}

function Clamp([int]$v) { if ($v -lt 0) { return 0 } elseif ($v -gt 255) { return 255 } else { return $v } }

# Same as New-Tile but the paint block returns (r, g, b, a), so translucent (ice) and cutout
# (spruce leaves) tiles can carry an alpha channel.
function New-TileA([string]$name, [scriptblock]$paint) {
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    for ($y = 0; $y -lt 32; $y++) {
        for ($x = 0; $x -lt 32; $x++) {
            $rgba = & $paint $x $y
            $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($rgba[3], $rgba[0], $rgba[1], $rgba[2]))
        }
    }
    $path = Join-Path $outDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "wrote $path"
}

# Sandstone: tan stone in horizontal strata, with a fine speckle so the bands read as rock.
New-Tile 'sandstone' {
    param($x, $y)
    $band = [int]([Math]::Floor($y / 8))
    $shade = @(0, -10, -4, -14)[$band % 4]
    $n = (Get-Noise $x $y 11) * 18 - 9
    return @((Clamp (216 + $shade + $n)), (Clamp (203 + $shade + $n)), (Clamp (163 + $shade + $n)))
}

# Cactus side: ribbed green column, darker at the ribs (x % 8 == 0) and light spine dots.
New-Tile 'cactus_side' {
    param($x, $y)
    $rib = if ($x % 8 -eq 0) { -26 } else { 0 }
    $n = (Get-Noise $x $y 23) * 14 - 7
    $spine = if ((Get-Noise $x $y 24) -gt 0.93) { 55 } else { 0 }
    return @((Clamp (58 + $rib + $n + $spine)), (Clamp (122 + $rib + $n + $spine)), (Clamp (56 + $rib + $n + $spine)))
}

# Cactus top: a lighter pith inside the ribbed rim.
New-Tile 'cactus_top' {
    param($x, $y)
    $dx = $x - 15.5
    $dy = $y - 15.5
    $d = [Math]::Sqrt($dx * $dx + $dy * $dy)
    $center = if ($d -lt 9) { 34 } else { 0 }
    $ring = if ($d -ge 12 -and $d -lt 14) { -22 } else { 0 }
    $n = (Get-Noise $x $y 31) * 12 - 6
    return @((Clamp (58 + $center + $ring + $n)), (Clamp (122 + $center + $ring + $n)), (Clamp (56 + $center + $ring + $n)))
}

# Snow: near-white with a faint cold tint and occasional shallow dimples.
New-Tile 'snow' {
    param($x, $y)
    $n = (Get-Noise $x $y 41) * 10 - 5
    $dimple = if ((Get-Noise $x $y 42) -gt 0.9) { -16 } else { 0 }
    return @((Clamp (238 + $n + $dimple)), (Clamp (242 + $n + $dimple)), (Clamp (250 + $n + $dimple)))
}

# Ice: pale blue with brighter cracks, mostly opaque so a frozen sea reads as a sheet of ice.
New-TileA 'ice' {
    param($x, $y)
    $n = (Get-Noise $x $y 51) * 16 - 8
    $crack = if ((Get-Noise $x $y 52) -gt 0.88) { 26 } else { 0 }
    return @((Clamp (186 + $n + $crack)), (Clamp (222 + $n + $crack)), (Clamp (243 + $n + $crack)), 238)
}

# Spruce bark: dark reddish-brown with vertical streaks, darker than the oak log.
New-Tile 'spruce_log_side' {
    param($x, $y)
    $streak = if ($x % 7 -eq 0) { -22 } elseif ($x % 5 -eq 0) { 12 } else { 0 }
    $n = (Get-Noise $x $y 61) * 14 - 7
    return @((Clamp (74 + $streak + $n)), (Clamp (54 + $streak + $n)), (Clamp (38 + $streak + $n)))
}

# Spruce cut end: growth rings around the pith.
New-Tile 'spruce_log_top' {
    param($x, $y)
    $dx = $x - 15.5
    $dy = $y - 15.5
    $d = [Math]::Sqrt($dx * $dx + $dy * $dy)
    $ring = if (([int]($d / 2.6)) % 2 -eq 0) { 12 } else { -12 }
    $n = (Get-Noise $x $y 71) * 10 - 5
    return @((Clamp (120 + $ring + $n)), (Clamp (92 + $ring + $n)), (Clamp (62 + $ring + $n)))
}

# Spruce needles: dark green with a fine shade dither and a few alpha holes (cutout).
New-TileA 'spruce_leaves' {
    param($x, $y)
    $n = (Get-Noise $x $y 81)
    $a = if ($n -gt 0.86) { 0 } else { 255 }
    $shade = (Get-Noise $x $y 82) * 22 - 11
    return @((Clamp (38 + $shade)), (Clamp (86 + $shade)), (Clamp (48 + $shade)), $a)
}

# Item sprites live beside the block tiles and share the 32x32 canvas.
$itemDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\OpenGLTest\assets\textures\items'))

function New-ItemTile([string]$name, [scriptblock]$paint) {
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    for ($y = 0; $y -lt 32; $y++) {
        for ($x = 0; $x -lt 32; $x++) {
            $rgba = & $paint $x $y
            $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($rgba[3], $rgba[0], $rgba[1], $rgba[2]))
        }
    }
    $path = Join-Path $itemDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "wrote $path"
}

# Snow pickup sprite: a rounded white heap with a dimmer rim, transparent background.
New-ItemTile 'snow' {
    param($x, $y)
    $dx = ($x - 15.5) / 15.5
    $dy = ($y - 20.0) / 11.0
    $d = [Math]::Sqrt($dx * $dx + $dy * $dy)
    if ($d -gt 1.0) { return @(0, 0, 0, 0) }
    $n = (Get-Noise $x $y 91) * 12 - 6
    $edge = if ($d -gt 0.82) { -24 } else { 0 }
    return @((Clamp (236 + $n + $edge)), (Clamp (241 + $n + $edge)), (Clamp (250 + $n + $edge)), 255)
}
