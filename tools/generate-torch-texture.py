"""Deterministic generator of the torch tile (OpenGLTest/assets/textures/blocks/torch.png).

Minecraft-style torch art on the 32x32 atlas tile: a 4-texel-wide wooden stick (2 px in the 0..16
model units the torch models use) standing in columns 14..17, rows 12..31, with a bright flame on
its top four rows and a charred band under the flame. Every other texel is fully transparent, which
is why the block is cutout. The models (assets/models/block/torch.json and wall_torch.json) crop
their faces out of exactly that stick:

    sides  uv [7, 6, 9, 16]   -> columns 14..17, rows 12..31 (flame on top, wood below)
    top    uv [7, 6, 9, 8]    -> the flame
    bottom uv [7, 14, 9, 16]  -> the bottom of the stick

so moving the stick here means moving those uvs too.

A second tile, torch_top.png (32x32, fully opaque), is the burnt end of the stick seen straight
down: char with a faint ember still glowing at the center. The models' `up` faces sample it with
[0, 0, 16, 16] (the whole tile), so the top never shows the side crop's char band and wood rows.

The output is a pure function of the constants below (no noise, no randomness), so re-running
reproduces the PNG byte for byte. Run from the repo root:  python tools/generate-torch-texture.py
"""
import os

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "torch.png")
TOP_PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "torch_top.png")

SIZE = 32
STICK_COLUMNS = range(14, 18)
FLAME_ROWS = range(12, 16)
CHAR_ROWS = range(16, 18)
WOOD_ROWS = range(18, 32)

# Flame, hottest at the top center: one row per entry, (center two columns, outer two columns)
FLAME = [
    ((255, 251, 222), (255, 230, 132)),
    ((255, 238, 156), (255, 204, 84)),
    ((255, 196, 76), (246, 162, 52)),
    ((233, 134, 42), (206, 106, 32)),
]

CHAR = [(96, 61, 38), (82, 52, 32), (72, 45, 28), (60, 38, 24)]

# The top tile: char, with a faint ember still glowing at the center (radius in texels)
TOP_CHAR = (58, 37, 23)
TOP_EMBER = (208, 104, 40)
TOP_EMBER_RADIUS = 11

# Wood per column, lit side (14) to shaded side (17), so the stick reads round
WOOD = [(152, 108, 60), (134, 94, 51), (119, 83, 45), (99, 69, 37)]


def shade(color, amount):
    return tuple(max(0, min(255, c + amount)) for c in color)


def main():
    image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    pixels = image.load()

    for row_index, row in enumerate(FLAME_ROWS):
        center, outer = FLAME[row_index]
        for column in STICK_COLUMNS:
            rgb = center if column in (15, 16) else outer
            pixels[column, row] = rgb + (255,)

    for row_index, row in enumerate(CHAR_ROWS):
        for column_index, column in enumerate(STICK_COLUMNS):
            rgb = shade(CHAR[column_index], -8 * row_index)
            pixels[column, row] = rgb + (255,)

    for row in WOOD_ROWS:
        for column_index, column in enumerate(STICK_COLUMNS):
            # A faint grain: every third texel down a column darkens, staggered per column
            grain = -12 if (row + column_index) % 3 == 0 else 0
            pixels[column, row] = shade(WOOD[column_index], grain) + (255,)

    image.save(PATH, format="PNG", optimize=False)

    top = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 255))
    top_pixels = top.load()
    for y in range(SIZE):
        for x in range(SIZE):
            dx = x - (SIZE - 1) / 2
            dy = y - (SIZE - 1) / 2
            distance = (dx * dx + dy * dy) ** 0.5
            glow = max(0.0, 1.0 - distance / TOP_EMBER_RADIUS)
            glow = glow * glow * 0.6
            grain = -10 if (x + y) % 5 == 0 else 0
            rgb = tuple(
                max(0, min(255, round(c + (e - c) * glow) + grain))
                for c, e in zip(TOP_CHAR, TOP_EMBER)
            )
            top_pixels[x, y] = rgb + (255,)

    top.save(TOP_PATH, format="PNG", optimize=False)


if __name__ == "__main__":
    main()
