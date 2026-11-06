"""Deterministic generator of the status-effect HUD icons (OpenGLTest/assets/textures/ui/effect_*.png).

Two 16x16 RGBA icons, drawn from hand-laid pixel grids and rimmed with a dark outline on every empty
texel that touches a filled one, like the hand-made sprites:
    effect_burning.png    a flame: a red body, an orange core and a yellow heart, with a side lick
    effect_frostburn.png  a six-armed snowflake in pale blue around a white center

The files are named by the effects' `icon` fields (assets/data/effects/*.json), which the HUD loads
(HudRenderer.DrawEffects).

No randomness and no image library: the PNG is encoded here with zlib at a fixed level and filter
0, so re-running reproduces the files byte for byte. Run from anywhere:
    python -I tools/generate-effect-icons.py           write the icons
    python -I tools/generate-effect-icons.py --check   exit 1 if a file on disk differs
"""
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
UI = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "ui")

SIZE = 16

FLAME = [
    "................",
    "................",
    ".......R........",
    ".......RR.......",
    "......RRR...R...",
    "......RoRR..RR..",
    ".....RRoRRR.RR..",
    "....RRooRRRRRR..",
    "....RooooRRRRR..",
    "...RRooYooRRRR..",
    "...RooYYYooRR...",
    "...RooYYYYoRR...",
    "...RRoYYYYoRR...",
    "....RRooooRR....",
    ".....RRRRRR.....",
    "................",
]

SNOWFLAKE = [
    "................",
    ".......B........",
    "......BBB.......",
    "..B....B....B...",
    "...B...B...B....",
    "....B..B..B.....",
    ".....BBWBB......",
    ".BBBBBWWWBBBBB..",
    ".....BBWBB......",
    "....B..B..B.....",
    "...B...B...B....",
    "..B....B....B...",
    "......BBB.......",
    ".......B........",
    "................",
    "................",
]

ICONS = [
    (
        "effect_burning.png",
        FLAME,
        {"R": (0xD8, 0x2A, 0x14), "o": (0xFF, 0x8A, 0x1E), "Y": (0xFF, 0xE0, 0x5A)},
        (0x4A, 0x0E, 0x06),
    ),
    (
        "effect_frostburn.png",
        SNOWFLAKE,
        {"B": (0xA8, 0xD8, 0xFF), "W": (0xF4, 0xFB, 0xFF)},
        (0x1E, 0x3A, 0x66),
    ),
]


def encode_png(rows):
    """Minimal RGBA8 PNG: IHDR, one IDAT (filter 0 on every row, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + r for r in rows)
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    return png


def render(grid, palette, outline):
    """The icon's rows: palette colors where the grid is filled, the outline on empty texels that
    touch a filled one (4-neighbors), transparent black elsewhere."""
    assert len(grid) == SIZE and all(len(line) == SIZE for line in grid), "grid must be 16x16"
    fill = [[palette[c] if c != "." else None for c in line] for line in grid]
    rows = []
    for y in range(SIZE):
        row = bytearray()
        for x in range(SIZE):
            color = fill[y][x]
            if color is None:
                rimmed = any(
                    0 <= x + dx < SIZE and 0 <= y + dy < SIZE and fill[y + dy][x + dx] is not None
                    for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1))
                )
                row += bytes(outline if rimmed else (0, 0, 0)) + (b"\xff" if rimmed else b"\x00")
            else:
                row += bytes(color) + b"\xff"
        rows.append(bytes(row))
    return encode_png(rows)


def main():
    check = "--check" in sys.argv[1:]
    stale = False
    for name, grid, palette, outline in ICONS:
        path = os.path.join(UI, name)
        png = render(grid, palette, outline)
        if check:
            try:
                with open(path, "rb") as f:
                    same = f.read() == png
            except OSError:
                same = False
            if not same:
                print("stale", os.path.relpath(path, ROOT))
                stale = True
            continue
        with open(path, "wb") as f:
            f.write(png)
        print("wrote", os.path.relpath(path, ROOT))
    if stale:
        sys.exit(1)


if __name__ == "__main__":
    main()
