"""Deterministic generator of the oak trapdoor and door tiles.

Writes 32x32 opaque RGBA tiles (the size of every block tile) into OpenGLTest/assets/textures/blocks/:
  oak_trapdoor.png  a square hatch: a dark frame around four vertical boards, a cross brace and
                    two dark slits, in the plank palette
The trapdoor models (assets/models/block/oak_trapdoor*.json) crop their faces out of it with
default (position-derived) uvs, so the whole tile shows on the hatch's broad faces and its edge
rows and columns on the thin sides; every texel is opaque, so the block is drawn without cutout.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces every file byte for byte. Run from anywhere:
    python -I tools/generate-door-textures.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCKS = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")

SIZE = 32

# The plank tile's palette (tools/generate-plank-texture.py), plus a darker frame and a slit
EDGE = (184, 145, 94)
LIGHT = (172, 134, 86)
BASE = (158, 121, 76)
DARK = (139, 104, 64)
SEAM = (88, 62, 39)
FRAME = (118, 86, 52)
FRAME_DARK = (96, 69, 42)
SLIT = (46, 32, 21)


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def board(x, y, salt):
    """Vertical-grain board texel: streaks run down the board."""
    streak = hash2(x + salt * 31, y // 5) % 9
    if streak < 2:
        return DARK
    if streak > 6:
        return LIGHT
    return BASE


def trapdoor(x, y):
    # A three-texel frame, darker on its outer row
    if x in (0, SIZE - 1) or y in (0, SIZE - 1):
        return FRAME_DARK
    if x < 3 or x > SIZE - 4 or y < 3 or y > SIZE - 4:
        return FRAME
    # A cross brace through the middle
    if y in (15, 16):
        return EDGE if y == 15 else SEAM
    # Two dark slits in the upper and lower panels
    if 13 <= x <= 18 and (y in (8, 9) or y in (22, 23)):
        return SLIT
    # Four vertical boards between the frame, a seam between each
    if (x - 3) % 7 == 6:
        return SEAM
    return board(x, y, (x - 3) // 7)


def write_png(path, pixel):
    """Minimal RGBA8 PNG of pixel(x, y): IHDR, one IDAT (filter 0, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    rows = []
    for y in range(SIZE):
        row = bytearray(b"\x00")
        for x in range(SIZE):
            row += bytes(pixel(x, y)) + b"\xff"
        rows.append(bytes(row))
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)
    print("wrote", os.path.relpath(path, ROOT))


def main():
    write_png(os.path.join(BLOCKS, "oak_trapdoor.png"), trapdoor)


if __name__ == "__main__":
    main()
