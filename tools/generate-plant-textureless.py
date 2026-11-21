"""Shades the gray `_textureless` tiles of the climate-tinted ground plants (bush, fern, tall fern).

Each tile is the colored twin's (`<name>.png`) shading carried over to a fixed four-step gray
ramp: the texel's luminance is ranked within the tile and cut at its quartiles, so the darkest
quarter of the leaf takes the darkest gray and the brightest quarter the lightest. The alpha
mask is the twin's exactly. The ramp's mean sits near the old flat 98, so the tinted plant keeps
its brightness against the grass while gaining the contrast the tall grass and grass top have.

Run: python -I tools/generate-plant-textureless.py
"""
from pathlib import Path

from PIL import Image

TILES = ["bush", "fern", "tall_fern_bottom", "tall_fern_top"]
RAMP = [58, 82, 106, 136]
BLOCKS = Path(__file__).resolve().parent.parent / "OpenGLTest" / "assets" / "textures" / "blocks"


def luminance(p):
    return 0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2]


def shade(name):
    src = Image.open(BLOCKS / f"{name}.png").convert("RGBA")
    pixels = list(src.getdata())
    lums = sorted(luminance(p) for p in pixels if p[3] > 0)
    cuts = [lums[len(lums) * q // 4] for q in (1, 2, 3)]
    out = []
    for p in pixels:
        if p[3] == 0:
            out.append((0, 0, 0, 0))
            continue
        step = sum(1 for c in cuts if luminance(p) >= c)
        g = RAMP[step]
        out.append((g, g, g, 255))
    dst = Image.new("RGBA", src.size)
    dst.putdata(out)
    dst.save(BLOCKS / f"{name}_textureless.png")
    colors = {p for p in out if p[3] > 0}
    print(f"{name}_textureless.png: {len(colors)} colors")


for tile in TILES:
    shade(tile)
