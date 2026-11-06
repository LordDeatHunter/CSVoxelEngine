"""Deterministic generator of the bow sprite (OpenGLTest/assets/textures/items/bow.png).

A 32x32 RGBA sprite in the usual inventory-bow pose: a wooden stave bowed toward the upper left
with a leather-wrapped grip at its middle and a notched nock at each tip, strung with a pale
sinew string that runs straight between the two tips on the lower-right side, all rimmed in a
dark outline like the hand-made sprites. The stave is a quadratic curve between the tips, so
the grip sits on the sprite's (1, 1) diagonal through the middle, which is the axis a held item
is posed along.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces the file byte for byte. Run from anywhere:
    python -I tools/generate-bow-texture.py
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items", "bow.png")

SIZE = 32

WOOD_LIGHT = (0xB4, 0x86, 0x52)
WOOD = (0x8E, 0x63, 0x38)
WOOD_SHADE = (0x69, 0x46, 0x26)
GRIP = (0x5A, 0x3B, 0x24)
GRIP_LIGHT = (0x7A, 0x52, 0x33)
STRING = (0xE9, 0xE2, 0xCC)
OUTLINE = (0x2E, 0x22, 0x18)

# The stave: a quadratic curve from tip A through the pull point P (not on the curve) to tip B
A = (4.5, 27.5)
B = (27.5, 4.5)
P = (1.5, 1.5)
STAVE_HALF = 1.3
GRIP_HALF_LENGTH = 2.6
SAMPLES = 400


def bezier(t):
    u = 1.0 - t
    return (u * u * A[0] + 2 * u * t * P[0] + t * t * B[0],
            u * u * A[1] + 2 * u * t * P[1] + t * t * B[1])


CURVE = [bezier(i / SAMPLES) for i in range(SAMPLES + 1)]
MID = bezier(0.5)


def nearest_stave(x, y):
    best = 1e9
    best_t = 0.0
    for i, (cx, cy) in enumerate(CURVE):
        d = math.hypot(x - cx, y - cy)
        if d < best:
            best = d
            best_t = i / SAMPLES
    return best, best_t


def distance_to_string(x, y):
    ax, ay = A
    bx, by = B
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(x - (ax + dx * t), y - (ay + dy * t))


def texel(x, y):
    px, py = x + 0.5, y + 0.5
    stave, t = nearest_stave(px, py)
    string = distance_to_string(px, py)
    if string <= 0.6 and 0.04 < t < 0.96:
        return STRING
    if stave > STAVE_HALF + 0.9:
        return None
    if stave > STAVE_HALF:
        return OUTLINE
    # The grip wrap around the middle of the stave
    if math.hypot(px - MID[0], py - MID[1]) <= GRIP_HALF_LENGTH:
        return GRIP_LIGHT if (x + y) % 3 == 0 else GRIP
    # Lit side of the stave is the outer (upper-left) one
    cx, cy = CURVE[int(t * SAMPLES)]
    outward = (px - cx) + (py - cy)
    if outward < -STAVE_HALF * 0.3:
        return WOOD_LIGHT
    return WOOD if outward < STAVE_HALF * 0.5 else WOOD_SHADE


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
            color = texel(x, y)
            row += bytes((0, 0, 0, 0)) if color is None else bytes(color) + b"\xff"
        rows.append(bytes(row))
    write_png(PATH, rows)


if __name__ == "__main__":
    main()
