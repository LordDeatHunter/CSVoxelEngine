"""Deterministic generator of the cotton art.

Writes two 32x32 RGBA tiles (the size of every other block tile and item sprite):
  OpenGLTest/assets/textures/blocks/cotton_plant.png  the cross tile of the cotton_plant block: a
      small bush of green stems and lobed leaves carrying open white bolls in brown calyxes,
      transparent elsewhere, rooted at the bottom edge like the flower and tall grass tiles;
  OpenGLTest/assets/textures/items/cotton.png         the cotton item: one fluffy white boll of
      four locks sitting in a brown star-shaped calyx over a stub of green stem.

The palette is taken from the existing art (the flower's greens and teal outline, the snowball's
whites and gray rim, the mudball's browns), and every shape is drawn in code with an outline pass
that rims each material in its own dark color, the way the hand-made sprites are rimmed.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces both files byte for byte. Run from anywhere:
    python -I tools/generate-cotton-textures.py
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCK_PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "cotton_plant.png")
ITEM_PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items", "cotton.png")

SIZE = 32

# Materials: each fill color belongs to one material, and the outline pass rims a material with
# its own dark color.
GREEN, FLUFF, CALYX = "green", "fluff", "calyx"
OUTLINE = {
    GREEN: (0x04, 0x4C, 0x4C),   # the flower's teal rim
    FLUFF: (0xA4, 0xAD, 0xAF),   # the snowball's gray rim
    CALYX: (0x3B, 0x29, 0x21),   # the mudball's darkest brown
}
# Outline priority where two materials meet the same empty pixel: the fluff reads best rimmed gray
PRIORITY = (FLUFF, CALYX, GREEN)

LEAF_DARK = (0x1A, 0x75, 0x46)
LEAF_MID = (0x42, 0xA2, 0x3D)
LEAF_LIGHT = (0x7A, 0xCE, 0x4A)
LEAF_HIGHLIGHT = (0xAF, 0xEF, 0x5A)
FLUFF_WHITE = (0xF6, 0xF7, 0xF7)
FLUFF_LIGHT = (0xE9, 0xEC, 0xEC)
FLUFF_SHADE = (0xD8, 0xDE, 0xDF)
FLUFF_CREASE = (0xC2, 0xC9, 0xCB)  # between the snowball's shade and its rim
CALYX_DARK = (0x4C, 0x33, 0x24)
CALYX_MID = (0x71, 0x53, 0x33)
CALYX_LIGHT = (0x96, 0x76, 0x4F)


class Canvas:
    """A 32x32 grid of (rgb, material) or None for transparent."""

    def __init__(self):
        self.px = [[None] * SIZE for _ in range(SIZE)]

    def set(self, x, y, rgb, material):
        if 0 <= x < SIZE and 0 <= y < SIZE:
            self.px[y][x] = (rgb, material)

    def get(self, x, y):
        if 0 <= x < SIZE and 0 <= y < SIZE:
            return self.px[y][x]
        return None

    def outline(self):
        """Rims every filled region: each empty pixel 4-adjacent to a fill takes the darkest
        outline among its neighbors' materials (by PRIORITY)."""
        rim = []
        for y in range(SIZE):
            for x in range(SIZE):
                if self.px[y][x] is not None:
                    continue
                near = set()
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    p = self.get(x + dx, y + dy)
                    if p is not None and p[1] in OUTLINE:
                        near.add(p[1])
                for material in PRIORITY:
                    if material in near:
                        rim.append((x, y, OUTLINE[material]))
                        break
        for x, y, rgb in rim:
            self.px[y][x] = (rgb, "rim")

    def rows(self):
        out = []
        for y in range(SIZE):
            row = bytearray()
            for x in range(SIZE):
                p = self.px[y][x]
                if p is None:
                    row += bytes((0, 0, 0, 0))
                else:
                    row += bytes(p[0]) + b"\xff"
            out.append(bytes(row))
        return out


def write_png(path, rows):
    """Minimal RGBA8 PNG: IHDR, one IDAT (filter 0 on every row, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + r for r in rows)
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def line(c, x0, y0, x1, y1, rgb, material=GREEN):
    """Bresenham line, inclusive."""
    dx, dy = abs(x1 - x0), -abs(y1 - y0)
    sx = 1 if x0 < x1 else -1
    sy = 1 if y0 < y1 else -1
    err = dx + dy
    while True:
        c.set(x0, y0, rgb, material)
        if x0 == x1 and y0 == y1:
            return
        e2 = 2 * err
        if e2 >= dy:
            err += dy
            x0 += sx
        if e2 <= dx:
            err += dx
            y0 += sy


def stamp(c, x0, y0, art, colors):
    """Stamps an ASCII sprite: each character maps to (rgb, material), '.' is left alone."""
    for dy, row in enumerate(art):
        for dx, ch in enumerate(row):
            if ch != ".":
                rgb, material = colors[ch]
                c.set(x0 + dx, y0 + dy, rgb, material)


LEAF_COLORS = {
    "d": (LEAF_DARK, GREEN),
    "g": (LEAF_MID, GREEN),
    "l": (LEAF_LIGHT, GREEN),
    "h": (LEAF_HIGHLIGHT, GREEN),
}
BOLL_COLORS = {
    "W": (FLUFF_WHITE, FLUFF),
    "w": (FLUFF_LIGHT, FLUFF),
    "s": (FLUFF_SHADE, FLUFF),
    "b": (CALYX_DARK, CALYX),
    "B": (CALYX_MID, CALYX),
    "c": (CALYX_LIGHT, CALYX),
}

# A cotton leaf: three lobes, lit from the top left, the vein dark down the middle.
LEAF_LEFT = [
    "hl..l.",
    "lglgl.",
    ".gggdg",
    "..gdgd",
    "...dd.",
]
LEAF_RIGHT = [row[::-1] for row in LEAF_LEFT]

# An open boll: a white puff of locks split by shade creases, sitting in a brown calyx cup whose
# points wrap up its sides and which narrows into the stem.
BOLL = [
    ".wWWw.",
    "wWWsWw",
    "WWsWWs",
    "sWWWss",
    "bssssb",
    ".bBcb.",
    "..bb..",
]


def cotton_plant():
    c = Canvas()

    # Main stem, two pixels wide and lit on its left, from the ground up to the top boll
    for y in range(9, SIZE):
        c.set(15, y, LEAF_LIGHT, GREEN)
        c.set(16, y, LEAF_MID, GREEN)

    # Branches out to the side bolls, then two low shoots for the lower leaves
    line(c, 15, 21, 8, 14, LEAF_MID)
    line(c, 16, 19, 23, 12, LEAF_MID)
    line(c, 15, 27, 6, 22, LEAF_MID)
    line(c, 16, 25, 25, 20, LEAF_MID)

    # Leaves along the stems
    stamp(c, 9, 25, LEAF_LEFT, LEAF_COLORS)
    stamp(c, 18, 26, LEAF_RIGHT, LEAF_COLORS)
    stamp(c, 10, 16, LEAF_LEFT, LEAF_COLORS)
    stamp(c, 18, 14, LEAF_RIGHT, LEAF_COLORS)

    # Bolls at the branch tips, the top one over the main stem
    stamp(c, 13, 2, BOLL, BOLL_COLORS)
    stamp(c, 5, 8, BOLL, BOLL_COLORS)
    stamp(c, 21, 6, BOLL, BOLL_COLORS)
    stamp(c, 3, 16, BOLL, BOLL_COLORS)
    stamp(c, 23, 14, BOLL, BOLL_COLORS)

    c.outline()
    return c


def bract(c, ox, oy, angle_deg, length, base_half):
    """One pointed calyx bract: a triangle from (ox, oy) along angle_deg (0 = right, 90 = up),
    base_half wide at the root and tapering to a point; its upper-left edge catches the light."""
    a = math.radians(angle_deg)
    ux, uy = math.cos(a), -math.sin(a)
    for y in range(SIZE):
        for x in range(SIZE):
            rx, ry = x + 0.5 - ox, y + 0.5 - oy
            along = rx * ux + ry * uy
            perp = rx * -uy + ry * ux
            if along < 0 or along > length:
                continue
            half = base_half * (1.0 - along / length)
            if abs(perp) > half + 0.25:
                continue
            # The side whose normal points up-left is lit
            lit_side = (-uy * -0.6 + ux * -0.8) * (1 if perp > 0 else -1) > 0
            if abs(perp) < 0.6 and along < length * 0.55:
                rgb = CALYX_LIGHT
            elif lit_side:
                rgb = CALYX_MID
            else:
                rgb = CALYX_DARK
            c.set(x, y, rgb, CALYX)


def cotton_item():
    c = Canvas()

    # Stem stub, then the calyx's star of pointed bracts; both first, so the fluff covers their roots
    for y in range(24, 30):
        c.set(15, y, LEAF_LIGHT, GREEN)
        c.set(16, y, LEAF_MID, GREEN)
    for angle_deg in (165, 210, 252, 288, 330, 15):
        bract(c, 16.0, 16.0, angle_deg, 13.5, 2.6)

    # Five locks of uneven size, painted back to front: each is a disc lit from the top left and
    # shaded round its lower-right rim, so where a front lock overlaps a back one its bright edge
    # meets the other's shadow in a curved crease
    locks = (
        (19.5, 10.5, 5.6),   # back right
        (11.5, 11.5, 5.4),   # back left
        (9.5, 18.0, 4.6),    # front left
        (22.0, 17.5, 4.8),   # front right
        (15.5, 18.5, 5.3),   # front center
    )
    light = (-0.6, -0.8)
    for lx, ly, r in locks:
        for y in range(SIZE):
            for x in range(SIZE):
                nx = (x + 0.5 - lx) / r
                ny = (y + 0.5 - ly) / r
                rel = math.hypot(nx, ny)
                if rel > 1.0:
                    continue
                lit = -(nx * light[0] + ny * light[1])
                if rel > 0.78 and lit < -0.2:
                    rgb = FLUFF_CREASE
                elif lit < -0.35:
                    rgb = FLUFF_SHADE
                elif lit > 0.4 and rel < 0.72:
                    rgb = FLUFF_WHITE
                else:
                    rgb = FLUFF_LIGHT
                c.set(x, y, rgb, FLUFF)

    c.outline()
    return c


def main():
    write_png(BLOCK_PATH, cotton_plant().rows())
    write_png(ITEM_PATH, cotton_item().rows())
    print("wrote", os.path.relpath(BLOCK_PATH, ROOT))
    print("wrote", os.path.relpath(ITEM_PATH, ROOT))


if __name__ == "__main__":
    main()
