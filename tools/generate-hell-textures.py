"""Deterministic generator of the hell block tiles (OpenGLTest/assets/textures/blocks/).

Every tile is a 32x32 RGBA image described by one entry of TILES: a tileable value-noise mottle
between a dark and a light tone, optional crack seams where a second noise field crosses its
middle, and optional bright specks (embers) hashed per texel. A spec's "kind" picks the painter:
"mottle" (the default, the stone-like blocks of L-8), "bark" (the same mottle stretched into
vertical grain, for a log's side), "rings" (a log's end: growth rings inside a bark rim) or one of
the cutout plant shapes of L-9 ("fern", "bloom", "vine", transparent around the plant). A new hell
block adds its spec to TILES; nothing else changes. Every lattice wraps at the tile size, so each
tile repeats seamlessly on both axes. No randomness: the noise is an integer hash, and Pillow
writes the PNG with fixed options, so re-running reproduces each file byte for byte.

Run from anywhere:  python tools/generate-hell-textures.py
Check that the committed tiles are current (exit code 1 when not):
                    python tools/generate-hell-textures.py --check
"""
import io
import math
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
    {
        "name": "fungal_log_side",
        "kind": "bark",
        "cell": 4,
        "dark": (122, 104, 128),
        "light": (196, 182, 196),
        "seam": (70, 150, 132),
        "seam_width": 0.05,
        "speck": (150, 255, 220),
        "speck_rate": 89,
        "salt": 11,
    },
    {
        "name": "fungal_log_top",
        "kind": "rings",
        "cell": 8,
        "dark": (150, 132, 152),
        "light": (214, 202, 210),
        "seam": (122, 104, 128),
        "seam_width": 0.0,
        "speck": (150, 255, 220),
        "speck_rate": 0,
        "salt": 12,
    },
    {
        "name": "charred_log_side",
        "kind": "bark",
        "cell": 4,
        "dark": (18, 14, 14),
        "light": (52, 42, 38),
        "seam": (150, 48, 18),
        "seam_width": 0.04,
        "speck": (236, 112, 40),
        "speck_rate": 113,
        "salt": 13,
    },
    {
        "name": "charred_log_top",
        "kind": "rings",
        "cell": 8,
        "dark": (40, 30, 28),
        "light": (78, 60, 52),
        "seam": (18, 14, 14),
        "seam_width": 0.0,
        "speck": (236, 112, 40),
        "speck_rate": 0,
        "salt": 14,
    },
    {
        "name": "fungal_cap",
        "cell": 8,
        "dark": (36, 150, 132),
        "light": (104, 232, 196),
        "seam": (22, 96, 92),
        "seam_width": 0.05,
        "speck": (226, 255, 240),
        "speck_rate": 19,
        "salt": 15,
    },
    {
        "name": "glow_fern",
        "kind": "fern",
        "cell": 8,
        "dark": (24, 96, 86),
        "light": (96, 236, 196),
        "seam": None,
        "seam_width": 0.0,
        "speck": (210, 255, 236),
        "speck_rate": 0,
        "salt": 16,
    },
    {
        "name": "ember_bloom",
        "kind": "bloom",
        "cell": 8,
        "dark": (40, 22, 20),
        "light": (236, 98, 36),
        "seam": None,
        "seam_width": 0.0,
        "speck": (255, 214, 110),
        "speck_rate": 0,
        "salt": 17,
    },
    {
        "name": "hell_vine",
        "kind": "vine",
        "cell": 8,
        "dark": (30, 56, 50),
        "light": (74, 126, 104),
        "seam": None,
        "seam_width": 0.0,
        "speck": (150, 255, 220),
        "speck_rate": 0,
        "salt": 18,
    },
]


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def lattice(ix, iy, cell_x, cell_y, salt):
    """A lattice value in 0..1, wrapped at the tile size so the noise tiles."""
    return (hash2((ix % (SIZE // cell_x)) + salt * 101, (iy % (SIZE // cell_y)) - salt * 37) & 0xFFFF) / 65535.0


def value_noise(x, y, cell, salt, cell_y=None):
    """Bilinear value noise with a smoothstep fade, tileable over the tile; cell_y (default cell)
    stretches it vertically."""
    cell_y = cell if cell_y is None else cell_y
    ix, fx = divmod(x, cell)
    iy, fy = divmod(y, cell_y)
    tx = fx / cell
    ty = fy / cell_y
    tx = tx * tx * (3 - 2 * tx)
    ty = ty * ty * (3 - 2 * ty)
    a = lattice(ix, iy, cell, cell_y, salt)
    b = lattice(ix + 1, iy, cell, cell_y, salt)
    c = lattice(ix, iy + 1, cell, cell_y, salt)
    d = lattice(ix + 1, iy + 1, cell, cell_y, salt)
    top = a + (b - a) * tx
    bottom = c + (d - c) * tx
    return top + (bottom - top) * ty


def mix(a, b, t):
    return [a[i] + (b[i] - a[i]) * t for i in range(3)]


def finish(color):
    return tuple(max(0, min(255, int(round(c)))) for c in color) + (255,)


CLEAR = (0, 0, 0, 0)


def specks(spec, x, y, color):
    """Embers: isolated bright texels, a darker rim texel to their lower right."""
    rate = spec["speck_rate"]
    salt = spec["salt"] * 7
    if rate and hash2(x + 911 * salt, y - 313) % rate == 0:
        return list(spec["speck"])
    if rate and hash2((x - 1) % SIZE + 911 * salt, (y - 1) % SIZE - 313) % rate == 0:
        return [c * 0.6 + s * 0.4 for c, s in zip(color, spec["speck"])]
    return color


def mottle_texel(spec, x, y, stretch=1):
    cell = spec["cell"]
    salt = spec["salt"] * 7
    cy = cell * stretch
    mottle = (0.65 * value_noise(x, y, cell, salt, cy)
              + 0.35 * value_noise(x, y, max(1, cell // 2), salt + 1, max(1, cy // 2)))
    color = mix(spec["dark"], spec["light"], mottle)

    # Cracks where a second field crosses its middle: thin, wandering dark lines (vertical grain
    # when stretched)
    if spec["seam"] is not None and abs(value_noise(x, y, cell, salt + 2, cy) - 0.5) < spec["seam_width"]:
        color = [color[i] * 0.3 + spec["seam"][i] * 0.7 for i in range(3)]
    return specks(spec, x, y, color)


def rings_texel(spec, x, y):
    """A log's end: growth rings around the middle inside a two-texel bark rim."""
    dx = x - (SIZE - 1) / 2
    dy = y - (SIZE - 1) / 2
    r = (dx * dx + dy * dy) ** 0.5
    edge = max(abs(dx), abs(dy))
    if edge > SIZE / 2 - 2.5:
        return list(spec["seam"])
    wobble = value_noise(x, y, spec["cell"], spec["salt"] * 7) * 1.5
    ring = (r + wobble) % 4
    return mix(spec["dark"], spec["light"], 0.25 if ring < 1.2 else 0.85)


def fern_shape(spec):
    """A fan of curved fronds from the bottom middle, leaflets along each, glowing tips."""
    texels = {}
    fronds = 5
    for f in range(fronds):
        lean = (f - (fronds - 1) / 2) * 0.42
        length = 26 - abs(f - (fronds - 1) / 2) * 4
        for step in range(int(length * 2)):
            t = step / (length * 2)
            # The frond rises, leans out and droops near its tip
            x = 15.5 + math.sin(lean) * t * length + math.sin(lean) * t * t * 4
            y = 31 - math.cos(lean) * t * length + t * t * 6 * abs(math.sin(lean))
            px, py = int(round(x)), int(round(y))
            shade = 0.25 + 0.75 * t
            texels[(px, py)] = mix(spec["dark"], spec["light"], shade)
            # Leaflets every few texels along the frond, both sides
            if step % 3 == 0 and 0.15 < t < 0.92:
                reach = 1 + int(3 * (1 - t))
                for side in (-1, 1):
                    for k in range(1, reach + 1):
                        lx = px + side * k
                        ly = py - (k // 2)
                        texels[(lx, ly)] = mix(spec["dark"], spec["light"], shade * 0.8)
            if t > 0.93:
                texels[(px, py)] = list(spec["speck"])
    return texels


def bloom_shape(spec):
    """A thin dark stem with two leaves under a smoldering five-petal head and a bright heart."""
    texels = {}
    stem = spec["dark"]
    for y in range(12, 32):
        x = 16 + (1 if 18 < y < 26 else 0)
        texels[(x, y)] = stem
    for k in range(1, 5):
        texels[(17 + k, 24 - k // 2)] = mix(stem, (70, 40, 30), 0.5)
        texels[(16 - k, 20 - k // 2)] = mix(stem, (70, 40, 30), 0.5)
    cx, cy = 16, 9
    for y in range(SIZE):
        for x in range(SIZE):
            dx, dy = x - cx, y - cy
            r = (dx * dx + dy * dy) ** 0.5
            if r > 6.5:
                continue
            angle = math.atan2(dy, dx)
            petal = 4.5 + 2.0 * math.cos(5 * angle)
            if r <= 1.6:
                texels[(x, y)] = list(spec["speck"])
            elif r <= petal:
                texels[(x, y)] = mix(spec["light"], (120, 26, 14), r / 7)
    return texels


def vine_shape(spec):
    """Three wavy strands down the whole tile (periodic in y, so stacked cells join) with leaves
    and a few glowing beads."""
    texels = {}
    for strand, (base, amp, phase) in enumerate(((10, 2.0, 0.0), (17, 2.5, 2.1), (23, 1.5, 4.2))):
        for y in range(SIZE):
            x = int(round(base + amp * math.sin(2 * math.pi * y / SIZE + phase)))
            shade = value_noise(x, y, spec["cell"], spec["salt"] * 7 + strand)
            texels[(x, y)] = mix(spec["dark"], spec["light"], shade)
            if (y + strand * 5) % 7 == 0:
                for k in (1, 2):
                    texels[(x + k, y - k + 1)] = mix(spec["dark"], spec["light"], 0.9)
                    texels[(x - k, y + k)] = mix(spec["dark"], spec["light"], 0.6)
            if (y + strand * 11) % 13 == 0:
                texels[(x, y)] = list(spec["speck"])
    return texels


SHAPES = {"fern": fern_shape, "bloom": bloom_shape, "vine": vine_shape}


def render(spec):
    """The tile's PNG bytes."""
    kind = spec.get("kind", "mottle")
    if kind in SHAPES:
        texels = SHAPES[kind](spec)
        data = [finish(texels[(x, y)]) if (x, y) in texels else CLEAR for y in range(SIZE) for x in range(SIZE)]
    elif kind == "rings":
        data = [finish(rings_texel(spec, x, y)) for y in range(SIZE) for x in range(SIZE)]
    else:
        stretch = 4 if kind == "bark" else 1
        data = [finish(mottle_texel(spec, x, y, stretch)) for y in range(SIZE) for x in range(SIZE)]
    image = Image.new("RGBA", (SIZE, SIZE))
    image.putdata(data)
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
