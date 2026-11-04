"""Deterministic edit of the copper tool-head sprites: removes the wooden handle.

copper_pickaxe_head.png and copper_axe_head.png started as copies of the finished copper tools
(copper_pickaxe.png, copper_axe.png), handle and all. A cast head is only the metal, so this strips
the handle in place:

  * every pixel in the handle's four colors (its dark rim and its three browns) becomes transparent;
  * a removed pixel that touches the copper (4-neighborhood) is repainted in the copper's own
    dark rim color, so the head keeps a closed outline where the handle ran through it.

The finished tool sprites are not touched. Re-running is a no-op once the handle is gone (nothing is
left to strip), and the PNG is encoded here with zlib at a fixed level and filter 0, so the output is
byte-stable. No image library. Run from anywhere:
    python -I tools/remove-copper-head-handles.py
"""
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ITEMS = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items")
HEADS = ("copper_pickaxe_head.png", "copper_axe_head.png")

HANDLE = {(0x21, 0x19, 0x19), (0x3A, 0x2A, 0x22), (0x52, 0x3A, 0x2A), (0x6F, 0x51, 0x34)}
COPPER_RIM = (0x2A, 0x00, 0x00)


def read_png(path):
    data = open(path, "rb").read()
    pos, idat = 8, b""
    while pos < len(data):
        (n,) = struct.unpack(">I", data[pos:pos + 4])
        kind, body = data[pos + 4:pos + 8], data[pos + 8:pos + 8 + n]
        pos += 12 + n
        if kind == b"IHDR":
            w, h, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            assert (depth, color, interlace) == (8, 6, 0), "expects 8-bit non-interlaced RGBA"
        elif kind == b"IDAT":
            idat += body
    raw = zlib.decompress(idat)
    stride, bpp, rows, prev, o = w * 4, 4, [], bytearray(w * 4), 0
    for _ in range(h):
        f = raw[o]
        line = bytearray(raw[o + 1:o + 1 + stride])
        o += 1 + stride
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + (a + b) // 2) & 255
            elif f == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pred = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 255
        rows.append(line)
        prev = line
    return w, h, [[tuple(r[x * 4:x * 4 + 4]) for x in range(w)] for r in rows]


def write_png(path, w, h, px):
    raw = b"".join(b"\x00" + bytes(c for p in row for c in p) for row in px)

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))

    png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
    open(path, "wb").write(png)


def strip_handle(w, h, px):
    removed = {(x, y) for y in range(h) for x in range(w) if px[y][x][3] and px[y][x][:3] in HANDLE}
    out = [[(0, 0, 0, 0) if (x, y) in removed else px[y][x] for x in range(w)] for y in range(h)]
    for x, y in sorted(removed):
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, ny = x + dx, y + dy
            if not (0 <= nx < w and 0 <= ny < h) or (nx, ny) in removed:
                continue
            p = px[ny][nx]
            if p[3] and p[:3] != COPPER_RIM:
                out[y][x] = COPPER_RIM + (255,)
                break
    return out


def main():
    for name in HEADS:
        path = os.path.join(ITEMS, name)
        w, h, px = read_png(path)
        write_png(path, w, h, strip_handle(w, h, px))
        print("stripped", name)


if __name__ == "__main__":
    main()
