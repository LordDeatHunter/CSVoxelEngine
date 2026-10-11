"""Deterministic generator of the bandage sprite (OpenGLTest/assets/textures/items/bandage.png).

A 32x32 RGBA sprite: a rolled cloth band running from the lower left to the upper right, a lighter
lit top edge, a darker underside, fine wrap creases across it, and a knot loop tied over its middle,
all rimmed in a dark tan outline like the hand-made sprites. The palette reuses the cotton art's
warm whites and the mudball's browns.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces the file byte for byte. Run from anywhere:
    python -I tools/generate-bandage-texture.py
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items", "bandage.png")

SIZE = 32

CLOTH_LIGHT = (0xF6, 0xF1, 0xE4)
CLOTH = (0xE8, 0xDF, 0xC9)
CLOTH_SHADE = (0xD3, 0xC6, 0xA8)
CLOTH_CREASE = (0xBC, 0xAC, 0x8C)
OUTLINE = (0x8A, 0x76, 0x54)

# The band: the segment from A to B, half-width HALF, plus the knot loop over its middle
A = (5.0, 26.0)
B = (27.0, 6.0)
HALF = 3.6
KNOT = (17.5, 14.5)
KNOT_RADIUS = 4.6


def distance_to_band(x, y):
    ax, ay = A
    bx, by = B
    dx, dy = bx - ax, by - ay
    length2 = dx * dx + dy * dy
    t = ((x - ax) * dx + (y - ay) * dy) / length2
    t = max(0.0, min(1.0, t))
    px, py = ax + dx * t, ay + dy * t
    return math.hypot(x - px, y - py)


def cloth_colour(distance, x, y):
    # The lit edge is the upper side of the band (smaller y along the perpendicular), and the
    # wrap creases run across the band every four texels of its length
    if distance > HALF - 1.0:
        return CLOTH_SHADE
    if (x + y) % 4 == 0:
        return CLOTH_CREASE
    return CLOTH_LIGHT if (x - y) % 7 < 3 else CLOTH


def texel(x, y):
    in_knot = math.hypot(x - KNOT[0], y - KNOT[1]) <= KNOT_RADIUS
    distance = distance_to_band(x, y)
    in_band = distance <= HALF
    if not in_band and not in_knot:
        return None
    if in_knot and not in_band:
        return CLOTH_SHADE if (x + y) % 3 else CLOTH
    if in_knot:
        return CLOTH_LIGHT
    return cloth_colour(distance, x, y)


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
    # Fill pass, then an outline pass rims every filled texel with the dark tan on empty neighbours
    fill = [[texel(x, y) for x in range(SIZE)] for y in range(SIZE)]
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            colour = fill[y][x]
            if colour is None:
                rimmed = any(
                    0 <= x + dx < SIZE and 0 <= y + dy < SIZE and fill[y + dy][x + dx] is not None
                    for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))
                )
                row += bytes(OUTLINE if rimmed else (0, 0, 0)) + (b"\xff" if rimmed else b"\x00")
            else:
                row += bytes(colour) + b"\xff"
        rows.append(bytes(row))
    write_png(PATH, rows)
    print("wrote", os.path.relpath(PATH, ROOT))


if __name__ == "__main__":
    main()
