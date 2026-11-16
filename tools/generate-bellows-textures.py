"""Deterministic generator of the bellows and tuyere block tiles (placeholder art, listed in USER_TODO.md).

Writes, under OpenGLTest/assets/textures/blocks (32x32 each):
  bloomery_tuyere.png  the bloomery's brick tile with a sooty round hole in the middle, ringed in fired mud: the outer
                       face of the clay collar on each side wall where a bellows goes (models/block/bloomery.json)
  bellows_side.png     two oak boards (top and bottom rows of the oak planks tile) with a pleated leather bag between
  bellows_top.png      the oak planks tile with a leather patch and a dark vent in the middle

The bricks, the planks and the fired mud are read from their committed tiles, so a retouched source carries over on a
re-run; the leather is a fixed four-shade ramp. No randomness and no image library: the stdlib PNG reader and writer
the other generators use, so re-running reproduces every file byte for byte.
Run from the repo root:
    python -I tools/generate-bellows-textures.py           (writes the three tiles)
    python -I tools/generate-bellows-textures.py --check   (exit 1 if any file differs)
"""
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCK_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")
SIZE = 32

# Leather, darkest first: fold shadow, body, lit fold, highlight
LEATHER = ((70, 44, 28), (104, 68, 42), (132, 90, 56), (154, 110, 70))
SOOT = ((22, 16, 14), (34, 24, 20), (48, 34, 28))


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


def tile(name):
    """A committed 32x32 block tile's rows, sampled modulo its size so any square tile works."""
    width, height, rows = read_png(os.path.join(BLOCK_DIR, name + ".png"))
    return lambda x, y: rows[(y * height // SIZE) % height][(x * width // SIZE) % width]


def tuyere():
    brick, mud = tile("bloomery_bricks"), tile("fired_mud")
    center = (SIZE - 1) / 2

    def texel(x, y):
        d2 = (x - center) ** 2 + (y - center) ** 2
        if d2 <= 7.5 ** 2:
            # Sooty throat, darker toward the middle and its lower lip lit a little
            shade = 0 if d2 <= 4.5 ** 2 else (1 if y < center + 3 else 2)
            return SOOT[shade] + (255,)
        if d2 <= 11.5 ** 2:
            return mud(x, y)
        return brick(x, y)

    return texel


def bellows_side():
    planks = tile("oak_planks")

    def texel(x, y):
        if y < 6:
            return planks(x, y)
        if y >= SIZE - 6:
            return planks(x, y - SIZE + 12)
        # The bag: a pleat every 5 rows, its crease dark and the row above it lit; the edges pinch in shadow
        pleat = (y - 6) % 5
        shade = 0 if pleat == 4 else (3 if pleat == 0 else (2 if pleat == 1 else 1))
        if x in (0, SIZE - 1):
            shade = 0
        return LEATHER[shade] + (255,)

    return texel


def bellows_top():
    planks = tile("oak_planks")
    center = (SIZE - 1) / 2

    def texel(x, y):
        dx, dy = abs(x - center), abs(y - center)
        if dx <= 2 and dy <= 2:
            return SOOT[1] + (255,)
        if dx <= 8 and dy <= 8:
            edge = dx > 7 or dy > 7
            return LEATHER[0 if edge else (2 if (x + y) % 7 == 0 else 1)] + (255,)
        return planks(x, y)

    return texel


TILES = (("bloomery_tuyere", tuyere), ("bellows_side", bellows_side), ("bellows_top", bellows_top))


def main():
    check = "--check" in sys.argv[1:]
    differs = False
    for name, make in TILES:
        data = png_bytes(SIZE, SIZE, make())
        path = os.path.join(BLOCK_DIR, name + ".png")
        if check:
            with open(path, "rb") as f:
                same = f.read() == data
            if not same:
                print("differs: " + path)
                differs = True
            continue
        with open(path, "wb") as f:
            f.write(data)
        print("wrote " + path)
    sys.exit(1 if differs else 0)


if __name__ == "__main__":
    main()
