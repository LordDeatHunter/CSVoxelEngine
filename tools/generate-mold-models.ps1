# Generates the block models for the eight cast molds into assets/models/block/:
# mold_pickaxe.json, mold_axe.json, mold_ingot.json, fired_mold_pickaxe.json, fired_mold_axe.json
# fired_mold_ingot.json, mold_sword.json and fired_mold_sword.json (mold_sword.png is committed art). Each one is a
# flat slab whose top is carved out into a pocket shaped like the tool the mold casts, so the
# geometry is derived from the tool sprite rather than modelled by hand - never hand-edit the
# output, re-run this instead.
#
# The pocket footprint is the sprite's opaque-pixel silhouette (assets/textures/items/mold_*.png)
# scaled onto the inner cells of the model's 16x16 top grid. Those sprites are *input only*: no
# JSON references them, because a block's inventory icon is rendered from its baked model by
# BlockCubeCache, not from an item sprite. The slab is then decomposed into box elements by a
# greedy row-major rectangle merge - rim cells at full height, pocket cells at the reduced floor
# height - which keeps the quad count in the low hundreds instead of one box per cell.
#
# Conventions that must not drift:
#   * A "cullface" culls the quad against the WORLD neighbour of the cell, never against a sibling
#     element, so only a face that actually sits on the cell boundary may carry one. The pocket
#     walls stand inside the cell and are therefore emitted without a cullface, which is what makes
#     them render; a seam buried between two rim rectangles is omitted altogether.
#   * No "uv" is written. ModelBaker.DefaultUv crops the tile from the box extents, and the engine
#     crops partial-block textures rather than stretching them.
#   * The block JSON's hitbox max Y must equal $SlabHeight/16; MoldTests pins that tie.
#
# The output is a pure function of the input pixels - no noise, no randomness - so re-running
# reproduces each model byte for byte.
#
# Run from the repo root: powershell -File tools/generate-mold-models.ps1
Add-Type -AssemblyName System.Drawing

$root      = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$blocksDir = Join-Path $root 'OpenGLTest\assets\textures\blocks'
$itemsDir  = Join-Path $root 'OpenGLTest\assets\textures\items'
$modelsDir = Join-Path $root 'OpenGLTest\assets\models\block'

# Slab height in model units of 16, i.e. a quarter block. Set to 2 for a 1/32 slab; the hitbox
# "max" Y in every assets/data/blocks/*mold*.json must then be changed to match ($SlabHeight/16).
$SlabHeight = 4
# How deep the pocket is sunk into the slab top. A floor has to remain under it.
$PocketDepth = [int]($SlabHeight / 2)
if (($SlabHeight - $PocketDepth) -lt 1) { throw "PocketDepth $PocketDepth leaves no floor under a $SlabHeight-unit slab." }
# Cells of untouched rim on every side, so the pocket lives in the inner 12x12 and no pocket wall
# ever lands on the cell boundary (where it would need - and wrongly get - a cullface).
$RimCells = 2
# A sprite texel counts as part of the silhouette at or above this alpha.
$AlphaThreshold = 128
# The model grid: Minecraft models measure 0..16 across a block.
$Grid = 16
# $RimCells and $AlphaThreshold are the two knobs that trade pocket detail for quads: a wider rim
# or a higher threshold shrinks the silhouette and merges more cells into fewer rectangles.

# Placeholder art, copied only when it is missing so a real replacement is never clobbered.
# fired_mud.png belongs in textures/blocks/ and must NEVER be dropped into textures/blocks_material/:
# that folder is index-parallel to the atlas and globbed unfiltered, so a stray file there renumbers
# every material slice after it.
function Copy-IfMissing([string]$src, [string]$dst) {
    if (-not (Test-Path $src)) { throw "Source texture '$src' not found." }
    if (Test-Path $dst) { return }
    [System.IO.File]::Copy($src, $dst)
    Write-Output "wrote $dst"
}

# Reads a 32x32 mold sprite and returns its silhouette as a bool[$Grid,$Grid] indexed [x,z]:
# a sprite column runs along +X and a sprite row along +Z. The silhouette's bounding box is scaled
# by one uniform factor (so the shape keeps its aspect) to fill the inner $Grid-2*$RimCells cells
# and centred on the block, and a cell is part of the pocket when any opaque texel falls in it.
function Get-PocketMask([string]$spritePath) {
    if (-not (Test-Path $spritePath)) { throw "Mold sprite '$spritePath' not found." }
    $bmp = New-Object System.Drawing.Bitmap $spritePath
    try {
        if ($bmp.Width -ne 32 -or $bmp.Height -ne 32) {
            throw "Mold sprite '$spritePath' is $($bmp.Width)x$($bmp.Height); expected 32x32."
        }

        $opaque = New-Object 'bool[,]' 32, 32
        $minX = 32; $maxX = -1; $minY = 32; $maxY = -1
        for ($ty = 0; $ty -lt 32; $ty++) {
            for ($tx = 0; $tx -lt 32; $tx++) {
                if ($bmp.GetPixel($tx, $ty).A -lt $AlphaThreshold) { continue }
                $opaque[$tx, $ty] = $true
                if ($tx -lt $minX) { $minX = $tx }
                if ($tx -gt $maxX) { $maxX = $tx }
                if ($ty -lt $minY) { $minY = $ty }
                if ($ty -gt $maxY) { $maxY = $ty }
            }
        }
        if ($maxX -lt 0) { throw "Mold sprite '$spritePath' has no texel at or above alpha $AlphaThreshold." }

        $bw = $maxX - $minX + 1
        $bh = $maxY - $minY + 1
        $inner = $Grid - 2 * $RimCells
        $scale = [Math]::Max($bw, $bh) / [double]$inner   # sprite texels per model cell
        $cx = ($minX + $maxX + 1) / 2.0
        $cy = ($minY + $maxY + 1) / 2.0
        $half = $Grid / 2.0

        $mask = New-Object 'bool[,]' $Grid, $Grid
        for ($ty = 0; $ty -lt 32; $ty++) {
            for ($tx = 0; $tx -lt 32; $tx++) {
                if (-not $opaque[$tx, $ty]) { continue }
                # Which cell's source rect this texel's centre falls into.
                $i = [int][Math]::Floor(($tx + 0.5 - $cx) / $scale + $half)
                $k = [int][Math]::Floor(($ty + 0.5 - $cy) / $scale + $half)
                if ($i -lt 0 -or $i -ge $Grid -or $k -lt 0 -or $k -ge $Grid) { continue }
                $mask[$i, $k] = $true
            }
        }

        # The rim invariant the cullface rule rests on: a pocket cell may never touch the boundary.
        for ($k = 0; $k -lt $Grid; $k++) {
            for ($i = 0; $i -lt $Grid; $i++) {
                if (-not $mask[$i, $k]) { continue }
                if ($i -lt $RimCells -or $i -ge ($Grid - $RimCells) -or
                    $k -lt $RimCells -or $k -ge ($Grid - $RimCells)) {
                    throw "Mold sprite '$spritePath' spills its pocket into the $RimCells-cell rim at ($i, $k)."
                }
            }
        }

        # Without -NoEnumerate PowerShell would unroll the 2D array into 256 booleans.
        Write-Output -NoEnumerate $mask
    }
    finally { $bmp.Dispose() }
}

# Greedy row-major merge of every cell matching $wantPocket into maximal rectangles
# (X1/Z1 exclusive). Deterministic: the scan order alone decides the decomposition. The list is
# returned unwrapped, so every call site wraps it in @(...) and a single rectangle still arrives
# as a one-element array.
function Merge-Rects($mask, [bool]$wantPocket) {
    $claimed = New-Object 'bool[,]' $Grid, $Grid
    $rects = New-Object System.Collections.Generic.List[object]
    for ($z = 0; $z -lt $Grid; $z++) {
        for ($x = 0; $x -lt $Grid; $x++) {
            if ($claimed[$x, $z] -or $mask[$x, $z] -ne $wantPocket) { continue }

            # Widen along x, then grow whole rows along z while the full span still matches.
            $x1 = $x
            while ($x1 -lt $Grid -and -not $claimed[$x1, $z] -and $mask[$x1, $z] -eq $wantPocket) { $x1++ }

            $z1 = $z + 1
            while ($z1 -lt $Grid) {
                $rowMatches = $true
                for ($xx = $x; $xx -lt $x1; $xx++) {
                    if ($claimed[$xx, $z1] -or $mask[$xx, $z1] -ne $wantPocket) { $rowMatches = $false; break }
                }
                if (-not $rowMatches) { break }
                $z1++
            }

            for ($zz = $z; $zz -lt $z1; $zz++) {
                for ($xx = $x; $xx -lt $x1; $xx++) { $claimed[$xx, $zz] = $true }
            }
            $rects.Add(@{ X0 = $x; Z0 = $z; X1 = $x1; Z1 = $z1 })
        }
    }
    return $rects
}

function Test-Pocket($mask, [int]$x, [int]$z) {
    if ($x -lt 0 -or $x -ge $Grid -or $z -lt 0 -or $z -ge $Grid) { return $false }
    return $mask[$x, $z]
}

# True when any cell directly across the given edge of the rectangle is a pocket cell, i.e. this
# side faces into the pocket and has to be drawn as a wall.
function Test-FacesPocket($mask, $rect, [string]$face) {
    switch ($face) {
        'north' { for ($x = $rect.X0; $x -lt $rect.X1; $x++) { if (Test-Pocket $mask $x ($rect.Z0 - 1)) { return $true } } }
        'south' { for ($x = $rect.X0; $x -lt $rect.X1; $x++) { if (Test-Pocket $mask $x $rect.Z1) { return $true } } }
        'west'  { for ($z = $rect.Z0; $z -lt $rect.Z1; $z++) { if (Test-Pocket $mask ($rect.X0 - 1) $z) { return $true } } }
        'east'  { for ($z = $rect.Z0; $z -lt $rect.Z1; $z++) { if (Test-Pocket $mask $rect.X1 $z) { return $true } } }
    }
    return $false
}

function Format-Face([string]$name, [string]$cullface) {
    if ($cullface) { return "        ""$name"": { ""texture"": ""#mold"", ""cullface"": ""$cullface"" }" }
    return "        ""$name"": { ""texture"": ""#mold"" }"
}

function Format-Element([int[]]$from, [int[]]$to, [string[]]$faces) {
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("    {")
    $lines.Add("      ""from"": [$($from[0]), $($from[1]), $($from[2])],")
    $lines.Add("      ""to"": [$($to[0]), $($to[1]), $($to[2])],")
    $lines.Add("      ""faces"": {")
    $lines.Add(($faces -join ",`n"))
    $lines.Add("      }")
    $lines.Add("    }")
    return ($lines -join "`n")
}

# Builds the whole model text for one mask: rim rectangles at full height carrying the boundary
# cullfaces and the pocket walls, floor rectangles sunk by $PocketDepth under the pocket.
function Build-Model($mask, [string]$tile) {
    $elements = New-Object System.Collections.Generic.List[string]
    $faceCount = 0

    foreach ($rect in @(Merge-Rects $mask $false)) {
        $faces = New-Object System.Collections.Generic.List[string]
        $faces.Add((Format-Face 'down' 'down'))
        $faces.Add((Format-Face 'up' ''))
        # north is the Z0 edge (-Z), south the Z1 edge (+Z), west X0 (-X), east X1 (+X). A side on
        # the cell boundary carries its cullface; a side facing the pocket is drawn unculled; a side
        # buried against another rim rectangle is dropped.
        $sides = @(
            @{ Name = 'north'; OnBoundary = ($rect.Z0 -eq 0) },
            @{ Name = 'south'; OnBoundary = ($rect.Z1 -eq $Grid) },
            @{ Name = 'west';  OnBoundary = ($rect.X0 -eq 0) },
            @{ Name = 'east';  OnBoundary = ($rect.X1 -eq $Grid) }
        )
        foreach ($side in $sides) {
            if ($side.OnBoundary) { $faces.Add((Format-Face $side.Name $side.Name)) }
            elseif (Test-FacesPocket $mask $rect $side.Name) { $faces.Add((Format-Face $side.Name '')) }
        }
        $faceCount += $faces.Count
        $elements.Add((Format-Element @($rect.X0, 0, $rect.Z0) @($rect.X1, $SlabHeight, $rect.Z1) $faces.ToArray()))
    }

    foreach ($rect in @(Merge-Rects $mask $true)) {
        # The floor's own sides are buried in the surrounding rim or coplanar with a sibling floor.
        $faces = @((Format-Face 'down' 'down'), (Format-Face 'up' ''))
        $faceCount += $faces.Count
        $elements.Add((Format-Element @($rect.X0, 0, $rect.Z0) @($rect.X1, ($SlabHeight - $PocketDepth), $rect.Z1) $faces))
    }

    $text = "{`n  ""textures"": {`n    ""mold"": ""$tile""`n  },`n  ""elements"": [`n" +
            ($elements -join ",`n") + "`n  ]`n}`n"
    return @{ Text = $text; Elements = $elements.Count; Faces = $faceCount }
}

Copy-IfMissing (Join-Path $blocksDir 'stone.png')         (Join-Path $blocksDir 'fired_mud.png')
Copy-IfMissing (Join-Path $itemsDir 'copper_pickaxe.png') (Join-Path $itemsDir 'mold_pickaxe.png')
Copy-IfMissing (Join-Path $itemsDir 'copper_axe.png')     (Join-Path $itemsDir 'mold_axe.png')
Copy-IfMissing (Join-Path $itemsDir 'copper_ingot.png')  (Join-Path $itemsDir 'mold_ingot.png')

# model name -> the sprite whose silhouette carves it and the atlas tile it wears. A fired mold is
# the same geometry as its raw twin in a different tile, so both read the same mask.
$models = @(
    @{ Model = 'mold_pickaxe';       Sprite = 'mold_pickaxe'; Tile = 'mud' },
    @{ Model = 'mold_axe';           Sprite = 'mold_axe';     Tile = 'mud' },
    @{ Model = 'mold_ingot';         Sprite = 'mold_ingot';   Tile = 'mud' },
    @{ Model = 'mold_sword';         Sprite = 'mold_sword';   Tile = 'mud' },
    @{ Model = 'fired_mold_pickaxe'; Sprite = 'mold_pickaxe'; Tile = 'fired_mud' },
    @{ Model = 'fired_mold_axe';     Sprite = 'mold_axe';     Tile = 'fired_mud' },
    @{ Model = 'fired_mold_ingot';   Sprite = 'mold_ingot';   Tile = 'fired_mud' },
    @{ Model = 'fired_mold_sword';   Sprite = 'mold_sword';   Tile = 'fired_mud' }
)

$masks = @{}
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
foreach ($entry in $models) {
    if (-not $masks.ContainsKey($entry.Sprite)) {
        $masks[$entry.Sprite] = Get-PocketMask (Join-Path $itemsDir "$($entry.Sprite).png")
    }
    $built = Build-Model $masks[$entry.Sprite] $entry.Tile
    $path = Join-Path $modelsDir "$($entry.Model).json"
    [System.IO.File]::WriteAllText($path, $built.Text, $utf8NoBom)
    Write-Output "wrote $path"
    Write-Output "$($entry.Model): elements=$($built.Elements) faces=$($built.Faces)"
}
