"""Deterministic generator of the vulcanite item sprites (placeholder art, listed in USER_TODO.md).

Writes, under OpenGLTest/assets/textures/items:
  vulcanite_pickaxe_head.png, vulcanite_axe_head.png
The sword head is not generated any more: bleeding_edge_head.png is hand-drawn art and is never written.

Each is a palette swap of its iron sprite, never noise: the iron's cool gray metal pixels (blue at or over red) are
repainted with the six shades of the user-authored vulcanite_shard.png, picked by the source shade's luminance rank
spread over the six (darkest source shade -> darkest shard shade), and every other pixel (the wooden haft, the
transparent border) is kept as it is. So a sprite keeps the iron tool's silhouette and shading and only the metal's
color changes. vulcanite_shard.png, vulcanite_ingot.png (refined vulcanite), vulcanite_pickaxe.png and
vulcanite_axe.png are committed art and never written.

No randomness and no image library: a small stdlib PNG reader (zlib, filters 0-4, RGB8 or RGBA8) and the PNG writer
the other generators use (filter 0, zlib level 9), so re-running reproduces every file byte for byte.
Run from the repo root:
    python -I tools/generate-vulcanite-sprites.py           (writes the two sprites)
    python -I tools/generate-vulcanite-sprites.py --check   (exit 1 if any file differs)
"""
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ITEM_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items")

# (output, iron source) pairs, both under ITEM_DIR
SPRITES = (
    ("vulcanite_pickaxe_head", "iron_pickaxe_head"),
    ("vulcanite_axe_head", "iron_axe_head"),
)


def _paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def read_png(path):
    """Returns (width, height, rows of RGBA tuples) for an 8-bit RGB or RGBA, non-interlaced PNG."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(path + ": not a PNG")
    pos, idat, width, height, channels = 8, b"", 0, 0, 0
    while pos < len(data):
        length = struct.unpack(">I", data[pos:pos + 4])[0]
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if tag == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            if depth != 8 or color not in (2, 6) or interlace != 0:
                raise ValueError(path + ": only 8-bit RGB/RGBA, non-interlaced PNGs are read")
            channels = 3 if color == 2 else 4
        elif tag == b"IDAT":
            idat += body
        elif tag == b"IEND":
            break
    raw = zlib.decompress(idat)
    stride = width * channels
    rows, prev, i = [], bytearray(stride), 0
    for _ in range(height):
        kind = raw[i]
        line = bytearray(raw[i + 1:i + 1 + stride])
        i += 1 + stride
        for x in range(stride):
            a = line[x - channels] if x >= channels else 0
            b = prev[x]
            c = prev[x - channels] if x >= channels else 0
            if kind == 1:
                line[x] = (line[x] + a) & 0xFF
            elif kind == 2:
                line[x] = (line[x] + b) & 0xFF
            elif kind == 3:
                line[x] = (line[x] + ((a + b) >> 1)) & 0xFF
            elif kind == 4:
                line[x] = (line[x] + _paeth(a, b, c)) & 0xFF
            elif kind != 0:
                raise ValueError(path + ": bad filter type")
        prev = line
        row = []
        for x in range(width):
            px = tuple(line[x * channels:(x + 1) * channels])
            row.append(px if channels == 4 else px + (255,))
        rows.append(row)
    return width, height, rows


def png_bytes(width, height, texel):
    """Minimal RGBA8 PNG: IHDR, one IDAT (filter 0 on every row, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    rows = []
    for y in range(height):
        row = bytearray()
        for x in range(width):
            row += bytes(texel(x, y))
        rows.append(b"\x00" + bytes(row))
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
    png += chunk(b"IEND", b"")
    return png


def _luma(px):
    return 299 * px[0] + 587 * px[1] + 114 * px[2]


def _metal(px):
    """The iron's cool gray metal (blue at or over red); the warm brown haft is kept as it is."""
    return px[3] > 0 and px[2] >= px[0]


def shard_palette():
    """The shard's opaque shades, darkest first."""
    _, _, rows = read_png(os.path.join(ITEM_DIR, "vulcanite_shard.png"))
    return sorted({px[:3] for row in rows for px in row if px[3] > 0}, key=_luma)


def recolor(rows, palette):
    """A texel function: the sprite with every metal shade repainted by its luminance rank, spread over the palette."""
    shades = sorted({_luma(px) for row in rows for px in row if _metal(px)})
    last = max(len(shades) - 1, 1)

    def texel(x, y):
        px = rows[y][x]
        if not _metal(px):
            return px
        rank = shades.index(_luma(px))
        return palette[round(rank * (len(palette) - 1) / last)] + (px[3],)

    return texel


def outputs():
    palette = shard_palette()
    result = []
    for name, source in SPRITES:
        width, height, rows = read_png(os.path.join(ITEM_DIR, source + ".png"))
        result.append((os.path.join(ITEM_DIR, name + ".png"), png_bytes(width, height, recolor(rows, palette))))
    return result


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
