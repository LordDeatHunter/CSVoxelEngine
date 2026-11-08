"""Deterministic generator of the battle tower brick tiles (tower_bricks, tower_bricks_sandstone,
tower_bricks_frost) in OpenGLTest/assets/textures/blocks/.

Pixel art has a small, hand-picked palette, so these are not painted from noise: each tile is the
stone brick tile (stone_bricks.png) with every one of its colors swapped one-to-one for one of a
variant palette, so the brick pattern and the palette size match the original exactly. The source
colors are ranked dark to light by luminance (ties broken by the RGB tuple) and mapped onto each
palette in order; a palette whose length differs from the source's color count is an error, so a
changed source tile cannot slip through.

No image library: the source PNG is decoded here (any of the five row filters, 8-bit RGBA only, which
fails loudly on anything else) and the tiles are encoded with zlib at a fixed level and filter 0, so
re-running reproduces the files byte for byte. Never writes into blocks_material/.

Run:    python -I tools/generate-tower-brick-textures.py
Check:  python -I tools/generate-tower-brick-textures.py --check   (exit code 1 when a tile is stale)
"""
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCKS = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")
SOURCE = os.path.join(BLOCKS, "stone_bricks.png")

# Dark to light, one entry per source color (mortar first, the lightest brick face last)
PALETTES = [
    # Weathered, cool blue-gray stone, darker than the plain stone bricks
    ("tower_bricks", [(46, 50, 60), (58, 63, 75), (74, 80, 94), (92, 99, 114), (114, 121, 137)]),
    # Warm tans in the sandstone tile's hue family
    ("tower_bricks_sandstone", [(150, 128, 88), (176, 158, 114), (196, 180, 138), (216, 202, 162), (238, 226, 190)]),
    # Pale icy blue-whites in the frost stone tile's family
    ("tower_bricks_frost", [(92, 108, 128), (108, 124, 144), (128, 146, 166), (158, 176, 194), (192, 210, 226)]),
]


def paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def read_png(path):
    """Decodes an 8-bit RGBA, non-interlaced PNG into (width, height, rows of RGBA bytes)."""
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"{path} is not a PNG")
    pos, idat, header = 8, b"", None
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        tag = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if tag == b"IHDR":
            header = struct.unpack(">IIBBBBB", body)
        elif tag == b"IDAT":
            idat += body
    if header is None:
        raise ValueError(f"{path} has no IHDR")
    width, height, depth, color, _, _, interlace = header
    if depth != 8 or color != 6 or interlace != 0:
        raise ValueError(f"{path} must be 8-bit RGBA, non-interlaced (got depth {depth}, color {color})")
    raw = zlib.decompress(idat)
    stride = width * 4
    if len(raw) != height * (stride + 1):
        raise ValueError(f"{path} has a truncated image body")
    rows, previous = [], bytearray(stride)
    for y in range(height):
        kind = raw[y * (stride + 1)]
        line = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for i in range(stride):
            left = line[i - 4] if i >= 4 else 0
            up = previous[i]
            up_left = previous[i - 4] if i >= 4 else 0
            if kind == 0:
                predictor = 0
            elif kind == 1:
                predictor = left
            elif kind == 2:
                predictor = up
            elif kind == 3:
                predictor = (left + up) // 2
            elif kind == 4:
                predictor = paeth(left, up, up_left)
            else:
                raise ValueError(f"{path} row {y} has unknown filter {kind}")
            line[i] = (line[i] + predictor) & 0xFF
        rows.append(bytes(line))
        previous = line
    return width, height, rows


def encode_png(width, height, rows):
    """Minimal RGBA8 PNG: IHDR, one IDAT (filter 0 on every row, zlib level 9), IEND."""

    def chunk(tag, body):
        payload = tag + body
        return struct.pack(">I", len(body)) + payload + struct.pack(">I", zlib.crc32(payload) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + r for r in rows)
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    return png


def luminance(rgb):
    r, g, b = rgb
    return 299 * r + 587 * g + 114 * b


def render(width, height, rows, palette):
    colors = sorted({row[i:i + 3] for row in rows for i in range(0, len(row), 4)},
                    key=lambda c: (luminance(c), tuple(c)))
    if len(colors) != len(palette):
        raise ValueError(f"stone_bricks.png has {len(colors)} colors but the palette has {len(palette)}")
    mapping = {c: bytes(p) for c, p in zip(colors, palette)}
    out = []
    for row in rows:
        line = bytearray()
        for i in range(0, len(row), 4):
            line += mapping[row[i:i + 3]] + row[i + 3:i + 4]
        out.append(bytes(line))
    return encode_png(width, height, out)


def main():
    check = "--check" in sys.argv
    width, height, rows = read_png(SOURCE)
    stale = False
    for name, palette in PALETTES:
        data = render(width, height, rows, palette)
        path = os.path.join(BLOCKS, name + ".png")
        if check:
            if not os.path.exists(path) or open(path, "rb").read() != data:
                print("stale:", name)
                stale = True
            continue
        with open(path, "wb") as f:
            f.write(data)
        print("wrote", os.path.relpath(path, ROOT))
    if stale:
        sys.exit(1)


if __name__ == "__main__":
    main()
