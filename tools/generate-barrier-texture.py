"""Deterministic generator of the barrier tile (OpenGLTest/assets/textures/blocks/barrier.png).

A 32x32 opaque tile of near-black, obsidian-like rock: a tileable value-noise mottle in dark
blue-violet grays, a few faceted lighter glints, and thin dull-red seams where two noise cells
meet, so the band between the overworld and hell reads as something other than stone. Tileable
on both axes (every lattice wraps at the tile size). No randomness and no image library: the PNG
is encoded here with zlib at a fixed level and filter 0, so re-running reproduces the file byte
for byte. Run from anywhere:  python -I tools/generate-barrier-texture.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "barrier.png")

SIZE = 32
CELL = 8                       # value-noise lattice spacing in texels; SIZE must be a multiple of it

BASE = (26, 22, 34)            # the rock's darkest tone
SPAN = (20, 16, 26)            # added on top at the mottle's brightest
SEAM = (92, 24, 22)            # the dull red seams
GLINT = (78, 70, 104)          # the rare faceted highlights


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def lattice(ix, iy, salt):
    """A lattice value in 0..1, wrapped at the tile size so the noise tiles."""
    n = SIZE // CELL
    return (hash2((ix % n) + salt * 101, (iy % n) - salt * 37) & 0xFFFF) / 65535.0


def value_noise(x, y, salt):
    """Bilinear value noise with a smoothstep fade, tileable over the tile."""
    ix, fx = divmod(x, CELL)
    iy, fy = divmod(y, CELL)
    tx = fx / CELL
    ty = fy / CELL
    tx = tx * tx * (3 - 2 * tx)
    ty = ty * ty * (3 - 2 * ty)
    a = lattice(ix, iy, salt)
    b = lattice(ix + 1, iy, salt)
    c = lattice(ix, iy + 1, salt)
    d = lattice(ix + 1, iy + 1, salt)
    top = a + (b - a) * tx
    bottom = c + (d - c) * tx
    return top + (bottom - top) * ty


def texel(x, y):
    mottle = 0.65 * value_noise(x, y, 1) + 0.35 * value_noise((x * 2) % SIZE, (y * 2) % SIZE, 2)
    color = [BASE[i] + SPAN[i] * mottle for i in range(3)]

    # A seam where the second field crosses its middle: a thin, wandering line
    seam = value_noise(x, y, 3)
    if abs(seam - 0.5) < 0.035:
        color = [color[i] * 0.4 + SEAM[i] * 0.6 for i in range(3)]

    # A few isolated glints, as on a fractured glassy face
    if hash2(x + 911, y - 313) % 61 == 0:
        color = list(GLINT)

    return tuple(max(0, min(255, int(round(c)))) for c in color)


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
            row += bytes(texel(x, y)) + b"\xff"
        rows.append(bytes(row))
    write_png(PATH, rows)
    print("wrote", os.path.relpath(PATH, ROOT))


if __name__ == "__main__":
    main()
