"""Deterministic generator of the sky block tiles (OpenGLTest/assets/textures/blocks/).

Every tile is a 32x32 RGBA image described by one entry of TILES. A spec's "kind" picks the painter:
"vapor" (the default) is a tileable value-noise mottle between a dark and a light tone with its alpha
mottled between "alpha_low" and "alpha_high" by a second noise field, so a translucent block (the
solid cloud of L-11) reads as soft, uneven vapor rather than a flat sheet; "leaves" is an opaque
mottle punched with cutout holes where a second field falls under "hole", dotted with blossom specks;
"turf_side" is a block side whose top band (with a wavy lower edge) wears the top tones and the rest
the soil tones. The sky palette of L-13 also borrows the hell generator's painters, unchanged: a spec
whose kind is one of HELL_KINDS ("stone" is its mottle, "bark", "rings", "fern", "vine") is painted by
tools/generate-hell-textures.py's render from the same keys that script reads. A new sky block adds its
spec to TILES. Every lattice wraps at the tile size, so each tile repeats seamlessly on both axes. No
randomness: the noise is an integer hash, and Pillow writes the PNG with fixed options, so re-running
reproduces each file byte for byte.

Run from anywhere:  python tools/generate-sky-textures.py
Check that the committed tiles are current (exit code 1 when not):
                    python tools/generate-sky-textures.py --check
"""
import importlib.util
import io
import os
import sys

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCKS = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")

SIZE = 32


def load_hell_painters():
    """tools/generate-hell-textures.py as a module (its file name is not importable as is)."""
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "generate-hell-textures.py")
    spec = importlib.util.spec_from_file_location("hell_textures", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# The sky kinds the hell painters draw, mapped to the hell kind that draws them
HELL_KINDS = {"stone": "mottle", "bark": "bark", "rings": "rings", "fern": "fern", "vine": "vine"}

# One spec per tile. "cell" is the coarse lattice spacing in texels (SIZE must be a multiple of
# it, and of half of it, for the fine octave); "dark"/"light" bound the color mottle;
# "alpha_low"/"alpha_high" bound a vapor tile's alpha mottle (0..255); "hole" is the share of a
# leaves tile cut away and "speck"/"speck_rate" its specks; "soil_dark"/"soil_light" and "band" (its
# top band's height in texels) shape a turf side; a hell-painted kind reads the hell script's keys
# ("seam", "seam_width", "speck", "speck_rate"); "salt" decorrelates the tiles' fields.
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
    # --- The sky palette of L-13 ---
    {
        "name": "crystal_log_side",
        "kind": "bark",
        "cell": 4,
        "dark": (126, 176, 200),
        "light": (206, 238, 250),
        "seam": (84, 134, 164),
        "seam_width": 0.05,
        "speck": (250, 255, 255),
        "speck_rate": 41,
        "salt": 5,
    },
    {
        "name": "crystal_log_top",
        "kind": "rings",
        "cell": 8,
        "dark": (150, 196, 218),
        "light": (220, 244, 252),
        "seam": (96, 148, 176),
        "seam_width": 0.0,
        "speck": (250, 255, 255),
        "speck_rate": 0,
        "salt": 6,
    },
    {
        "name": "crystal_leaves",
        "kind": "leaves",
        "cell": 8,
        "dark": (110, 196, 226),
        "light": (196, 244, 255),
        "hole": 0.42,
        "speck": (250, 255, 255),
        "speck_rate": 17,
        "salt": 7,
    },
    {
        "name": "frost_fern",
        "kind": "fern",
        "cell": 8,
        "dark": (96, 142, 158),
        "light": (214, 240, 248),
        "seam": None,
        "seam_width": 0.0,
        "speck": (250, 255, 255),
        "speck_rate": 0,
        "salt": 8,
    },
    {
        "name": "sky_vine",
        "kind": "vine",
        "cell": 8,
        "dark": (48, 104, 52),
        "light": (118, 180, 92),
        "seam": None,
        "seam_width": 0.0,
        "speck": (246, 236, 160),
        "speck_rate": 0,
        "salt": 9,
    },
    {
        "name": "sky_oak_leaves",
        "kind": "leaves",
        "cell": 8,
        "dark": (70, 150, 52),
        "light": (150, 214, 92),
        "hole": 0.36,
        "speck": (252, 236, 244),
        "speck_rate": 29,
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
    """A vapor texel: the color mottle with a mottled alpha."""
    salt = spec["salt"] * 7
    shade = two_octaves(x, y, spec["cell"], salt)
    color = [spec["dark"][i] + (spec["light"][i] - spec["dark"][i]) * shade for i in range(3)]
    density = two_octaves(x, y, spec["cell"], salt + 2)
    alpha = spec["alpha_low"] + (spec["alpha_high"] - spec["alpha_low"]) * density
    return tuple(max(0, min(255, int(round(c)))) for c in color) + (max(0, min(255, int(round(alpha)))),)


def clamp_color(color):
    return tuple(max(0, min(255, int(round(c)))) for c in color)


def leaves_texel(spec, x, y):
    """An opaque mottle with cutout holes where the second field falls under "hole", and specks."""
    salt = spec["salt"] * 7
    if two_octaves(x, y, spec["cell"], salt + 2) < spec["hole"]:
        return (0, 0, 0, 0)
    if hash2(x + 911 * salt, y - 313) % spec["speck_rate"] == 0:
        return clamp_color(spec["speck"]) + (255,)
    shade = two_octaves(x, y, spec["cell"], salt)
    color = [spec["dark"][i] + (spec["light"][i] - spec["dark"][i]) * shade for i in range(3)]
    return clamp_color(color) + (255,)


PAINTERS = {"vapor": texel, "leaves": leaves_texel}


def render(spec, hell):
    """The tile's PNG bytes."""
    kind = spec.get("kind", "vapor")
    if kind in HELL_KINDS:
        return hell.render(dict(spec, kind=HELL_KINDS[kind]))

    painter = PAINTERS[kind]
    image = Image.new("RGBA", (SIZE, SIZE))
    image.putdata([painter(spec, x, y) for y in range(SIZE) for x in range(SIZE)])
    out = io.BytesIO()
    image.save(out, format="PNG", optimize=False, compress_level=9)
    return out.getvalue()


def main():
    check = "--check" in sys.argv[1:]
    hell = load_hell_painters()
    stale = []
    for spec in TILES:
        path = os.path.join(BLOCKS, spec["name"] + ".png")
        data = render(spec, hell)
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
