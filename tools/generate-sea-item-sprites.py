"""Deterministic generator of the sea animals' drop sprites (placeholder art).

Writes, under OpenGLTest/assets/textures/items:
  raw_cod.png, cooked_cod.png              a cod fillet-fish, raw and off the campfire
  raw_salmon.png, cooked_salmon.png        a salmon, raw and cooked
  raw_tuna.png, cooked_tuna.png            a tuna, raw and cooked
  shark_tooth.png                          a serrated tooth
  jellyfish_gel.png                        a blob of translucent gel (half transparent)

Each sprite is drawn at half resolution, one string per row and one character per 2x2 texels of the 32x32
image, with its own small palette (at most 6 colors, ' ' transparent, '#' the outline). The PNG encoder and the
write/--check routine are the hell mobs' generator's (tools/generate-hell-mob-models.py), so re-running
reproduces every file byte for byte. Run from the repo root:
    python -I tools/generate-sea-item-sprites.py           (writes the eight files)
    python -I tools/generate-sea-item-sprites.py --check   (exit 1 if any file differs)
"""
import importlib.util
import os

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("hell_mobs", os.path.join(HERE, "generate-hell-mob-models.py"))
hell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hell)

SIZE = 32

FISH = [
    "                ",
    "                ",
    "      ###       ",
    "    ##bbb##  ## ",
    "  ##bbBBBbb##ff#",
    " #bbBBBBBBbbfff#",
    "#bwBBBBBBBBbfff#",
    "#bBeBBBBBBBBbff#",
    "#bwBBBBBBBBbfff#",
    " #bbBBBBBBbbfff#",
    "  ##bbBBBbb##ff#",
    "    ##bbb##  ## ",
    "      ###       ",
    "                ",
    "                ",
    "                ",
]

TOOTH = [
    "                ",
    "      ####      ",
    "     #tTTt#     ",
    "     #tTTt#     ",
    "     #tTTt#     ",
    "    #tTTTTt#    ",
    "    #tTTTTt#    ",
    "    #tTTTt#     ",
    "    #tTTTt#     ",
    "    #tTTt#      ",
    "     #tTt#      ",
    "     #tTt#      ",
    "      #tt#      ",
    "      #t#       ",
    "       ##       ",
    "                ",
]

GEL = [
    "                ",
    "                ",
    "                ",
    "      ####      ",
    "    ##gggg##    ",
    "   #ggGGGGgg#   ",
    "  #ggGGhhGGgg#  ",
    "  #gGGhGGGGGg#  ",
    "  #gGGGGGGGGg#  ",
    "  #ggGGGGGGgg#  ",
    "   #ggGGGGgg#   ",
    "    ##gggg##    ",
    "      ####      ",
    "                ",
    "                ",
    "                ",
]

COD_RAW = {"#": (52, 48, 36), "b": (120, 108, 70), "B": (158, 142, 92), "w": (226, 218, 190), "e": (24, 24, 24),
           "f": (96, 84, 56)}
COD_COOKED = {"#": (60, 34, 20), "b": (140, 92, 52), "B": (184, 128, 70), "w": (226, 196, 140), "e": (40, 24, 16),
              "f": (110, 70, 40)}
SALMON_RAW = {"#": (70, 28, 30), "b": (170, 70, 70), "B": (222, 108, 92), "w": (240, 200, 190), "e": (24, 24, 24),
              "f": (130, 50, 54)}
SALMON_COOKED = {"#": (60, 34, 20), "b": (170, 100, 60), "B": (214, 140, 84), "w": (240, 200, 150), "e": (40, 24, 16),
                 "f": (130, 78, 44)}
TUNA_RAW = {"#": (22, 32, 56), "b": (50, 78, 128), "B": (86, 120, 168), "w": (210, 220, 232), "e": (12, 12, 16),
            "f": (36, 54, 92)}
TUNA_COOKED = {"#": (60, 34, 20), "b": (128, 76, 52), "B": (170, 108, 74), "w": (216, 170, 130), "e": (40, 24, 16),
               "f": (104, 62, 42)}

SPRITES = [
    ("raw_cod", FISH, COD_RAW),
    ("cooked_cod", FISH, COD_COOKED),
    ("raw_salmon", FISH, SALMON_RAW),
    ("cooked_salmon", FISH, SALMON_COOKED),
    ("raw_tuna", FISH, TUNA_RAW),
    ("cooked_tuna", FISH, TUNA_COOKED),
    ("shark_tooth", TOOTH, {"#": (84, 78, 70), "t": (206, 200, 184), "T": (240, 236, 224)}),
    ("jellyfish_gel", GEL,
     {"#": (86, 70, 128), "g": (176, 150, 230, 200), "G": (206, 186, 244, 170), "h": (244, 236, 255, 230)}),
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
