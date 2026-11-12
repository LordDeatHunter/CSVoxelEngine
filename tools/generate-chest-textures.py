"""Deterministic generator of the chest art (placeholder art, listed in USER_TODO.md).

Every chest block shares one Blockbench model (OpenGLTest/assets/models/block/chest.bbmodel: a hollow base, a
hinged lid and a latch) and differs only in its skin. For each variant this writes:
  OpenGLTest/assets/textures/chests/chest_<variant>.png              the 128 x 128 skin the model's face UVs address
                                                                     (64 x 64 UV units, two texels per model pixel,
                                                                     the density of a block tile)
  OpenGLTest/assets/textures/blocks/chest_<variant>_{front,side,top}.png   the 32 x 32 tiles of the static twin,
                                                                     models/block/chest.json (the inventory icon,
                                                                     the held block, drops and break particles),
                                                                     cut from the very faces the skin is built of

The skin layout (REGIONS, in model pixels) is the one the model's faces were mapped to in Blockbench; moving a
region means remapping those faces. No randomness and no image library: every texel comes from a fixed rule and a
small hand-picked palette, and the PNG writer (stdlib zlib and struct, filter 0, level 9) is the one the other
generators use, so re-running reproduces every file byte for byte. Run from the repo root:
    python -I tools/generate-chest-textures.py           (writes every file)
    python -I tools/generate-chest-textures.py --check   (exit 1 if any file differs)
"""
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIN_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "chests")
BLOCK_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")

TEXELS = 2          # texels per model pixel
SKIN_UNITS = 64     # the skin is 64 x 64 model pixels
TILE = 32           # a block tile, 16 model pixels a side

# ---- the skin layout, in model pixels: name -> (x, y, width, height) ------------------------------

REGIONS = {
    "base_front": (0, 0, 14, 10),
    "base_back": (14, 0, 14, 10),
    "base_left": (28, 0, 14, 10),
    "base_right": (42, 0, 14, 10),
    "base_bottom": (0, 10, 14, 14),
    "base_rim": (14, 10, 14, 14),      # the top of the base walls
    "base_floor": (28, 10, 14, 14),    # the inside floor
    "base_inner": (42, 10, 14, 10),    # every inside wall of the base
    "lid_top": (0, 24, 14, 14),
    "lid_front": (14, 24, 14, 4),
    "lid_back": (14, 28, 14, 4),
    "lid_left": (14, 32, 14, 4),
    "lid_right": (14, 36, 14, 4),
    "lid_rim": (28, 24, 14, 14),       # the underside of the lid walls
    "lid_ceiling": (42, 24, 14, 14),   # the inside of the lid's top
    "lid_inner": (0, 38, 14, 4),       # every inside wall of the lid
    "latch_front": (14, 40, 2, 4),
    "latch_side": (16, 40, 1, 4),
    "latch_end": (17, 40, 2, 1),
    "latch_back": (19, 40, 2, 4),
}

# ---- palettes: body (groove, dark, mid, light), trim (dark, mid, light), latch (dark, mid, light), inside (dark, mid)


def rgb(value):
    return ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF)


def palette(body, trim, latch, inside, style, groove_rows, fleck=None):
    return {
        "body": [rgb(c) for c in body], "trim": [rgb(c) for c in trim], "latch": [rgb(c) for c in latch],
        "inside": [rgb(c) for c in inside], "style": style, "groove": groove_rows,
        "fleck": rgb(fleck) if fleck is not None else None,
    }


VARIANTS = {
    # Oak planks under dark oak corner posts, a brass latch
    "wooden": palette((0x6E4A26, 0x8C6231, 0xA87B40, 0xC0924F), (0x3A2614, 0x5A3C1E, 0x7A5630),
                      (0x5C4410, 0xD9A92E, 0xF6DC7A), (0x2A1B0E, 0x3E2A16), "wood", 6),
    # Riveted iron plates, a pale steel latch
    "iron": palette((0x55595E, 0x7C8188, 0x9BA1A8, 0xBCC2C8), (0x2C2F33, 0x484C52, 0x6A6F76),
                    (0x3A3D42, 0xC9CED4, 0xF2F5F7), (0x1C1E21, 0x2E3135), "metal", 10),
    # Bronze plates under darker bronze bands, a pale gold latch
    "bronze": palette((0x7A4A1E, 0xA8682A, 0xC98A3C, 0xE6B05E), (0x4A2C12, 0x6E431C, 0x93602A),
                      (0x5A4212, 0xE2C25A, 0xFBEFA6), (0x2A190B, 0x402812), "metal", 10),
    # Weathered gray boards with moss in the grain, rusted iron bands
    "ruin_surface": palette((0x4E4A3C, 0x6F6A56, 0x8A856C, 0xA39E84), (0x3A2A1E, 0x5E4230, 0x7E5C42),
                            (0x3E2E1E, 0x9A7A4A, 0xC8A870), (0x22201A, 0x34312A), "wood", 6, fleck=0x5E7A3A),
    # A miner's crate: dark timber under iron bands, a rusty latch
    "ruin_underground": palette((0x3E2C1C, 0x58402A, 0x705438, 0x886A48), (0x26282C, 0x3E4248, 0x5C6168),
                                (0x3A2416, 0xA0603A, 0xD08A5A), (0x1A130C, 0x2A1F14), "wood", 6),
    # Pale cloud-wood under gold bands, a sky-blue latch
    "ruin_sky": palette((0xA8B4C4, 0xCAD4E0, 0xE2E9F1, 0xF6F9FC), (0x8A6A1E, 0xC49A30, 0xEAC860),
                        (0x2E5A8A, 0x6CB4EA, 0xC4E6FA), (0x4A5666, 0x66748A), "wood", 7),
    # Basalt slabs under charred bands, an ember latch
    "ruin_hell": palette((0x1E1A1C, 0x322C30, 0x463E44, 0x5C535A), (0x120E10, 0x26191A, 0x4A2420),
                         (0x5A1A08, 0xF0661A, 0xFFC860), (0x0C0A0B, 0x181415), "metal", 9, fleck=0xB03A12),
}


# ---- painting ------------------------------------------------------------------------------------

def noise(x, y, seed):
    """A fixed 0..1 hash of a texel."""
    h = (x * 73856093) ^ (y * 19349663) ^ (seed * 83492791)
    h = (h ^ (h >> 13)) * 1274126177 & 0xFFFFFFFF
    return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0


def body_texel(pal, x, y, seed, vertical=False):
    """One texel of the body material: boards with grooves and grain, or plates with seams and scratches."""
    groove, dark, mid, light = pal["body"]
    along, across = (y, x) if vertical else (x, y)
    rows = pal["groove"]
    if across % rows == rows - 1:
        return groove
    board = across // rows
    n = noise(x, y, seed)
    if pal["style"] == "wood":
        # A butt joint once along each board, at a spot fixed by the board
        if along == 5 + int(noise(board, 7, seed) * 17):
            return groove
        if pal["fleck"] is not None and n > 0.93:
            return pal["fleck"]
        streak = noise(along // 3, across, seed + 1)
        if streak > 0.72:
            return light
        return dark if streak < 0.2 else mid
    # Metal: flat plates, a lit upper edge, sparse scratches and (hell) glowing cracks
    if pal["fleck"] is not None and n > 0.955:
        return pal["fleck"]
    if across % rows == 0:
        return light
    if n > 0.95:
        return light
    return dark if n < 0.06 else mid


def paint(width, height, texel):
    return [[texel(x, y) for x in range(width)] for y in range(height)]


def side_face(pal, width, height, seed, lid):
    """An outer side of the base or the lid: body material between two corner posts, banded at its outer edge
    (the lid's top, the base's bottom) and shadowed along the seam where lid and base meet."""
    t_dark, t_mid, t_light = pal["trim"]

    def texel(x, y):
        edge = min(x, width - 1 - x)
        outer = y if lid else height - 1 - y      # distance from the banded edge
        seam = height - 1 - y if lid else y        # distance from the lid/base seam
        if edge < 2 or outer < 2:
            # A rivet where a post meets the band, and one mid-post on the taller base
            if edge == 1 and (outer == 1 or (not lid and seam == 4)):
                return t_light
            return t_mid
        if edge == 2 or outer == 2:
            return t_dark
        if seam == 0:
            return t_dark if lid else t_mid
        return body_texel(pal, x, y, seed)
    return paint(width, height, texel)


def top_face(pal, size, seed):
    """The lid's top: boards inside a banded frame with a rivet in each corner."""
    t_dark, t_mid, t_light = pal["trim"]

    def texel(x, y):
        edge = min(x, y, size - 1 - x, size - 1 - y)
        if edge < 2:
            corner = min(x, size - 1 - x) == 1 and min(y, size - 1 - y) == 1
            return t_light if corner else t_mid
        if edge == 2:
            return t_dark
        return body_texel(pal, x, y, seed)
    return paint(size, size, texel)


def bottom_face(pal, size, seed):
    groove, dark, _, _ = pal["body"]
    return paint(size, size, lambda x, y: groove if y % pal["groove"] == pal["groove"] - 1
                 else (groove if noise(x, y, seed) < 0.2 else dark))


def rim_face(pal, size, seed):
    """The cut edge of the walls (the top of the base, the underside of the lid): the plain body shade."""
    _, dark, mid, _ = pal["body"]
    return paint(size, size, lambda x, y: dark if noise(x, y, seed) < 0.25 else mid)


def inside_face(pal, width, height, seed, lit_rows):
    """An inside wall or floor: the dark inside shade, a little lighter in the rows nearest the opening."""
    in_dark, in_mid = pal["inside"]

    def texel(x, y):
        if y < lit_rows:
            return in_mid
        return in_mid if noise(x, y, seed) > 0.86 else in_dark
    return paint(width, height, texel)


def latch_front(pal):
    l_dark, l_mid, l_light = pal["latch"]
    rows = (
        "dddd",
        "dlmd",
        "dlmd",
        "dmmd",
        "dkkd",
        "dkkd",
        "dmmd",
        "dddd",
    )
    key = {"d": l_dark, "m": l_mid, "l": l_light, "k": l_dark}
    return [[key[c] for c in row] for row in rows]


def faces(pal):
    """Every face of the skin, by region name, as rows of RGB texels."""
    l_dark, l_mid, _ = pal["latch"]
    t = TEXELS
    out = {}
    for i, name in enumerate(("base_front", "base_back", "base_left", "base_right")):
        out[name] = side_face(pal, 14 * t, 10 * t, 11 + i, lid=False)
    for i, name in enumerate(("lid_front", "lid_back", "lid_left", "lid_right")):
        out[name] = side_face(pal, 14 * t, 4 * t, 21 + i, lid=True)
    out["lid_top"] = top_face(pal, 14 * t, 31)
    out["base_bottom"] = bottom_face(pal, 14 * t, 32)
    out["base_rim"] = rim_face(pal, 14 * t, 33)
    out["lid_rim"] = rim_face(pal, 14 * t, 34)
    out["base_floor"] = inside_face(pal, 14 * t, 14 * t, 35, lit_rows=0)
    out["lid_ceiling"] = inside_face(pal, 14 * t, 14 * t, 36, lit_rows=0)
    out["base_inner"] = inside_face(pal, 14 * t, 10 * t, 37, lit_rows=3)
    out["lid_inner"] = inside_face(pal, 14 * t, 4 * t, 38, lit_rows=0)
    out["latch_front"] = latch_front(pal)
    out["latch_side"] = paint(1 * t, 4 * t, lambda x, y: l_dark if y in (0, 4 * t - 1) else l_mid)
    out["latch_end"] = paint(2 * t, 1 * t, lambda x, y: l_mid)
    out["latch_back"] = paint(2 * t, 4 * t, lambda x, y: l_dark)
    return out


def blit(canvas, face, x0, y0):
    for y, row in enumerate(face):
        for x, color in enumerate(row):
            canvas[y0 + y][x0 + x] = color + (255,)


def skin(pal):
    size = SKIN_UNITS * TEXELS
    canvas = [[(0, 0, 0, 0)] * size for _ in range(size)]
    painted = faces(pal)
    for name, (x, y, _, _) in REGIONS.items():
        blit(canvas, painted[name], x * TEXELS, y * TEXELS)
    return canvas


def tiles(pal):
    """The static twin's three tiles. models/block/chest.json crops each face out of its tile by the face's place
    in the cell, so the lid lands on tile rows 2..6 and the base on rows 6..16 (in model pixels), one pixel in from
    each side; the rest of a tile is trim, opaque so the tile never reads as a cutout."""
    painted = faces(pal)
    t = TEXELS
    fill = pal["trim"][1] + (255,)

    def blank():
        return [[fill] * TILE for _ in range(TILE)]

    front = blank()
    blit(front, painted["lid_front"], 1 * t, 2 * t)
    blit(front, painted["base_front"], 1 * t, 6 * t)
    blit(front, painted["latch_front"], 7 * t, 4 * t)
    side = blank()
    blit(side, painted["lid_left"], 1 * t, 2 * t)
    blit(side, painted["base_left"], 1 * t, 6 * t)
    top = blank()
    blit(top, painted["lid_top"], 1 * t, 1 * t)
    return {"front": front, "side": side, "top": top}


# ---- PNG -----------------------------------------------------------------------------------------

def png_bytes(canvas):
    height, width = len(canvas), len(canvas[0])

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = bytearray()
    for row in canvas:
        raw.append(0)
        for r, g, b, a in row:
            raw += bytes((r, g, b, a))
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))


def outputs():
    files = {}
    for name, pal in VARIANTS.items():
        files[os.path.join(SKIN_DIR, f"chest_{name}.png")] = png_bytes(skin(pal))
        for part, canvas in tiles(pal).items():
            files[os.path.join(BLOCK_DIR, f"chest_{name}_{part}.png")] = png_bytes(canvas)
    return files


def main():
    check = "--check" in sys.argv[1:]
    stale = 0
    for path, data in outputs().items():
        current = None
        if os.path.exists(path):
            with open(path, "rb") as f:
                current = f.read()
        if current == data:
            continue
        stale += 1
        if check:
            print(f"differs: {os.path.relpath(path, ROOT)}")
            continue
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)
        print(f"wrote {os.path.relpath(path, ROOT)}")
    if check and stale:
        sys.exit(1)
    if not stale:
        print("chest textures up to date")


if __name__ == "__main__":
    main()
