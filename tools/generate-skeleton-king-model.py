"""Deterministic generator of the Skeleton King's model and skin (placeholder art).

Writes, under OpenGLTest/assets:
  models/entity/skeleton_king.bbmodel + skeleton_king.png   the battle tower's roof boss (plans/BATTLE_TOWER.md BT-6)

The rig, UV, PNG, JSON and clip machinery is the hell mobs' generator's (tools/generate-hell-mob-models.py), and the
bone palette and painters are the skeleton's (tools/generate-skeleton-model.py), both loaded from their files like
the skeleton's generator loads the hell one, so every script writes its projects the same way: no randomness and
no image library, every shade picked from a small hand-picked palette by an integer hash of the texel, so
re-running reproduces both files byte for byte. Run from the repo root:
    python -I tools/generate-skeleton-king-model.py           (writes the two files)
    python -I tools/generate-skeleton-king-model.py --check   (exit 1 if any file differs)

Rig contract (EntityContract.RequireHumanoid): the groups Torso, Head, ArmL, ArmR, LegL and LegR, with Head and
the arms nested under Torso and the legs at the top level; the model faces -Z, units are pixels (16 px = 1 block)
with the feet on y = 0, 48 px tall (3 blocks, MobConfig.SkeletonKingHeight) with broad gold pauldrons. The head
wears a gold crown, the torso a dark purple loin robe and cape, and a big bone club hangs from the right hand
along the arm, so raised overhead it stands up behind the head and swung down it reaches the ground in front.
Clips: idle, walk, "slam" (the club overhead, then smashed down) carrying the "impact" event, "volley" (a left-arm
sweep that flings the bones) carrying "release" and "summon" (both arms raised and spread) carrying "summon"
(SkeletonKing.ValidateContent), plus the shared heavy hurt and death and a "spawn" that rises out of the floor (every
top-level group starts 24 px down). The event times pace the attacks.

Sign convention (BbmodelLoader.ReadAnimations): a rotation (x, y, z) in degrees maps to engine Euler (-x, -y, z).
For limbs a NEGATIVE x raises a hanging limb forward; for the torso and head a positive x leans forward. About z a
positive angle lifts the right arm (+X) out to the side and swings the left one in. Position keys are in pixels,
y up, +z backward.

Pixel art: every surface picks among two or three shades of one hand-picked palette (paletted), the skeleton's
bone shades recolored with gold and purple, never a noisy speckle.
"""
import importlib.util
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))


def _load(name, file):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, file))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


hell = _load("hell_mobs", "generate-hell-mob-models.py")
bones = _load("skeleton_model", "generate-skeleton-model.py")

Clip, v, cycle = hell.Clip, hell.v, hell.cycle
paletted, ribcage, spine, limb = bones.paletted, bones.ribcage, bones.spine, bones.limb

# ---- the palette: the skeleton's bone shades, its socket and red eye, gold and purple ------------

BONE, BONE_SHADE, BONE_DARK = bones.BONE, bones.BONE_SHADE, bones.BONE_DARK
SOCKET, EYE = bones.SOCKET, bones.EYE
GOLD = (222, 176, 58)
GOLD_DARK = (156, 112, 34)
PURPLE = (78, 36, 102)
PURPLE_DARK = (48, 22, 64)


def king_skull(seed):
    """The face: two dark sockets each holding a red glint, a nose hole and two rows of teeth."""
    bone = paletted((BONE, BONE, BONE_SHADE), seed)

    def paint(face, x, y, w, h, ax, ay):
        if face == "north":
            if y in (3, 4) and x in (2, 3, w - 4, w - 3):
                return EYE if y == 3 and x in (3, w - 4) else SOCKET
            if y == 5 and x in (w // 2 - 1, w // 2):
                return SOCKET
            if y >= h - 2 and 2 <= x <= w - 3:
                return SOCKET if (x + y) % 2 == 0 else BONE
        return bone(face, x, y, w, h, ax, ay)
    return paint


def crown_band(seed):
    """The crown's band: gold over a dark gold rim row, a red gem in the middle of the front."""
    gold = paletted((GOLD, GOLD, GOLD_DARK), seed)

    def paint(face, x, y, w, h, ax, ay):
        if face == "north" and y == 0 and x in (w // 2 - 1, w // 2):
            return EYE
        if face not in ("up", "down") and y == h - 1:
            return GOLD_DARK
        return gold(face, x, y, w, h, ax, ay)
    return paint


def robe(seed, hem_rows):
    """Deep purple cloth with a gold trim along its hem (the bottom rows of every side)."""
    cloth = paletted((PURPLE, PURPLE, PURPLE_DARK), seed)
    trim = paletted((GOLD, GOLD_DARK), seed + 1)

    def paint(face, x, y, w, h, ax, ay):
        if face not in ("up", "down") and y >= h - hem_rows:
            return trim(face, x, y, w, h, ax, ay)
        return cloth(face, x, y, w, h, ax, ay)
    return paint


# ---- the rig: cubes as (name, bone, from, to, material) -----------------------------------------

KING = {
    "name": "skeleton_king",
    "uuid": 0x9a,
    "skin": 128,
    "origins": {"Torso": (0, 17, 0), "Head": (0, 36, 0), "ArmR": (9.5, 34, 0), "ArmL": (-9.5, 34, 0),
                "LegR": (3, 17, 0), "LegL": (-3, 17, 0)},
    "cubes": [
        ("robe", "Torso", (-5.5, 11, -2.5), (5.5, 20, 2.5), robe(101, 2)),
        ("spine", "Torso", (-1.5, 20, -1.5), (1.5, 25, 1.5), spine(102)),
        ("ribcage", "Torso", (-7, 25, -3.5), (7, 36, 3.5), ribcage(103)),
        ("cape", "Torso", (-7, 13, 3.5), (7, 35, 4.5), robe(104, 2)),
        ("skull", "Head", (-5, 36, -5), (5, 45, 5), king_skull(105)),
        ("crown", "Head", (-5.5, 44, -5.5), (5.5, 46, 5.5), crown_band(106)),
        ("crown_spike_nw", "Head", (-5.5, 46, -5.5), (-3.5, 48, -3.5), paletted((GOLD, GOLD_DARK), 107)),
        ("crown_spike_ne", "Head", (3.5, 46, -5.5), (5.5, 48, -3.5), paletted((GOLD, GOLD_DARK), 107)),
        ("crown_spike_sw", "Head", (-5.5, 46, 3.5), (-3.5, 48, 5.5), paletted((GOLD, GOLD_DARK), 107)),
        ("crown_spike_se", "Head", (3.5, 46, 3.5), (5.5, 48, 5.5), paletted((GOLD, GOLD_DARK), 107)),
        ("crown_spike_front", "Head", (-1, 46, -5.5), (1, 49, -4.5), paletted((GOLD, GOLD_DARK), 108)),
        ("pauldron_right", "ArmR", (7.5, 32, -3), (12, 36, 3), paletted((GOLD, GOLD, GOLD_DARK), 109)),
        ("arm_right", "ArmR", (8, 19, -1.5), (11, 33, 1.5), limb(110)),
        ("club_shaft", "ArmR", (8.5, 7, -1), (10.5, 19, 1), limb(111)),
        ("club_head", "ArmR", (7.5, 1, -2), (11.5, 7, 2), paletted((BONE, BONE_SHADE, BONE_DARK), 112)),
        ("pauldron_left", "ArmL", (-12, 32, -3), (-7.5, 36, 3), paletted((GOLD, GOLD, GOLD_DARK), 109)),
        ("arm_left", "ArmL", (-11, 19, -1.5), (-8, 33, 1.5), limb(110)),
        ("leg_right", "LegR", (1.5, 2, -1.5), (4.5, 17, 1.5), limb(113)),
        ("foot_right", "LegR", (1, 0, -3), (5, 2, 1.5), paletted((BONE_SHADE, BONE_DARK), 114)),
        ("leg_left", "LegL", (-4.5, 2, -1.5), (-1.5, 17, 1.5), limb(113)),
        ("foot_left", "LegL", (-5, 0, -3), (-1, 2, 1.5), paletted((BONE_SHADE, BONE_DARK), 114)),
    ],
}


# ---- clips ---------------------------------------------------------------------------------------

def king_clips():
    idle = Clip("idle", 3.0, "loop", 1)
    # A heavy, regal stand: the chest heaves slowly, the crowned skull turns to survey, the club hangs.
    idle.pos("Torso", [(0, v()), (1.5, v(0, 0.6)), (3.0, v())])
    idle.rot("Torso", [(0, v(3, 0, 0)), (0.75, v(4, 0, 1)), (1.5, v(3, 0, 0)), (2.25, v(4, 0, -1)), (3.0, v(3, 0, 0))])
    idle.rot("Head", [(0, v()), (0.9, v(-3, 16, 0)), (1.5, v()), (2.2, v(3, -14, 0)), (3.0, v())])
    idle.rot("ArmR", [(0, v(-6, 0, 4)), (1.5, v(0, 0, 6)), (3.0, v(-6, 0, 4))])
    idle.rot("ArmL", [(0, v(-4, 0, -4)), (1.5, v(2, 0, -6)), (3.0, v(-4, 0, -4))])
    t = [0, 0.35, 0.7, 1.05, 1.4]
    walk = Clip("walk", 1.4, "loop", 2)
    # A slow, stomping stride: the legs swing, the left arm swings against them, the club arm barely
    # moves under its weight, the body bobs deep at each footfall.
    walk.pos("Torso", [(0, v(0, -0.8)), (0.35, v(0, 0.5)), (0.7, v(0, -0.8)), (1.05, v(0, 0.5)), (1.4, v(0, -0.8))])
    walk.rot("Torso", [(0, v(4, 7, 0)), (0.35, v(3, 0, 2)), (0.7, v(4, -7, 0)), (1.05, v(3, 0, -2)), (1.4, v(4, 7, 0))])
    walk.rot("Head", [(0, v(0, -5, 0)), (0.7, v(0, 5, 0)), (1.4, v(0, -5, 0))])
    walk.rot("ArmR", cycle(t, [-12, -2, 10, 2, -12], lambda x: v(x, 0, 6)))
    walk.rot("ArmL", cycle(t, [24, 2, -24, -2, 24], lambda x: v(x, 0, -5)))
    walk.rot("LegR", cycle(t, [22, 2, -22, -2, 22], lambda x: v(x)))
    walk.rot("LegL", cycle(t, [-22, -2, 22, 2, -22], lambda x: v(x)))
    slam = Clip("slam", 1.0, "once", 3)
    # The club heaved up overhead (it stands behind the crown), a held beat, then smashed down in front at
    # the impact (0.55 s) with the whole body folding after it, a rest at the bottom and a slow heave back.
    slam.pos("Torso", [(0, v()), (0.3, v(0, 1.0)), (0.45, v(0, 1.2)), (0.55, v(0, -2.6)), (0.72, v(0, -2.2)), (1.0, v())])
    slam.rot("Torso", [(0, v(3, 0, 0)), (0.3, v(-14, 8, 0)), (0.45, v(-18, 10, 0)), (0.55, v(26, -4, 0)),
                       (0.72, v(24, -4, 0)), (1.0, v(3, 0, 0))])
    slam.rot("Head", [(0, v()), (0.3, v(-8, -6, 0)), (0.55, v(-16, 0, 0)), (0.72, v(-14, 0, 0)), (1.0, v())])
    slam.rot("ArmR", [(0, v(-6, 0, 4)), (0.3, v(-168, 0, -6)), (0.45, v(-176, 0, -8)), (0.55, v(-52, 0, -4)),
                      (0.72, v(-46, 0, -4)), (1.0, v(-6, 0, 4))])
    slam.rot("ArmL", [(0, v(-4, 0, -4)), (0.3, v(-30, 0, -24)), (0.55, v(-10, 0, -10)), (1.0, v(-4, 0, -4))])
    slam.rot("LegR", [(0, v()), (0.3, v(-6)), (0.55, v(18)), (0.72, v(14)), (1.0, v())])
    slam.rot("LegL", [(0, v()), (0.3, v(8)), (0.55, v(-10)), (0.72, v(-8)), (1.0, v())])
    slam.event(0.55, "impact")
    volley = Clip("volley", 0.8, "once", 4)
    # The left arm drawn back across the chest, then swept wide out to the side and forward, flinging the
    # bones at the release (0.45 s), the body twisting into the throw, then a settle.
    volley.rot("Torso", [(0, v(3, 0, 0)), (0.3, v(0, -20, 0)), (0.45, v(6, 22, 0)), (0.6, v(5, 18, 0)), (0.8, v(3, 0, 0))])
    volley.rot("Head", [(0, v()), (0.3, v(-4, 14, 0)), (0.45, v(2, -10, 0)), (0.8, v())])
    volley.rot("ArmL", [(0, v(-4, 0, -4)), (0.3, v(-70, -40, 30)), (0.45, v(-88, 30, -40)), (0.6, v(-80, 24, -36)),
                        (0.8, v(-4, 0, -4))])
    volley.rot("ArmR", [(0, v(-6, 0, 4)), (0.3, v(-14, 0, 10)), (0.45, v(4, 0, 6)), (0.8, v(-6, 0, 4))])
    volley.rot("LegR", [(0, v()), (0.3, v(6)), (0.45, v(-10)), (0.8, v())])
    volley.rot("LegL", [(0, v()), (0.3, v(-6)), (0.45, v(10)), (0.8, v())])
    volley.event(0.45, "release")
    summon = Clip("summon", 1.2, "once", 8)
    # Both arms raised high and spread wide, the skull thrown back to the sky, a shudder at the summons
    # (0.7 s) as the minions answer, then the arms come down.
    summon.pos("Torso", [(0, v()), (0.4, v(0, 0.8)), (0.7, v(0, 1.2)), (0.76, v(0, 0.4)), (1.2, v())])
    summon.rot("Torso", [(0, v(3, 0, 0)), (0.4, v(-10, 0, 0)), (0.7, v(-14, 0, 0)), (0.76, v(-8, 0, 2)), (1.2, v(3, 0, 0))])
    summon.rot("Head", [(0, v()), (0.4, v(-20, 0, 0)), (0.7, v(-26, 0, 0)), (0.76, v(-18, 0, 0)), (1.2, v())])
    summon.rot("ArmR", [(0, v(-6, 0, 4)), (0.4, v(-140, 0, 40)), (0.7, v(-150, 0, 48)), (0.76, v(-136, 0, 40)),
                        (1.2, v(-6, 0, 4))])
    summon.rot("ArmL", [(0, v(-4, 0, -4)), (0.4, v(-140, 0, -40)), (0.7, v(-150, 0, -48)), (0.76, v(-136, 0, -40)),
                        (1.2, v(-4, 0, -4))])
    summon.rot("LegR", [(0, v()), (0.4, v(0, 0, 6)), (1.2, v())])
    summon.rot("LegL", [(0, v()), (0.4, v(0, 0, -6)), (1.2, v())])
    summon.event(0.7, "summon")
    spawn = Clip("spawn", 1.4, "once", 7)
    # Rises out of the altar floor: every top-level group starts 24 px down and climbs, arms reaching up
    # out of the ground, a stagger once it stands, then the regal stance.
    rise = [(0, v(0, -24)), (0.8, v(0, -4)), (1.05, v(0, 0.8)), (1.2, v(0, -0.3)), (1.4, v())]
    spawn.pos("Torso", rise)
    spawn.pos("LegR", rise)
    spawn.pos("LegL", rise)
    spawn.rot("Torso", [(0, v(-14, 0, 0)), (0.8, v(-8, 0, 0)), (1.05, v(6, 0, 0)), (1.4, v(3, 0, 0))])
    spawn.rot("Head", [(0, v(-24, 0, 0)), (0.8, v(-16, 0, 0)), (1.2, v(4, 0, 0)), (1.4, v())])
    spawn.rot("ArmR", [(0, v(-160, 0, 20)), (0.8, v(-120, 0, 24)), (1.2, v(-20, 0, 8)), (1.4, v(-6, 0, 4))])
    spawn.rot("ArmL", [(0, v(-160, 0, -20)), (0.8, v(-120, 0, -24)), (1.2, v(-20, 0, -8)), (1.4, v(-4, 0, -4))])
    hurt, death = hell.common_clips(heavy=True)[:2]
    return [idle, walk, slam, volley, summon, hurt, death, spawn]


# ---- outputs -------------------------------------------------------------------------------------

def outputs():
    text = hell.inline_number_arrays(json.dumps(hell.project(KING, king_clips()), indent=4)) + "\n"
    return [
        (os.path.join(hell.ENTITY_DIR, "skeleton_king.bbmodel"), text.encode("utf-8"), True),
        (os.path.join(hell.ENTITY_DIR, "skeleton_king.png"), hell.skin(KING), False),
    ]


if __name__ == "__main__":
    hell.main(outputs)
