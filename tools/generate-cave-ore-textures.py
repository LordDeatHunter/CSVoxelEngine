"""Deterministic generator of the cave biome exclusive ore tiles (placeholder art, listed in USER_TODO.md).

Writes, under OpenGLTest/assets/textures/blocks (plans/CAVE_BIOMES.md CB-2):
  amethyst_ore.png   the crystal caves' ore: violet mineral
  jade_ore.png       the fungal caves' ore: pale green mineral
  cinnabar_ore.png   the magma-touched deep's ore: red mineral

Each is a palette swap of the regular ore tile, never noise, exactly as tin_ore.png recolors malachite_ore.png: the
script decodes malachite_ore.png, keeps its brown matrix pixels (red over green) as they are, and repaints every
green mineral pixel with a six-color hand palette, the color picked by the source shade's luminance rank (darkest
source shade -> darkest palette color). So the tile keeps malachite's shapes and shading, and only the hue changes.

No randomness and no image library: a small stdlib PNG reader (zlib, filters 0-4, RGB8 or RGBA8) and the same
PNG writer the other generators use (filter 0, zlib level 9), so re-running reproduces every file byte for byte.
Run from the repo root:
    python -I tools/generate-cave-ore-textures.py           (writes the three tiles)
    python -I tools/generate-cave-ore-textures.py --check   (exit 1 if any file differs)
"""
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCK_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")

# ---- palettes, darkest first (one color per malachite green shade) ------------------------------

AMETHYST = ((0x24, 0x14, 0x34), (0x34, 0x1C, 0x4C), (0x4A, 0x28, 0x6E), (0x68, 0x3A, 0x98), (0x8C, 0x56, 0xC4),
            (0xB0, 0x78, 0xE4))
JADE = ((0x1E, 0x2E, 0x1C), (0x28, 0x40, 0x24), (0x38, 0x5C, 0x30), (0x54, 0x80, 0x42), (0x7A, 0xAA, 0x5C),
        (0xAA, 0xCE, 0x80))
CINNABAR = ((0x30, 0x0E, 0x0E), (0x46, 0x14, 0x12), (0x68, 0x1C, 0x18), (0x92, 0x28, 0x20), (0xC0, 0x3A, 0x2A),
            (0xE2, 0x68, 0x4E))

# ---- PNG reading --------------------------------------------------------------------------------


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


# ---- PNG writing --------------------------------------------------------------------------------


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


# ---- the recolor --------------------------------------------------------------------------------


def _luma(px):
    return 299 * px[0] + 587 * px[1] + 114 * px[2]


def _mineral(px):
    """Malachite's green mineral shades; its brown matrix (red over green) is kept as it is."""
    return px[1] > px[0]


def recolor(ore, palette):
    """A texel function: the ore tile with every mineral shade repainted by its luminance rank."""
    shades = sorted({_luma(px) for row in ore for px in row if _mineral(px)})
    if len(shades) != len(palette):
        raise ValueError("expected %d mineral shades in malachite_ore.png, found %d" % (len(palette), len(shades)))

    def texel(x, y):
        px = ore[y][x]
        if not _mineral(px):
            return px[:3] + (255,)
        return palette[shades.index(_luma(px))] + (255,)

    return texel


def outputs():
    width, height, ore = read_png(os.path.join(BLOCK_DIR, "malachite_ore.png"))
    if width != height:
        raise ValueError("malachite_ore.png must be square")
    return [
        (os.path.join(BLOCK_DIR, name + ".png"), png_bytes(width, recolor(ore, palette)))
        for name, palette in (("amethyst_ore", AMETHYST), ("jade_ore", JADE), ("cinnabar_ore", CINNABAR))
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
