"""Deterministic generator of the mana draught sprite (placeholder art).

Writes one 32x32 RGBA sprite, OpenGLTest/assets/textures/items/mana_draught.png: a round-bellied glass
flask with a cork, filled with glowing blue liquid, rimmed in a dark outline like the hand-made sprites.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter 0, so
re-running reproduces the file byte for byte. Run from anywhere:
    python -I tools/generate-mana-draught-texture.py
    python -I tools/generate-mana-draught-texture.py --check   (compares against the file on disk)
"""
import math
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items", "mana_draught.png")

SIZE = 32
OUTLINE = (0x1E, 0x2A, 0x4A)
GLASS = (0xCC, 0xE4, 0xF2)
LIQUID_LIGHT, LIQUID, LIQUID_DARK = (0x9A, 0xE0, 0xFF), (0x3C, 0x8C, 0xE8), (0x24, 0x4C, 0xB0)
CORK, CORK_DARK = (0xB8, 0x8A, 0x56), (0x8A, 0x62, 0x38)

BELLY_CENTER = (16.0, 20.5)
BELLY_RADIUS = 8.5
NECK_HALF = 2.6
NECK_TOP, NECK_BOTTOM = 7.0, 14.0
CORK_TOP, CORK_BOTTOM = 3.0, 7.0
LIQUID_LEVEL = 17.0


def inside_belly(px, py, grow=0.0):
    return math.hypot(px - BELLY_CENTER[0], py - BELLY_CENTER[1]) <= BELLY_RADIUS + grow


def inside_neck(px, py, grow=0.0):
    return abs(px - 16.0) <= NECK_HALF + grow and NECK_TOP - grow <= py <= NECK_BOTTOM + 3.0


def inside_cork(px, py, grow=0.0):
    return abs(px - 16.0) <= NECK_HALF + 0.4 + grow and CORK_TOP - grow <= py <= CORK_BOTTOM + grow


def texel(x, y):
    px, py = x + 0.5, y + 0.5
    if inside_cork(px, py):
        return CORK if px < 16.0 else CORK_DARK
    flask = inside_belly(px, py) or inside_neck(px, py)
    if flask:
        if py >= LIQUID_LEVEL:
            diag = (px - BELLY_CENTER[0]) - (py - BELLY_CENTER[1])
            return LIQUID_LIGHT if diag < -5.0 else LIQUID if diag < 3.0 else LIQUID_DARK
        return GLASS
    if inside_cork(px, py, 1.0) or inside_belly(px, py, 1.0) or inside_neck(px, py, 1.0):
        return OUTLINE
    return None


def encode_png(rows):
    """Minimal RGBA8 PNG: IHDR, one IDAT (filter 0 on every row, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + r for r in rows)
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    return png


def render():
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            color = texel(x, y)
            row += bytes((0, 0, 0, 0)) if color is None else bytes(color) + b"\xff"
        rows.append(bytes(row))
    return rows


def main():
    png = encode_png(render())
    if "--check" in sys.argv[1:]:
        try:
            with open(PATH, "rb") as f:
                if f.read() == png:
                    return
        except FileNotFoundError:
            pass
        print("out of date: " + PATH)
        sys.exit(1)
    with open(PATH, "wb") as f:
        f.write(png)


if __name__ == "__main__":
    main()
