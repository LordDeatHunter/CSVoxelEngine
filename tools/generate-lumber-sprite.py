"""Deterministic generator of the lumber item sprite (placeholder art).

Writes OpenGLTest/assets/textures/items/lumber.png: a small stack of rough-cut boards.

The sprite is drawn at half resolution, one string per row and one character per 2x2 texels of the 32x32
image: ' ' is transparent, '#' the outline. The PNG encoder and the write/--check routine are the hell mobs'
generator's (tools/generate-hell-mob-models.py), so re-running reproduces the file byte for byte. Run from
the repo root:
    python -I tools/generate-lumber-sprite.py           (writes the file)
    python -I tools/generate-lumber-sprite.py --check   (exit 1 if it differs)
"""
import importlib.util
import os

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("hell_mobs", os.path.join(HERE, "generate-hell-mob-models.py"))
hell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hell)

SIZE = 32

# 'e' is a board's pale end grain, 'w' its face, 'd' the shaded underside.
LUMBER = [
    "                ",
    "                ",
    "    ###########  ",
    "   #wwwwwwwwwee# ",
    "   #dddddddddee# ",
    "   ############# ",
    "  ###########    ",
    " #wwwwwwwwwee#   ",
    " #dddddddddee#   ",
    " #############   ",
    "   ###########   ",
    "  #wwwwwwwwwee#  ",
    "  #dddddddddee#  ",
    "  #############  ",
    "                ",
    "                ",
]

PALETTE = {"#": (74, 48, 26), "w": (176, 132, 78), "d": (136, 98, 56), "e": (222, 190, 132)}


def texel(x, y):
    c = LUMBER[y // 2][x // 2]
    if c == " ":
        return (0, 0, 0, 0)
    return PALETTE[c] + (255,)


def outputs():
    return [(os.path.join(hell.ITEM_DIR, "lumber.png"), hell.png_bytes(SIZE, texel), False)]


if __name__ == "__main__":
    hell.main(outputs)
