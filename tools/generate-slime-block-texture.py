"""Deterministic generator of the slime block tile (OpenGLTest/assets/textures/blocks/slime_block.png).

A 32x32 opaque tile of green gel: 4x4-texel cells in three shades of slime green, a lighter rim on
every cell so it reads as a soft quilt of blobs, and rare bright glints. No randomness and no image
library: the shades come from a small integer hash of the texel, and the PNG is encoded here with
zlib at a fixed level and filter 0, so re-running reproduces the file byte for byte. Run from
anywhere:  python -I tools/generate-slime-block-texture.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "slime_block.png")

SIZE = 32
CELL = 4                  # texel cells; SIZE must be a multiple of it

BASE = (110, 196, 92)
LIGHT = (134, 216, 112)
DARK = (88, 170, 74)
RIM = (150, 226, 128)     # the top-left edge of every cell
GLINT = (188, 240, 164)


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def texel(x, y):
    cell_x, in_x = divmod(x, CELL)
    cell_y, in_y = divmod(y, CELL)
    if hash2(x, y) % 29 == 0:
        return GLINT
    if in_x == 0 or in_y == 0:
        return RIM
    shade = hash2(cell_x, cell_y) % 3
    if shade == 0:
        return LIGHT
    if shade == 1:
        return DARK
    return BASE


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
