"""Deterministic art of the bloomery chain: the bloomery_bricks, slag_bricks and anvil block tiles (recolors of
mud_bricks.png and cobblestone.png, six colors ranked dark to light mapped onto a hand-picked palette, so the bond and
shading are kept) and the bloom, slag and smithing_hammer item sprites (32x32, small palettes, drawn from fixed shapes).
PNGs are encoded with zlib level 9 and filter 0, so re-runs are byte-identical. Run: python tools/generate-bloomery-textures.py
"""
import os
import struct
import zlib

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TEX = os.path.join(ROOT, "OpenGLTest", "assets", "textures")
BLOCKS = os.path.join(TEX, "blocks")
ITEMS = os.path.join(TEX, "items")

# dark (mortar) to light (course edge)
PALETTES = {
    "bloomery_bricks": ("mud_bricks", [(34, 28, 26), (110, 52, 36), (134, 66, 44), (156, 80, 52), (176, 98, 62), (112, 100, 92)]),
    "slag_bricks": ("mud_bricks", [(22, 28, 26), (44, 62, 54), (56, 78, 66), (72, 96, 82), (92, 118, 100), (132, 152, 140)]),
    "anvil": ("cobblestone", [(24, 26, 32), (44, 48, 58), (60, 65, 76), (78, 84, 96), (100, 108, 122), (136, 144, 158)]),
}


def luminance(rgb):
    return 299 * rgb[0] + 587 * rgb[1] + 114 * rgb[2]


def write_png(path, width, height, rows):
    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + bytes(r) for r in rows)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def recolor_tile(name, source, palette):
    src = Image.open(os.path.join(BLOCKS, source + ".png")).convert("RGBA")
    w, h = src.size
    colors = sorted({src.getpixel((x, y))[:3] for y in range(h) for x in range(w)}, key=luminance)
    step = (len(palette) - 1) / max(1, len(colors) - 1)
    mapping = {c: palette[round(i * step)] for i, c in enumerate(colors)}
    rows = []
    for y in range(h):
        row = bytearray()
        for x in range(w):
            r, g, b, a = src.getpixel((x, y))
            row += bytes(mapping[(r, g, b)]) + bytes([a])
        rows.append(row)
    write_png(os.path.join(BLOCKS, name + ".png"), w, h, rows)


def canvas():
    return [[(0, 0, 0, 0)] * 32 for _ in range(32)]


def save(name, px):
    write_png(os.path.join(ITEMS, name + ".png"), 32, 32, [b"".join(bytes(p) for p in row) for row in px])


def disc(px, cx, cy, rx, ry, pick):
    """Fill an ellipse; pick(dx, dy) chooses the color from the offset to the center."""
    for y in range(32):
        for x in range(32):
            dx, dy = (x + 0.5 - cx) / rx, (y + 0.5 - cy) / ry
            if dx * dx + dy * dy <= 1:
                px[y][x] = tuple(pick(x + 0.5 - cx, y + 0.5 - cy)) + (255,)


def lump(name, outline, shades, specks):
    """A lumpy blob of three overlapping ellipses: outline rim, a body ramp lit from the upper left, fixed specks."""
    px = canvas()
    shapes = [(15, 18, 11, 8), (11, 14, 7, 6), (20, 13, 6, 5)]
    for cx, cy, rx, ry in shapes:
        disc(px, cx, cy, rx, ry, lambda dx, dy: outline)
    for cx, cy, rx, ry in shapes:
        disc(px, cx, cy, rx - 1.4, ry - 1.4, lambda dx, dy: shades[0] if dx + dy > 3 else shades[1] if dx + dy > -2 else shades[2])
    for x, y, i in specks:
        if px[y][x][3]:
            px[y][x] = specks_color(shades, outline, i) + (255,)
    save(name, px)


def specks_color(shades, outline, i):
    return (outline, shades[3], shades[2])[i]


def hammer():
    px = canvas()
    wood = [(70, 46, 24), (122, 84, 46), (168, 122, 72)]
    stone = [(54, 56, 60), (104, 108, 114), (150, 154, 160)]
    # diagonal handle from the lower left to the upper right, two px thick
    for i in range(4, 22):
        x, y = 3 + i, 28 - i
        for dx, dy in ((0, 0), (1, 0), (0, 1)):
            px[y + dy][x + dx] = wood[1] + (255,)
        px[y - 1][x] = wood[2] + (255,)
        px[y + 1][x + 1] = wood[0] + (255,)
    # the head: a block across the top end, rim, body, highlight
    for y in range(3, 12):
        for x in range(14, 29):
            px[y][x] = stone[0] + (255,)
    for y in range(4, 11):
        for x in range(15, 28):
            px[y][x] = stone[1] + (255,)
    for x in range(15, 28):
        px[4][x] = stone[2] + (255,)
    for y in range(4, 11):
        px[y][15] = stone[2] + (255,)
    save("smithing_hammer", px)


def main():
    for name, (source, palette) in PALETTES.items():
        recolor_tile(name, source, palette)
    lump("bloom", (44, 30, 26), [(96, 62, 40), (150, 98, 58), (206, 138, 76), (232, 176, 100)],
         [(9, 13, 0), (13, 20, 0), (19, 16, 0), (21, 12, 1), (12, 16, 2), (16, 22, 0), (17, 15, 2), (7, 15, 2)])
    lump("slag", (14, 20, 18), [(30, 44, 40), (52, 76, 66), (88, 124, 108), (150, 190, 170)],
         [(9, 13, 2), (13, 20, 0), (19, 16, 0), (21, 12, 1), (12, 16, 2), (16, 22, 0), (17, 15, 2), (7, 15, 0)])
    hammer()
    print("wrote bloomery textures")


if __name__ == "__main__":
    main()
