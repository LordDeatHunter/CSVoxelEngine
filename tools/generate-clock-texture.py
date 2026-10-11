"""Deterministic generator of the clock sprite (OpenGLTest/assets/textures/items/clock.png).

A 32x32 RGBA sprite: a round bronze clock face, a pale dial, twelve tick marks and two dark hands
(hour up, minute right), rimmed in a dark bronze outline like the hand-made sprites.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces the file byte for byte. Run from anywhere:
    python -I tools/generate-clock-texture.py
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items", "clock.png")

SIZE = 32
CENTRE = (15.5, 15.5)
RADIUS = 11.5
RING = 3.0                # the bronze rim's thickness in texels

BRONZE = (0xC9, 0x8A, 0x3A)
BRONZE_LIGHT = (0xE0, 0xAA, 0x5C)
BRONZE_DARK = (0x8A, 0x5A, 0x24)
DIAL = (0xEF, 0xE6, 0xCE)
DIAL_SHADE = (0xD6, 0xC9, 0xA8)
TICK = (0x5A, 0x46, 0x2C)
HAND = (0x3A, 0x2C, 0x1C)
OUTLINE = (0x5A, 0x3C, 0x16)


def texel(x, y):
    dx = x - CENTRE[0]
    dy = y - CENTRE[1]
    distance = math.hypot(dx, dy)
    if distance > RADIUS:
        return None
    if distance > RADIUS - RING:
        # The rim: lit on the upper left, shaded on the lower right
        return BRONZE_LIGHT if dx + dy < 0 else (BRONZE_DARK if dx + dy > 6 else BRONZE)

    angle = math.atan2(dy, dx)
    # Twelve tick marks just inside the rim
    if distance > RADIUS - RING - 2.2 and math.cos(angle * 12.0) > 0.92:
        return TICK
    # The hands: the hour up, the minute to the right, drawn from the centre
    if abs(dx) <= 1.0 and dy <= 0.5 and distance > 1.5:
        return HAND
    if abs(dy) <= 1.0 and dx >= -0.5 and distance > 1.5 and distance < RADIUS - RING - 1.0:
        return HAND
    if distance <= 1.5:
        return HAND
    return DIAL if dx + dy < 2 else DIAL_SHADE


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
