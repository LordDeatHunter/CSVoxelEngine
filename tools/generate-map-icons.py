"""Deterministic generator of the map's pin icons (placeholder art, listed in USER_TODO.md).

Writes under OpenGLTest/assets/textures/ui/map/:
  pin_arrow.png  a 32x32 arrowhead pointing up (north): a white fill inside a dark outline, so MinimapRenderer's
                 per-kind tint (the player, a hostile, a neutral mob) colors the fill and the outline stays dark
  waystone.png   a 32x32 obelisk on a plinth with a glowing rune line, drawn untinted at a discovered waystone
  tower.png      a 32x32 battle tower with battlements and a door, for the located towers (not drawn yet)

No randomness and no image library: every texel is 4x4 supersampled from plain polygons, and the PNG writer below
(stdlib zlib and struct, filter 0, level 9) is the one the other tools/ generators use, so re-running reproduces the
files byte for byte. Run from the repo root:
    python -I tools/generate-map-icons.py           (writes the three files)
    python -I tools/generate-map-icons.py --check   (exit 1 if a file differs)
"""
import math
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "ui", "map")
SIZE = 32
SUB = 4
OUTLINE_WIDTH = 1.6

OUTLINE = (20, 22, 28)
WHITE = (255, 255, 255)
STONE = (150, 154, 160)
STONE_DARK = (104, 108, 116)
RUNE = (110, 220, 255)
TOWER = (148, 132, 112)
TOWER_DARK = (96, 84, 70)


def inside(x, y, polygon):
    """Even-odd ray cast: whether (x, y) lies inside the polygon."""
    result = False
    for i in range(len(polygon)):
        ax, ay = polygon[i]
        bx, by = polygon[(i + 1) % len(polygon)]
        if (ay > y) != (by > y) and x < ax + (y - ay) * (bx - ax) / (by - ay):
            result = not result
    return result


def segment_distance(x, y, a, b):
    ax, ay = a
    bx, by = b
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(x - (ax + t * dx), y - (ay + t * dy))


def edge_distance(x, y, polygon):
    return min(segment_distance(x, y, polygon[i], polygon[(i + 1) % len(polygon)]) for i in range(len(polygon)))


def rect(x0, y0, x1, y1):
    return [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]


ARROW = [(16, 2.5), (27.5, 29), (16, 21.5), (4.5, 29)]


def arrow_texel(x, y):
    if not inside(x, y, ARROW):
        return None
    return OUTLINE if edge_distance(x, y, ARROW) < OUTLINE_WIDTH else WHITE


def union_texel(x, y, shapes):
    """The first shape holding the point paints it; the outline runs only along the union's own edge, never where two
    of its shapes meet."""
    for polygon, color in shapes:
        if inside(x, y, polygon):
            if edge_distance(x, y, polygon) < OUTLINE_WIDTH and outside_union(x, y, shapes):
                return OUTLINE
            return color(x, y) if callable(color) else color
    return None


def outside_union(x, y, shapes):
    """Whether a point within the outline width of (x, y) lies outside every shape: the union's own edge."""
    for angle in range(0, 360, 30):
        px = x + OUTLINE_WIDTH * math.cos(math.radians(angle))
        py = y + OUTLINE_WIDTH * math.sin(math.radians(angle))
        if not any(inside(px, py, polygon) for polygon, _ in shapes):
            return True
    return False


def obelisk_color(x, y):
    if abs(x - 16) < 1.1 and 9 < y < 22:
        return RUNE
    return STONE if x < 16 else STONE_DARK


WAYSTONE = [
    ([(16, 2), (20, 7), (12, 7)], STONE),
    ([(12, 7), (20, 7), (21.5, 25), (10.5, 25)], obelisk_color),
    (rect(6, 25, 26, 30), STONE_DARK),
]


def tower_color(x, y):
    if 11.5 < x < 20.5 and y > 22 and (y > 24 or math.hypot(x - 16, y - 24) < 4.5):
        return OUTLINE
    if 14 < x < 18 and 13 < y < 17:
        return OUTLINE
    return TOWER if x < 16 else TOWER_DARK


TOWER_SHAPES = [
    (rect(6, 3, 11, 9), TOWER),
    (rect(13.5, 3, 18.5, 9), TOWER),
    (rect(21, 3, 26, 9), TOWER_DARK),
    (rect(6, 8, 26, 11), TOWER),
    (rect(8.5, 10, 23.5, 30), tower_color),
]


def render(texel):
    rows = []
    for py in range(SIZE):
        row = bytearray([0])
        for px in range(SIZE):
            r = g = b = a = 0
            for sy in range(SUB):
                for sx in range(SUB):
                    color = texel(px + (sx + 0.5) / SUB, py + (sy + 0.5) / SUB)
                    if color is None:
                        continue
                    r += color[0]
                    g += color[1]
                    b += color[2]
                    a += 1
            if a == 0:
                row += bytes(4)
            else:
                row += bytes((round(r / a), round(g / a), round(b / a), round(255 * a / (SUB * SUB))))
        rows.append(bytes(row))
    return b"".join(rows)


def png(raw):
    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


FILES = {
    "pin_arrow.png": arrow_texel,
    "waystone.png": lambda x, y: union_texel(x, y, WAYSTONE),
    "tower.png": lambda x, y: union_texel(x, y, TOWER_SHAPES),
}


def main():
    check = "--check" in sys.argv[1:]
    differs = False
    os.makedirs(OUT_DIR, exist_ok=True)
    for name, texel in FILES.items():
        path = os.path.join(OUT_DIR, name)
        data = png(render(texel))
        if check:
            with open(path, "rb") as existing:
                if existing.read() != data:
                    print(f"differs: {path}")
                    differs = True
            continue
        with open(path, "wb") as out:
            out.write(data)
        print(f"wrote {path}")
    sys.exit(1 if differs else 0)


if __name__ == "__main__":
    main()
