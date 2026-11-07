# Deterministic placeholder tiles of the chests (assets/textures/blocks/chest_<variant>_{side,top,front}.png, 32x32):
# planks with metal bands, a lid top, and a front with a lock plate across the lid seam (the model crops the front's
# lock between y 6/16 and 12/16 of the tile). One variant per chest block: the crafted wooden, bronze and iron ones and
# the four ruin chests. Plain filled rectangles, so a re-run is byte-identical. Run: pwsh tools/generate-chest-textures.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\OpenGLTest\assets\textures\blocks'

function C([int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb(255, $r, $g, $b) }

# mid (body), dark (outline and plank lines), band (metal straps), plate (lock plate)
$variants = [ordered]@{
    wooden            = @{ Mid = (C 150 100 56);  Dark = (C 84 52 26);   Band = (C 64 64 70);    Plate = (C 214 180 64) }
    bronze            = @{ Mid = (C 190 124 52);  Dark = (C 92 56 24);   Band = (C 236 178 96);  Plate = (C 255 230 120) }
    iron              = @{ Mid = (C 150 152 158); Dark = (C 70 72 78);   Band = (C 204 206 212); Plate = (C 238 238 244) }
    ruin_surface      = @{ Mid = (C 120 126 112); Dark = (C 60 70 56);   Band = (C 92 112 72);   Plate = (C 172 152 92) }
    ruin_underground  = @{ Mid = (C 96 72 52);    Dark = (C 50 36 24);   Band = (C 112 112 122); Plate = (C 164 132 72) }
    ruin_hell         = @{ Mid = (C 70 40 36);    Dark = (C 30 16 14);   Band = (C 204 72 32);   Plate = (C 255 152 64) }
    ruin_sky          = @{ Mid = (C 226 231 241); Dark = (C 140 150 170); Band = (C 120 200 232); Plate = (C 255 236 152) }
}

function Fill($bmp, [int]$x, [int]$y, [int]$w, [int]$h, $color) {
    for ($j = $y; $j -lt $y + $h; $j++) { for ($i = $x; $i -lt $x + $w; $i++) { $bmp.SetPixel($i, $j, $color) } }
}

function New-Planks($m, [bool]$top) {
    $bmp = New-Object System.Drawing.Bitmap 32, 32, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    Fill $bmp 0 0 32 32 $m.Mid
    # plank seams
    foreach ($y in 8, 16, 24) { Fill $bmp 0 $y 32 1 $m.Dark }
    # outline
    Fill $bmp 0 0 32 1 $m.Dark; Fill $bmp 0 31 32 1 $m.Dark; Fill $bmp 0 0 1 32 $m.Dark; Fill $bmp 31 0 1 32 $m.Dark
    # two metal straps
    $strapWidth = 4
    foreach ($x in 4, 24) { Fill $bmp $x 0 $strapWidth 32 $m.Band; Fill $bmp $x 0 1 32 $m.Dark }
    if ($top) { Fill $bmp 0 0 32 2 $m.Dark; Fill $bmp 0 30 32 2 $m.Dark }
    return $bmp
}

foreach ($name in $variants.Keys) {
    $m = $variants[$name]
    $side = New-Planks $m $false
    $side.Save((Join-Path $out "chest_${name}_side.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $side.Dispose()

    $top = New-Planks $m $true
    $top.Save((Join-Path $out "chest_${name}_top.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $top.Dispose()

    $front = New-Planks $m $false
    # the lock plate sits across the lid seam (the lid and the body meet at tile row 12)
    Fill $front 12 9 8 9 $m.Dark
    Fill $front 13 10 6 7 $m.Plate
    Fill $front 15 12 2 3 $m.Dark
    $front.Save((Join-Path $out "chest_${name}_front.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $front.Dispose()
}
