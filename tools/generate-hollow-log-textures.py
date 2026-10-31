"""Deterministic generator of the hollow fallen-log placeholder tiles
(OpenGLTest/assets/textures/blocks/hollow_<species>_log_inner.png and _end.png).

Two 32x32 opaque tiles (the size of every block tile) per species that lays a hollow fallen log
(oak, spruce, stone_pine). The outside of a hollow log wears the species' own <species>_log_side
tile, so only the inside and the cut ends need art here:

- hollow_<species>_log_inner.png is the rotten heartwood seen inside the hole: a dull, dark wood
  speckled from a hash of the texel, with faint short fibers in both directions, so it reads the
  same on every inner face whichever way the face is turned (no uv rotation needed).
- hollow_<species>_log_end.png is the cut wall seen at an open end. The model's ring faces crop
  only the outer 3/16 band of the tile (6 texels at 32 px) through default UVs, so that border is
  painted as a 2-texel bark rim and then concentric square growth rings in lighter wood; the inner
  20x20, which is never sampled, is a flat dark hollow color, so a wrong crop is obvious.

These are placeholders, meant to be swapped for proper art. No randomness and no image library:
the colors come from a small integer hash of the texel, and the PNG is encoded here with zlib at a
fixed level and filter 0, so re-running reproduces every file byte for byte. Run from anywhere:
    python -I tools/generate-hollow-log-textures.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")

SIZE = 32
RING_BAND = 6   # texels of the end tile the ring faces sample (3/16 of the tile)
BARK_RIM = 2    # outer texels of that band painted as bark

# Per-species palettes: the bark of the rim, the lighter cut wood of the rings, the ring lines,
# the rotten heartwood inside (base, dark, light) and the never-sampled hollow core
SPECIES = {
    "oak": {
        "bark": (92, 70, 44), "bark_dark": (66, 49, 30),
        "wood": (176, 138, 88), "ring": (138, 104, 64),
        "rot": (104, 78, 50), "rot_dark": (78, 57, 36), "rot_light": (122, 94, 62),
        "hollow": (34, 24, 16),
    },
    "spruce": {
        "bark": (70, 46, 30), "bark_dark": (48, 31, 20),
        "wood": (150, 106, 70), "ring": (112, 76, 48),
        "rot": (84, 58, 40), "rot_dark": (62, 42, 28), "rot_light": (100, 72, 50),
        "hollow": (28, 18, 12),
    },
    "stone_pine": {
        "bark": (112, 70, 40), "bark_dark": (82, 50, 28),
        "wood": (196, 140, 86), "ring": (156, 104, 60),
        "rot": (118, 80, 48), "rot_dark": (90, 60, 34), "rot_light": (138, 96, 60),
        "hollow": (38, 24, 14),
    },
}


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def inner_texel(p, salt, x, y):
    # Short fibers in both directions: a texel joins a horizontal or a vertical streak picked by
    # a coarse hash, so neither axis dominates and the tile reads alike in any orientation
    if hash2(x // 4 + salt, y // 4) % 2 == 0:
        fiber = hash2(y + salt, x // 3) % 7
    else:
        fiber = hash2(x + salt * 3, y // 3) % 7
    speck = hash2(x * 31 + salt, y * 17) % 11
    if speck == 0:
        return p["rot_dark"]
    if speck == 1:
        return p["rot_light"]
    if fiber == 0:
        return p["rot_dark"]
    if fiber == 6:
        return p["rot_light"]
    return p["rot"]


def end_texel(p, salt, x, y):
    # Chebyshev depth from the tile's edge: 0 on the border, rising toward the center
    depth = min(x, y, SIZE - 1 - x, SIZE - 1 - y)
    if depth >= RING_BAND:
        return p["hollow"]
    if depth < BARK_RIM:
        return p["bark_dark"] if hash2(x + salt, y) % 4 == 0 else p["bark"]
    # Square growth rings: alternate cut wood and a ring line, with a little hashed grain
    if depth % 2 == 1:
        return p["ring"]
    return p["ring"] if hash2(x * 7 + salt, y * 13) % 9 == 0 else p["wood"]


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


def render(texel, palette, salt):
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            row += bytes(texel(palette, salt, x, y)) + b"\xff"
        rows.append(bytes(row))
    return rows


def main():
    for index, (species, palette) in enumerate(SPECIES.items()):
        salt = (index + 1) * 101
        for suffix, texel in (("inner", inner_texel), ("end", end_texel)):
            path = os.path.join(OUT, "hollow_%s_log_%s.png" % (species, suffix))
            write_png(path, render(texel, palette, salt))
            print("wrote", os.path.relpath(path, ROOT))


if __name__ == "__main__":
    main()
