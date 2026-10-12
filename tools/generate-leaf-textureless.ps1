# Generates the gray ("textureless") leaf tiles the biome tint draws in place of the colored
# leaf art: assets/textures/blocks/<name>_textureless.png beside each <name>.png in -Sources
# (default: every leaf tile; tools/generate-tree-textures.ps1 passes only the leaves it made, so
# a run from there never rewrites the oak and spruce tiles).
#
# The world shader draws a biome-tinted face as texel * vertexRgb * BiomeTint.Gain, and the one
# Gain (4) is sized against tiles whose opaque texels average the same sRGB gray the two supplied
# grass tiles (grass_top_textureless, grass_side_textureless) measure - about 98. So each leaf
# tile is desaturated to its Rec. 601 luminance (as tools/generate-ore-materials.ps1 reads it) and
# then scaled uniformly until the mean gray of its opaque texels lands on that same value. Alpha
# is copied unchanged, so the canopy keeps exactly the holes of the source art.
#
# Conventions that must not drift:
#   * $TargetGray must equal WorldGenerationConfig.BiomeTintTextureGray * 255, and the LOD canopy
#     hole fallback in test-fragment.glsl is that gray decoded to linear.
#   * $OpaqueAlpha is the first alpha byte at or above WorldGenerationConfig.AlphaCutoff (0.5) * 255,
#     i.e. the texels the alpha test keeps; only those count toward the mean.
#   * Every texel is written, transparent ones too, so the output never depends on what the
#     encoder would make of skipped pixels.
#
# BiomeTintTextureTests checks the result (gray on every texel, the opaque mean at the target,
# alpha equal to the source texel for texel).
#
# The output is a pure function of the input pixels - no noise, no randomness - so re-running
# reproduces each PNG byte for byte, like tools/generate-ore-materials.ps1.
#
# Run from the repo root: powershell -File tools/generate-leaf-textureless.ps1 [-Sources a,b]
param([string[]]$Sources = @('oak_leaves', 'spruce_leaves', 'baobab_leaves', 'stone_pine_leaves'))

Add-Type -AssemblyName System.Drawing

$TargetGray  = 98
$OpaqueAlpha = 128

$root     = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$blockDir = Join-Path $root 'OpenGLTest\assets\textures\blocks'

function Clamp255([double]$v) {
    $i = [int][Math]::Round($v)
    if ($i -lt 0) { return 0 } elseif ($i -gt 255) { return 255 } else { return $i }
}

foreach ($name in $Sources) {
    $src = Join-Path $blockDir "$name.png"
    if (-not (Test-Path $src)) { throw "Leaf tile '$src' not found." }

    $bmp = New-Object System.Drawing.Bitmap $src
    $w = $bmp.Width
    $h = $bmp.Height

    # Rec. 601 luminance of every texel, and the mean over the ones the alpha test keeps.
    $lum = New-Object 'double[]' ($w * $h)
    $alpha = New-Object 'int[]' ($w * $h)
    $sum = 0.0
    $count = 0
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $l = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
            $lum[$y * $w + $x] = $l
            $alpha[$y * $w + $x] = $c.A
            if ($c.A -ge $OpaqueAlpha) {
                $sum += $l
                $count++
            }
        }
    }
    if ($count -eq 0) { throw "Leaf tile '$src' has no opaque texel." }
    $mean = $sum / $count
    $scale = $TargetGray / $mean

    $out = New-Object System.Drawing.Bitmap $w, $h
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $i = $y * $w + $x
            $g = Clamp255 ($lum[$i] * $scale)
            $out.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha[$i], $g, $g, $g))
        }
    }

    $path = Join-Path $blockDir "${name}_textureless.png"
    $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
    $bmp.Dispose()
    Write-Output ("{0}: opaque mean luminance {1:F2} over {2} texels, scale {3:F4} -> wrote {4}" -f $name, $mean, $count, $scale, $path)
}
