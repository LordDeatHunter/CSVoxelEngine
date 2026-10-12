"""Deterministic generator of the oak trapdoor and door tiles.

Writes 32x32 opaque RGBA tiles (the size of every block tile) into OpenGLTest/assets/textures/blocks/:
  oak_trapdoor.png     a square hatch: a dark frame around four vertical boards, a cross brace
                       and two dark slits, in the plank palette
  oak_door_bottom.png  the door's lower half: the frame down both sides and along the bottom,
                       two recessed panels and an iron ring pull centered near its top
  oak_door_top.png     the door's upper half: the frame down both sides and along the top, four
                       dark window panes over a recessed panel
The trapdoor models (assets/models/block/trapdoor_*.json) and door models (door_*.json) crop
their faces out of these with default (position-derived) uvs, so the whole tile shows on the
broad faces and its edge rows and columns on the thin sides; every texel is opaque, so the blocks
are drawn without cutout. The two door tiles meet without a frame row where the halves touch.
The pull sits on the door's center line because the model baker mirrors a face's uv
(ModelBaker.FlipU), so an off-center handle would land on the hinge edge seen from one side.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces every file byte for byte. Run from anywhere:
    python -I tools/generate-door-textures.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCKS = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")

SIZE = 32

# The plank tile's palette (tools/generate-plank-texture.py), plus a darker frame and a slit
EDGE = (184, 145, 94)
LIGHT = (172, 134, 86)
BASE = (158, 121, 76)
DARK = (139, 104, 64)
SEAM = (88, 62, 39)
FRAME = (118, 86, 52)
FRAME_DARK = (96, 69, 42)
SLIT = (46, 32, 21)
PANE = (52, 60, 70)
PANE_LIGHT = (92, 104, 118)
IRON = (58, 58, 62)
IRON_LIGHT = (128, 128, 134)


def hash2(a, b):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def board(x, y, salt):
    """Vertical-grain board texel: streaks run down the board."""
    streak = hash2(x + salt * 31, y // 5) % 9
    if streak < 2:
        return DARK
    if streak > 6:
        return LIGHT
    return BASE


def trapdoor(x, y):
    # A three-texel frame, darker on its outer row
    if x in (0, SIZE - 1) or y in (0, SIZE - 1):
        return FRAME_DARK
    if x < 3 or x > SIZE - 4 or y < 3 or y > SIZE - 4:
        return FRAME
    # A cross brace through the middle
    if y in (15, 16):
        return EDGE if y == 15 else SEAM
    # Two dark slits in the upper and lower panels
    if 13 <= x <= 18 and (y in (8, 9) or y in (22, 23)):
        return SLIT
    # Four vertical boards between the frame, a seam between each
    if (x - 3) % 7 == 6:
        return SEAM
    return board(x, y, (x - 3) // 7)


def door_frame(x, y, top):
    """The door's frame: both sides, and the top edge of the upper tile or the bottom edge of the
    lower one; None inside it."""
    edge_row = 0 if top else SIZE - 1
    inner_row = range(0, 3) if top else range(SIZE - 3, SIZE)
    if x in (0, SIZE - 1) or y == edge_row:
        return FRAME_DARK
    if x < 3 or x > SIZE - 4 or y in inner_row:
        return FRAME
    return None


def recessed(x, y, x0, y0, x1, y1):
    """A recessed panel's rim inside [x0, x1] x [y0, y1]: shadowed top and left, lit bottom and
    right; None off the rim."""
    if not (x0 <= x <= x1 and y0 <= y <= y1):
        return None
    if y == y0 or x == x0:
        return SEAM
    if y == y1 or x == x1:
        return EDGE
    return None


def door_bottom(x, y):
    frame = door_frame(x, y, top=False)
    if frame is not None:
        return frame
    # An iron ring pull on the center line near the top: a ring around a dark middle
    if 13 <= x <= 18 and 2 <= y <= 7:
        if 15 <= x <= 16 and 4 <= y <= 5:
            return SLIT
        return IRON_LIGHT if y == 2 else IRON
    # Two recessed panels, one over the other
    for rim in (recessed(x, y, 6, 9, 25, 15), recessed(x, y, 6, 18, 25, 26)):
        if rim is not None:
            return rim
    if (x - 3) % 7 == 6:
        return SEAM
    return board(x, y, (x - 3) // 7 + 4)


def door_top(x, y):
    frame = door_frame(x, y, top=True)
    if frame is not None:
        return frame
    # Four window panes in a 2x2 grid with a cross bar between them
    if 6 <= x <= 25 and 5 <= y <= 18:
        if x in (15, 16) or y in (11, 12):
            return FRAME
        lit = (x in (7, 17) and y in range(6, 11)) or (y in (6, 13) and x in range(7, 14))
        return PANE_LIGHT if lit else PANE
    if x in (5, 26) and 4 <= y <= 19 or y in (4, 19) and 5 <= x <= 26:
        return FRAME_DARK
    rim = recessed(x, y, 6, 22, 25, 30)
    if rim is not None:
        return rim
    if (x - 3) % 7 == 6:
        return SEAM
    return board(x, y, (x - 3) // 7 + 8)


def write_png(path, pixel):
    """Minimal RGBA8 PNG of pixel(x, y): IHDR, one IDAT (filter 0, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    rows = []
    for y in range(SIZE):
        row = bytearray(b"\x00")
        for x in range(SIZE):
            row += bytes(pixel(x, y)) + b"\xff"
        rows.append(bytes(row))
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)
    print("wrote", os.path.relpath(path, ROOT))


def main():
    write_png(os.path.join(BLOCKS, "oak_trapdoor.png"), trapdoor)
    write_png(os.path.join(BLOCKS, "oak_door_bottom.png"), door_bottom)
    write_png(os.path.join(BLOCKS, "oak_door_top.png"), door_top)


if __name__ == "__main__":
    main()
