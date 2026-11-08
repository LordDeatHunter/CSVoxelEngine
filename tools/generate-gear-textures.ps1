# Deterministic placeholder sprites of the first gear (assets/textures/items/<name>.png, 16x16): a leather, a bronze
# and an iron armor set (cap, tunic, pants, boots, gloves), the cloud boots (the iron boots in a pale cloud palette
# with two wing puffs) and the accessories (ring, charms, amulet, belt), including the loot-only ability charms
# (feather, gill, frost, fire, magnet: one gold pendant with a glyph each). Plain
# filled rectangles in a material's three tones, so a re-run is byte-identical; replace with real art any time,
# the item JSON only names the files. Run: pwsh tools/generate-gear-textures.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\OpenGLTest\assets\textures\items'

function Tone([int[]]$rgb) { [System.Drawing.Color]::FromArgb(255, $rgb[0], $rgb[1], $rgb[2]) }

# dark outline, body, highlight
$materials = @{
    leather = @{ Dark = (Tone 70, 44, 26);  Mid = (Tone 138, 92, 54);  Light = (Tone 182, 130, 82) }
    bronze  = @{ Dark = (Tone 92, 56, 24);  Mid = (Tone 190, 124, 52); Light = (Tone 236, 178, 96) }
    iron    = @{ Dark = (Tone 40, 44, 54);  Mid = (Tone 128, 136, 150); Light = (Tone 206, 214, 226) }
    gem     = @{ Dark = (Tone 30, 70, 120); Mid = (Tone 70, 150, 220); Light = (Tone 180, 226, 255) }
    ember   = @{ Dark = (Tone 110, 20, 10); Mid = (Tone 220, 70, 30); Light = (Tone 255, 170, 100) }
    gold    = @{ Dark = (Tone 120, 84, 12); Mid = (Tone 224, 176, 40); Light = (Tone 255, 230, 120) }
    cloud   = @{ Dark = (Tone 96, 120, 150); Mid = (Tone 214, 226, 240); Light = (Tone 250, 252, 255) }
    aqua    = @{ Dark = (Tone 16, 78, 70);  Mid = (Tone 40, 160, 140); Light = (Tone 140, 230, 200) }
    frost   = @{ Dark = (Tone 90, 140, 170); Mid = (Tone 170, 214, 236); Light = (Tone 236, 250, 255) }
    magnet  = @{ Dark = (Tone 100, 16, 20); Mid = (Tone 200, 40, 44); Light = (Tone 250, 120, 110) }
}

function New-Sprite([string]$name, [scriptblock]$draw) {
    $bmp = New-Object System.Drawing.Bitmap 16, 16, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $script:bmp = $bmp
    & $draw
    $bmp.Save((Join-Path $out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Px([int]$x, [int]$y, $color) { $script:bmp.SetPixel($x, $y, $color) }
function Rect([int]$x, [int]$y, [int]$w, [int]$h, $color) {
    for ($j = $y; $j -lt $y + $h; $j++) { for ($i = $x; $i -lt $x + $w; $i++) { Px $i $j $color } }
}
# an outlined block: dark rim, mid body, light top edge
function Block([int]$x, [int]$y, [int]$w, [int]$h, $m) {
    Rect $x $y $w $h $m.Dark
    if ($w -gt 2 -and $h -gt 2) {
        Rect ($x + 1) ($y + 1) ($w - 2) ($h - 2) $m.Mid
        Rect ($x + 1) ($y + 1) ($w - 2) 1 $m.Light
    }
}

foreach ($name in 'leather', 'bronze', 'iron') {
    $m = $materials[$name]
    New-Sprite "${name}_cap" { Block 3 4 10 7 $m; Block 2 9 12 3 $m; Rect 6 11 4 2 ([System.Drawing.Color]::Transparent) }
    New-Sprite "${name}_tunic" {
        Block 5 2 6 12 $m; Block 1 2 4 6 $m; Block 11 2 4 6 $m
        Rect 7 2 2 2 ([System.Drawing.Color]::Transparent)
    }
    New-Sprite "${name}_pants" { Block 3 2 10 4 $m; Block 3 6 4 8 $m; Block 9 6 4 8 $m }
    New-Sprite "${name}_boots" { Block 2 5 5 8 $m; Block 9 5 5 8 $m; Block 1 11 6 3 $m; Block 9 11 6 3 $m }
    New-Sprite "${name}_gloves" { Block 2 3 5 10 $m; Block 9 3 5 10 $m; Block 1 8 3 3 $m; Block 12 8 3 3 $m }
}

# The iron boots' shape in the cloud palette, plus a small wing puff on each side
New-Sprite 'cloud_boots' {
    $c = $materials.cloud
    Block 2 5 5 8 $c; Block 9 5 5 8 $c; Block 1 11 6 3 $c; Block 9 11 6 3 $c
    Rect 0 6 2 2 $c.Light; Rect 14 6 2 2 $c.Light
}

New-Sprite 'copper_ring' {
    $g = $materials.gold; $e = $materials.gem
    Block 4 6 8 8 $g
    Rect 6 8 4 4 ([System.Drawing.Color]::Transparent)
    Block 6 3 4 4 $e
}
New-Sprite 'tin_charm' {
    $g = $materials.gold; $e = $materials.gem
    Rect 5 1 1 5 $g.Dark; Rect 10 1 1 5 $g.Dark; Rect 6 5 4 1 $g.Dark
    Block 4 6 8 8 $g
    Block 6 8 4 4 $e
}
New-Sprite 'amulet_of_vigor' {
    $g = $materials.gold; $e = $materials.gem
    Rect 3 1 1 3 $g.Mid; Rect 12 1 1 3 $g.Mid; Rect 4 3 1 2 $g.Mid; Rect 11 3 1 2 $g.Mid
    Rect 5 5 6 1 $g.Mid
    Block 3 6 10 9 $g
    Block 5 8 6 5 $e
}
New-Sprite 'ember_charm' {
    $g = $materials.gold; $e = $materials.ember
    Rect 5 1 1 5 $g.Dark; Rect 10 1 1 5 $g.Dark; Rect 6 5 4 1 $g.Dark
    Block 4 6 8 8 $g
    Block 6 8 4 4 $e
}

# The AC-4 ability charms: the same cord and gold pendant, each with its own glyph inset
function Pendant {
    $g = $materials.gold
    Rect 5 1 1 5 $g.Dark; Rect 10 1 1 5 $g.Dark; Rect 6 5 4 1 $g.Dark
    Block 4 6 8 8 $g
}
New-Sprite 'feather_charm' {
    $c = $materials.cloud
    Pendant
    Px 9 8 $c.Light; Px 8 9 $c.Light; Px 7 10 $c.Light; Px 6 11 $c.Dark; Px 5 12 $c.Dark
    Px 8 8 $c.Mid; Px 9 9 $c.Mid; Px 7 9 $c.Mid; Px 8 10 $c.Mid; Px 6 10 $c.Mid; Px 7 11 $c.Mid
}
New-Sprite 'gill_charm' {
    $a = $materials.aqua
    Pendant
    Block 5 7 6 6 $a
    Rect 6 9 4 1 $a.Dark; Rect 6 11 4 1 $a.Dark
}
New-Sprite 'frost_charm' {
    $f = $materials.frost
    Pendant
    Rect 7 7 2 6 $f.Mid; Rect 5 9 6 2 $f.Mid
    Rect 7 9 2 2 $f.Light
    Px 5 7 $f.Dark; Px 10 7 $f.Dark; Px 5 12 $f.Dark; Px 10 12 $f.Dark
}
New-Sprite 'fire_charm' {
    $e = $materials.ember
    Pendant
    Px 7 7 $e.Mid; Rect 7 8 2 1 $e.Mid; Rect 6 9 4 2 $e.Mid; Rect 5 11 6 2 $e.Dark
    Px 7 9 $e.Light; Rect 7 10 2 2 $e.Light
}
New-Sprite 'magnet_charm' {
    $r = $materials.magnet; $i = $materials.iron
    Pendant
    Rect 5 8 2 4 $r.Mid; Rect 9 8 2 4 $r.Mid; Rect 5 11 6 2 $r.Dark; Rect 7 11 2 1 $r.Mid
    Rect 5 7 2 1 $i.Light; Rect 9 7 2 1 $i.Light
}

New-Sprite 'miners_belt' {
    $l = $materials.leather; $g = $materials.gold
    Block 1 5 14 6 $l
    Block 6 4 4 8 $g
    Rect 7 6 2 4 $l.Dark
}
