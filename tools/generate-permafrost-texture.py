"""Deterministic generator of the permafrost tile (OpenGLTest/assets/textures/blocks/permafrost.png).

A 32x32 opaque tile of frozen soil: a dark gray-brown dirt base shaded by two octaves of value
noise, a few thin pale bluish-white frost veins wandering across it, and scattered ice-white
specks. The noise lattice and the veins wrap at the tile edge, so a greedy quad that repeats the
tile shows no seam. No randomness and no image library: every shade comes from a small integer
hash, all arithmetic is integer, and the PNG is encoded here with zlib at a fixed level and
filter 0, so re-running reproduces the file byte for byte. Run from anywhere:
python -I tools/generate-permafrost-texture.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "permafrost.png")

SIZE = 32

DARK = (64, 54, 48)       # the soil's darkest shade
LIGHT = (104, 92, 82)     # the soil's lightest shade
VEIN = (176, 196, 210)    # pale bluish-white frost
VEIN_EDGE = (128, 138, 146)  # the frost fading into the soil beside a vein
SPECK = (226, 236, 242)   # ice-white grains

VEINS = 4                 # frost veins per tile
SPECK_EVERY = 41          # one texel in this many (by hash) is an ice speck


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def lattice(ix, iy, cell, salt):
    """The noise value (0..255) at one lattice point, wrapped so the tile repeats seamlessly."""
    n = SIZE // cell
    return hash2((ix % n) + salt * 101, (iy % n) + salt * 211) & 0xFF


def value_noise(x, y, cell, salt):
    """Bilinear value noise in 0..255 over a lattice of the given cell size, integer math only."""
    ix, fx = divmod(x, cell)
    iy, fy = divmod(y, cell)
    a = lattice(ix, iy, cell, salt)
    b = lattice(ix + 1, iy, cell, salt)
    c = lattice(ix, iy + 1, cell, salt)
    d = lattice(ix + 1, iy + 1, cell, salt)
    top = a * (cell - fx) + b * fx
    bottom = c * (cell - fx) + d * fx
    return (top * (cell - fy) + bottom * fy) // (cell * cell)


def vein_texels():
    """The texels the frost veins cover: each vein walks across the tile from a hashed start,
    one column a step, drifting up or down by a hashed amount, wrapping at the edges."""
    cells = set()
    for v in range(VEINS):
        x = hash2(v, 7) % SIZE
        y = hash2(v, 13) % SIZE
        length = 10 + hash2(v, 19) % 12
        for step in range(length):
            cells.add((x % SIZE, y % SIZE))
            drift = hash2(v * 64 + step, 29) % 5
            y += -1 if drift == 0 else 1 if drift == 1 else 0
            x += 1
    return cells


def mix(a, b, t):
    """Integer lerp from color a to color b by t in 0..255."""
    return tuple((ca * (255 - t) + cb * t) // 255 for ca, cb in zip(a, b))


def texel(x, y, veins):
    if hash2(x + 500, y + 500) % SPECK_EVERY == 0:
        return SPECK
    if (x, y) in veins:
        return VEIN
    near = ((x, (y - 1) % SIZE) in veins) or ((x, (y + 1) % SIZE) in veins)
    shade = (value_noise(x, y, 8, 1) * 3 + value_noise(x, y, 4, 2)) // 4
    shade = max(0, min(255, shade + (hash2(x, y) & 0x1F) - 16))
    soil = mix(DARK, LIGHT, shade)
    return mix(soil, VEIN_EDGE, 96) if near else soil


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
    veins = vein_texels()
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            row += bytes(texel(x, y, veins)) + b"\xff"
        rows.append(bytes(row))
    write_png(PATH, rows)
    print("wrote", os.path.relpath(PATH, ROOT))


if __name__ == "__main__":
    main()
