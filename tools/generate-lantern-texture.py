"""Deterministic generator of the hanging lantern tile (OpenGLTest/assets/textures/blocks/lantern.png).

One 32x32 opaque atlas tile (2 texels per model unit) holding every part the lantern model
(assets/models/block/lantern.json) crops out of it with explicit uvs. The model baker mirrors a
Minecraft uv on the way in (ModelBaker.FlipU/FlipV): a side face samples texel column 32 - 2u and
an up or down face texel row 32 - 2v, so each uv below is written to land on its region:

    body sides   uv [10, 0, 16, 7]  -> texels x 0..11,  y 0..13   a copper cage around a warm glow
    body ends    uv [0, 1, 6, 7]    -> texels x 0..11,  y 18..29  a copper plate (top and bottom)
    cap sides    uv [6, 0, 10, 2]   -> texels x 12..19, y 0..3    the lid's rim
    cap top      uv [6, 10, 10, 14] -> texels x 12..19, y 4..11   the lid, a dark eyelet in its middle
    chain        uv [4, 0, 5, 5]    -> texels x 22..23, y 0..9    alternating chain links

so moving a part here means moving that uv in the model too. Every part is drawn symmetric about
its own vertical center line, since the mirror also flips which way it reads. Every other texel is
filled with the darkest copper, never left transparent, so the tile is drawn opaque (no cutout)
and its mips never bleed a hole into the parts.

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces the file byte for byte. Run from anywhere:
    python -I tools/generate-lantern-texture.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks", "lantern.png")

SIZE = 32

COPPER_DARK = (74, 44, 30)
COPPER = (128, 76, 46)
COPPER_LIGHT = (176, 108, 64)
COPPER_SHINE = (214, 146, 92)
EYELET = (34, 22, 16)
GLOW_HOT = (255, 238, 170)
GLOW = (255, 206, 110)
GLOW_EDGE = (236, 150, 62)


def body_side(x, y):
    """The 12x14 cage side: a two-texel copper frame and a middle bar around a glow that is
    hottest at its center, symmetric left to right."""
    if x < 2 or x > 9 or y < 2 or y > 11:
        # Frame: lit along its top edge, shaded along the bottom, its outer columns darker
        if y == 0:
            return COPPER_LIGHT
        if y == 13 or x in (0, 11):
            return COPPER_DARK
        return COPPER
    if x in (5, 6):
        return COPPER_DARK
    dx = min(abs(x - 3.5), abs(x - 8.5))
    dy = abs(y - 6.5)
    heat = dx + dy * 0.6
    if heat < 1.6:
        return GLOW_HOT
    if heat < 3.4:
        return GLOW
    return GLOW_EDGE


def body_end(x, y):
    """The 12x12 copper plate closing the cage top and bottom, with a raised rim."""
    if x in (0, 11) or y in (0, 11):
        return COPPER_DARK
    if x in (1, 10) or y in (1, 10):
        return COPPER_LIGHT
    return COPPER


def cap_side(x, y):
    """The 8x4 lid rim."""
    if y == 0:
        return COPPER_SHINE
    if y == 3:
        return COPPER_DARK
    return COPPER_LIGHT if x in (2, 5) else COPPER


def cap_top(x, y):
    """The 8x8 lid seen from above, with the eyelet the chain runs through and a lit ring."""
    if 3 <= x <= 4 and 3 <= y <= 4:
        return EYELET
    if x in (0, 7) or y in (0, 7):
        return COPPER_DARK
    if x in (2, 5) or y in (2, 5):
        return COPPER_LIGHT
    return COPPER


def chain(x, y):
    """The 2x10 chain: links two texels long, alternately facing (lit) and edge-on (dark)."""
    link = (y // 2) % 2
    return COPPER_LIGHT if link == 0 else COPPER_DARK


def texel(x, y):
    if x < 12 and y < 14:
        return body_side(x, y)
    if x < 12 and 18 <= y < 30:
        return body_end(x, y - 18)
    if 12 <= x < 20 and y < 4:
        return cap_side(x - 12, y)
    if 12 <= x < 20 and 4 <= y < 12:
        return cap_top(x - 12, y - 4)
    if 22 <= x < 24 and y < 10:
        return chain(x - 22, y)
    return COPPER_DARK


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


def main():
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            row += bytes(texel(x, y)) + b"\xff"
        rows.append(bytes(row))
    write_png(PATH, rows)
    print("wrote", os.path.relpath(PATH, ROOT))


if __name__ == "__main__":
    main()
