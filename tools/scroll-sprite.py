"""Lay one sprite out as a scrolling animation sheet.

Every frame is the previous one scrolled by --pixels toward --direction, and the pixels that leave
one edge wrap back in on the opposite one (the sprite is treated as a torus), so the frames loop
seamlessly. Frame 0 is the untouched original.

Layouts (--layout):
    vertical     the original on top, the next frame --height pixels below it, and so on (default)
    horizontal   the original on the left, the next frame --width pixels to its right, and so on
    square       a near-square grid filled left to right, top to bottom; --columns overrides the
                 column count and the last row may be short

The frame count: by default the sheet holds one full cycle - the number of frames after which the
sprite has wrapped back onto itself. That is lcm(W / gcd(W, dx), H / gcd(H, dy)), where each axis
the direction does not scroll counts as 1, so a 16x16 sprite gives 4 frames at 4 px/frame and 16 at
5 px/frame, and one more step after the last frame would give the original again. --frames
overrides it with an exact count (the original included), with no guarantee that looping is
seamless.

The step is applied per axis, so --direction left+down at 3 px moves the pixels 3 left and 3 down
(a 45-degree diagonal). Directions combine with commas, plus signs or spaces.

Reads PNG only, with no image library: 8- and 16-bit gray, RGB, gray+alpha and RGBA, 1- to 8-bit
palettes, every filter type and tRNS transparency. Interlaced (Adam7) PNGs are refused - re-export
the sprite without interlacing. The sheet is always written as an 8-bit RGBA PNG, beside the sprite
unless --output names another path.

Examples (run from anywhere):
    python -I tools/scroll-sprite.py sprite.png
    python -I tools/scroll-sprite.py sprite.png --direction left,down --pixels 2 --layout square
    python -I tools/scroll-sprite.py sprite.png -d right -p 4 --frames 8 --gap 2 -o out.png
"""
import argparse
import math
import os
import re
import struct
import sys
import zlib

DIRECTIONS = ("left", "right", "up", "down")
LAYOUTS = ("vertical", "horizontal", "square")
OPPOSITES = (("left", "right"), ("up", "down"))
TOKEN_SPLIT = re.compile(r"[,\s+]+")
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}


class SpecError(ValueError):
    """A bad argument value, reported without a traceback."""


class ImageError(ValueError):
    """An image that cannot be decoded or written as PNG, reported without a traceback."""


class Raster:
    """One 8-bit RGBA image as a flat row-major bytearray."""

    __slots__ = ("width", "height", "pixels")

    def __init__(self, width, height, pixels):
        self.width = width
        self.height = height
        self.pixels = pixels


# ---- PNG reading -------------------------------------------------------------------------------

def read_png(path):
    """Decode a PNG file into a Raster of 8-bit RGBA pixels."""
    try:
        with open(path, "rb") as handle:
            data = handle.read()
    except OSError as error:
        raise ImageError(f"cannot read '{path}': {error.strerror or error}") from None
    if not data.startswith(PNG_SIGNATURE):
        raise ImageError(f"'{path}' is not a PNG")

    header = None
    palette = b""
    transparency = b""
    compressed = bytearray()
    position = len(PNG_SIGNATURE)
    while position + 8 <= len(data):
        length, tag = struct.unpack(">I4s", data[position:position + 8])
        position += 8
        body = data[position:position + length]
        if len(body) != length:
            raise ImageError(f"'{path}' is truncated")
        position += length + 4  # skip the CRC
        if tag == b"IHDR":
            if len(body) != 13:
                raise ImageError(f"'{path}' has a malformed PNG header")
            header = struct.unpack(">IIBBBBB", body)
        elif tag == b"PLTE":
            palette = body
        elif tag == b"tRNS":
            transparency = body
        elif tag == b"IDAT":
            compressed += body
        elif tag == b"IEND":
            break
    if header is None or not compressed:
        raise ImageError(f"'{path}' has no image data")

    width, height, depth, color, compression, filter_method, interlace = header
    if width < 1 or height < 1:
        raise ImageError(f"'{path}' has an empty image")
    if color not in CHANNELS:
        raise ImageError(f"'{path}' uses PNG color type {color}, which does not exist")
    if depth not in (1, 2, 4, 8, 16) or (color in (2, 4, 6) and depth < 8):
        raise ImageError(f"'{path}' uses an unsupported bit depth {depth}")
    if color == 3 and depth == 16:
        raise ImageError(f"'{path}' is a palette PNG at 16 bits, which does not exist")
    if compression != 0 or filter_method != 0:
        raise ImageError(f"'{path}' uses an unknown PNG compression or filter method")
    if interlace != 0:
        raise ImageError(f"'{path}' is an interlaced PNG; re-export it without interlacing")
    if color == 3 and not palette:
        raise ImageError(f"'{path}' is a palette PNG with no palette")

    channels = CHANNELS[color]
    row_bytes = (width * channels * depth + 7) // 8
    bytes_per_pixel = max(1, (channels * depth + 7) // 8)
    try:
        raw = zlib.decompress(bytes(compressed))
    except zlib.error as error:
        raise ImageError(f"'{path}' has a corrupt image stream: {error}") from None
    if len(raw) != height * (row_bytes + 1):
        raise ImageError(f"'{path}' has a wrong image size")

    rows = unfilter(raw, height, row_bytes, bytes_per_pixel)
    if color == 6 and depth == 8:
        return Raster(width, height, bytearray(b"".join(rows)))
    return Raster(width, height, to_rgba(rows, width, height, depth, color, palette, transparency))


def unfilter(raw, height, row_bytes, bytes_per_pixel):
    """Undo the per-row PNG filters, returning one bytearray per row."""
    rows = []
    previous = bytearray(row_bytes)
    position = 0
    for _ in range(height):
        filter_type = raw[position]
        position += 1
        row = bytearray(raw[position:position + row_bytes])
        position += row_bytes
        if filter_type == 1:  # Sub
            for i in range(bytes_per_pixel, row_bytes):
                row[i] = (row[i] + row[i - bytes_per_pixel]) & 0xFF
        elif filter_type == 2:  # Up
            for i in range(row_bytes):
                row[i] = (row[i] + previous[i]) & 0xFF
        elif filter_type == 3:  # Average
            for i in range(row_bytes):
                left = row[i - bytes_per_pixel] if i >= bytes_per_pixel else 0
                row[i] = (row[i] + ((left + previous[i]) >> 1)) & 0xFF
        elif filter_type == 4:  # Paeth
            for i in range(row_bytes):
                left = row[i - bytes_per_pixel] if i >= bytes_per_pixel else 0
                up = previous[i]
                corner = previous[i - bytes_per_pixel] if i >= bytes_per_pixel else 0
                estimate = left + up - corner
                distance_left = abs(estimate - left)
                distance_up = abs(estimate - up)
                distance_corner = abs(estimate - corner)
                if distance_left <= distance_up and distance_left <= distance_corner:
                    predictor = left
                elif distance_up <= distance_corner:
                    predictor = up
                else:
                    predictor = corner
                row[i] = (row[i] + predictor) & 0xFF
        elif filter_type != 0:
            raise ImageError(f"unknown PNG filter type {filter_type}")
        rows.append(row)
        previous = row
    return rows


def row_samples(row, count, depth):
    """The row's channel samples, MSB first, as PNG stores them."""
    if depth == 8:
        return row[:count]
    if depth == 16:
        return [(row[2 * i] << 8) | row[2 * i + 1] for i in range(count)]
    per_byte = 8 // depth
    mask = (1 << depth) - 1
    samples = []
    for i in range(count):
        shift = 8 - depth * (i % per_byte + 1)
        samples.append((row[i // per_byte] >> shift) & mask)
    return samples


def to_rgba(rows, width, height, depth, color, palette, transparency):
    """Convert the decoded rows of a non-RGBA8 PNG into 8-bit RGBA pixels."""
    channels = CHANNELS[color]
    palette_colors = [tuple(palette[i:i + 3]) for i in range(0, len(palette) - 2, 3)]
    key = None
    if color == 0 and len(transparency) >= 2:
        key = (int.from_bytes(transparency[0:2], "big"),)
    elif color == 2 and len(transparency) >= 6:
        key = tuple(int.from_bytes(transparency[i:i + 2], "big") for i in (0, 2, 4))

    pixels = bytearray(width * height * 4)
    for y, row in enumerate(rows):
        samples = row_samples(row, width * channels, depth)
        for x in range(width):
            base = x * channels
            red = green = blue = alpha = 255
            if color == 0:
                raw_gray = samples[base]
                red = green = blue = to8(raw_gray, depth)
                if key is not None and raw_gray == key[0]:
                    alpha = 0
            elif color == 2:
                raw_triple = tuple(samples[base:base + 3])
                red, green, blue = (to8(value, depth) for value in raw_triple)
                if key is not None and raw_triple == key:
                    alpha = 0
            elif color == 3:
                index = samples[base]
                if index < len(palette_colors):
                    red, green, blue = palette_colors[index]
                if index < len(transparency):
                    alpha = transparency[index]
            elif color == 4:
                red = green = blue = to8(samples[base], depth)
                alpha = to8(samples[base + 1], depth)
            else:  # color 6
                red, green, blue, alpha = (to8(value, depth) for value in samples[base:base + 4])
            offset = (y * width + x) * 4
            pixels[offset] = red
            pixels[offset + 1] = green
            pixels[offset + 2] = blue
            pixels[offset + 3] = alpha
    return pixels


def to8(value, depth):
    """Scale a PNG sample of the given depth to 0..255."""
    if depth == 8:
        return value
    if depth == 16:
        return value >> 8
    return value * 255 // ((1 << depth) - 1)


# ---- PNG writing -------------------------------------------------------------------------------

def write_png(path, image):
    """Write an RGBA Raster as an 8-bit PNG (one IDAT, filter 0, zlib level 9)."""
    def chunk(tag, body):
        return (struct.pack(">I", len(body)) + tag + body
                + struct.pack(">I", zlib.crc32(tag + body) & 0xFFFFFFFF))

    stride = image.width * 4
    rows = [b"\x00" + bytes(image.pixels[y * stride:(y + 1) * stride])
            for y in range(image.height)]
    payload = (PNG_SIGNATURE
               + chunk(b"IHDR", struct.pack(">IIBBBBB", image.width, image.height, 8, 6, 0, 0, 0))
               + chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
               + chunk(b"IEND", b""))
    try:
        with open(path, "wb") as handle:
            handle.write(payload)
    except OSError as error:
        raise ImageError(f"cannot write '{path}': {error.strerror or error}") from None


# ---- the command line --------------------------------------------------------------------------

def parse_direction(text):
    """Split 'left+down' / 'left, down' / 'up' into a validated list of directions."""
    parts = [part for part in TOKEN_SPLIT.split(text.strip().lower()) if part]
    if not parts:
        raise SpecError("--direction needs at least one of: " + ", ".join(DIRECTIONS))
    for part in parts:
        if part not in DIRECTIONS:
            raise SpecError(f"unknown direction '{part}' (choose from {', '.join(DIRECTIONS)})")
    if len(set(parts)) != len(parts):
        raise SpecError(f"--direction repeats a direction: '{text}'")
    chosen = set(parts)
    for first, second in OPPOSITES:
        if first in chosen and second in chosen:
            raise SpecError(f"--direction cannot scroll both {first} and {second}: they cancel out")
    return parts


# ---- the scrolling sheet -----------------------------------------------------------------------

def step_of(directions, pixels):
    """How far the content moves per frame: (dx, dy), positive right and down."""
    dx = pixels if "right" in directions else (-pixels if "left" in directions else 0)
    dy = pixels if "down" in directions else (-pixels if "up" in directions else 0)
    return dx, dy


def cycle_of(size, step):
    """Frames until this axis wraps back onto itself (1 when it does not scroll)."""
    return size // math.gcd(size, abs(step)) if step else 1


def cycle_frames(width, height, dx, dy):
    """Frames in one full cycle of a diagonal scroll."""
    return math.lcm(cycle_of(width, dx), cycle_of(height, dy))


def scrolled(image, dx, dy):
    """The image with its content moved by (dx, dy), wrapping at every edge.

    out(x, y) reads src((x - dx) % W, (y - dy) % H), so a positive dx moves the pixels right and a
    positive dy moves them down. Whole-row slicing keeps the copy exact and fast.
    """
    width, height = image.width, image.height
    dx %= width
    dy %= height
    if dx == 0 and dy == 0:
        return Raster(width, height, bytearray(image.pixels))

    stride = width * 4
    split = (width - dx) * 4
    source = image.pixels
    out = bytearray(width * height * 4)
    for y in range(height):
        source_y = (y - dy) % height
        source_row = source[source_y * stride:(source_y + 1) * stride]
        # out x < dx comes from the sprite's right-hand wrap, the rest from its left.
        target = y * stride
        if dx:
            out[target:target + (dx * 4)] = source_row[split:]
            out[target + (dx * 4):target + stride] = source_row[:split]
        else:
            out[target:target + stride] = source_row
    return Raster(width, height, out)


def grid_of(layout, frames, columns):
    """How many frames per row and how many rows the layout puts on the sheet."""
    if layout == "vertical":
        return 1, frames
    if layout == "horizontal":
        return frames, 1
    across = columns or math.ceil(math.sqrt(frames))
    across = max(1, min(across, frames))
    return across, math.ceil(frames / across)


def build_sheet(image, dx, dy, frames, layout, columns, gap):
    """Paste every scrolled frame into one sheet, returning the Raster and its grid."""
    across, down = grid_of(layout, frames, columns)
    sheet_width = across * image.width + (across - 1) * gap
    sheet_height = down * image.height + (down - 1) * gap
    sheet = bytearray(sheet_width * sheet_height * 4)
    stride = sheet_width * 4
    tile_bytes = image.width * 4
    for index in range(frames):
        frame = scrolled(image, dx * index, dy * index)
        left = (index % across) * (image.width + gap)
        top = (index // across) * (image.height + gap)
        for y in range(image.height):
            start = (top + y) * stride + left * 4
            sheet[start:start + tile_bytes] = frame.pixels[y * tile_bytes:(y + 1) * tile_bytes]
    return Raster(sheet_width, sheet_height, sheet), across, down


def parse_args(argv):
    parser = argparse.ArgumentParser(
        prog="scroll-sprite.py",
        description="Lay a sprite out as a scrolling animation sheet (the pixels wrap at the edges).",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "Frame count: one full cycle by default, lcm(W/gcd(W,dx), H/gcd(H,dy)) with the axes "
            "the direction does not scroll\ncounting as 1 (a 16x16 sprite is 4 frames at 4 px and "
            "16 frames at 5 px); --frames N overrides it, the original included.\n"
            "The step applies per axis, so left+down at 3 px moves the pixels 3 left and 3 down."
        ),
    )
    parser.add_argument("sprite", help="the PNG to scroll")
    parser.add_argument("-d", "--direction", default="left",
                        help="left, right, up, down, or two joined by a comma, plus or space "
                             "(default: left); two make a diagonal, e.g. left+down")
    parser.add_argument("-p", "--pixels", type=int, default=1,
                        help="pixels every frame scrolls, applied per axis (default: 1)")
    parser.add_argument("-l", "--layout", choices=LAYOUTS, default="vertical",
                        help="vertical (one column, default), horizontal (one row) or square (grid)")
    parser.add_argument("-f", "--frames", type=int, default=0,
                        help="frames to write, the original included; 0 (default) writes one full cycle")
    parser.add_argument("-c", "--columns", type=int, default=0,
                        help="square layout only: frames per row; 0 (default) picks a near-square grid")
    parser.add_argument("-g", "--gap", type=int, default=0,
                        help="transparent pixels between frames (default: 0)")
    parser.add_argument("-o", "--output", default="",
                        help="the PNG to write (default: <sprite>_scroll.png beside the sprite)")
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    try:
        directions = parse_direction(args.direction)
        if args.pixels < 1:
            raise SpecError("--pixels must be at least 1")
        if args.frames < 0:
            raise SpecError("--frames must be 0 or more")
        if args.columns < 0:
            raise SpecError("--columns must be 0 or more")
        if args.gap < 0:
            raise SpecError("--gap must be 0 or more")
        if args.columns and args.layout != "square":
            raise SpecError("--columns only applies to the square layout")
        if not os.path.isfile(args.sprite):
            raise SpecError(f"no such sprite '{args.sprite}'")

        image = read_png(args.sprite)
        dx, dy = step_of(directions, args.pixels)
        if dx % image.width == 0 and dy % image.height == 0:
            raise SpecError(f"{args.pixels} px toward {args.direction} moves a whole "
                            f"{image.width}x{image.height} sprite, so nothing would scroll")

        frames = args.frames or cycle_frames(image.width, image.height, dx, dy)
        sheet, across, down = build_sheet(image, dx, dy, frames, args.layout, args.columns, args.gap)
        output = args.output or os.path.splitext(args.sprite)[0] + "_scroll.png"
        write_png(output, sheet)
    except (SpecError, ImageError) as error:
        sys.exit(f"scroll-sprite: {error}")

    print(f"{output}: {frames} frame{'s' if frames != 1 else ''} of "
          f"{image.width}x{image.height}, {args.pixels} px {args.direction} per frame, "
          f"{args.layout} {across}x{down}, {sheet.width}x{sheet.height}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
