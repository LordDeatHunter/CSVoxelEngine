"""Deterministic generator of the alloy brick tile (OpenGLTest/assets/textures/blocks/alloy_bricks.png).

Recolors the mud brick tile (mud_bricks.png) texel by texel: the source is a six-color brown ramp,
and each of its colors, ranked dark to light, maps onto PALETTE below - the darkest (the mortar
lines) to a dark gray, the lightest (the courses' top edges) to a pale metallic gray and the four
brick shades in between to a bronze ramp. So the alloy bricks keep the mud bricks' exact bond and
shading and only change color. Pillow reads the source; the PNG is encoded here with zlib at a fixed
level and filter 0, so re-running reproduces the file byte for byte. Run from anywhere:
python tools/generate-alloy-bricks-texture.py
"""
import os
import struct
import zlib

from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLOCKS = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "blocks")
SOURCE = os.path.join(BLOCKS, "mud_bricks.png")
PATH = os.path.join(BLOCKS, "alloy_bricks.png")

# Dark to light: mortar gray, four bronze brick shades, the pale gray edge highlight
PALETTE = [
    (52, 50, 50),
    (92, 66, 42),
    (118, 84, 50),
    (140, 100, 58),
    (162, 118, 68),
    (176, 168, 158),
]


def luminance(rgb):
    r, g, b = rgb
    return 299 * r + 587 * g + 114 * b


def write_png(path, width, height, rows):
    """Minimal RGBA8 PNG: IHDR, one IDAT (filter 0 on every row, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + r for r in rows)
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def main():
    source = Image.open(SOURCE).convert("RGBA")
    width, height = source.size
    colors = sorted({source.getpixel((x, y))[:3] for y in range(height) for x in range(width)}, key=luminance)
    if len(colors) > len(PALETTE):
        raise SystemExit(f"{SOURCE} has {len(colors)} colors; the palette maps at most {len(PALETTE)}")
    # Spread the source ranks over the palette so a ramp of fewer colors still spans it
    step = (len(PALETTE) - 1) / max(1, len(colors) - 1)
    mapping = {color: PALETTE[round(i * step)] for i, color in enumerate(colors)}

    rows = []
    for y in range(height):
        row = bytearray()
        for x in range(width):
            r, g, b, a = source.getpixel((x, y))
            row += bytes(mapping[(r, g, b)]) + bytes([a])
        rows.append(bytes(row))
    write_png(PATH, width, height, rows)
    print("wrote", os.path.relpath(PATH, ROOT))


if __name__ == "__main__":
    main()
