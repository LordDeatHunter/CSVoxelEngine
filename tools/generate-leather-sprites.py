"""Deterministic generator of the leatherworking sprites (placeholder art).

Writes, under OpenGLTest/assets/textures/items:
  flint_knife.png   a knapped flint blade bound to a short haft
  salt.png          a little heap of white salt crystals
  salted_hide.png   a scraped pelt, pale and dusted with salt
  leather.png       a folded sheet of tanned brown leather
  bark.png          a curl of oak bark (the tanning vat's tannin)
and under OpenGLTest/assets/textures/blocks:
  rock_salt.png     a pale pink-gray rock threaded with white salt crystals

Each item sprite is drawn at half resolution, one string per row and one character per 2x2 texels of the 32x32
image, with its own small palette (' ' is transparent, '#' the outline), the wildlife generator's way. The rock salt
tile is a hashed speckle over a fixed palette. The PNG encoder and the write/--check routine are the hell mobs'
generator's (tools/generate-hell-mob-models.py), so re-running reproduces every file byte for byte. Run from the
repo root:
    python -I tools/generate-leather-sprites.py           (writes the six files)
    python -I tools/generate-leather-sprites.py --check   (exit 1 if any file differs)
"""
import importlib.util
import os

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("hell_mobs", os.path.join(HERE, "generate-hell-mob-models.py"))
hell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hell)

SIZE = 32
BLOCK_DIR = os.path.join(hell.ROOT, "OpenGLTest", "assets", "textures", "blocks")

KNIFE = [
    "                ",
    "            ##  ",
    "           #fF# ",
    "          #fFF# ",
    "         #fFF#  ",
    "        #fFF#   ",
    "       #fFF#    ",
    "      #fFF#     ",
    "     #ffF#      ",
    "    #cc##       ",
    "   #bbc#        ",
    "  #bBb#         ",
    " #bBb#          ",
    " #Bb#           ",
    "  ##            ",
    "                ",
]

SALT = [
    "                ",
    "                ",
    "                ",
    "                ",
    "       ##       ",
    "      #wW#      ",
    "     #wWWw#     ",
    "    #wWsWWw#    ",
    "   #wWWWWsWw#   ",
    "  #wsWWWWWWWw#  ",
    "  #wWWWsWWWsw#  ",
    " #wwWWWWWWWWww# ",
    " #gwwwwwwwwwwg# ",
    "  ############  ",
    "                ",
    "                ",
]

SALTED_HIDE = [
    "                ",
    "  ##        ##  ",
    " #hh#      #hh# ",
    " #hhh######hhh# ",
    "  #hhHHsHHHhh#  ",
    "   #hHHHHsHh#   ",
    "   #hsHHHHHh#   ",
    "   #hHHHsHHh#   ",
    "   #hHsHHHHh#   ",
    "   #hHHHHsHh#   ",
    "   #hHsHHHHh#   ",
    "  #hhHHHsHHhh#  ",
    " #hhh######hhh# ",
    " #hh#      #hh# ",
    "  ##        ##  ",
    "                ",
]

LEATHER = [
    "                ",
    "                ",
    "   ##########   ",
    "  #lLLLLLLLLl#  ",
    "  #lLLLLLLLLl#  ",
    "  #ldddddddddd# ",
    "  #lLLLLLLLLLl# ",
    "  #lLLLLLLLLLl# ",
    "  #lLLLLLLLLLl# ",
    "  #ldddddddddd# ",
    "  #lLLLLLLLLLl# ",
    "  #lLLLLLLLLLl# ",
    "  #llllllllll#  ",
    "   ##########   ",
    "                ",
    "                ",
]

BARK = [
    "                ",
    "                ",
    "      ####      ",
    "    ##bBBb##    ",
    "   #bBBbbBBb#   ",
    "  #bBbiiiibBb#  ",
    "  #bBi####iBb#  ",
    " #bBi#    #iBb# ",
    " #bBi#    #iBb# ",
    " #bBbi#   #ib#  ",
    "  #bBbi#  ##    ",
    "   #bBbi#       ",
    "    #bbb#       ",
    "     ###        ",
    "                ",
    "                ",
]

SPRITES = [
    ("flint_knife", KNIFE,
     {"#": (36, 32, 30), "f": (92, 90, 96), "F": (150, 148, 156), "c": (180, 150, 100), "b": (96, 64, 36),
      "B": (132, 92, 54)}),
    ("salt", SALT, {"#": (120, 116, 112), "w": (214, 212, 210), "W": (246, 246, 244), "s": (255, 255, 255),
                    "g": (176, 172, 168)}),
    ("salted_hide", SALTED_HIDE,
     {"#": (82, 62, 44), "h": (170, 136, 100), "H": (204, 174, 138), "s": (246, 244, 236)}),
    ("leather", LEATHER, {"#": (52, 30, 16), "l": (108, 64, 32), "L": (140, 86, 44), "d": (92, 52, 26)}),
    ("bark", BARK, {"#": (40, 26, 16), "b": (84, 58, 36), "B": (112, 80, 50), "i": (168, 128, 82)}),
]


def texel_of(rows, palette):
    def texel(x, y):
        c = rows[y // 2][x // 2]
        if c == " ":
            return (0, 0, 0, 0)
        color = palette[c]
        return color if len(color) == 4 else color + (255,)
    return texel


ROCK_SALT_BASE = [(176, 160, 162), (188, 172, 172), (164, 148, 152), (196, 182, 182)]
ROCK_SALT_CRYSTAL = [(236, 232, 232), (250, 248, 248), (220, 210, 214)]


def rock_salt_texel(x, y):
    # 2x2 texel clumps, so the tile reads at the same scale as the other block tiles
    cx, cy = x // 2, y // 2
    if hell.hash2(cx, cy, 7) % 100 < 22:
        color = ROCK_SALT_CRYSTAL[hell.hash2(cx, cy, 11) % len(ROCK_SALT_CRYSTAL)]
    else:
        color = ROCK_SALT_BASE[hell.hash2(cx, cy, 3) % len(ROCK_SALT_BASE)]
    return color + (255,)


def outputs():
    files = [(os.path.join(hell.ITEM_DIR, name + ".png"), hell.png_bytes(SIZE, texel_of(rows, palette)), False)
             for name, rows, palette in SPRITES]
    files.append((os.path.join(BLOCK_DIR, "rock_salt.png"), hell.png_bytes(SIZE, rock_salt_texel), False))
    return files


if __name__ == "__main__":
    hell.main(outputs)
