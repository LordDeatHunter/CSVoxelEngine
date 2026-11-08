"""Deterministic generator of the bone-shrapnel art.

Writes two 32x32 RGBA item sprites (the size of every other item sprite):
  OpenGLTest/assets/textures/items/bone_breaker_blade.png  a broad ivory bone blade on a leather
      grip: a stepped diagonal blade with a chipped, serrated edge, a darker shaded side, a bone
      crossguard and a wrapped pommel;
  OpenGLTest/assets/textures/items/bone_charm.png          a bone shard talisman: a tapered ivory
      shard with a drilled hole, bound with two vine wraps and hung from a dark cord loop.

Both are drawn in code with an outline pass that rims each material in its own dark color, the way
the hand-made sprites are rimmed. No randomness and no image library: the PNG is encoded here with
zlib at a fixed level and filter 0, so re-running reproduces both files byte for byte. Run from
anywhere:
    python -I tools/generate-bone-items.py
"""
import math
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BLADE_PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items", "bone_breaker_blade.png")
CHARM_PATH = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items", "bone_charm.png")

SIZE = 32

BONE_LIGHT = (0xEF, 0xE9, 0xD6)
BONE_MID = (0xD6, 0xCC, 0xB2)
BONE_SHADE = (0xB2, 0xA6, 0x89)
BONE_DEEP = (0x8C, 0x80, 0x64)
GRIP_DARK = (0x4A, 0x32, 0x22)
GRIP_MID = (0x6B, 0x4A, 0x30)
GRIP_LIGHT = (0x8A, 0x64, 0x42)
VINE = (0x3D, 0x8A, 0x3A)
CORD = (0x33, 0x24, 0x1C)

# Each fill material and the dark color its rim takes
OUTLINE = {
    "bone": BONE_DEEP,
    "grip": GRIP_DARK,
    "vine": (0x22, 0x52, 0x22),
}
PRIORITY = ("vine", "grip", "bone")


class Canvas:
    """A 32x32 grid of (rgb, material) or None for transparent."""

    def __init__(self):
        self.px = [[None] * SIZE for _ in range(SIZE)]

    def set(self, x, y, rgb, material):
        if 0 <= x < SIZE and 0 <= y < SIZE:
            self.px[y][x] = (rgb, material)

    def clear(self, x, y):
        if 0 <= x < SIZE and 0 <= y < SIZE:
            self.px[y][x] = None

    def get(self, x, y):
        if 0 <= x < SIZE and 0 <= y < SIZE:
            return self.px[y][x]
        return None

    def outline(self):
        rim = []
        for y in range(SIZE):
            for x in range(SIZE):
                if self.px[y][x] is not None:
                    continue
                near = set()
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    p = self.get(x + dx, y + dy)
                    if p is not None and p[1] in OUTLINE:
                        near.add(p[1])
                for material in PRIORITY:
                    if material in near:
                        rim.append((x, y, OUTLINE[material]))
                        break
        for x, y, rgb in rim:
            self.px[y][x] = (rgb, "rim")

    def rows(self):
        out = []
        for y in range(SIZE):
            row = bytearray()
            for x in range(SIZE):
                p = self.px[y][x]
                row += b"\x00\x00\x00\x00" if p is None else bytes(p[0]) + b"\xff"
            out.append(bytes(row))
        return out


def write_png(path, rows):
    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    raw = b"".join(b"\x00" + r for r in rows)
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    with open(path, "wb") as f:
        f.write(png)


def fill_capsule(c, ax, ay, bx, by, half, rgb, material, taper=0.0):
    """Fills a capsule around segment a-b, its half-width shrinking to (1 - taper) at b."""
    dx, dy = bx - ax, by - ay
    length_sq = dx * dx + dy * dy
    if length_sq == 0:
        return
    for y in range(SIZE):
        for x in range(SIZE):
            px, py = x + 0.5, y + 0.5
            t = ((px - ax) * dx + (py - ay) * dy) / length_sq
            if t < 0.0 or t > 1.0:
                continue
            perp = abs((px - ax) * dy - (py - ay) * dx) / math.sqrt(length_sq)
            if perp <= half * (1.0 - taper * t):
                c.set(x, y, rgb, material)


def blade():
    c = Canvas()
    # The blade: a broad ivory capsule tapering into a point
    fill_capsule(c, 10.0, 23.0, 26.0, 5.5, 2.7, BONE_MID, "bone", taper=0.82)
    # Light catching the upper-left of the blade
    fill_capsule(c, 11.5, 22.5, 24.5, 7.5, 1.0, BONE_LIGHT, "bone", taper=0.8)
    # The shaded lower edge
    fill_capsule(c, 9.5, 24.0, 25.0, 8.5, 0.9, BONE_SHADE, "bone", taper=0.8)

    # Chipped serrations down the lower edge: bite a few pixels out, then re-rim
    for x, y in ((16, 16), (19, 13), (22, 10)):
        c.clear(x, y)
        c.clear(x, y + 1)

    # Crossguard, grip and pommel (a bone handle over a leather wrap)
    fill_capsule(c, 6.0, 27.0, 14.0, 19.0, 1.3, BONE_MID, "bone")
    fill_capsule(c, 5.0, 27.5, 2.8, 29.8, 1.4, GRIP_MID, "grip")
    # Wrap rings on the grip
    for t in (0.25, 0.6, 0.9):
        x = 5.0 + (2.8 - 5.0) * t
        y = 27.5 + (29.8 - 27.5) * t
        fill_capsule(c, x - 1.5, y - 1.5, x + 1.5, y + 1.5, 0.5, GRIP_DARK, "grip")
    # Pommel knob
    for y in range(SIZE):
        for x in range(SIZE):
            if (x + 0.5 - 2.8) ** 2 + (y + 0.5 - 29.8) ** 2 <= 1.4 ** 2:
                c.set(x, y, GRIP_LIGHT, "grip")

    c.outline()
    return c


def charm():
    c = Canvas()
    # The cord loop: a thin arc from one shoulder of the shard to the other
    for step in range(61):
        t = step / 60.0
        x = 13.0 + 6.0 * t
        y = 12.0 - 8.0 * math.sin(math.pi * t)
        c.set(int(x), int(y), CORD, "cord")

    # The shard: a tapered lens, widest in the middle
    for y in range(11, 28):
        u = (y - 11) / 16.0
        half = 1.0 + 2.6 * math.sin(math.pi * min(1.0, u * 1.15))
        cx = 16.0
        for x in range(SIZE):
            if abs(x + 0.5 - cx) <= half:
                shade = BONE_LIGHT if x + 0.5 < cx - 0.4 else BONE_MID
                c.set(x, y, shade, "bone")
        # The right flank in shadow
        edge = int(cx + half) - 1
        if c.get(edge, y) is not None:
            c.set(edge, y, BONE_SHADE, "bone")

    # A drilled hole the cord runs through
    for x, y in ((15, 12), (16, 12), (15, 13), (16, 13)):
        c.clear(x, y)

    # Two vine wraps
    for y in (19, 22):
        for x in range(11, 22):
            if c.get(x, y) is not None:
                c.set(x, y, VINE, "vine")
        for x in (12, 20):
            if c.get(x, y + 1) is not None:
                c.set(x, y + 1, VINE, "vine")

    c.outline()
    return c


def main():
    write_png(BLADE_PATH, blade().rows())
    write_png(CHARM_PATH, charm().rows())
    print("wrote", BLADE_PATH)
    print("wrote", CHARM_PATH)


if __name__ == "__main__":
    main()
