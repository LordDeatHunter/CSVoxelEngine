"""Deterministic generator of the quest book's node frames and check badge (placeholder art, listed in USER_TODO.md).

Writes under OpenGLTest/assets/textures/ui/:
  quest_node_circle.png  a 64x64 disc: a dark slate fill inside a light rim (a non-root quest's frame)
  quest_node_hex.png     a 64x64 pointy-top hexagon with the same fill and rim (a root quest's frame)
  quest_check.png        a 64x64 green badge with a white check mark (drawn on a complete quest's node)
The frames are near-gray so QuestBookRenderer's tint (open, complete, hovered, the gold selection ring) colors them.

No randomness and no image library: every texel is 4x4 supersampled from plain geometry, and the PNG writer below (stdlib
zlib and struct, filter 0, level 9) is the one the other tools/ generators use, so re-running reproduces the files byte
for byte. Run from the repo root:
    python -I tools/generate-quest-node-textures.py           (writes the three files)
    python -I tools/generate-quest-node-textures.py --check   (exit 1 if a file differs)
"""
import math
import os
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "ui")
SIZE = 64
SUB = 4
CENTER = SIZE / 2.0

RIM = (226, 228, 232)
FILL = (78, 84, 96)
OUTLINE = (24, 26, 32)
GREEN = (92, 186, 84)
WHITE = (255, 255, 255)


def circle_distance(x, y, radius):
    """Signed distance from the circle's edge (negative inside)."""
    return math.hypot(x - CENTER, y - CENTER) - radius


def hex_distance(x, y, radius):
    """Signed distance from a pointy-top regular hexagon's edge with circumradius `radius` (negative inside)."""
    px, py = abs(x - CENTER), abs(y - CENTER)
    apothem = radius * math.sqrt(3) / 2
    # The two edge normals of a pointy-top hexagon in the first quadrant: horizontal (1, 0) and 60 degrees up
    return max(px - apothem, px * 0.5 + py * math.sqrt(3) / 2 - apothem)


def segment_distance(x, y, a, b):
    ax, ay = a
    bx, by = b
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)))
    return math.hypot(x - (ax + t * dx), y - (ay + t * dy))


def frame_texel(distance):
    """The frame's color at a signed distance: outline, then rim, then fill inward; None outside."""
    if distance > 0:
        return None
    if distance > -1.5:
        return OUTLINE
    if distance > -5.5:
        return RIM
    return FILL


def check_texel(x, y):
    distance = circle_distance(x, y, 29.5)
    if distance > 0:
        return None
    if distance > -2.5:
        return OUTLINE
    tick = min(segment_distance(x, y, (18, 33), (28, 43)), segment_distance(x, y, (28, 43), (47, 22)))
    return WHITE if tick < 4.0 else GREEN


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
    "quest_node_circle.png": lambda x, y: frame_texel(circle_distance(x, y, 31.0)),
    "quest_node_hex.png": lambda x, y: frame_texel(hex_distance(x, y, 31.5)),
    "quest_check.png": check_texel,
}


def main():
    check = "--check" in sys.argv[1:]
    differs = False
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
