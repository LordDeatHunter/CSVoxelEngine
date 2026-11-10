"""Deterministic generator of the wild animals' drop sprites (placeholder art).

Writes, under OpenGLTest/assets/textures/items:
  raw_venison.png, cooked_venison.png      a deer's steak, raw and off the campfire
  raw_boar_meat.png, cooked_boar_meat.png  a boar's haunch, raw and cooked
  hide.png                                 a stretched pelt (deer and boar)
  crow_feather.png                         a crow's flight feather
  boar_tusk.png, dire_tusk.png             a boar's tusk and the dire boar's stained trophy
  dragonfly_wing.png                       a glassy, veined wing (its film is half transparent)

Each sprite is drawn at half resolution, one string per row and one character per 2x2 texels of the 32x32
image, with its own small palette: ' ' is transparent, '#' the outline, and a palette entry may carry an
alpha. The PNG encoder and the write/--check routine are the hell mobs' generator's
(tools/generate-hell-mob-models.py), so re-running reproduces every file byte for byte. Run from the repo root:
    python -I tools/generate-wildlife-item-sprites.py           (writes the nine files)
    python -I tools/generate-wildlife-item-sprites.py --check   (exit 1 if any file differs)
"""
import importlib.util
import os

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("hell_mobs", os.path.join(HERE, "generate-hell-mob-models.py"))
hell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hell)

SIZE = 32

STEAK = [
    "                ",
    "      ####      ",
    "    ##rrrr##    ",
    "   #rrRRRrrr#   ",
    "  #rrRRRRRRrr#  ",
    "  #rRRRwRRRRr#  ",
    " #rrRRwwwRRRrr# ",
    " #rRRRRwRRRRRr# ",
    " #rRRRRRRRRRRr# ",
    " #rrRRRRRRRRrr# ",
    "  #rrRRRRRRrr#  ",
    "  #ffrrRRrrff#  ",
    "   #fffrrfff#   ",
    "    ##ffff##    ",
    "      ####      ",
    "                ",
]

HAUNCH = [
    "                ",
    "           ###  ",
    "          #bb#  ",
    "         #bbb#  ",
    "     ####bb##   ",
    "   ##pppp##     ",
    "  #ppPPPPpp#    ",
    " #ppPPPPPPpp#   ",
    " #pPPPPPPPPp#   ",
    " #pPPPwPPPPp#   ",
    " #ppPPPPPPpp#   ",
    "  #ppPPPPpp#    ",
    "   ##pppp##     ",
    "     ####       ",
    "                ",
    "                ",
]

HIDE = [
    "                ",
    "  ##        ##  ",
    " #hh#      #hh# ",
    " #hhh######hhh# ",
    "  #hhHHHHHHhh#  ",
    "   #hHHHHHHh#   ",
    "   #hHHllHHh#   ",
    "   #hHllllHh#   ",
    "   #hHllllHh#   ",
    "   #hHHllHHh#   ",
    "   #hHHHHHHh#   ",
    "  #hhHHHHHHhh#  ",
    " #hhh######hhh# ",
    " #hh#      #hh# ",
    "  ##        ##  ",
    "                ",
]

FEATHER = [
    "                ",
    "            ##  ",
    "          ##fs# ",
    "         #ffss# ",
    "        #ffsf#  ",
    "       #fsff#   ",
    "      #ffsf#    ",
    "     #fsff#     ",
    "    #ffsf#      ",
    "    #fsf#       ",
    "   #qff#        ",
    "   #q##         ",
    "  #q#           ",
    "  ##            ",
    "                ",
    "                ",
]

TUSK = [
    "                ",
    "         ##     ",
    "        #tt#    ",
    "        #tT#    ",
    "        #tT#    ",
    "       #tTt#    ",
    "       #tT#     ",
    "      #tTt#     ",
    "     #tTT#      ",
    "    #tTTt#      ",
    "   #ttTt#       ",
    "  #bbtt#        ",
    "  #bbb#         ",
    "   ###          ",
    "                ",
    "                ",
]

DIRE_TUSK = [
    "          ###   ",
    "         #rrr#  ",
    "         #trr#  ",
    "        #tTt#   ",
    "        #tTt#   ",
    "       #tTTt#   ",
    "       #tTt#    ",
    "      #tTTt#    ",
    "     #ttTt#     ",
    "    #tTTTt#     ",
    "   #ttTTt#      ",
    "  #tttTt#       ",
    " #bbbtt#        ",
    " #bbbb#         ",
    "  ####          ",
    "                ",
]

WING = [
    "                ",
    "             ## ",
    "          ###ms#",
    "        ##mvmms#",
    "      ##mmvmmv# ",
    "    ##mvmmvmm#  ",
    "   #mmvmmvmm#   ",
    "  #vvvvvvvv#    ",
    "  #mmvmmvm#     ",
    "   #mvmmv#      ",
    "    #mvm#       ",
    "     ###        ",
    "                ",
    "                ",
    "                ",
    "                ",
]

RAW_MEAT = {"#": (70, 20, 24), "r": (150, 40, 44), "R": (190, 56, 60), "w": (236, 190, 180), "f": (240, 214, 196)}
COOKED_MEAT = {"#": (60, 34, 20), "r": (128, 74, 40), "R": (164, 100, 54), "w": (214, 160, 96), "f": (226, 190, 130)}
RAW_HAUNCH = {"#": (80, 28, 36), "p": (176, 70, 86), "P": (214, 104, 116), "w": (240, 200, 200), "b": (236, 226, 200)}
COOKED_HAUNCH = {"#": (60, 34, 20), "p": (132, 78, 40), "P": (172, 108, 56), "w": (220, 170, 100), "b": (236, 226, 200)}

SPRITES = [
    ("raw_venison", STEAK, RAW_MEAT),
    ("cooked_venison", STEAK, COOKED_MEAT),
    ("raw_boar_meat", HAUNCH, RAW_HAUNCH),
    ("cooked_boar_meat", HAUNCH, COOKED_HAUNCH),
    ("hide", HIDE, {"#": (64, 42, 28), "h": (120, 82, 50), "H": (150, 106, 64), "l": (176, 130, 84)}),
    ("crow_feather", FEATHER, {"#": (12, 12, 18), "f": (30, 30, 40), "s": (58, 66, 104), "q": (210, 206, 196)}),
    ("boar_tusk", TUSK, {"#": (90, 76, 58), "t": (226, 214, 184), "T": (246, 238, 218), "b": (168, 150, 120)}),
    ("dire_tusk", DIRE_TUSK,
     {"#": (70, 56, 40), "t": (214, 198, 152), "T": (236, 224, 186), "r": (150, 40, 34), "b": (140, 122, 92)}),
    ("dragonfly_wing", WING,
     {"#": (40, 54, 64), "m": (200, 230, 240, 190), "v": (56, 72, 84), "s": (226, 150, 48)}),
]


def texel_of(rows, palette):
    def texel(x, y):
        c = rows[y // 2][x // 2]
        if c == " ":
            return (0, 0, 0, 0)
        color = palette[c]
        return color if len(color) == 4 else color + (255,)
    return texel


def outputs():
    return [(os.path.join(hell.ITEM_DIR, name + ".png"), hell.png_bytes(SIZE, texel_of(rows, palette)), False)
            for name, rows, palette in SPRITES]


if __name__ == "__main__":
    hell.main(outputs)
