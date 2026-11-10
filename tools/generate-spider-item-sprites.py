"""Deterministic generator of the spiders' drop sprite (placeholder art).

Writes, under OpenGLTest/assets/textures/items:
  spider_silk.png    a loose skein of pale silk

The sprite is drawn at half resolution, one string per row and one character per 2x2 texels of the 32x32 image,
with its own small palette: ' ' is transparent and '#' the outline. The PNG encoder and the write/--check routine
are the hell mobs' generator's (tools/generate-hell-mob-models.py), so re-running reproduces the file byte for
byte. Run from the repo root:
    python -I tools/generate-spider-item-sprites.py           (writes the file)
    python -I tools/generate-spider-item-sprites.py --check   (exit 1 if the file differs)
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

SPRITES = [
    ("spider_silk", SILK, {"#": (118, 124, 132), "s": (196, 202, 206), "S": (226, 230, 232), "w": (250, 250, 248)}),
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
