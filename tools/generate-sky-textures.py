"""Deterministic generator of the sky block tiles (OpenGLTest/assets/textures/blocks/).

Every tile is a 32x32 RGBA image described by one entry of TILES: a tileable value-noise mottle
between a dark and a light tone, with its alpha mottled between "alpha_low" and "alpha_high" by a
second noise field, so a translucent block (the solid cloud of L-11) reads as soft, uneven vapor
rather than a flat sheet. A new sky block (the islands and flora of L-12 and L-13) adds its spec to
TILES; an opaque one sets both alphas to 255. Every lattice wraps at the tile size, so each tile
repeats seamlessly on both axes. No randomness: the noise is an integer hash, and Pillow writes the
PNG with fixed options, so re-running reproduces each file byte for byte.

Run from anywhere:  python tools/generate-sky-textures.py
Check that the committed tiles are current (exit code 1 when not):
                    python tools/generate-sky-textures.py --check
"""
import io
import os
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCKS = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")

SIZE = 32

# One spec per tile. "cell" is the coarse lattice spacing in texels (SIZE must be a multiple of
# it, and of half of it, for the fine octave); "dark"/"light" bound the color mottle;
# "alpha_low"/"alpha_high" bound the alpha mottle (0..255); "salt" decorrelates the tiles' fields.
TILES = [
    {
        "name": "cloud_block",
        "cell": 8,
        "dark": (214, 222, 232),
        "light": (252, 253, 255),
        "alpha_low": 160,
        "alpha_high": 200,
        "salt": 1,
    },
]


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def lattice(ix, iy, cell, salt):
    """A lattice value in 0..1, wrapped at the tile size so the noise tiles."""
    return (hash2((ix % (SIZE // cell)) + salt * 101, (iy % (SIZE // cell)) - salt * 37) & 0xFFFF) / 65535.0


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


def two_octaves(x, y, cell, salt):
    """A coarse octave plus a finer one, still in 0..1."""
    return 0.65 * value_noise(x, y, cell, salt) + 0.35 * value_noise(x, y, max(1, cell // 2), salt + 1)


def texel(spec, x, y):
    salt = spec["salt"] * 7
    shade = two_octaves(x, y, spec["cell"], salt)
    color = [spec["dark"][i] + (spec["light"][i] - spec["dark"][i]) * shade for i in range(3)]
    density = two_octaves(x, y, spec["cell"], salt + 2)
    alpha = spec["alpha_low"] + (spec["alpha_high"] - spec["alpha_low"]) * density
    return tuple(max(0, min(255, int(round(c)))) for c in color) + (max(0, min(255, int(round(alpha)))),)


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
