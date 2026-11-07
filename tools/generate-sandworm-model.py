"""Deterministic generator of the sandworm's model, skin and fang sprite (placeholder art).

Writes, under OpenGLTest/assets:
  models/entity/sandworm.bbmodel + sandworm.png   the desert boss (plans/SANDWORM.md)
  textures/items/sandworm_fang.png                 the fang it drops

The rig, UV, PNG, JSON and clip machinery is the hell mobs' generator's (tools/generate-hell-mob-models.py),
loaded from that file, so every generated project is written the same way: no randomness and no image
library, so re-running reproduces every file byte for byte. Run from the repo root:
    python -I tools/generate-sandworm-model.py           (writes the three files)
    python -I tools/generate-sandworm-model.py --check   (exit 1 if any file differs)

This is NOT a humanoid rig. The worm is a chain of rigid segments (SandwormChain), and the renderer draws the
same three parts once per segment at that segment's position and orientation:
  Head   the skull, upper jaw, upper fangs, eyes and crest (drawn at the head segment), with the lower
         jaw nested under it as the group Jaw (hinge at the back of the mouth)
  Body   one ring of the body (drawn at every body segment, scaled down toward the tail)
  Tail   the tapering last segment with its spike
Every part is authored centered on its own origin (0, 0, 0) in pixels (16 px = 1 block) and faces -Z, so the
chain places a part by a matrix alone. The body ring is BODY_LINK_PX long plus an overlap on each end, so a
bent chain shows no gap.

Clip contract: idle, bite (its "impact" event is the moment the jaws snap shut) and hurt, all on Head and Jaw.
Sign convention (BbmodelLoader.ReadAnimations): a rotation (x, y, z) in degrees maps to engine Euler
(-x, -y, z); a positive x on Head or Jaw tips the snout down, so a positive Jaw x opens the mouth.

Pixel art uses a small hand-picked palette (never smooth noise): each face texel picks one of a few colors.
"""
import importlib.util
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("hell_mobs", os.path.join(HERE, "generate-hell-mob-models.py"))
hell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hell)

Clip, v, hash2 = hell.Clip, hell.v, hell.hash2

# The hell generator's humanoid bone list drives its clip and uuid code; this rig has its own.
hell.BONES = ("Head", "Jaw", "Body", "Tail")

BODY_LINK_PX = 40  # the chain's link distance for a body segment, in pixels (2.5 blocks)

SAND = (214, 184, 120)
SAND_DARK = (170, 138, 84)
SAND_LIGHT = (236, 214, 160)
PLATE = (140, 108, 66)
PLATE_DARK = (104, 78, 48)
BONE = (240, 232, 208)
BONE_SHADE = (196, 186, 158)
MOUTH = (150, 40, 40)
MOUTH_DARK = (96, 22, 28)
EYE = (255, 130, 40)


def palette(colors, seed, weights=None):
    """A small-palette mottle: every texel is one of `colors`, picked by a hash (the first most often)."""
    weights = weights or [6, 2, 1][: len(colors)] + [1] * max(0, len(colors) - 3)
    total = sum(weights)

    def paint(face, x, y, w, h, ax, ay):
        r = hash2(ax // 2, ay // 2, seed) % total
        for color, weight in zip(colors, weights):
            if r < weight:
                return color
            r -= weight
        return colors[0]
    return paint


def ringed(colors, seed, every, groove):
    """The body ring: the palette mottle with a dark groove across every `every`-th row of each side."""
    base = palette(colors, seed)

    def paint(face, x, y, w, h, ax, ay):
        if face not in ("up", "down", "north", "south") and y % every == every - 1:
            return groove
        return base(face, x, y, w, h, ax, ay)
    return paint


def eye(seed):
    base = palette([SAND_DARK, PLATE], seed)

    def paint(face, x, y, w, h, ax, ay):
        if face in ("west", "east", "north") and 1 <= x < w - 1 and 1 <= y < h - 1:
            return EYE
        return base(face, x, y, w, h, ax, ay)
    return paint


SANDWORM = {
    "name": "sandworm",
    "uuid": 0xf0,
    "skin": 288,
    "origins": {"Head": (0, 0, 0), "Jaw": (0, -16, -18), "Body": (0, 0, 0), "Tail": (0, 0, 0)},
    "cubes": [
        # ---- Head: faces -Z, about 3 blocks across; the mouth opens under the upper jaw ----
        ("skull", "Head", (-24, -20, -18), (24, 24, 24), palette([SAND, SAND_DARK, PLATE], 71)),
        ("brow", "Head", (-24, 14, -26), (24, 24, -18), palette([PLATE, PLATE_DARK, SAND_DARK], 72)),
        ("upper_jaw", "Head", (-20, -10, -36), (20, 14, -18), palette([SAND_LIGHT, SAND, SAND_DARK], 73)),
        ("palate", "Head", (-16, -12, -34), (16, -10, -18), palette([MOUTH, MOUTH_DARK], 74)),
        ("fang_upper_front_right", "Head", (14, -30, -35), (20, -10, -29), palette([BONE, BONE_SHADE], 75)),
        ("fang_upper_front_left", "Head", (-20, -30, -35), (-14, -10, -29), palette([BONE, BONE_SHADE], 75)),
        ("fang_upper_back_right", "Head", (15, -22, -27), (20, -10, -22), palette([BONE, BONE_SHADE], 76)),
        ("fang_upper_back_left", "Head", (-20, -22, -27), (-15, -10, -22), palette([BONE, BONE_SHADE], 76)),
        ("eye_right", "Head", (24, 2, -14), (27, 12, -4), eye(77)),
        ("eye_left", "Head", (-27, 2, -14), (-24, 12, -4), eye(77)),
        ("crest", "Head", (-4, 24, -12), (4, 34, 22), palette([PLATE, PLATE_DARK], 78)),
        # ---- Jaw: nested under Head, hinged at the back of the mouth ----
        ("lower_jaw", "Jaw", (-14, -26, -34), (14, -16, -18), palette([SAND, SAND_DARK, PLATE], 79)),
        ("tongue", "Jaw", (-10, -16, -32), (10, -14, -18), palette([MOUTH, MOUTH_DARK], 80)),
        ("fang_lower_right", "Jaw", (7, -16, -34), (12, -6, -30), palette([BONE, BONE_SHADE], 81)),
        ("fang_lower_left", "Jaw", (-12, -16, -34), (-7, -6, -30), palette([BONE, BONE_SHADE], 81)),
        # ---- Body: one ring, a hand's width longer than the link so a bent chain shows no gap ----
        ("ring", "Body", (-20, -20, -22), (20, 20, 22),
         ringed([SAND, SAND_DARK, PLATE], 82, 11, PLATE_DARK)),
        ("belly", "Body", (-14, -23, -20), (14, -20, 20), palette([SAND_LIGHT, SAND], 83)),
        ("ridge", "Body", (-10, 20, -18), (10, 25, 18), palette([PLATE, PLATE_DARK], 84)),
        # ---- Tail: tapering last segment with a spike pointing back (+Z) ----
        ("tail_ring", "Tail", (-14, -14, -22), (14, 14, 14), ringed([SAND, SAND_DARK, PLATE], 85, 9, PLATE_DARK)),
        ("tail_spike", "Tail", (-6, -6, 14), (6, 6, 30), palette([PLATE, PLATE_DARK, SAND_DARK], 86)),
        ("tail_tip", "Tail", (-2.5, -2.5, 30), (2.5, 2.5, 42), palette([BONE, BONE_SHADE], 87)),
    ],
}

NESTED_UNDER_HEAD = ("Jaw",)


def outliner(rig):
    cubes_of = {bone: [hell.uuid(rig, i + 1) for i, c in enumerate(rig["cubes"]) if c[1] == bone] for bone in hell.BONES}
    head = hell.group(rig, "Head", cubes_of["Head"] + [hell.group(rig, "Jaw", cubes_of["Jaw"])])
    return [head, hell.group(rig, "Body", cubes_of["Body"]), hell.group(rig, "Tail", cubes_of["Tail"])]


hell.outliner = outliner


def clips():
    idle = Clip("idle", 2.4, "loop", 1)
    # Breathing: the jaw works slowly and the head sways, as it lies in wait.
    idle.rot("Head", [(0, v(0, -4, 0)), (1.2, v(2, 4, 0)), (2.4, v(0, -4, 0))])
    idle.rot("Jaw", [(0, v(6)), (1.2, v(14)), (2.4, v(6))])
    bite = Clip("bite", 1.0, "once", 2)
    # Rears back with the jaws gaping, then snaps shut at the impact (0.45 s) and eases open again.
    bite.rot("Head", [(0, v()), (0.3, v(-16)), (0.45, v(22)), (0.7, v(8)), (1.0, v())])
    bite.rot("Jaw", [(0, v(4)), (0.3, v(52)), (0.45, v(-2)), (0.7, v(10)), (1.0, v(4))])
    bite.event(0.45, "impact")
    hurt = Clip("hurt", 0.4, "once", 3)
    hurt.rot("Head", [(0, v()), (0.06, v(-14, 10, 4)), (0.2, v(-4, 3, 1)), (0.4, v())])
    hurt.rot("Jaw", [(0, v()), (0.06, v(40)), (0.2, v(16)), (0.4, v())])
    return [idle, bite, hurt]


# ---- the fang sprite -----------------------------------------------------------------------------

FANG_SIZE = 32
FANG_OUTLINE = (84, 70, 46)
FANG_BODY, FANG_SHADE, FANG_LIGHT = BONE_SHADE, (168, 154, 120), BONE
# A curved tooth at half resolution ('#' outline, '.' body, '+' bright edge, ',' shade); each character
# covers 2x2 texels of the 32x32 sprite, like the shard sprites.
FANG = [
    "                ",
    "   ######       ",
    "  #++++..#      ",
    "  #+++....#     ",
    "   #+...,,.#    ",
    "   #+....,,.#   ",
    "    #+....,,#   ",
    "    #+....,,#   ",
    "     #+...,#    ",
    "     #+..,,#    ",
    "      #+.,#     ",
    "      #+.#      ",
    "       #.#      ",
    "       #,#      ",
    "        #       ",
    "                ",
]


def fang_texel(x, y):
    c = FANG[y // 2][x // 2]
    if c == " ":
        return (0, 0, 0, 0)
    color = {"#": FANG_OUTLINE, "+": FANG_LIGHT, ",": FANG_SHADE}.get(c, FANG_BODY)
    return color + (255,)


# ---- outputs -------------------------------------------------------------------------------------

def outputs():
    text = hell.inline_number_arrays(json.dumps(hell.project(SANDWORM, clips()), indent=4)) + "\n"
    return [
        (os.path.join(hell.ENTITY_DIR, "sandworm.bbmodel"), text.encode("utf-8"), True),
        (os.path.join(hell.ENTITY_DIR, "sandworm.png"), hell.skin(SANDWORM), False),
        (os.path.join(hell.ITEM_DIR, "sandworm_fang.png"), hell.png_bytes(FANG_SIZE, fang_texel), False),
    ]


if __name__ == "__main__":
    hell.main(outputs)
