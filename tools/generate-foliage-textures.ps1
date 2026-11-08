# Generates the placeholder 32x32 cutout tiles for the two ground-cover plants: tall grass
# (forest) and the dead shrub (desert). These are the coloured source art; the world shader
# actually draws each plant through its grey <name>_textureless.png tile, which
# tools/generate-leaf-textureless.ps1 derives from these (run it with
# -Sources tall_grass,dead_shrub). The art is a pure function of (x, y), so re-running
# reproduces each PNG byte for byte.
# Run from the repo root: powershell -File tools/generate-foliage-textures.ps1
Add-Type -AssemblyName System.Drawing

$outDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\OpenGLTest\assets\textures\blocks'))

function Get-Noise([int]$x, [int]$y, [int]$seed) {
    $h = [int64](([int64]$x * 73856093) -bxor ([int64]$y * 19349663) -bxor ([int64]$seed * 83492791))
    $h = $h -band 0x7fffffff
    return [double]($h % 1000) / 1000.0
}

function Clamp([int]$v) { if ($v -lt 0) { return 0 } elseif ($v -gt 255) { return 255 } else { return $v } }

function New-PlantTile([string]$name, [string[]]$blades, [scriptblock]$paint) {
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $solid = New-Object 'bool[,]' 32, 32
    foreach ($blade in $blades) {
        $parts = $blade.Split(',')
        $bx = [int]$parts[0]; $top = [int]$parts[1]; $lean = [double]$parts[2]
        for ($y = 31; $y -ge $top; $y--) {
            $t = if ($y -ge 31) { 0.0 } else { (31.0 - $y) / (31.0 - $top) }
            $px = [int][Math]::Round($bx + $lean * $t)
            if ($px -lt 0 -or $px -gt 31) { continue }
            $solid[$px, $y] = $true
            if ($t -lt 0.35 -and $px + 1 -le 31) { $solid[($px + 1), $y] = $true }
        }
    }
    for ($y = 0; $y -lt 32; $y++) {
        for ($x = 0; $x -lt 32; $x++) {
            if (-not $solid[$x, $y]) { $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(0, 0, 0, 0)); continue }
            $rgba = & $paint $x $y
            $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $rgba[0], $rgba[1], $rgba[2]))
        }
    }
    $path = Join-Path $outDir "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "wrote $path"
}

# Tall grass: seven blades of varying height and lean, shaded up their length.
$grassBlades = @('6,13,1.8', '10,5,-0.9', '14,15,1.2', '17,4,0.6', '21,10,-1.4', '25,15,0.9', '28,7,-0.6')
New-PlantTile 'tall_grass' $grassBlades {
    param($x, $y)
    $t = (31.0 - $y) / 31.0
    $n = (Get-Noise $x $y 101) * 16 - 8
    $shade = 30.0 * $t
    return @((Clamp (52 + $shade + $n)), (Clamp (128 + $shade + $n)), (Clamp (54 + $shade + $n)))
}

# Dead shrub: a central stem plus four splayed twigs, dry brown.
$shrubBlades = @('15,10,0.0', '16,12,0.0', '11,14,-0.7', '13,17,0.5', '19,15,0.7', '21,18,-0.5')
New-PlantTile 'dead_shrub' $shrubBlades {
    param($x, $y)
    $n = (Get-Noise $x $y 111) * 18 - 9
    $shade = 24.0 * ((31.0 - $y) / 31.0)
    return @((Clamp (120 + $shade + $n)), (Clamp (92 + $shade + $n)), (Clamp (54 + $shade + $n)))
}
