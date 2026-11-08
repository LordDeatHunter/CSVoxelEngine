"""Deterministic generator of the waystone tiles (OpenGLTest/assets/textures/blocks/waystone_<realm>_*.png).

Three 32x32 opaque tiles per realm (surface, underground, hell, sky), each from a small hand-picked palette. The
waystone is two cells tall (block/waystone below, block/waystone_upper above), and one carved rune panel runs up its
obelisk shaft across both:

- waystone_<realm>_stone: dressed stone in 4x4 texel patches of three shades with a few dark specks. It is
  cropped by every box of the two models but the shaft (plinth, step, cap, tip), so it has no border of its own.
- waystone_<realm>_runes: the same stone with the lower part of the rune panel where the lower shaft crops the tile
  (model x 5..11, y 5..16: tile columns 10..21, rows 0..21): a groove, a dark inlay and two glowing glyphs in the
  realm's rune color, which matches the block's light color in its JSON. The panel is open at its top edge.
- waystone_<realm>_runes_upper: the panel's top, where the upper shaft crops the tile (model y 0..10: rows 12..31),
  open at its bottom edge so it meets the lower tile's panel, with a third glyph.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter 0, so
re-running reproduces the files byte for byte. Run from anywhere:
    python -I tools/generate-waystone-textures.py
    python -I tools/generate-waystone-textures.py --check   (compares against the files on disk)
"""
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")

SIZE = 32
PATCH = 4  # texels per stone patch side

# The shaft's crop of the runes tiles (columns, end exclusive), and the rows of each half's part of the panel: the
# groove closes the panel at the upper tile's top row and the lower tile's bottom row, and the two meet at the seam
PANEL_X0, PANEL_X1 = 10, 22
UPPER_PANEL_TOP = 15
LOWER_PANEL_BOTTOM = 19

# Per realm: three stone shades, a speck, the groove, the inlay, the rune and its bright core
PALETTES = {
    "surface": {
        "stone": [(148, 150, 140), (132, 134, 126), (118, 120, 112)],
        "speck": (100, 102, 96), "groove": (84, 86, 82), "inlay": (60, 64, 62),
        "rune": (70, 240, 190), "core": (190, 255, 230),
    },
    "underground": {
        "stone": [(92, 90, 100), (80, 78, 88), (68, 66, 76)],
        "speck": (54, 52, 60), "groove": (44, 42, 50), "inlay": (32, 30, 36),
        "rune": (255, 190, 90), "core": (255, 236, 170),
    },
    "hell": {
        "stone": [(112, 58, 52), (96, 48, 44), (82, 40, 38)],
        "speck": (64, 30, 28), "groove": (52, 24, 22), "inlay": (36, 16, 16),
        "rune": (255, 90, 40), "core": (255, 200, 120),
    },
    "sky": {
        "stone": [(232, 236, 240), (218, 224, 232), (204, 212, 222)],
        "speck": (186, 196, 210), "groove": (160, 170, 186), "inlay": (120, 132, 152),
        "rune": (176, 236, 255), "core": (240, 252, 255),
    },
}

# Glyphs, 6 wide by 5 tall: '#' the rune, '*' its bright core, '.' the inlay. Three per realm, top to bottom: the
# first on the upper tile, the other two on the lower one.
GLYPHS = {
    "surface": [
        ["..#...", ".###..", "#.*.#.", "..#...", ".#.#.."],
        ["..#...", ".#*#..", "#.#.#.", "..#...", "..#..."],
        ["#...#.", ".#.#..", "..*...", ".#.#..", "#...#."],
    ],
    "underground": [
        ["#.##.#", "#....#", "#.**.#", "#....#", "#.##.#"],
        ["####..", "#..#..", "#*##..", "#.....", "#....."],
        [".#..#.", ".#..#.", ".#**#.", ".#..#.", ".#..#."],
    ],
    "hell": [
        ["#...#.", "##.##.", "#.*.#.", "#...#.", "#...#."],
        ["#.#.#.", "#.#.#.", "#*#*#.", "..#...", "..#..."],
        ["..#...", ".###..", "#.*.#.", ".###..", "..#..."],
    ],
    "sky": [
        [".###..", "#...#.", "#.*.#.", "#...#.", ".###.."],
        ["..#...", "..#...", "##*##.", "..#...", "..#..."],
        ["#....#", ".#..#.", "..**..", ".#..#.", "#....#"],
    ],
}

GLYPH_X = 13            # left column of every glyph
UPPER_GLYPH_Y = (21,)   # top row of the upper tile's glyph
LOWER_GLYPH_Y = (3, 11)  # top rows of the lower tile's two glyphs


def hash2(a, b, salt):
    """A small integer hash of two ints and a salt, stable across Python versions."""
    v = (a * 374761393 + b * 668265263 + salt * 2246822519) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def stone(palette, x, y, salt):
    shades = palette["stone"]
    color = shades[hash2(x // PATCH, y // PATCH, salt) % len(shades)]
    if hash2(x, y, salt + 7) % 23 == 0:
        color = palette["speck"]
    return color


def stone_tile(palette, salt):
    return [[stone(palette, x, y, salt) for x in range(SIZE)] for y in range(SIZE)]


def runes_tile(palette, glyphs, glyph_rows, salt, y0, y1, closed_row):
    """The stone tile with rows y0..y1 (end exclusive) of the rune panel carved in: a groove down both sides and
    across closed_row, the inlay inside, and the glyphs at their top rows."""
    pixels = stone_tile(palette, salt)
    for y in range(y0, y1):
        for x in range(PANEL_X0 + 1, PANEL_X1 - 1):
            edge = y == closed_row or x in (PANEL_X0 + 1, PANEL_X1 - 2)
            pixels[y][x] = palette["groove"] if edge else palette["inlay"]
    for glyph, top in zip(glyphs, glyph_rows):
        for gy, line in enumerate(glyph):
            for gx, mark in enumerate(line):
                if mark == "#":
                    pixels[top + gy][GLYPH_X + gx] = palette["rune"]
                elif mark == "*":
                    pixels[top + gy][GLYPH_X + gx] = palette["core"]
    return pixels


def encode_png(pixels):
    """Minimal RGBA8 PNG: IHDR, one IDAT (filter 0 on every row, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + b"".join(bytes(c) + b"\xff" for c in row) for row in pixels)
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    return png


def tiles():
    for salt, (realm, palette) in enumerate(PALETTES.items()):
        yield f"waystone_{realm}_stone.png", encode_png(stone_tile(palette, salt))
        glyphs = GLYPHS[realm]
        yield f"waystone_{realm}_runes.png", encode_png(
            runes_tile(palette, glyphs[1:], LOWER_GLYPH_Y, salt, 0, LOWER_PANEL_BOTTOM + 1, LOWER_PANEL_BOTTOM))
        yield f"waystone_{realm}_runes_upper.png", encode_png(
            runes_tile(palette, glyphs[:1], UPPER_GLYPH_Y, salt, UPPER_PANEL_TOP, SIZE, UPPER_PANEL_TOP))


def main():
    check = "--check" in sys.argv[1:]
    stale = []
    for name, png in tiles():
        path = os.path.join(OUT, name)
        if check:
            try:
                with open(path, "rb") as f:
                    if f.read() == png:
                        continue
            except FileNotFoundError:
                pass
            stale.append(path)
            continue
        with open(path, "wb") as f:
            f.write(png)
        print("wrote", os.path.relpath(path, ROOT))
    if stale:
        for path in stale:
            print("out of date: " + path)
        sys.exit(1)


if __name__ == "__main__":
    main()
