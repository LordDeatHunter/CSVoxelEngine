"""Deterministic generator of the crushed iron sprite (OpenGLTest/assets/textures/items/crushed_iron.png).

A 32x32 RGBA sprite: a low, wide pile of ground hematite on a transparent background, built from
2x2 granules of rust red and iron gray, each granule lighter toward the lit top of the pile and
darker toward its base, with a few loose grains spilled beside it, all rimmed in the darkest gray
like the hand-made sprites. The palette is sampled from the hematite chunk sprite (hematite_ore.png).

No randomness and no image library: every per-texel variation comes from a fixed integer hash, and
the PNG is encoded here with zlib at a fixed level and filter 0, so re-running reproduces the file
byte for byte. Run from anywhere:
    python -I tools/generate-crushed-iron-texture.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items", "crushed_iron.png")

SIZE = 32

# Darkest to lightest, sampled from hematite_ore.png
RUST = [(0x43, 0x23, 0x2C), (0x64, 0x32, 0x3F), (0x85, 0x41, 0x52), (0xA5, 0x63, 0x76), (0xC5, 0x86, 0x9A)]
IRON = [(0x1A, 0x1F, 0x2B), (0x23, 0x29, 0x37), (0x28, 0x33, 0x3F), (0x35, 0x44, 0x4F)]
OUTLINE = (0x12, 0x16, 0x20)

# The pile: a rounded mound centered on CENTER_X, resting on BASE_Y, HEIGHT tall, HALF_WIDTH wide
CENTER_X = 15.5
BASE_Y = 27
HEIGHT = 11.0
HALF_WIDTH = 13.5

# Loose grains beside the pile, (x, y) of each 1x1 grain on the ground line
LOOSE = ((0, 27), (30, 27))


def hash2(x, y, salt=0):
    """A fixed 32-bit integer hash of (x, y, salt)."""
    h = (x * 374761393 + y * 668265263 + salt * 2246822519) & 0xFFFFFFFF
    h = ((h ^ (h >> 13)) * 1274126177) & 0xFFFFFFFF
    return h ^ (h >> 16)


def surface_y(x):
    """The top of the pile over column x, or None outside it; each granule column is bumped by a
    hashed 0..1 texels so the outline reads grainy rather than smooth."""
    u = (x + 0.5 - CENTER_X) / HALF_WIDTH
    if abs(u) >= 1.0:
        return None
    rise = HEIGHT * (1.0 - u * u) ** 1.3
    bump = hash2(x // 2, 0, 1) % 2
    return BASE_Y - rise + bump


def granule_color(x, y, top):
    # Granules sit on a 2x2 grid whose odd rows shift a texel, so the grains interlock like a heap
    gx = (x + (y // 2) % 2) // 2
    gy = y // 2
    h = hash2(gx, gy)
    depth = y - top                      # texels below the pile's surface
    height01 = (BASE_Y - y) / HEIGHT     # 0 at the base, 1 at the peak
    shade = 0.35 + 0.65 * height01       # brighter up the pile
    if depth < 1.5:
        shade += 0.35                    # the lit top edge
    shade += ((h >> 4) % 3 - 1) * 0.18   # each granule a step lighter or darker
    family = RUST if h % 5 < 3 else IRON
    index = int(shade * (len(family) - 1) + 0.5)
    return family[max(0, min(len(family) - 1, index))]


def texel(x, y):
    if (x, y) in LOOSE:
        return RUST[1] if hash2(x, y, 2) % 2 else IRON[1]
    top = surface_y(x)
    if top is None or y < top or y > BASE_Y:
        return None
    return granule_color(x, y, top)


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
    # Fill pass, then an outline pass rims every filled texel with the darkest gray on empty neighbors
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
