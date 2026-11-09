"""Deterministic generator of the battle tower lock tiles (placeholder art, listed in USER_TODO.md).

Writes, under OpenGLTest/assets/textures/blocks:
  tower_door.png      the iron gate sealing each floor's stairwell (plans/BATTLE_TOWER.md BT-4): a cutout
                      portcullis, a frame round three vertical and two horizontal bars, the gaps fully clear
  skeleton_pile.png   the bone heap a floor's skeletons rise from: opaque, cropped by the skeleton_pile model
  tower_altar_top.png the roof altar's top face (BT-6): a stone slab, a grooved rune ring with eight glowing runes and
                      a crystal set in its center; opaque, the altar's sides and bottom wear stone_bricks

No randomness and no image library: every texel comes from a fixed rule or a hand-drawn char map, painted
with a small hand-picked palette, and the PNG writer below (stdlib zlib and struct, filter 0, level 9) is the
same one the mob generators use, so re-running reproduces every file byte for byte. Run from the repo root:
    python -I tools/generate-tower-lock-textures.py           (writes the three tiles)
    python -I tools/generate-tower-lock-textures.py --check   (exit 1 if any file differs)
"""
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCK_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")
SIZE = 32

# ---- palettes -----------------------------------------------------------------------------------

# The gate: four grays picked from assets/textures/items/iron_ingot.png.
IRON_OUTLINE = (0x20, 0x20, 0x20)
IRON_BODY = (0x4C, 0x4C, 0x4C)
IRON_HIGHLIGHT = (0xA1, 0xA1, 0xA1)
IRON_RIVET = (0xD2, 0xD2, 0xD2)

# The heap: five shades picked from assets/textures/items/bone.png and the skeleton's skin.
BONE_OUTLINE = (0x2E, 0x26, 0x20)
BONE_DARK = (0x92, 0x8A, 0x74)
BONE_SHADOW = (0xC2, 0xBA, 0xA0)
BONE = (0xE2, 0xDC, 0xC6)
BONE_HIGHLIGHT = (0xF6, 0xF2, 0xE2)

# The altar: the four grays of assets/textures/blocks/stone_bricks.png, two cyans of crystal_block.png, and BONE_HIGHLIGHT.
STONE_GROOVE = (0x56, 0x56, 0x56)
STONE_SHADOW = (0x65, 0x65, 0x65)
STONE = (0x79, 0x79, 0x79)
STONE_LIT = (0x90, 0x90, 0x90)
CRYSTAL = (0x79, 0xC6, 0xD6)
CRYSTAL_LIT = (0xC5, 0xF3, 0xF8)

CLEAR = (0, 0, 0, 0)

# ---- the gate -----------------------------------------------------------------------------------

FRAME = 2                                   # the frame's width on every edge
VERTICAL_BARS = ((6, 9), (14, 17), (22, 25))  # inclusive x spans, mirror-symmetric about the middle
HORIZONTAL_BARS = ((8, 11), (20, 23))       # inclusive y spans, mirror-symmetric about the middle


def _span_of(v, spans):
    for lo, hi in spans:
        if lo <= v <= hi:
            return v - lo
    return None


def _bar_shade(offset):
    """Across a four-pixel bar: an outline edge, the lit side, the body, an outline edge."""
    return (IRON_OUTLINE, IRON_HIGHLIGHT, IRON_BODY, IRON_OUTLINE)[offset]


def door_texel(x, y):
    edge = min(x, y, SIZE - 1 - x, SIZE - 1 - y)
    if edge < FRAME:
        if edge == 0:
            return IRON_OUTLINE + (255,)
        lit = x == 1 or y == 1                   # the frame's inner ring is lit on its top and left
        return (IRON_HIGHLIGHT if lit and x < SIZE - 2 and y < SIZE - 2 else IRON_BODY) + (255,)
    across = _span_of(x, VERTICAL_BARS)
    down = _span_of(y, HORIZONTAL_BARS)
    if across is not None and down is not None:
        if across in (1, 2) and down in (1, 2):
            return IRON_RIVET + (255,)            # a rivet where the bars cross
        return IRON_OUTLINE + (255,)
    if across is not None:
        return _bar_shade(across) + (255,)
    if down is not None:
        return _bar_shade(down) + (255,)
    return CLEAR


# ---- the heap -----------------------------------------------------------------------------------

# Half resolution: each char is a 2x2 block of the tile. The skull's sockets sit in map rows 10..11, cols 6..10,
# where the model's skull sides crop (tile rows 18..25, cols 10..21); the base's sides crop tile rows 26..31.
HEAP = [
    "..+.s#ds..+..sd#",
    "+..s#d.+.+..s#d.",
    ".ss#d.+..s.s#d.+",
    "s#d..+.ss##ds.+.",
    "#d.+..s#d+..+.s#",
    "d.+..s#d.+.+..sd",
    "+.ss#d..+..s.s#d",
    ".s#d.+..s#ds..+.",
    "s#d.+.ss#d.+.s#d",
    "#d.+.+....+.s#d.",
    "d.+.s.##.##.s#d+",
    ".+..s.#d.#d.s.+.",
    "+.ss..+...+..ss.",
    "ss#ds.s.d.s.sd#s",
    "#d.+.ss#d.+.s#d.",
    "d##ds#dd##ds##ds",
]
HEAP_COLORS = {"#": BONE_OUTLINE, "d": BONE_DARK, "s": BONE_SHADOW, ".": BONE, "+": BONE_HIGHLIGHT}


def pile_texel(x, y):
    return HEAP_COLORS[HEAP[y // 2][x // 2]] + (255,)


# ---- the altar ---------------------------------------------------------------------------------

# Integer geometry in half-texel units about the tile's center: (2x - 31, 2y - 31) is odd on both axes, so the art is
# exactly mirror-symmetric on both axes and both diagonals, and no float ever decides a texel.
RING_INNER2 = 20 * 20          # squared half-unit radius where the groove starts (10 texels)
RING_OUTER2 = 26 * 26          # where it ends (13 texels)
LIP2 = 18 * 18                 # the lit lip just inside the groove (9 texels)
CRYSTAL_REACH = 10             # the crystal diamond's |dx| + |dy| in half units (5 texels)


def altar_texel(x, y):
    dx, dy = 2 * x - (SIZE - 1), 2 * y - (SIZE - 1)
    edge = min(x, y, SIZE - 1 - x, SIZE - 1 - y)
    if edge == 0:
        return STONE_GROOVE + (255,)
    if edge == 1:
        lit = (x == 1 or y == 1) and x < SIZE - 2 and y < SIZE - 2   # the slab's bevel is lit on its top and left
        return (STONE_LIT if lit else STONE_SHADOW) + (255,)
    reach = abs(dx) + abs(dy)
    if reach <= CRYSTAL_REACH:
        if reach >= CRYSTAL_REACH - 1:
            return STONE_GROOVE + (255,)                          # the setting round the crystal
        if (abs(dx), abs(dy)) == (1, 3) and dx < 0 and dy < 0:
            return BONE_HIGHLIGHT + (255,)                        # one glint on its lit facet
        return (CRYSTAL_LIT if dx + dy < 0 else CRYSTAL) + (255,)  # lit toward the top left
    r2 = dx * dx + dy * dy
    if RING_INNER2 <= r2 < RING_OUTER2:
        on_axis = abs(dx) <= 3 or abs(dy) <= 3
        on_diagonal = abs(abs(dx) - abs(dy)) <= 4
        if on_axis or on_diagonal:
            middle = 22 * 22 <= r2 < 24 * 24
            return (CRYSTAL_LIT if middle else CRYSTAL) + (255,)  # a rune glowing in the groove
        return STONE_GROOVE + (255,)
    if LIP2 <= r2 < RING_INNER2:
        return STONE_LIT + (255,)
    return STONE + (255,)


# ---- PNG and outputs ----------------------------------------------------------------------------

def png_bytes(size, texel):
    """Minimal RGBA8 PNG: IHDR, one IDAT (filter 0 on every row, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    rows = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            row += bytes(texel(x, y))
        rows.append(b"\x00" + bytes(row))
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
    png += chunk(b"IEND", b"")
    return png


def outputs():
    assert len(HEAP) == SIZE // 2 and all(len(row) == SIZE // 2 for row in HEAP), "the heap map must be 16x16"
    return [
        (os.path.join(BLOCK_DIR, "tower_door.png"), png_bytes(SIZE, door_texel)),
        (os.path.join(BLOCK_DIR, "skeleton_pile.png"), png_bytes(SIZE, pile_texel)),
        (os.path.join(BLOCK_DIR, "tower_altar_top.png"), png_bytes(SIZE, altar_texel)),
    ]


def main():
    check = "--check" in sys.argv
    stale = []
    for path, data in outputs():
        old = None
        if os.path.exists(path):
            with open(path, "rb") as f:
                old = f.read()
        same = old == data
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
