"""Deterministic generator of the battle tower's forcefield tile (placeholder art, listed in USER_TODO.md).

Writes OpenGLTest/assets/textures/blocks/tower_forcefield.png: the roof dome's forcefield (TowerDome), a faint cyan
honeycomb, nearly clear. It is only the fallback where the world shader's own shimmer is off (the forcefield term in
test-fragment.glsl, ForcefieldShimmer; the ItemDebugger), since the block draws translucent and the shader replaces its
color and alpha.

No randomness and no image library: every texel comes from integer geometry, and the PNG writer below (stdlib zlib and
struct, filter 0, level 9) is the one the other tools/ generators use, so re-running reproduces the file byte for byte.
It writes only this tile, so a re-run never touches art that replaced another placeholder. Run from the repo root:
    python -I tools/generate-forcefield-texture.py           (writes the tile)
    python -I tools/generate-forcefield-texture.py --check   (exit 1 if the file differs)
"""
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTPUT = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "tower_forcefield.png")
SIZE = 32

# A honeycomb: the cells round a staggered lattice of centers 16 texels apart in rows 16 texels apart (each row shifted
# half a cell), so it tiles in 32x32. Doubled texel-center coordinates keep every test in integers: a texel whose squared
# distances to its two nearest centers differ by less than EDGE (about 1.25 texels from their bisector) is an edge.
FIELD = (0x9A, 0xD8, 0xFF)
FILL_ALPHA = 28
EDGE_ALPHA = 112
CENTERS = ((8, 8), (24, 8), (0, 24), (16, 24))
EDGE = 160


def texel(x, y):
    px, py = 2 * x + 1, 2 * y + 1
    d = sorted((px - 2 * (cx + ox)) ** 2 + (py - 2 * (cy + oy)) ** 2
               for cx, cy in CENTERS for ox in (-SIZE, 0, SIZE) for oy in (-SIZE, 0, SIZE))
    return FIELD + (EDGE_ALPHA if d[1] - d[0] < EDGE else FILL_ALPHA,)


def png_bytes(size, texel_of):
    """Minimal RGBA8 PNG: IHDR, one IDAT (filter 0 on every row, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    rows = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            row += bytes(texel_of(x, y))
        rows.append(b"\x00" + bytes(row))
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
    png += chunk(b"IEND", b"")
    return png


def main():
    data = png_bytes(SIZE, texel)
    old = None
    if os.path.exists(OUTPUT):
        with open(OUTPUT, "rb") as f:
            old = f.read()
    same = old == data
    if "--check" in sys.argv:
        if not same:
            print("differs:", os.path.relpath(OUTPUT, ROOT))
        sys.exit(0 if same else 1)
    if not same:
        with open(OUTPUT, "wb") as f:
            f.write(data)
    print("wrote" if not same else "unchanged", os.path.relpath(OUTPUT, ROOT))


if __name__ == "__main__":
    main()
