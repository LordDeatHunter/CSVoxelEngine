# Generates the material maps for the reflective ore blocks into
# assets/textures/blocks_material/. Each output is index-parallel to the albedo tile of the same
# name in assets/textures/blocks/: RGB is a tangent-space normal, A is a metallic multiplier.
#
# The normal comes from the albedo's own luminance treated as a heightfield, so the art stays the
# single source of truth - coal's nodules become rounded bumps and obsidian's diagonal striations
# become grooves that streak a highlight along the banding. Every ore tile here is a solid block
# of ore (no stone matrix), so A stays 255 and metalness is uniform; the per-block strength lives
# in assets/data/blocks/<name>.json, not here.
#
# Conventions that must not drift:
#   * +G = +v. v runs down the image exactly as it runs down the tile (rows upload top-first and
#     GL's t=0 is the first row), and test-fragment.glsl builds its bitangent from Chunk.TiledUv's
#     dv direction to match.
#   * The Sobel wraps at the tile edges. Merged greedy quads tile these textures through the
#     sampler's REPEAT, so a clamped filter would show a seam at every cell border.
#
# The output is a pure function of the input pixels - no noise, no randomness - so re-running
# reproduces each PNG byte for byte, like tools/generate-biome-textures.ps1.
#
# Run from the repo root: powershell -File tools/generate-ore-materials.ps1
Add-Type -AssemblyName System.Drawing

$root       = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$albedoDir  = Join-Path $root 'OpenGLTest\assets\textures\blocks'
$outDir     = Join-Path $root 'OpenGLTest\assets\textures\blocks_material'

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

function Clamp255([double]$v) {
    $i = [int][Math]::Round($v)
    if ($i -lt 0) { return 0 } elseif ($i -gt 255) { return 255 } else { return $i }
}

# Rec. 601 luma of the albedo, stretched to 0..1 over the tile's own range. The ore art is very
# low contrast (coal spans luminance 17-52 of 255, obsidian 18-40), so without the stretch the
# Sobel below would return almost nothing and every face would come out flat.
function Get-HeightField([System.Drawing.Bitmap]$bmp, [int]$w, [int]$h) {
    $lum = New-Object 'double[]' ($w * $h)
    $min = [double]::MaxValue
    $max = [double]::MinValue
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $l = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
            $lum[$y * $w + $x] = $l
            if ($l -lt $min) { $min = $l }
            if ($l -gt $max) { $max = $l }
        }
    }
    $span = $max - $min
    if ($span -lt 1e-6) { $span = 1.0 }
    for ($i = 0; $i -lt $lum.Length; $i++) { $lum[$i] = ($lum[$i] - $min) / $span }
    return $lum
}

# Wrapping 3x3 box blur. One pass rounds a hard-edged pixel cluster into a dome; without it a
# single-texel colour step becomes a vertical cliff and the highlight reads as a hard speckle
# rather than a bump.
function Invoke-Blur([double[]]$src, [int]$w, [int]$h) {
    $dst = New-Object 'double[]' ($w * $h)
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $sum = 0.0
            for ($j = -1; $j -le 1; $j++) {
                for ($i = -1; $i -le 1; $i++) {
                    $sx = (($x + $i) % $w + $w) % $w
                    $sy = (($y + $j) % $h + $h) % $h
                    $sum += $src[$sy * $w + $sx]
                }
            }
            $dst[$y * $w + $x] = $sum / 9.0
        }
    }
    return $dst
}

# Sobel kernels, row-major over the 3x3 neighbourhood. X increases with u, Y increases with v
# (down the image), which is the +G = +v convention the fragment shader's bitangent matches.
$SobelX = @(-1, 0, 1,
            -2, 0, 2,
            -1, 0, 1)
$SobelY = @(-1, -2, -1,
             0,  0,  0,
             1,  2,  1)

# name -> how the heightfield is filtered and how steep the result is.
$tiles = @(
    # Soft lumpy nodules: blur twice and stay gentle, so coal reads as a dull satin sheen
    # rather than a field of glitter.
    @{ Name = 'coal_ore';      Blur = 2; Scale = 2.2 },
    # The copper flecks are their own little nodules, so only one blur - enough to round them,
    # not enough to dissolve them into the green body.
    @{ Name = 'malachite_ore'; Blur = 1; Scale = 2.6 },
    # Volcanic glass is smooth: its diagonal banding is flow colour, not relief. So this gets the
    # *shallowest* scale of the three, which is what lets a whole face catch one broad highlight
    # instead of breaking it into per-texel sparkle - the relief only bends that sheen a little
    # as it crosses the bands. Two blurs keep the bands continuous rather than dithered.
    @{ Name = 'obsidian';      Blur = 2; Scale = 0.9 }
)

foreach ($tile in $tiles) {
    $name = $tile.Name
    $src = Join-Path $albedoDir "$name.png"
    if (-not (Test-Path $src)) { throw "Albedo tile '$src' not found." }

    $bmp = New-Object System.Drawing.Bitmap $src
    $w = $bmp.Width
    $h = $bmp.Height

    $height = Get-HeightField $bmp $w $h
    for ($p = 0; $p -lt $tile.Blur; $p++) { $height = Invoke-Blur $height $w $h }

    $out = New-Object System.Drawing.Bitmap $w, $h
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            # Sobel over the wrapped neighbourhood.
            $gx = 0.0
            $gy = 0.0
            for ($j = -1; $j -le 1; $j++) {
                for ($i = -1; $i -le 1; $i++) {
                    $sx = (($x + $i) % $w + $w) % $w
                    $sy = (($y + $j) % $h + $h) % $h
                    $v = $height[$sy * $w + $sx]
                    $k = ($j + 1) * 3 + ($i + 1)
                    $gx += $v * $SobelX[$k]
                    $gy += $v * $SobelY[$k]
                }
            }
            $gx = $gx / 8.0 * $tile.Scale
            $gy = $gy / 8.0 * $tile.Scale

            # Heightfield normal: the surface tilts away from an uphill gradient, and z stays 1
            # so Scale alone sets how steep the tile reads.
            $nx = -$gx
            $ny = -$gy
            $nz = 1.0
            $len = [Math]::Sqrt($nx * $nx + $ny * $ny + $nz * $nz)
            $nx /= $len; $ny /= $len; $nz /= $len

            $r = Clamp255 (($nx * 0.5 + 0.5) * 255.0)
            $g = Clamp255 (($ny * 0.5 + 0.5) * 255.0)
            $b = Clamp255 (($nz * 0.5 + 0.5) * 255.0)
            # A is the metallic multiplier. These are solid ore blocks, so every texel is fully
            # metallic and the block's JSON sets the actual amount.
            $out.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $r, $g, $b))
        }
    }

    $path = Join-Path $outDir "$name.png"
    $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
    $bmp.Dispose()
    Write-Output "wrote $path"
}
