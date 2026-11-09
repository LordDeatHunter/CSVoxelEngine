"""Deterministic generator of the skeletons' models and skins and the bone sprite (placeholder art).

Writes, under OpenGLTest/assets:
  models/entity/skeleton.bbmodel + skeleton.png   the battle tower's archer (plans/BATTLE_TOWER.md BT-3): lanky and
                                                  tall, a bow under the left arm
  models/entity/skeleton_warrior.bbmodel + .png   its melee kin: the same bones a little shorter and bulkier, a copper
                                                  dagger in the right hand instead of the bow
  textures/items/bone.png                         the bone it drops

The rig, UV, PNG, JSON and clip machinery is the hell mobs' generator's (tools/generate-hell-mob-models.py),
loaded from that file like the sky mobs' generator does, so every script writes its projects the same way:
no randomness and no image library, every shade picked from a small hand-picked palette by an integer hash of
the texel, so re-running reproduces every file byte for byte. Run from the repo root:
    python -I tools/generate-skeleton-model.py           (writes the six files)
    python -I tools/generate-skeleton-model.py --check   (exit 1 if any file differs)

Rig contract (EntityContract.RequireHumanoid): the groups Torso, Head, ArmL, ArmR, LegL and LegR, with Head
and the arms nested under Torso and the legs at the top level; the model faces -Z, units are pixels (16 px =
1 block) with the feet on y = 0, the archer 32 px tall and the warrior 28. The bow is a few thin cubes under ArmL:
at rest it hangs front to back beside the leg, and with the arm raised forward it stands upright with its string
toward the body; the warrior's dagger is a grip, a guard and a blade under ArmR, pointing forward from the hand.
Both share every clip: idle, walk, "melee" (an overhand rake of the right arm) carrying the "impact" event and "shoot"
(the bow raised, the string drawn back to the cheek, a snap at the shot) carrying "release"
(Skeleton.ValidateContent), plus the shared hurt, death and spawn. The event times pace the attacks.

Sign convention (BbmodelLoader.ReadAnimations): a rotation (x, y, z) in degrees maps to engine Euler
(-x, -y, z). For limbs a NEGATIVE x raises a hanging limb forward; for the torso and head a positive x leans
forward. About z a positive angle lifts the right arm (+X) out to the side and swings the left one in.
Position keys are in pixels, y up, +z backward.

Pixel art: every surface picks among two or three shades of one hand-picked palette (paletted), never a
noisy speckle, and the sprites are char maps at half resolution whose dark outline is traced around the
filled characters.
"""
import importlib.util
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("hell_mobs", os.path.join(HERE, "generate-hell-mob-models.py"))
hell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hell)

Clip, v, cycle = hell.Clip, hell.v, hell.cycle

# ---- the palette --------------------------------------------------------------------------------

BONE = (226, 220, 198)
BONE_SHADE = (194, 186, 160)
BONE_DARK = (146, 138, 116)
SOCKET = (40, 34, 34)
EYE = (222, 52, 40)
WOOD = (122, 84, 48)
WOOD_DARK = (88, 58, 34)
STRING = (234, 232, 220)
COPPER = (206, 118, 64)
COPPER_LIGHT = (232, 156, 98)
COPPER_DARK = (150, 80, 44)
OUTLINE = (46, 38, 32)


def paletted(shades, seed):
    """Picks one of a few hand-picked shades per texel by a hash; list a shade twice to weight it."""
    def paint(face, x, y, w, h, ax, ay):
        return shades[hell.hash2(ax, ay, seed) % len(shades)]
    return paint


def ribcage(seed):
    """Ribs: bone rows over dark gaps on the sides, a sternum down the front middle and the spine at the back."""
    bone = paletted((BONE, BONE, BONE_SHADE), seed)

    def paint(face, x, y, w, h, ax, ay):
        if face in ("up", "down"):
            return bone(face, x, y, w, h, ax, ay)
        middle = face in ("north", "south") and w // 2 - 1 <= x <= w // 2
        if y % 2 == 1 and not middle and y < h - 1:
            return SOCKET
        return bone(face, x, y, w, h, ax, ay)
    return paint


def spine(seed):
    """Vertebrae: bone segments split by a dark line every other row."""
    bone = paletted((BONE_SHADE, BONE), seed)

    def paint(face, x, y, w, h, ax, ay):
        if face not in ("up", "down") and y % 2 == 1:
            return BONE_DARK
        return bone(face, x, y, w, h, ax, ay)
    return paint


def limb(seed):
    """A long bone: two shades along it and a darker joint across its middle."""
    bone = paletted((BONE, BONE_SHADE), seed)

    def paint(face, x, y, w, h, ax, ay):
        if face not in ("up", "down") and y == h // 2:
            return BONE_DARK
        return bone(face, x, y, w, h, ax, ay)
    return paint


def skull(seed):
    """The face: two dark sockets with a red glint at their inner corners, a nose hole and a row of teeth."""
    bone = paletted((BONE, BONE, BONE_SHADE), seed)

    def paint(face, x, y, w, h, ax, ay):
        if face == "north":
            if y in (2, 3) and x in (1, 2, w - 3, w - 2):
                return EYE if y == 2 and x in (2, w - 3) else SOCKET
            if y == 4 and x == w // 2:
                return SOCKET
            if y == h - 1 and 1 <= x <= w - 2:
                return SOCKET if x % 2 == 0 else BONE
        return bone(face, x, y, w, h, ax, ay)
    return paint


# ---- the rigs: cubes as (name, bone, from, to, material) ----------------------------------------

def bones(leg, hip, spine_len, rib_w, rib_d, arm_x, arm_w, arm_len):
    """One skeleton's bones from its proportions: leg length, pelvis half-width, spine length, ribcage half-width
    and half-depth, the arms' center offset, half-width and length. Returns the bone origins, the cubes and the
    bottom of the hands (where a held thing hangs)."""
    pelvis_top = leg + 2
    rib_bottom = pelvis_top + spine_len
    rib_top = rib_bottom + 7
    hand = rib_top - arm_len
    half = leg_half_width(leg)
    origins = {"Torso": (0, leg, 0), "Head": (0, rib_top, 0), "ArmR": (arm_x, rib_top - 1, 0),
               "ArmL": (-arm_x, rib_top - 1, 0), "LegR": (2, leg, 0), "LegL": (-2, leg, 0)}
    cubes = [
        ("pelvis", "Torso", (-hip, leg, -1), (hip, pelvis_top, 1), paletted((BONE_SHADE, BONE_SHADE, BONE_DARK), 71)),
        ("spine", "Torso", (-1, pelvis_top, -1), (1, rib_bottom, 1), spine(72)),
        ("ribcage", "Torso", (-rib_w, rib_bottom, -rib_d), (rib_w, rib_top, rib_d), ribcage(73)),
        ("skull", "Head", (-3.5, rib_top, -3.5), (3.5, rib_top + 6, 3.5), skull(74)),
        ("arm_right", "ArmR", (arm_x - arm_w, hand, -arm_w), (arm_x + arm_w, rib_top, arm_w), limb(75)),
        ("arm_left", "ArmL", (-arm_x - arm_w, hand, -arm_w), (-arm_x + arm_w, rib_top, arm_w), limb(75)),
        ("leg_right", "LegR", (2 - half, 0, -half), (2 + half, leg, half), limb(80)),
        ("leg_left", "LegL", (-2 - half, 0, -half), (-2 + half, leg, half), limb(80)),
    ]
    return origins, cubes, hand


def leg_half_width(leg):
    """A leg's half-width: thinner on the longer legs, so the archer reads lanky and the warrior sturdy."""
    return 1.25 if leg <= 11 else 0.75


def bow(arm_x, hand):
    """The archer's bow under ArmL, hanging from the hand front to back."""
    x0, x1 = -arm_x - 0.5, -arm_x + 0.5
    return [
        ("bow_grip", "ArmL", (x0, hand - 3, -1), (x1, hand, 1), paletted((WOOD_DARK,), 76)),
        ("bow_front", "ArmL", (x0, hand - 3, -6), (x1, hand - 2, -1), paletted((WOOD, WOOD, WOOD_DARK), 77)),
        ("bow_back", "ArmL", (x0, hand - 3, 1), (x1, hand - 2, 6), paletted((WOOD, WOOD, WOOD_DARK), 77)),
        ("bow_tip_front", "ArmL", (x0, hand - 2, -7), (x1, hand, -6), paletted((WOOD, WOOD_DARK), 78)),
        ("bow_tip_back", "ArmL", (x0, hand - 2, 6), (x1, hand, 7), paletted((WOOD, WOOD_DARK), 78)),
        ("bow_string", "ArmL", (x0 + 0.25, hand - 0.5, -6), (x1 - 0.25, hand, 6), paletted((STRING,), 79)),
    ]


def dagger(arm_x, hand):
    """The warrior's copper dagger under ArmR: a grip in the fist, a guard and a blade pointing forward."""
    x0, x1 = arm_x - 0.5, arm_x + 0.5
    return [
        ("dagger_grip", "ArmR", (x0, hand - 2, -0.5), (x1, hand, 0.5), paletted((WOOD_DARK, WOOD), 81)),
        ("dagger_guard", "ArmR", (x0 - 0.5, hand - 2, -1.5), (x1 + 0.5, hand - 1, -0.5),
         paletted((COPPER_DARK, COPPER), 82)),
        ("dagger_blade", "ArmR", (x0 + 0.25, hand - 2, -6.5), (x1 - 0.25, hand - 1, -1.5),
         paletted((COPPER, COPPER_LIGHT, COPPER), 83)),
    ]


def archer():
    """Lanky and tall (32 px): long thin legs and arms, a narrow ribcage and a long spine."""
    origins, cubes, hand = bones(leg=13, hip=2.5, spine_len=4, rib_w=3.5, rib_d=2, arm_x=4.75, arm_w=0.75, arm_len=14)
    return {"name": "skeleton", "uuid": 0x90, "skin": 64, "origins": origins, "cubes": cubes + bow(4.75, hand)}


def warrior():
    """Shorter and bulkier (28 px): short sturdy legs, a wide deep ribcage, thick arms and a short spine."""
    origins, cubes, hand = bones(leg=11, hip=3.5, spine_len=2, rib_w=4.5, rib_d=2.5, arm_x=5.75, arm_w=1.25,
                                 arm_len=13)
    return {"name": "skeleton_warrior", "uuid": 0x94, "skin": 64, "origins": origins,
            "cubes": cubes + dagger(5.75, hand)}


# ---- clips ---------------------------------------------------------------------------------------

def skeleton_clips():
    idle = Clip("idle", 2.0, "loop", 1)
    # A loose, rattling stand: the ribcage sways, the skull turns and nods, the arms hang and drift.
    idle.pos("Torso", [(0, v()), (1.0, v(0, 0.3)), (2.0, v())])
    idle.rot("Torso", [(0, v(2, 0, 0)), (0.5, v(3, 0, 1.5)), (1.0, v(2, 0, 0)), (1.5, v(3, 0, -1.5)), (2.0, v(2, 0, 0))])
    idle.rot("Head", [(0, v()), (0.6, v(-4, 14, 0)), (1.0, v()), (1.5, v(4, -12, 0)), (2.0, v())])
    idle.rot("ArmR", [(0, v(-4, 0, 3)), (1.0, v(4, 0, 5)), (2.0, v(-4, 0, 3))])
    idle.rot("ArmL", [(0, v(-8, 0, -3)), (1.0, v(-2, 0, -5)), (2.0, v(-8, 0, -3))])
    t = [0, 0.25, 0.5, 0.75, 1.0]
    walk = Clip("walk", 1.0, "loop", 2)
    # A light, stiff stride: the legs swing wide, the arms swing against them, the skull bobs.
    walk.pos("Torso", [(0, v(0, -0.3)), (0.25, v(0, 0.4)), (0.5, v(0, -0.3)), (0.75, v(0, 0.4)), (1.0, v(0, -0.3))])
    walk.rot("Torso", [(0, v(2, 6, 0)), (0.25, v(1, 0, 2)), (0.5, v(2, -6, 0)), (0.75, v(1, 0, -2)), (1.0, v(2, 6, 0))])
    walk.rot("Head", [(0, v(0, -5, 0)), (0.5, v(0, 5, 0)), (1.0, v(0, -5, 0))])
    walk.rot("ArmR", cycle(t, [-24, -2, 24, 2, -24], lambda x: v(x, 0, 4)))
    walk.rot("ArmL", cycle(t, [24, 2, -24, -2, 24], lambda x: v(x, 0, -4)))
    walk.rot("LegR", cycle(t, [24, 2, -24, -2, 24], lambda x: v(x)))
    walk.rot("LegL", cycle(t, [-24, -2, 24, 2, -24], lambda x: v(x)))
    melee = Clip("melee", 0.7, "once", 4)
    # The right arm swung up over the shoulder, a short hold, then raked down across the front at the
    # impact (0.4 s) with the body lunging after it, then a settle.
    melee.rot("Torso", [(0, v()), (0.25, v(-8, 14, 0)), (0.33, v(-10, 16, 0)), (0.4, v(14, -12, 0)), (0.52, v(10, -8, 0)), (0.7, v())])
    melee.rot("Head", [(0, v()), (0.25, v(-4, -10, 0)), (0.4, v(6, 8, 0)), (0.7, v())])
    melee.rot("ArmR", [(0, v()), (0.25, v(-160, 0, 18)), (0.33, v(-168, 0, 20)), (0.4, v(-40, 0, -16)),
                       (0.52, v(-28, 0, -12)), (0.7, v())])
    melee.rot("ArmL", [(0, v(-8, 0, -3)), (0.25, v(-20, 0, -12)), (0.4, v(10, 0, -6)), (0.7, v(-8, 0, -3))])
    melee.rot("LegR", [(0, v()), (0.25, v(8)), (0.4, v(-12)), (0.7, v())])
    melee.rot("LegL", [(0, v()), (0.25, v(-8)), (0.4, v(14)), (0.7, v())])
    melee.event(0.4, "impact")
    shoot = Clip("shoot", 0.9, "once", 3)
    # The bow arm raised straight out in front (the bow then stands upright), the right hand pulling the
    # string back to the cheek as the body leans back into the draw, a snap forward at the release
    # (0.6 s), then both arms come down.
    shoot.rot("Torso", [(0, v()), (0.25, v(-3, 0, 0)), (0.6, v(-6, 0, 0)), (0.65, v(4, 0, 0)), (0.9, v())])
    shoot.rot("Head", [(0, v()), (0.25, v(4, 0, 0)), (0.6, v(4, 0, 0)), (0.65, v(-2, 0, 0)), (0.9, v())])
    shoot.rot("ArmL", [(0, v(-8, 0, -3)), (0.25, v(-90, 0, 0)), (0.6, v(-92, 0, 0)), (0.65, v(-84, 0, 0)),
                       (0.78, v(-80, 0, 0)), (0.9, v(-8, 0, -3))])
    shoot.rot("ArmR", [(0, v()), (0.25, v(-90, 0, 0)), (0.55, v(-84, 0, -18)), (0.6, v(-84, 0, -18)),
                       (0.65, v(-96, 0, 14)), (0.9, v())])
    shoot.pos("ArmR", [(0, v()), (0.25, v()), (0.55, v(0, 0, 4)), (0.6, v(0, 0, 4)), (0.65, v(0, 0, 1)), (0.9, v())])
    shoot.rot("LegR", [(0, v()), (0.25, v(-6)), (0.65, v(-4)), (0.9, v())])
    shoot.rot("LegL", [(0, v()), (0.25, v(8)), (0.65, v(6)), (0.9, v())])
    shoot.event(0.6, "release")
    return [idle, walk, melee, shoot] + hell.common_clips(heavy=False)


# ---- the sprites -------------------------------------------------------------------------------

SPRITE_SIZE = 32
# Half-resolution char maps (each character covers 2x2 texels of the 32x32 sprite); a blank cell beside a
# filled one becomes the dark outline. (The arrow it shoots is hand-drawn now: flint_arrow.png.)
BONE_SPRITE = [
    "                ",
    "                ",
    "           ++   ",
    "          +..s  ",
    "          ...ss ",
    "         +.ss.s ",
    "        +.s  s  ",
    "       +.s      ",
    "      +.s       ",
    "     +.s        ",
    "  + +.s         ",
    " +..+s          ",
    " +..s           ",
    "  ss.s          ",
    "   s            ",
    "                ",
]
BONE_COLORS = {"+": (246, 242, 226), ".": BONE, "s": BONE_DARK}


def sprite(rows, colors):
    def texel(x, y):
        cx, cy = x // 2, y // 2
        c = rows[cy][cx]
        if c != " ":
            return colors[c] + (255,)
        for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nx, ny = cx + dx, cy + dy
            if 0 <= nx < 16 and 0 <= ny < 16 and rows[ny][nx] != " ":
                return OUTLINE + (255,)
        return (0, 0, 0, 0)
    return texel


# ---- outputs -------------------------------------------------------------------------------------

def model_outputs(rig):
    text = hell.inline_number_arrays(json.dumps(hell.project(rig, skeleton_clips()), indent=4)) + "\n"
    return [
        (os.path.join(hell.ENTITY_DIR, rig["name"] + ".bbmodel"), text.encode("utf-8"), True),
        (os.path.join(hell.ENTITY_DIR, rig["name"] + ".png"), hell.skin(rig), False),
    ]


def outputs():
    return model_outputs(archer()) + model_outputs(warrior()) + [
        (os.path.join(hell.ITEM_DIR, "bone.png"), hell.png_bytes(SPRITE_SIZE, sprite(BONE_SPRITE, BONE_COLORS)), False),
    ]


if __name__ == "__main__":
    hell.main(outputs)
