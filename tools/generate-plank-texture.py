"""Deterministic generator of the oak plank tile (OpenGLTest/assets/textures/blocks/oak_planks.png).

A 32x32 opaque tile (the size of every block tile) of four horizontal boards, eight texels high
each: a lit top edge, a warm tan body with horizontal grain streaks, and a dark seam along the
bottom of every board. Each board has one butt joint, staggered from the board above it, so a wall
of planks reads as laid boards rather than stripes. The tile wraps on both axes (the grain is a
function of the texel only), so greedy-merged plank walls tile without a visible edge, and the
slabs, stairs, fence and door crop their faces out of it.

No randomness and no image library: the colours come from a small integer hash of the texel, and
the PNG is encoded here with zlib at a fixed level and filter 0, so re-running reproduces the file
byte for byte. Run from anywhere:
    python -I tools/generate-plank-texture.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "oak_planks.png")

SIZE = 32
BOARD = 8                 # texel rows per board; SIZE must be a multiple of it
JOINTS = (11, 27, 3, 19)  # the butt joint column of each board, staggered board to board

EDGE = (184, 145, 94)     # the lit top row of a board
LIGHT = (172, 134, 86)
BASE = (158, 121, 76)
DARK = (139, 104, 64)
SEAM = (88, 62, 39)       # the gap under a board and the joint's shadow side


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def texel(x, y):
    board, row = divmod(y, BOARD)
    joint = JOINTS[board % len(JOINTS)]
    if row == BOARD - 1:
        return SEAM
    if x == joint:
        return SEAM
    if x == (joint + 1) % SIZE:
        return EDGE
    if row == 0:
        return EDGE
    # Grain runs along the board: a streak's shade is constant over a few texels of x, so it
    # reads as long horizontal fibres, and each board starts its streaks somewhere else
    streak = hash2(y, (x + board * 7) // 5) % 9
    if streak < 2:
        return DARK
    if streak > 6:
        return LIGHT
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
