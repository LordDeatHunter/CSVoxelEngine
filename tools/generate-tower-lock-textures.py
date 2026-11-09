"""Deterministic generator of the battle tower lock tiles (placeholder art, listed in USER_TODO.md).

Writes, under OpenGLTest/assets/textures/blocks:
  tower_door.png      the iron gate sealing each floor's stairwell (plans/BATTLE_TOWER.md BT-4): a cutout
                      portcullis, a frame round three vertical and two horizontal bars, the gaps fully clear
  skeleton_pile.png   the bone heap a floor's skeletons rise from: opaque, cropped by the skeleton_pile model

No randomness and no image library: every texel comes from a fixed rule or a hand-drawn char map, painted
with a small hand-picked palette, and the PNG writer below (stdlib zlib and struct, filter 0, level 9) is the
same one the mob generators use, so re-running reproduces both files byte for byte. Run from the repo root:
    python -I tools/generate-tower-lock-textures.py           (writes the two tiles)
    python -I tools/generate-tower-lock-textures.py --check   (exit 1 if either file differs)
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
