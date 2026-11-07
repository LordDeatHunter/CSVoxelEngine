"""Deterministic generator of the teaching scroll sprites (placeholder art).

Writes five 32x32 RGBA sprites to OpenGLTest/assets/textures/items/:
  scroll_fire_projectile.png - the hint scroll, a red-orange wax seal
  scroll_frost.png           - a pale blue seal
  scroll_nature.png          - a green seal
  scroll_self.png            - a violet seal
  scroll_modifiers.png       - a gold seal

Each is a rolled parchment lying along the sprite's (1, 1) diagonal, the axis a held item is posed
along: a pale body between two darker rolled ends, a wax seal in its middle, rimmed in a dark
outline like the hand-made sprites.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces the files byte for byte. Run from anywhere:
    python -I tools/generate-scroll-textures.py
    python -I tools/generate-scroll-textures.py --check   (compares against the files on disk)
"""
import math
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items")

SIZE = 32
OUTLINE = (0x3A, 0x2A, 0x1C)

# The scroll's axis, from its lower-left end to its upper-right end, in pixel space
START = (6.5, 25.5)
END = (25.5, 6.5)
HALF_WIDTH = 5.2
ROLL_SHARE = 0.16
SEAL_RADIUS = 3.2

PARCHMENT_LIGHT, PARCHMENT, PARCHMENT_SHADE = (0xF4, 0xE6, 0xC0), (0xE0, 0xCC, 0x98), (0xC4, 0xAC, 0x76)
ROLL_LIGHT, ROLL, ROLL_SHADE = (0xC8, 0xA6, 0x6C), (0xA8, 0x86, 0x50), (0x86, 0x66, 0x3A)

# Seal colors per scroll: (light, base, dark)
SEALS = {
    "scroll_fire_projectile": ((0xFF, 0x9A, 0x5A), (0xDC, 0x4A, 0x22), (0x9A, 0x26, 0x10)),
    "scroll_frost": ((0xE6, 0xF6, 0xFF), (0x9C, 0xD2, 0xF0), (0x5E, 0x96, 0xC0)),
    "scroll_nature": ((0x9C, 0xE6, 0x7A), (0x4C, 0xAA, 0x3A), (0x2A, 0x6E, 0x22)),
    "scroll_self": ((0xD8, 0xA6, 0xFF), (0x9A, 0x5A, 0xD8), (0x62, 0x30, 0x96)),
    "scroll_modifiers": ((0xFF, 0xEE, 0x8A), (0xE0, 0xB2, 0x30), (0x9C, 0x76, 0x16)),
}


def axis(px, py):
    """Distance across the scroll's axis and the 0..1 position along it."""
    ax, ay = START
    dx, dy = END[0] - ax, END[1] - ay
    t = ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)
    across = ((px - ax) * dy - (py - ay) * dx) / math.hypot(dx, dy)
    return across, t


def shade(across, light, base, dark):
    """Lit on the upper-left side of the axis, shaded on the lower-right."""
    return light if across > HALF_WIDTH * 0.45 else base if across > -HALF_WIDTH * 0.4 else dark


def texel(x, y, seal):
    px, py = x + 0.5, y + 0.5
    cx, cy = (START[0] + END[0]) / 2.0, (START[1] + END[1]) / 2.0
    seal_distance = math.hypot(px - cx, py - cy) / SEAL_RADIUS
    if seal_distance <= 1.0:
        s = (px - cx) - (py - cy)
        return seal[0] if s < -1.2 else seal[1] if s < 1.4 else seal[2]
    if seal_distance <= 1.3:
        return OUTLINE

    across, t = axis(px, py)
    rolled = t < ROLL_SHARE or t > 1.0 - ROLL_SHARE
    # The rolled ends are a little thicker than the flat parchment between them
    half = HALF_WIDTH + (0.9 if rolled else 0.0)
    if t < -0.04 or t > 1.04 or abs(across) > half + 1.0:
        return None
    if abs(across) > half or t < 0.0 or t > 1.0:
        return OUTLINE
    if rolled:
        return shade(across, ROLL_LIGHT, ROLL, ROLL_SHADE)
    return shade(across, PARCHMENT_LIGHT, PARCHMENT, PARCHMENT_SHADE)


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


def render(seal):
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            color = texel(x, y, seal)
            row += bytes((0, 0, 0, 0)) if color is None else bytes(color) + b"\xff"
        rows.append(bytes(row))
    return rows


def main():
    check = "--check" in sys.argv[1:]
    stale = []
    for name, seal in SEALS.items():
        path = os.path.join(OUT, name + ".png")
        png = encode_png(render(seal))
        if check:
            try:
                with open(path, "rb") as f:
                    if f.read() != png:
                        stale.append(path)
            except FileNotFoundError:
                stale.append(path)
        else:
            with open(path, "wb") as f:
                f.write(png)
    if stale:
        for path in stale:
            print("out of date: " + path)
        sys.exit(1)


if __name__ == "__main__":
    main()
