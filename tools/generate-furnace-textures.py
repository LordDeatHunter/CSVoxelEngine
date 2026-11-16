"""Deterministic generator of the kiln, bloomery and alloy furnace model tiles (placeholder art, listed in USER_TODO.md).

Writes, under OpenGLTest/assets/textures/blocks (32x32 each):
  kiln_soot.png      sooty black, the floor of a flue, throat or chimney well
  kiln_daub.png      smooth fired clay plaster for the mud brick kiln's dome (the fired mud tile, evened out and lightened)
  bloomery_band.png  an iron strap with rivets, the hoops around the bloomery's stack
  alloy_band.png     a copper strap with tin rivets, the course under the alloy furnace's roof
  ember_bed.png      glowing coals, drawn self-lit on a lit hearth (StationPasses, SmithingPasses)

Both straps repeat every 4 rows (2 model units), so a band face 2 units tall on an even height shows one whole strap
whatever rows its default UV lands on. The fired mud, copper and tin are read from their committed tiles, so a
retouched source carries over on a re-run; the rest are fixed ramps and an integer hash. No randomness and no image
library: the stdlib PNG reader and writer the other generators use, so re-running reproduces every file byte for byte.
Run from the repo root:
    python -I tools/generate-furnace-textures.py           (writes the five tiles)
    python -I tools/generate-furnace-textures.py --check   (exit 1 if any file differs)
"""
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCK_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")
SIZE = 32

SOOT = ((16, 12, 11), (24, 18, 16), (34, 26, 22), (46, 36, 30))
IRON = ((46, 46, 52), (82, 82, 90), (118, 118, 128), (156, 156, 166))
# Coals, coldest first: charcoal, dull red, orange, yellow, white-hot
EMBER = ((30, 18, 14), (96, 26, 10), (196, 70, 14), (246, 150, 30), (255, 226, 130))


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


def noise(x, y, salt):
    """An integer hash of a texel, 0..255, the same on every run."""
    h = (x * 374761393 + y * 668265263 + salt * 1274126177) & 0xFFFFFFFF
    h = ((h ^ (h >> 13)) * 1103515245) & 0xFFFFFFFF
    return (h ^ (h >> 16)) & 0xFF


def average(sample):
    total = [0, 0, 0]
    for y in range(SIZE):
        for x in range(SIZE):
            px = sample(x, y)
            for c in range(3):
                total[c] += px[c]
    return tuple(t // (SIZE * SIZE) for t in total)


def soot():
    def texel(x, y):
        n = noise(x, y, 1)
        # Mostly the two darkest shades, a few lighter flecks of ash
        shade = 0 if n < 120 else (1 if n < 215 else (2 if n < 248 else 3))
        return SOOT[shade] + (255,)

    return texel


def daub():
    mud = tile("fired_mud")
    mean = average(mud)

    def texel(x, y):
        px = mud(x, y)
        # Pull every texel most of the way to the tile's mean, lighten, and leave faint horizontal trowel marks
        streak = -6 if (y + (noise(x // 6, y, 2) & 1)) % 7 == 0 else 0
        grain = (noise(x, y, 3) % 9) - 4
        return tuple(min(255, max(0, (px[c] + 3 * mean[c]) // 4 + 44 + streak + grain)) for c in range(3)) + (255,)

    return texel


def strap(ramp, rivet):
    """A strap repeating every 4 rows: lit edge, body, body with a rivet every 8 texels, shadow edge."""

    def texel(x, y):
        row = y % 4
        if row == 0:
            return ramp[2] + (255,)
        if row == 3:
            return ramp[0] + (255,)
        if row == 2 and x % 8 == 3:
            return rivet + (255,)
        return ramp[1] + (255,)

    return texel


def bloomery_band():
    return strap(IRON, IRON[3])


def alloy_band():
    copper = average(tile("copper_ingot_block"))
    tin = average(tile("tin_ingot_block"))
    ramp = (tuple(c * 6 // 10 for c in copper), copper, tuple(min(255, c * 13 // 10) for c in copper))
    return strap(ramp, tin)


def ember_bed():
    def texel(x, y):
        # Coals are 4x4 lumps on a staggered grid; each lump has its own heat, its rim is cooler and the gaps glow
        row = y // 4
        lx = (x + (2 if row % 2 else 0)) % SIZE
        heat = 1 + noise(lx // 4, row, 4) % 3
        edge = lx % 4 == 0 or y % 4 == 0
        if edge:
            heat = 4 if noise(x, y, 5) < 70 else 3
        elif noise(x, y, 6) < 40:
            heat = max(0, heat - 1)
        return EMBER[heat] + (255,)

    return texel


TILES = (("kiln_soot", soot), ("kiln_daub", daub), ("bloomery_band", bloomery_band), ("alloy_band", alloy_band),
         ("ember_bed", ember_bed))


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
