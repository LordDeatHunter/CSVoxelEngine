"""Deterministic generator of the hell block tiles (OpenGLTest/assets/textures/blocks/).

Every tile is a 32x32 RGBA image described by one entry of TILES: a tileable value-noise mottle
between a dark and a light tone, optional crack seams where a second noise field crosses its
middle, and optional bright specks (embers) hashed per texel. A new hell block (plans/LAYERS.md
L-8) adds its spec to TILES; nothing else changes. Every lattice wraps at the tile size, so each
tile repeats seamlessly on both axes. No randomness: the noise is an integer hash, and Pillow
writes the PNG with fixed options, so re-running reproduces each file byte for byte.

Run from anywhere:  python tools/generate-hell-textures.py
Check that the committed tiles are current (exit code 1 when not):
                    python tools/generate-hell-textures.py --check
"""
import io
import os
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCKS = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")

SIZE = 32

# One spec per tile. "cell" is the coarse lattice spacing in texels (SIZE must be a multiple of
# it, and of half of it, for the fine octave); "dark"/"light" bound the mottle; "seam" colors the
# cracks (None for none) and "seam_width" their half width in noise units; "speck" colors the
# embers and "speck_rate" makes one texel in that many a speck (0 for none); "salt" decorrelates
# the tiles' fields.
TILES = [
    {
        "name": "hellrock",
        "cell": 8,
        "dark": (46, 18, 16),
        "light": (92, 40, 30),
        "seam": (24, 8, 8),
        "seam_width": 0.04,
        "speck": (236, 112, 40),
        "speck_rate": 71,
        "salt": 1,
    },
    {
        "name": "ash",
        "cell": 8,
        "dark": (78, 74, 72),
        "light": (130, 124, 120),
        "seam": None,
        "seam_width": 0.0,
        "speck": (170, 70, 34),
        "speck_rate": 211,
        "salt": 2,
    },
    {
        "name": "cinder_soil",
        "cell": 8,
        "dark": (48, 34, 30),
        "light": (90, 62, 52),
        "seam": None,
        "seam_width": 0.0,
        "speck": (206, 92, 40),
        "speck_rate": 97,
        "salt": 3,
    },
    {
        "name": "scorched_hellrock",
        "cell": 8,
        "dark": (30, 18, 18),
        "light": (68, 40, 34),
        "seam": (10, 5, 5),
        "seam_width": 0.05,
        "speck": (150, 60, 30),
        "speck_rate": 0,
        "salt": 4,
    },
    {
        "name": "basalt",
        "cell": 4,
        "dark": (32, 32, 36),
        "light": (66, 64, 72),
        "seam": (16, 16, 20),
        "seam_width": 0.05,
        "speck": (90, 90, 96),
        "speck_rate": 0,
        "salt": 5,
    },
    {
        "name": "magma_block",
        "cell": 8,
        "dark": (96, 26, 10),
        "light": (232, 98, 32),
        "seam": (40, 10, 6),
        "seam_width": 0.08,
        "speck": (255, 206, 96),
        "speck_rate": 23,
        "salt": 6,
    },
    {
        "name": "ember_crystal",
        "cell": 8,
        "dark": (204, 72, 30),
        "light": (255, 172, 84),
        "seam": (124, 32, 12),
        "seam_width": 0.06,
        "speck": (255, 242, 186),
        "speck_rate": 13,
        "salt": 7,
    },
    {
        "name": "fungal_moss",
        "cell": 8,
        "dark": (40, 66, 70),
        "light": (74, 142, 122),
        "seam": None,
        "seam_width": 0.0,
        "speck": (124, 255, 204),
        "speck_rate": 61,
        "salt": 8,
    },
    {
        "name": "fungal_soil",
        "cell": 8,
        "dark": (46, 30, 44),
        "light": (86, 58, 74),
        "seam": None,
        "seam_width": 0.0,
        "speck": (92, 202, 162),
        "speck_rate": 131,
        "salt": 9,
    },
    {
        "name": "fungal_glow",
        "cell": 8,
        "dark": (40, 168, 140),
        "light": (134, 255, 212),
        "seam": (20, 92, 82),
        "seam_width": 0.06,
        "speck": (222, 255, 242),
        "speck_rate": 17,
        "salt": 10,
    },
]


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def lattice(ix, iy, cell, salt):
    """A lattice value in 0..1, wrapped at the tile size so the noise tiles."""
    n = SIZE // cell
    return (hash2((ix % n) + salt * 101, (iy % n) - salt * 37) & 0xFFFF) / 65535.0


def value_noise(x, y, cell, salt):
    """Bilinear value noise with a smoothstep fade, tileable over the tile."""
    ix, fx = divmod(x, cell)
    iy, fy = divmod(y, cell)
    tx = fx / cell
    ty = fy / cell
    tx = tx * tx * (3 - 2 * tx)
    ty = ty * ty * (3 - 2 * ty)
    a = lattice(ix, iy, cell, salt)
    b = lattice(ix + 1, iy, cell, salt)
    c = lattice(ix, iy + 1, cell, salt)
    d = lattice(ix + 1, iy + 1, cell, salt)
    top = a + (b - a) * tx
    bottom = c + (d - c) * tx
    return top + (bottom - top) * ty


def texel(spec, x, y):
    cell = spec["cell"]
    salt = spec["salt"] * 7
    mottle = 0.65 * value_noise(x, y, cell, salt) + 0.35 * value_noise(x, y, cell // 2, salt + 1)
    dark = spec["dark"]
    light = spec["light"]
    color = [dark[i] + (light[i] - dark[i]) * mottle for i in range(3)]

    # Cracks where a second field crosses its middle: thin, wandering dark lines
    if spec["seam"] is not None and abs(value_noise(x, y, cell, salt + 2) - 0.5) < spec["seam_width"]:
        color = [color[i] * 0.3 + spec["seam"][i] * 0.7 for i in range(3)]

    # Embers: isolated bright texels, a darker rim texel to their lower right
    rate = spec["speck_rate"]
    if rate and hash2(x + 911 * salt, y - 313) % rate == 0:
        color = list(spec["speck"])
    elif rate and hash2((x - 1) % SIZE + 911 * salt, (y - 1) % SIZE - 313) % rate == 0:
        color = [c * 0.6 + s * 0.4 for c, s in zip(color, spec["speck"])]

    return tuple(max(0, min(255, int(round(c)))) for c in color) + (255,)


def render(spec):
    """The tile's PNG bytes."""
    image = Image.new("RGBA", (SIZE, SIZE))
    image.putdata([texel(spec, x, y) for y in range(SIZE) for x in range(SIZE)])
    out = io.BytesIO()
    image.save(out, format="PNG", optimize=False, compress_level=9)
    return out.getvalue()


def main():
    check = "--check" in sys.argv[1:]
    stale = []
    for spec in TILES:
        path = os.path.join(BLOCKS, spec["name"] + ".png")
        data = render(spec)
        if check:
            current = open(path, "rb").read() if os.path.exists(path) else None
            if current != data:
                stale.append(os.path.relpath(path, ROOT))
            continue
        with open(path, "wb") as f:
            f.write(data)
        print("wrote", os.path.relpath(path, ROOT))

    if stale:
        print("stale:", ", ".join(stale))
        sys.exit(1)


if __name__ == "__main__":
    main()
