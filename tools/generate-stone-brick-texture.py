"""Deterministic generator of the stone brick tile (OpenGLTest/assets/textures/blocks/stone_bricks.png).

A 32x32 opaque tile of gray bricks in a running bond: four 8-texel rows, each brick 16 texels wide
and offset by 8 from the row above, with 2-texel mortar lines and a faint per-brick shade from a
small integer hash. Tileable on both axes (the pattern wraps). No randomness and no image library:
the PNG is encoded here with zlib at a fixed level and filter 0, so re-running reproduces the file
byte for byte. Run from anywhere:  python -I tools/generate-stone-brick-texture.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "stone_bricks.png")

SIZE = 32
ROW = 8                   # texel rows per brick course; SIZE must be a multiple of it
BRICK = 16                # brick length in texels
MORTAR = 2                # mortar thickness

MORTAR_COLOR = (108, 108, 108)
BRICK_SHADES = [(152, 152, 152), (144, 144, 144), (136, 136, 136), (148, 148, 148)]


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def texel(x, y):
    course, row = divmod(y, ROW)
    offset = (course % 2) * (BRICK // 2)
    brick_index = ((x + offset) % SIZE) // BRICK
    in_mortar = row < MORTAR or (x + offset) % BRICK < MORTAR
    if in_mortar:
        return MORTAR_COLOR
    base = BRICK_SHADES[hash2(brick_index, course) % len(BRICK_SHADES)]
    # A faint speckle so a wall of bricks is not perfectly flat
    speck = -6 if hash2(x, y) % 11 == 0 else 0
    return tuple(max(0, min(255, c + speck)) for c in base)


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


def main():
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            row += bytes(texel(x, y)) + b"\xff"
        rows.append(bytes(row))
    write_png(PATH, rows)
    print("wrote", os.path.relpath(PATH, ROOT))


if __name__ == "__main__":
    main()
