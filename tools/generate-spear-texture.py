"""Deterministic generator of the spear sprite (OpenGLTest/assets/textures/items/spear.png).

A 32x32 RGBA sprite: a long wooden shaft running from the lower left to the upper right with a
lighter lit upper edge and a darker underside, a leaf-shaped flint head at the tip, and a sinew
binding where the two meet, all rimmed in a dark outline like the hand-made sprites. The tip sits
on the sprite's (1, 1) diagonal, which is the axis a held item's blade points along and the axis
a thrown spear is flown along (ThrownProjectile / ObjectPasses).

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces the file byte for byte. Run from anywhere:
    python -I tools/generate-spear-texture.py
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items", "spear.png")

SIZE = 32

WOOD_LIGHT = (0xA9, 0x7C, 0x4B)
WOOD = (0x8A, 0x60, 0x37)
WOOD_SHADE = (0x66, 0x44, 0x26)
HEAD_LIGHT = (0xC9, 0xCD, 0xD1)
HEAD = (0x9A, 0xA0, 0xA8)
HEAD_SHADE = (0x6A, 0x70, 0x78)
BINDING = (0xD6, 0xC3, 0x9A)
BINDING_SHADE = (0xAE, 0x98, 0x6C)
OUTLINE = (0x2E, 0x22, 0x18)

# The shaft: the segment from A to B; the head is a leaf shape along the same line from B to TIP
A = (3.5, 28.5)
B = (21.0, 11.0)
TIP = (29.0, 3.0)
SHAFT_HALF = 1.5
HEAD_HALF = 3.4
BINDING_LENGTH = 3.0


def along(point):
    """Returns (t, distance) of a point against the A -> TIP line, t in texel lengths from A."""
    dx, dy = TIP[0] - A[0], TIP[1] - A[1]
    length = math.hypot(dx, dy)
    ux, uy = dx / length, dy / length
    t = (point[0] - A[0]) * ux + (point[1] - A[1]) * uy
    # Signed across distance: negative on the upper-left side (the lit side)
    across = -(point[0] - A[0]) * uy + (point[1] - A[1]) * ux
    return t, across


HEAD_START = along(B)[0]
HEAD_END = along(TIP)[0]


def texel(x, y):
    t, across = along((x + 0.5, y + 0.5))
    if t < -0.5 or t > HEAD_END + 0.4:
        return None
    if t >= HEAD_START:
        # Leaf head: widest a third of the way up, closing to the point at the tip
        u = (t - HEAD_START) / (HEAD_END - HEAD_START)
        half = HEAD_HALF * math.sin(math.pi * min(1.0, u * 0.75 + 0.05)) * (1.0 - u * 0.55)
        half = max(half, 0.0)
        if abs(across) > half + 0.8:
            return None
        if abs(across) > half:
            return OUTLINE
        if across < -half * 0.35:
            return HEAD_LIGHT
        return HEAD if across < half * 0.4 else HEAD_SHADE
    if abs(across) > SHAFT_HALF + 0.9:
        return None
    if abs(across) > SHAFT_HALF:
        return OUTLINE
    if HEAD_START - BINDING_LENGTH <= t < HEAD_START:
        return BINDING_SHADE if int(t) % 2 else BINDING
    if across < -SHAFT_HALF * 0.3:
        return WOOD_LIGHT
    return WOOD if across < SHAFT_HALF * 0.45 else WOOD_SHADE


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
