"""Deterministic generator of the acorn sprite (OpenGLTest/assets/textures/items/acorn.png).

A 32x32 RGBA sprite: a brown acorn with a darker cap, a short stalk and a pale highlight, rimmed
in a dark brown outline like the hand-made sprites.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces the file byte for byte. Run from anywhere:
    python -I tools/generate-acorn-texture.py
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items", "acorn.png")

SIZE = 32
CENTER = (15.5, 18.0)
RADIUS = 9.5
CAP_TOP = 10.0            # the cap covers everything above this row

BODY = (0x9A, 0x66, 0x32)
BODY_DARK = (0x7A, 0x4E, 0x24)
BODY_LIGHT = (0xB8, 0x84, 0x48)
CAP = (0x5A, 0x3C, 0x1E)
CAP_LIGHT = (0x74, 0x50, 0x2A)
STALK = (0x4A, 0x32, 0x18)
OUTLINE = (0x35, 0x22, 0x10)


def texel(x, y):
    dx = x - CENTER[0]
    dy = y - CENTER[1]
    # The nut: a circle, pointed at the bottom by shrinking the radius with dy
    taper = RADIUS - max(0.0, dy) * 0.35
    distance = math.hypot(dx, dy)
    in_nut = distance <= taper and y >= CAP_TOP
    in_cap = abs(dx) <= RADIUS - 1.5 and CAP_TOP - 5.0 <= y < CAP_TOP
    in_stalk = abs(dx) <= 1.0 and y < CAP_TOP - 5.0
    if not (in_nut or in_cap or in_stalk):
        return None
    if in_stalk:
        return STALK
    if in_cap:
        return CAP_LIGHT if dx + (y - CAP_TOP) < -2 else CAP
    # The body: lit on the upper left
    if dx + dy < -3:
        return BODY_LIGHT
    return BODY if dx + dy < 4 else BODY_DARK


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
            color = fill[y][x]
            if color is None:
                rimmed = any(
                    0 <= x + dx < SIZE and 0 <= y + dy < SIZE and fill[y + dy][x + dx] is not None
                    for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))
                )
                row += bytes(OUTLINE if rimmed else (0, 0, 0)) + (b"\xff" if rimmed else b"\x00")
            else:
                row += bytes(color) + b"\xff"
        rows.append(bytes(row))
    write_png(PATH, rows)
    print("wrote", os.path.relpath(PATH, ROOT))


if __name__ == "__main__":
    main()
