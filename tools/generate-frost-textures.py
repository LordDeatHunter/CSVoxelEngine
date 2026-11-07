"""Deterministic generator of the frost block tiles (frost_grass_top, frost_grass_side, frost_stone).

Pixel art has a small, hand-picked palette, so these are not painted from noise: each tile is the
regular tile (grass_top, grass_side, stone) with every one of its colors swapped one-to-one for an icy
one, so the pixel pattern and the palette size (4, 13 and 5 colors) match the originals exactly.
A source color missing from its map is an error, so a changed source tile cannot slip through.

Run:    python tools/generate-frost-textures.py
Check:  python tools/generate-frost-textures.py --check   (exit code 1 when a committed tile is stale)
"""
import io
import os
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCKS = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")

GRASS = {
    (46, 109, 55): (96, 142, 164),
    (53, 120, 62): (112, 156, 178),
    (60, 132, 70): (132, 174, 198),
    (70, 146, 80): (158, 196, 216),
    (81, 162, 90): (192, 222, 238),
}
# The soil under the frost turf wears the permafrost tile's tones
SOIL = {
    (81, 60, 38): (70, 79, 86),
    (91, 70, 44): (80, 88, 97),
    (100, 77, 49): (88, 97, 106),
    (112, 88, 56): (100, 109, 120),
    (122, 97, 62): (110, 119, 130),
    (68, 68, 68): (74, 86, 102),
    (84, 84, 84): (92, 104, 120),
    (99, 99, 99): (108, 120, 136),
    (111, 111, 111): (122, 134, 150),
}
STONE = {
    (86, 86, 86): (92, 108, 128),
    (101, 101, 101): (108, 124, 144),
    (121, 121, 121): (128, 146, 166),
    (144, 144, 144): (158, 176, 194),
    (156, 156, 156): (180, 198, 214),
}

TILES = [
    ("frost_grass_top", "grass_top", GRASS),
    ("frost_grass_side", "grass_side", {**GRASS, **SOIL}),
    ("frost_stone", "stone", STONE),
]


def render(source, mapping):
    img = Image.open(os.path.join(BLOCKS, source + ".png")).convert("RGBA")
    out = Image.new("RGBA", img.size)
    px = out.load()
    for y in range(img.height):
        for x in range(img.width):
            r, g, b, a = img.getpixel((x, y))
            if a == 0:
                px[x, y] = (0, 0, 0, 0)
                continue
            px[x, y] = mapping[(r, g, b)] + (a,)
    buf = io.BytesIO()
    out.save(buf, "PNG", optimize=False)
    return buf.getvalue()


def main():
    check = "--check" in sys.argv
    stale = False
    for name, source, mapping in TILES:
        data = render(source, mapping)
        path = os.path.join(BLOCKS, name + ".png")
        if check:
            if not os.path.exists(path) or open(path, "rb").read() != data:
                print("stale:", name)
                stale = True
            continue
        with open(path, "wb") as f:
            f.write(data)
        print("wrote", name)
    sys.exit(1 if stale else 0)


main()
