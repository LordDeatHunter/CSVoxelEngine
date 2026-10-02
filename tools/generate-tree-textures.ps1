# Generates the placeholder tiles of the tree species that have no art of their own yet (baobab,
# stone pine): assets/textures/blocks/<species>_log_side.png, <species>_log_top.png and
# <species>_leaves.png, each tinted from the matching oak tile, then their grey
# <species>_leaves_textureless.png through tools/generate-leaf-textureless.ps1.
#
# Every output texel is the oak texel's RGB multiplied channel by channel by the species' fixed
# bark or leaf tint in $Tints, rounded and clamped to 0..255, with the alpha copied unchanged, so a
# leaf tile keeps exactly the holes of the oak art and a log tile stays opaque.
#
# Conventions that must not drift:
#   * Tiles go in assets/textures/blocks/, never blocks_material/ (that folder is index-parallel
#     material data, see AGENTS.md "The material atlas").
#   * Every texel is written, transparent ones too, so the output never depends on what the
#     encoder would make of skipped pixels.
#   * The textureless pass is called with -Sources naming only the leaves made here, so a run of
#     this script never rewrites the oak or spruce textureless tiles.
#
# The output is a pure function of the oak pixels and $Tints - no noise, no randomness - so
# re-running reproduces each PNG byte for byte, like tools/generate-leaf-textureless.ps1. Replace
# a generated tile with real art by deleting its row from $Targets.
#
# Run from the repo root: powershell -File tools/generate-tree-textures.ps1
Add-Type -AssemblyName System.Drawing

# RGB multipliers per species and tile kind.
$Tints = @{
    'baobab_bark'       = @(1.30, 1.22, 1.12)   # pale grey-tan
    'baobab_leaves'     = @(1.15, 1.08, 0.55)   # olive / yellow-green
    'stone_pine_bark'   = @(1.18, 0.82, 0.70)   # warm red-brown
    'stone_pine_leaves' = @(0.62, 0.82, 0.92)   # dark blue-green
}

# Output tile, oak source tile, tint key.
$Targets = @(
    @('baobab_log_side',     'oak_log_side', 'baobab_bark'),
    @('baobab_log_top',      'oak_log_top',  'baobab_bark'),
    @('baobab_leaves',       'oak_leaves',   'baobab_leaves'),
    @('stone_pine_log_side', 'oak_log_side', 'stone_pine_bark'),
    @('stone_pine_log_top',  'oak_log_top',  'stone_pine_bark'),
    @('stone_pine_leaves',   'oak_leaves',   'stone_pine_leaves')
)

$root     = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$blockDir = Join-Path $root 'OpenGLTest\assets\textures\blocks'

function Clamp255([double]$v) {
    $i = [int][Math]::Round($v)
    if ($i -lt 0) { return 0 } elseif ($i -gt 255) { return 255 } else { return $i }
}

# The pure per-texel function: tint the colour, keep the alpha.
function Tint-Texel([System.Drawing.Color]$c, [double[]]$tint) {
    return [System.Drawing.Color]::FromArgb(
        $c.A,
        (Clamp255 ($c.R * $tint[0])),
        (Clamp255 ($c.G * $tint[1])),
        (Clamp255 ($c.B * $tint[2])))
}

foreach ($target in $Targets) {
    $name   = $target[0]
    $source = $target[1]
    $tint   = $Tints[$target[2]]
    if ($null -eq $tint) { throw "No tint '$($target[2])' for '$name'." }

    $src = Join-Path $blockDir "$source.png"
    if (-not (Test-Path $src)) { throw "Source tile '$src' not found." }

    $bmp = New-Object System.Drawing.Bitmap $src
    $w = $bmp.Width
    $h = $bmp.Height
    $out = New-Object System.Drawing.Bitmap $w, $h
    for ($y = 0; $y -lt $h; $y++) {
        for ($x = 0; $x -lt $w; $x++) {
            $out.SetPixel($x, $y, (Tint-Texel $bmp.GetPixel($x, $y) $tint))
        }
    }

    $path = Join-Path $blockDir "$name.png"
    $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
    $bmp.Dispose()
    Write-Output ("{0}: {1} tinted by {2} -> wrote {3}" -f $name, $source, $target[2], $path)
}

& (Join-Path $PSScriptRoot 'generate-leaf-textureless.ps1') -Sources baobab_leaves, stone_pine_leaves
