"""Deterministic generator of the crawlers' drop sprites (placeholder art).

Writes, under OpenGLTest/assets/textures/items:
  spider_silk.png         a loose skein of pale silk
  scorpion_stinger.png    a scorpion's amber tail bulb with its dark barb

Each sprite is drawn at half resolution, one string per row and one character per 2x2 texels of the 32x32 image,
with its own small palette: ' ' is transparent and '#' the outline. The PNG encoder and the write/--check routine
are the hell mobs' generator's (tools/generate-hell-mob-models.py), so re-running reproduces every file byte for
byte. Run from the repo root:
    python -I tools/generate-crawler-item-sprites.py           (writes the files)
    python -I tools/generate-crawler-item-sprites.py --check   (exit 1 if a file differs)
"""
import importlib.util
import os

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("hell_mobs", os.path.join(HERE, "generate-hell-mob-models.py"))
hell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hell)

SIZE = 32

SILK = [
    "                ",
    "          ##    ",
    "    ####  #s#   ",
    "   #sSSs## #s#  ",
    "  #sSwwSSs# #s# ",
    "  #SwSSsSS#  ## ",
    " #sSwSsSwSs#    ",
    " #sSSsSwwSs#    ",
    " #sSwSSsSSs#    ",
    " #sSwwSsSSs#    ",
    "  #SSsSSwS#     ",
    "  #sSSwwSs#     ",
    "   #sSSSs#  #   ",
    "    ####s###s#  ",
    "        #sss#   ",
    "         ###    ",
]

STINGER = [
    "                ",
    "         ####   ",
    "       ##aaAA#  ",
    "      #aaAAAhA# ",
    "     #aaAAAhhA# ",
    "     #aAAAAAAA# ",
    "     #aaAAAAAa# ",
    "      #aaAAAa#  ",
    "      #daaaa#   ",
    "     #dd####    ",
    "    #dd#        ",
    "   #dd#         ",
    "  #dt#          ",
    "  #t#           ",
    "  ##            ",
    "                ",
]

SPRITES = [
    ("spider_silk", SILK, {"#": (118, 124, 132), "s": (196, 202, 206), "S": (226, 230, 232), "w": (250, 250, 248)}),
    ("scorpion_stinger", STINGER,
     {"#": (64, 36, 20), "a": (150, 96, 40), "A": (188, 134, 60), "h": (226, 184, 104), "d": (84, 44, 28),
      "t": (40, 22, 18)}),
]


def texel_of(rows, palette):
    def texel(x, y):
        c = rows[y // 2][x // 2]
        if c == " ":
            return (0, 0, 0, 0)
        return palette[c] + (255,)
    return texel


def outputs():
    return [(os.path.join(hell.ITEM_DIR, name + ".png"), hell.png_bytes(SIZE, texel_of(rows, palette)), False)
            for name, rows, palette in SPRITES]


if __name__ == "__main__":
    hell.main(outputs)
