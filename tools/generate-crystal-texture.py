"""Deterministic generator of the crystal tile (OpenGLTest/assets/textures/blocks/crystal_block.png).

A 32x32 RGBA tile, fully opaque: dark rock shot through with pale cyan crystal facets, each facet a
hashed parallelogram with a lighter edge. Tileable on both axes (the facets wrap). No randomness
and no image library: the PNG is encoded here with zlib at a fixed level and filter 0, so re-running
reproduces the file byte for byte. Run from anywhere:
    python -I tools/generate-crystal-texture.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "crystal_block.png")

SIZE = 32
ROCK = (0x2E, 0x38, 0x42)
ROCK_LIGHT = (0x3A, 0x46, 0x52)
CRYSTAL = (0x8E, 0xD8, 0xE6)
CRYSTAL_LIGHT = (0xC6, 0xF2, 0xFA)
CRYSTAL_DARK = (0x5A, 0xA8, 0xBE)


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def texel(x, y):
    # The facets: a coarse grid of hashed cells, each drawing a diagonal band across it
    cell_x, in_x = divmod(x, 8)
    cell_y, in_y = divmod(y, 8)
    h = hash2(cell_x, cell_y)
    band = (in_x + in_y) % 8
    if h % 3 == 0 and band in (2, 3, 4):
        if band == 2:
            return CRYSTAL_LIGHT
        return CRYSTAL if band == 3 else CRYSTAL_DARK
    # The rock between the facets, with a faint speckle
    return ROCK_LIGHT if hash2(x, y) % 13 == 0 else ROCK


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
