"""Deterministic generator of the pond plants' art.

Writes two 32x32 RGBA tiles (the size of every other block tile):
  OpenGLTest/assets/textures/blocks/reed.png       the reed block's cross tile: a clump of tall
      thin green stalks with darker bases and lighter tips, transparent elsewhere, rooted on the
      bottom edge like the tall grass tile;
  OpenGLTest/assets/textures/blocks/lily_pad.png   the lily pad's top tile: a round green pad with
      a darker rim, a notch wedge and faint radial veins, transparent outside the disc.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces both files byte for byte. Run from anywhere:
    python -I tools/generate-pond-plants.py
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REED_PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "reed.png")
PAD_PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "lily_pad.png")

SIZE = 32

# Stalks: column, half width in texels, tip row and shade offset
STALKS = ((9, 1, 3, 0), (14, 2, 0, -18), (20, 1, 6, 12))
REED_DARK = (0x2E, 0x7A, 0x3C)
REED_MID = (0x46, 0xA0, 0x4C)
REED_LIGHT = (0x7A, 0xC8, 0x5E)
REED_TIP = (0xC2, 0xE0, 0x86)

PAD_DARK = (0x2A, 0x74, 0x3A)
PAD_MID = (0x3F, 0x96, 0x48)
PAD_LIGHT = (0x5C, 0xB2, 0x5A)
PAD_RIM = (0x1E, 0x5A, 0x2E)
PAD_VEIN = (0x35, 0x86, 0x42)


def reed_texel(x, y):
    for column, half, tip, shade in STALKS:
        if abs(x - column) <= half and y >= tip:
            base = REED_MID if shade == 0 else (REED_LIGHT if shade > 0 else REED_DARK)
            if y <= tip + 3:
                return REED_TIP
            if y >= 26:
                return REED_DARK
            if x == column - half:
                return REED_DARK
            if x == column + half:
                return REED_LIGHT
            return base
    return None


def pad_texel(x, y):
    dx = x - 15.5
    dy = y - 15.5
    distance = math.hypot(dx, dy)
    if distance > 14.5:
        return None
    angle = math.atan2(dy, dx)
    # The notch: a wedge cut out toward the right
    if abs(angle) < 0.30 and distance > 3.0:
        return None
    if distance > 13.0:
        return PAD_RIM
    # Radial veins every 45 degrees
    if abs(math.sin(angle * 4.0)) < 0.10 and distance > 4.0:
        return PAD_VEIN
    if dx + dy < -8:
        return PAD_LIGHT
    return PAD_MID if distance < 11.0 else PAD_DARK


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


def build(texel):
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            colour = texel(x, y)
            row += bytes(colour if colour else (0, 0, 0)) + (b"\xff" if colour else b"\x00")
        rows.append(bytes(row))
    return rows


def main():
    write_png(REED_PATH, build(reed_texel))
    print("wrote", os.path.relpath(REED_PATH, ROOT))
    write_png(PAD_PATH, build(pad_texel))
    print("wrote", os.path.relpath(PAD_PATH, ROOT))


if __name__ == "__main__":
    main()
