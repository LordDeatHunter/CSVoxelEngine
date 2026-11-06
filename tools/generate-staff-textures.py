"""Deterministic generator of the staff sprites (placeholder art).

Writes two 32x32 RGBA sprites to OpenGLTest/assets/textures/items/:
  wooden_staff.png  - a brown wooden shaft with a small pale crystal set in a forked top
  crystal_staff.png - a darker shaft with a copper band and a larger cyan crystal head

Both lie along the sprite's (1, 1) diagonal, the axis a held item is posed along: the shaft
runs from the lower left to the head at the upper right, rimmed in a dark outline like the
hand-made sprites.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces the files byte for byte. Run from anywhere:
    python -I tools/generate-staff-textures.py
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items")

SIZE = 32
OUTLINE = (0x2E, 0x22, 0x18)

# Shaft from its foot (lower left) to the base of the head (upper right), in pixel space
FOOT = (3.5, 28.5)


def segment_distance(px, py, a, b):
    """Distance from the point to segment ab, and the 0..1 position along it."""
    ax, ay = a
    bx, by = b
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(px - (ax + dx * t), py - (ay + dy * t)), t


def side(px, py):
    """Signed offset across the diagonal: negative is the upper-left (lit) side."""
    return ((px - py) / math.sqrt(2.0))


def diamond(px, py, center, radius):
    """Manhattan distance (rotated diamond) from the crystal's center, scaled by its radius."""
    return (abs(px - center[0]) + abs(py - center[1])) / radius


def wooden_texel(x, y):
    px, py = x + 0.5, y + 0.5
    light, wood, shade = (0xB4, 0x86, 0x52), (0x8E, 0x63, 0x38), (0x69, 0x46, 0x26)
    crystal_light, crystal, crystal_dark = (0xF2, 0xF6, 0xFF), (0xC6, 0xD8, 0xF0), (0x8E, 0xA6, 0xC8)

    head = (24.5, 7.5)
    gem = diamond(px, py, head, 3.0)
    if gem <= 1.0:
        s = side(px - head[0], py - head[1])
        return crystal_light if s < -0.6 else crystal if s < 0.8 else crystal_dark

    # The two prongs of the fork, clasping the crystal from the lower-left and the lower-right sides
    fork_left, _ = segment_distance(px, py, (19.5, 10.5), (21.0, 5.0))
    fork_right, _ = segment_distance(px, py, (21.5, 12.5), (27.0, 11.0))
    shaft, _ = segment_distance(px, py, FOOT, (20.5, 11.5))
    core = min(shaft, fork_left, fork_right)
    if gem <= 1.35:
        return OUTLINE
    if core > 2.1:
        return None
    if core > 1.2:
        return OUTLINE
    s = side(px, py)
    offset = s - side(*FOOT) if shaft <= core else 0.0
    return light if offset < -0.4 else wood if offset < 0.5 else shade


def crystal_texel(x, y):
    px, py = x + 0.5, y + 0.5
    light, wood, shade = (0x6E, 0x4A, 0x36), (0x52, 0x34, 0x26), (0x3A, 0x24, 0x1A)
    copper_light, copper = (0xE8, 0x9A, 0x5C), (0xB8, 0x6A, 0x36)
    crystal_light, crystal, crystal_dark = (0xC8, 0xFF, 0xFF), (0x48, 0xD8, 0xE8), (0x1E, 0x8C, 0xA8)

    head = (24.0, 8.0)
    gem = diamond(px, py, head, 5.0)
    if gem <= 1.0:
        s = side(px - head[0], py - head[1])
        return crystal_light if s < -1.2 else crystal if s < 1.4 else crystal_dark
    if gem <= 1.22:
        return OUTLINE

    shaft, t = segment_distance(px, py, FOOT, (20.5, 11.5))
    if shaft > 2.1:
        return None
    if shaft > 1.2:
        return OUTLINE
    # A copper band just under the head and one around the grip
    if 0.84 <= t <= 0.94 or 0.40 <= t <= 0.46:
        return copper_light if side(px, py) - side(*FOOT) < 0.0 else copper
    offset = side(px, py) - side(*FOOT)
    return light if offset < -0.4 else wood if offset < 0.5 else shade


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


def render(texel):
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            color = texel(x, y)
            row += bytes((0, 0, 0, 0)) if color is None else bytes(color) + b"\xff"
        rows.append(bytes(row))
    return rows


def main():
    write_png(os.path.join(OUT, "wooden_staff.png"), render(wooden_texel))
    write_png(os.path.join(OUT, "crystal_staff.png"), render(crystal_texel))


if __name__ == "__main__":
    main()
