"""Deterministic generator of the glow mushroom tile (OpenGLTest/assets/textures/blocks/glow_mushroom.png).

A 32x32 RGBA cross tile: a short pale stem under a rounded cyan cap dotted with brighter glow
spots, transparent elsewhere and rooted on the bottom edge like the other plant tiles.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces the file byte for byte. Run from anywhere:
    python -I tools/generate-cave-plants.py
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "glow_mushroom.png")

SIZE = 32
CENTRE = 15.5
CAP_TOP = 12.0
CAP_RADIUS = 8.5
STEM_HALF = 2.0
STEM_TOP = 22.0

STEM = (0xC8, 0xC2, 0xB4)
STEM_SHADE = (0xA8, 0xA2, 0x94)
CAP = (0x3E, 0x9A, 0xB8)
CAP_DARK = (0x2C, 0x76, 0x92)
CAP_LIGHT = (0x62, 0xBE, 0xD6)
GLOW = (0xB6, 0xF2, 0xFF)


def texel(x, y):
    # The cap: a dome over the stem
    dx = x - CENTRE
    dy = y - CAP_TOP
    if dy <= 0.0 and math.hypot(dx, dy) <= CAP_RADIUS:
        if dx * dx + dy * dy >= (CAP_RADIUS - 2.0) ** 2:
            return CAP_DARK
        if dx + dy < -4.0:
            return CAP_LIGHT
        return CAP
    # The glow spots: a few hashed dots on the cap
    if dy <= 0.0 and math.hypot(dx, dy) <= CAP_RADIUS - 1.5 and (x * 7 + y * 13) % 23 == 0:
        return GLOW
    # The stem: a short post under the cap's middle
    if abs(dx) <= STEM_HALF and CAP_TOP <= y <= STEM_TOP:
        return STEM if dx < 1.0 else STEM_SHADE
    return None


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
            colour = texel(x, y)
            row += bytes(colour if colour else (0, 0, 0)) + (b"\xff" if colour else b"\x00")
        rows.append(bytes(row))
    write_png(PATH, rows)
    print("wrote", os.path.relpath(PATH, ROOT))


if __name__ == "__main__":
    main()
