"""Deterministic generator of the iceberg entity models and their shared skin.

Writes OpenGLTest/assets/models/entity/iceberg_{small,medium,large}.bbmodel and iceberg.png. No
randomness and no image library: the skin's shades come from a small integer hash of the texel, the
PNG is encoded here with zlib at a fixed level and filter 0, and the JSON is dumped in a fixed key
order and indentation, so re-running reproduces every file byte for byte. Run from the repo root:
    python -I tools/generate-iceberg-models.py           (writes the four files)
    python -I tools/generate-iceberg-models.py --check   (exit 1 if any file differs)

Model frame (what IcebergShape reads through EntityModel.CubeBoxes):
  - one top-level group "Berg", pivot (0, 0, 0), no rotations, no animations;
  - model y = 0 is the WATERLINE: the berg floats with it at the water surface;
  - every cube is axis-aligned, units are pixels (16 px = 1 block).

Geometry rules (BodySolidsTests.ASwimmerCanBoardEveryIcebergVariant enforces them; keep them when
retuning a shape):
  - every cube's bottom and top are multiples of 8 px (half a block), so every step is 8 px;
  - the SHELF is the widest cube at the surface, a ring whose top is at 8 px (at most 8 px above the
    waterline), so a swimmer can climb out onto it;
  - every cube above the shelf is a STEP exactly 8 px high, standing on the one below it and inset
    from it on all four sides, so no two step edges stack into a taller wall and walking auto-step
    (PlayerPhysics.WalkStepHeight = 0.5 block) climbs the berg to its top from any side;
  - nothing climbable sits between the waterline and the shelf top, and the submerged base is wider
    than the shelf but its top lies 32 px (2 blocks) down, under a floating swimmer's feet;
  - the draft (depth below y = 0) is twice the freeboard (height above it): about 2/3 of the berg
    is under water;
  - the footprint is not square, so a quarter turn really swaps its X and Z extents.

UV layout of iceberg.png (32 x 32, four 16 x 16 regions; every cube face uses exactly one):
  A (0, 0)-(16, 16)    dry ice: white with pale blue specks. The up face of every cube, and the
                       sides of every cube wholly above the waterline.
  B (16, 0)-(32, 16)   waterline band: rows 0-7 dry ice, rows 8-15 submerged ice. The sides of the
                       cube that crosses y = 0 (the shelf), with its v range chosen so row 8 lands
                       exactly on the waterline.
  C (0, 16)-(16, 32)   submerged ice: a darker blue. Every face of a cube wholly under water.
  D (16, 16)-(32, 32)  shaded ice: the down face of a cube above the waterline (never seen today).
A cube's down face is disabled when the cube below covers it completely, so stacked steps draw no
hidden coplanar quads.
"""
import json
import os
import re
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ENTITY_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "models", "entity")
SKIN_NAME = "iceberg.png"
SKIN_SIZE = 32
REGION = 16
SHELF_TOP = 8

# Each variant: (name, cubes bottom-up as (name, y0, y1, x0, x1, z0, z1) in pixels). Base cubes
# first, then the shelf (the one cube crossing y = 0), then the steps.
VARIANTS = [
    ("iceberg_small", [
        ("Base", -48, -32, -32, 32, -24, 24),
        ("Shelf", -32, 8, -24, 24, -24, 16),
        ("Step1", 8, 16, -16, 16, -16, 8),
        ("Step2", 16, 24, -8, 8, -8, 0),
    ]),
    ("iceberg_medium", [
        ("Keel", -80, -56, -32, 32, -32, 32),
        ("Base", -56, -32, -48, 48, -40, 48),
        ("Shelf", -32, 8, -40, 40, -40, 32),
        ("Step1", 8, 16, -32, 32, -32, 24),
        ("Step2", 16, 24, -24, 24, -24, 16),
        ("Step3", 24, 32, -16, 16, -16, 8),
        ("Step4", 32, 40, -8, 8, -8, 0),
    ]),
    ("iceberg_large", [
        ("Keel", -128, -88, -48, 48, -48, 40),
        ("Base", -88, -32, -72, 72, -64, 72),
        ("Shelf", -32, 8, -64, 64, -56, 64),
        ("Step1", 8, 16, -56, 56, -48, 56),
        ("Step2", 16, 24, -48, 48, -40, 48),
        ("Step3", 24, 32, -40, 40, -32, 40),
        ("Step4", 32, 40, -32, 32, -24, 32),
        ("Step5", 40, 48, -24, 24, -16, 24),
        ("Step6", 48, 56, -16, 16, -8, 16),
        ("Step7", 56, 64, -8, 8, 0, 8),
    ]),
]

# Region origins (u, v) in texels.
DRY = (0, 0)
BAND = (16, 0)
WET = (0, 16)
SHADE = (16, 16)


def check_geometry(name, cubes):
    """Fails loudly if a shape breaks the header's rules, so a retune cannot slip one through."""
    shelf = [c for c in cubes if c[1] < 0 < c[2]]
    assert len(shelf) == 1, f"{name}: exactly one cube must cross the waterline"
    assert shelf[0][2] == SHELF_TOP, f"{name}: the shelf top must be {SHELF_TOP} px"
    for c in cubes:
        assert c[1] % 8 == 0 and c[2] % 8 == 0 and c[1] < c[2], f"{name}/{c[0]}: snap y to 8 px"
        assert c[3] < c[4] and c[5] < c[6], f"{name}/{c[0]}: empty footprint"
    steps = [c for c in cubes if c[1] >= SHELF_TOP]
    below = shelf[0]
    for c in steps:
        assert c[2] - c[1] == 8, f"{name}/{c[0]}: a step is exactly 8 px high"
        assert c[1] == below[2], f"{name}/{c[0]}: a step stands on the cube below it"
        assert below[3] < c[3] and c[4] < below[4] and below[5] < c[5] and c[6] < below[6], \
            f"{name}/{c[0]}: a step is inset from the cube below it on every side"
        below = c
    freeboard = max(c[2] for c in cubes)
    draft = -min(c[1] for c in cubes)
    assert draft == 2 * freeboard, f"{name}: the draft is twice the freeboard"
    xs = (min(c[3] for c in cubes), max(c[4] for c in cubes))
    zs = (min(c[5] for c in cubes), max(c[6] for c in cubes))
    assert xs[1] - xs[0] != zs[1] - zs[0], f"{name}: the footprint must not be square"


def num(v):
    return int(v) if float(v).is_integer() else round(v, 4)


def rect(origin, v0=0.0, v1=REGION):
    u, v = origin
    return [u, num(v + v0), u + REGION, num(v + v1)]


def side_uv(cube):
    _, y0, y1, *_ = cube
    if y1 <= 0:
        return rect(WET)
    if y0 >= 0:
        return rect(DRY)
    # Crossing the waterline: pick the band's v range so row 8 (the dry/wet line) lands on y = 0.
    # v(y) = v0 + (y1 - y) / h * (16 - v0) and v(0) = 8 give v0 = (8h - 16 y1) / (h - y1).
    h = y1 - y0
    v0 = (8 * h - 16 * y1) / (h - y1)
    assert 0 <= v0 < 8, "the shelf must reach at least as far below the waterline as above it"
    return rect(BAND, v0)


def covered_below(cube, cubes):
    """True when another cube's top face lies under this one's whole bottom face."""
    _, y0, _, x0, x1, z0, z1 = cube
    return any(o is not cube and o[2] == y0 and o[3] <= x0 and x1 <= o[4] and o[5] <= z0 and z1 <= o[6]
               for o in cubes)


def element(variant_index, cube_index, cube, cubes):
    name, y0, y1, x0, x1, z0, z1 = cube
    side = side_uv(cube)
    up = rect(WET) if y1 <= 0 else rect(DRY)
    down = rect(WET) if y1 <= 0 else rect(SHADE)
    faces = {}
    for key in ("north", "east", "south", "west"):
        faces[key] = {"uv": side, "texture": 0}
    faces["up"] = {"uv": up, "texture": 0}
    faces["down"] = {"uv": down, "texture": 0}
    if covered_below(cube, cubes):
        faces["down"]["enabled"] = False
    return {
        "name": name,
        "uuid": uuid(variant_index, cube_index + 1),
        "type": "cube",
        "from": [x0, y0, z0],
        "to": [x1, y1, z1],
        "box_uv": False,
        "rescale": False,
        "locked": False,
        "rotation": {"origin": [0, 0, 0], "axis": "y", "angle": 0, "rescale": False},
        "faces": faces,
    }


def uuid(variant_index, n):
    return "1cebe600-0000-4000-8000-%04x%08x" % (variant_index, n)


def project(variant_index, name, cubes):
    check_geometry(name, cubes)
    elements = [element(variant_index, i, c, cubes) for i, c in enumerate(cubes)]
    return {
        "meta": {"format_version": "4.0", "model_format": "free", "box_uv": False},
        "name": name,
        "resolution": {"width": SKIN_SIZE, "height": SKIN_SIZE},
        "textures": [{
            "id": "0",
            "name": SKIN_NAME,
            "source": SKIN_NAME,
            "width": SKIN_SIZE,
            "height": SKIN_SIZE,
            "uv_width": SKIN_SIZE,
            "uv_height": SKIN_SIZE,
            "particle": False,
            "use_as_default": False,
            "render_mode": "default",
        }],
        "elements": elements,
        "outliner": [{
            "name": "Berg",
            "origin": [0, 0, 0],
            "uuid": uuid(variant_index, 0xa0),
            "export": True,
            "visibility": True,
            "rotation": [0, 0, 0],
            "children": [e["uuid"] for e in elements],
        }],
    }


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


DRY_BASE = (232, 244, 250)
DRY_SPECK = (200, 226, 242)
DRY_GLINT = (252, 254, 255)
WET_BASE = (70, 132, 176)
WET_DEEP = (52, 108, 156)
WET_STREAK = (96, 158, 196)
SHADE_BASE = (176, 206, 226)


def dry(x, y):
    h = hash2(x, y) % 23
    if h == 0:
        return DRY_GLINT
    if h < 4:
        return DRY_SPECK
    return DRY_BASE


def wet(x, y, row):
    # Darker with depth inside the region, with sparse lighter vertical streaks.
    if hash2(x, 7) % 5 == 0 and hash2(x, y) % 3 != 0:
        return WET_STREAK
    return WET_DEEP if row >= REGION // 2 or hash2(x, y) % 4 == 0 else WET_BASE


def texel(x, y):
    in_x, in_y = x % REGION, y % REGION
    region = (x // REGION, y // REGION)
    if region == (0, 0):
        return dry(x, y)
    if region == (1, 0):
        return dry(x, y) if in_y < REGION // 2 else wet(x, y, in_y - REGION // 2)
    if region == (0, 1):
        return wet(x, y, in_y)
    return SHADE_BASE if hash2(x, y) % 6 else DRY_SPECK


def png_bytes():
    """Minimal RGBA8 PNG: IHDR, one IDAT (filter 0 on every row, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    rows = []
    for y in range(SKIN_SIZE):
        row = bytearray()
        for x in range(SKIN_SIZE):
            row += bytes(texel(x, y)) + b"\xff"
        rows.append(b"\x00" + bytes(row))
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", SKIN_SIZE, SKIN_SIZE, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
    png += chunk(b"IEND", b"")
    return png


def inline_number_arrays(text):
    """Folds every array of plain numbers onto one line, the way Blockbench writes them."""
    return re.sub(r"\[\s*(-?[\d.]+(?:,\s*-?[\d.]+)*)\s*\]",
                  lambda m: "[" + ", ".join(p.strip() for p in m.group(1).split(",")) + "]", text)


def outputs():
    files = []
    for i, (name, cubes) in enumerate(VARIANTS):
        text = inline_number_arrays(json.dumps(project(i, name, cubes), indent=4)) + "\n"
        files.append((os.path.join(ENTITY_DIR, name + ".bbmodel"), text.encode("utf-8"), True))
    files.append((os.path.join(ENTITY_DIR, SKIN_NAME), png_bytes(), False))
    return files


def main():
    check = "--check" in sys.argv
    stale = []
    for path, data, is_text in outputs():
        old = None
        if os.path.exists(path):
            with open(path, "rb") as f:
                old = f.read()
        # A text file may come back from git with CRLF line endings; compare it by content.
        same = old is not None and (old.replace(b"\r\n", b"\n") == data if is_text else old == data)
        if check:
            if not same:
                stale.append(os.path.relpath(path, ROOT))
            continue
        if not same:
            with open(path, "wb") as f:
                f.write(data)
        print("wrote" if not same else "unchanged", os.path.relpath(path, ROOT))
    if check:
        for path in stale:
            print("differs:", path)
        sys.exit(1 if stale else 0)


if __name__ == "__main__":
    main()
