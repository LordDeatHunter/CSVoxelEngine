"""Deterministic generator of the cave mobs' models and skins (placeholder art).

Writes, under OpenGLTest/assets:
  models/entity/shardling.bbmodel + shardling.png    the crystal caves' shard caster (plans/CAVE_BIOMES.md CB-4)
  models/entity/sporeling.bbmodel + sporeling.png    the fungal caves' spore walker
  models/entity/magmaborn.bbmodel + magmaborn.png    the magma-touched deep's burning brute

The rig, UV, PNG, JSON and clip machinery is the hell mobs' generator's (tools/generate-hell-mob-models.py),
loaded from that file like the sky mobs' generator loads it, so every script writes its projects the same
way: no randomness and no image library, every shade picked from a small hand-picked palette by an integer
hash of the texel, so re-running reproduces every file byte for byte. Run from the repo root:
    python -I tools/generate-cave-mob-models.py           (writes the six files)
    python -I tools/generate-cave-mob-models.py --check   (exit 1 if any file differs)

Rig contract (EntityContract.RequireHumanoid): all three carry the groups Torso, Head, ArmL, ArmR, LegL and
LegR, with Head and the arms nested under Torso and the legs at the top level; the model faces -Z, units are
pixels (16 px = 1 block) with the feet on y = 0. Group origins are absolute pivots and nothing carries a rest
rotation (BbmodelLoader rejects one).

Clip contract: every model has idle, walk and the shared hurt, death and spawn. The shardling's "throw" (its
cast) carries the "release" event; the sporeling's "melee" (its spore burst) and the magmaborn's "melee" (its
haymaker) carry "impact" (Shardling, Sporeling and Magmaborn ValidateContent). The event times pace the
attacks.

Sign convention (BbmodelLoader.ReadAnimations): a rotation (x, y, z) in degrees maps to engine Euler
(-x, -y, z). For limbs a NEGATIVE x raises a hanging limb forward and a positive x swings it back; for the
torso and head a positive x leans forward. About z a positive angle lifts the right arm (+X) out to the side
and swings the left one in. Position keys are in pixels, y up.

Pixel art: every surface picks among two or three shades of one hand-picked palette per mob (pale violet and
white, teal and brown, black and orange), never a noisy speckle or a gradient.
"""
import importlib.util
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("hell_mobs", os.path.join(HERE, "generate-hell-mob-models.py"))
hell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hell)

Clip, v, cycle = hell.Clip, hell.v, hell.cycle


# ---- painters: (face, x, y, w, h, ax, ay) -> rgb, every shade from a small palette ------------------

def paletted(shades, seed):
    """Picks one of a few hand-picked shades per texel by a hash; list a shade twice to weight it."""
    def paint(face, x, y, w, h, ax, ay):
        return shades[hell.hash2(ax, ay, seed) % len(shades)]
    return paint


def faced(base, eye, eye_row, eye_cols, mouth=None):
    """A head: the base shades with eyes (and an optional mouth row) painted on its front."""
    def paint(face, x, y, w, h, ax, ay):
        if face == "north":
            if y == eye_row and x in eye_cols:
                return eye
            if mouth is not None and y == eye_row + 2 and 1 <= x < w - 1:
                return mouth
        return base(face, x, y, w, h, ax, ay)
    return paint


def faceted(light, mid, dark):
    """Crystal: diagonal bands of three shades, so a spike reads as cut facets."""
    def paint(face, x, y, w, h, ax, ay):
        band = (x + y) % 4
        return light if band == 0 else dark if band == 3 else mid
    return paint


def spotted(base, spot, seed):
    """A mushroom cap: its shades with pale spots, each spot a 2x2 block picked by a hash."""
    def paint(face, x, y, w, h, ax, ay):
        if face != "down" and hell.hash2(ax // 2, ay // 2, seed) % 5 == 0:
            return spot
        return base(face, x, y, w, h, ax, ay)
    return paint


def cracked(rock, crack, glow, seed):
    """Cooled rock split by glowing cracks: dashed diagonal lines, the crack's hot core every few texels."""
    def paint(face, x, y, w, h, ax, ay):
        if (ax + ay) % 6 == 0 and hell.hash2(ax // 3, ay // 3, seed) % 3 == 0:
            return glow if hell.hash2(ax, ay, seed + 1) % 3 == 0 else crack
        return rock(face, x, y, w, h, ax, ay)
    return paint


# ---- the palettes ---------------------------------------------------------------------------------

VIOLET, VIOLET_DARK, VIOLET_DEEP = (176, 144, 214), (132, 100, 176), (88, 62, 128)
CRYSTAL, CRYSTAL_MID, CRYSTAL_DARK = (248, 244, 255), (214, 196, 246), (164, 132, 222)
SHARD_EYE = (255, 255, 255)

TEAL, TEAL_DARK, TEAL_DEEP = (64, 172, 150), (40, 128, 112), (26, 88, 78)
SPOT = (214, 246, 226)
STEM, STEM_DARK, STEM_DEEP = (150, 112, 78), (112, 80, 54), (76, 52, 36)
SPORE_EYE = (120, 255, 200)

ROCK, ROCK_DARK, ROCK_DEEP = (52, 46, 46), (36, 32, 32), (22, 20, 20)
EMBER, EMBER_HOT = (232, 104, 24), (255, 186, 64)


# ---- the three rigs: cubes as (name, bone, from, to, painter) -------------------------------------

SHARDLING = {
    "name": "shardling",
    "uuid": 0xa0,
    "skin": 64,
    "origins": {"Torso": (0, 12, 0), "Head": (0, 22, 0), "ArmR": (5, 21, 0), "ArmL": (-5, 21, 0),
                "LegR": (1.75, 12, 0), "LegL": (-1.75, 12, 0)},
    "cubes": [
        ("torso", "Torso", (-3.5, 12, -2), (3.5, 22, 2), paletted((VIOLET, VIOLET, VIOLET_DARK), 1)),
        ("spike_left", "Torso", (-2.5, 19, 2), (-0.5, 26, 4), faceted(CRYSTAL, CRYSTAL_MID, CRYSTAL_DARK)),
        ("spike_right", "Torso", (0.5, 17, 2), (2.5, 24, 4), faceted(CRYSTAL, CRYSTAL_MID, CRYSTAL_DARK)),
        ("head", "Head", (-3, 22, -3), (3, 28, 3),
         faced(paletted((VIOLET, VIOLET_DARK), 2), SHARD_EYE, 2, (1, 4))),
        ("head_crystal", "Head", (-1, 28, -1), (1, 32, 1), faceted(CRYSTAL, CRYSTAL_MID, CRYSTAL_DARK)),
        ("arm_right", "ArmR", (3.5, 13, -1.5), (6.5, 22, 1.5), paletted((VIOLET, VIOLET_DARK, VIOLET_DEEP), 3)),
        ("shard_right", "ArmR", (4, 9, -1.5), (6, 13, 1.5), faceted(CRYSTAL, CRYSTAL_MID, CRYSTAL_DARK)),
        ("arm_left", "ArmL", (-6.5, 13, -1.5), (-3.5, 22, 1.5), paletted((VIOLET, VIOLET_DARK, VIOLET_DEEP), 3)),
        ("shard_left", "ArmL", (-6, 9, -1.5), (-4, 13, 1.5), faceted(CRYSTAL, CRYSTAL_MID, CRYSTAL_DARK)),
        ("leg_right", "LegR", (0.5, 0, -1.5), (3, 12, 1.5), paletted((VIOLET_DARK, VIOLET_DEEP), 4)),
        ("leg_left", "LegL", (-3, 0, -1.5), (-0.5, 12, 1.5), paletted((VIOLET_DARK, VIOLET_DEEP), 4)),
    ],
}

SPORELING = {
    "name": "sporeling",
    "uuid": 0xa4,
    "skin": 64,
    "origins": {"Torso": (0, 7, 0), "Head": (0, 15, 0), "ArmR": (5, 14, 0), "ArmL": (-5, 14, 0),
                "LegR": (2, 7, 0), "LegL": (-2, 7, 0)},
    "cubes": [
        ("torso", "Torso", (-4, 7, -3), (4, 15, 3), paletted((STEM, STEM, STEM_DARK), 11)),
        ("head", "Head", (-3.5, 15, -3.5), (3.5, 20, 3.5),
         faced(paletted((STEM, STEM_DARK), 12), SPORE_EYE, 1, (1, 5), mouth=STEM_DEEP)),
        ("cap", "Head", (-6, 20, -6), (6, 23, 6), spotted(paletted((TEAL, TEAL, TEAL_DARK), 13), SPOT, 14)),
        ("cap_top", "Head", (-4, 23, -4), (4, 25, 4), spotted(paletted((TEAL, TEAL_DARK), 15), SPOT, 16)),
        ("gills", "Head", (-5.5, 19.5, -5.5), (5.5, 20, 5.5), paletted((TEAL_DEEP, TEAL_DARK), 17)),
        ("arm_right", "ArmR", (4, 7, -1.5), (6.5, 14, 1.5), paletted((STEM, STEM_DARK, STEM_DEEP), 18)),
        ("arm_left", "ArmL", (-6.5, 7, -1.5), (-4, 14, 1.5), paletted((STEM, STEM_DARK, STEM_DEEP), 18)),
        ("leg_right", "LegR", (0.5, 0, -2), (3.5, 7, 2), paletted((STEM_DARK, STEM_DEEP), 19)),
        ("leg_left", "LegL", (-3.5, 0, -2), (-0.5, 7, 2), paletted((STEM_DARK, STEM_DEEP), 19)),
    ],
}

MAGMABORN = {
    "name": "magmaborn",
    "uuid": 0xa8,
    "skin": 128,
    "origins": {"Torso": (0, 12, 0), "Head": (0, 26, -1), "ArmR": (10.5, 25, 0), "ArmL": (-10.5, 25, 0),
                "LegR": (4, 12, 0), "LegL": (-4, 12, 0)},
    "cubes": [
        ("torso", "Torso", (-8, 12, -4), (8, 27, 4),
         cracked(paletted((ROCK, ROCK, ROCK_DARK), 21), EMBER, EMBER_HOT, 22)),
        ("shoulders", "Torso", (-9, 23, -5), (9, 28, 5),
         cracked(paletted((ROCK_DARK, ROCK, ROCK_DEEP), 23), EMBER, EMBER_HOT, 24)),
        ("head", "Head", (-3.5, 25, -7), (3.5, 31, 0),
         faced(paletted((ROCK, ROCK_DARK), 25), EMBER_HOT, 2, (1, 2, 4, 5), mouth=EMBER)),
        ("arm_right", "ArmR", (8, 11, -3), (13, 25, 3),
         cracked(paletted((ROCK, ROCK_DARK), 26), EMBER, EMBER_HOT, 27)),
        ("fist_right", "ArmR", (7.5, 5, -3.5), (13.5, 11, 3.5),
         cracked(paletted((ROCK_DARK, ROCK_DEEP), 28), EMBER, EMBER_HOT, 29)),
        ("arm_left", "ArmL", (-13, 11, -3), (-8, 25, 3),
         cracked(paletted((ROCK, ROCK_DARK), 26), EMBER, EMBER_HOT, 27)),
        ("fist_left", "ArmL", (-13.5, 5, -3.5), (-7.5, 11, 3.5),
         cracked(paletted((ROCK_DARK, ROCK_DEEP), 28), EMBER, EMBER_HOT, 29)),
        ("leg_right", "LegR", (1, 0, -3), (7, 12, 3), cracked(paletted((ROCK_DARK, ROCK_DEEP), 30), EMBER, EMBER_HOT, 31)),
        ("leg_left", "LegL", (-7, 0, -3), (-1, 12, 3), cracked(paletted((ROCK_DARK, ROCK_DEEP), 30), EMBER, EMBER_HOT, 31)),
    ],
}


# ---- clips ---------------------------------------------------------------------------------------

def shardling_clips():
    idle = Clip("idle", 2.2, "loop", 1)
    # A brittle hover of a stance: the body sways, the shard hands drift, the head tilts to listen.
    idle.pos("Torso", [(0, v()), (1.1, v(0, 0.4)), (2.2, v())])
    idle.rot("Torso", [(0, v()), (0.55, v(0, 5, 2)), (1.1, v()), (1.65, v(0, -5, -2)), (2.2, v())])
    idle.rot("Head", [(0, v()), (0.7, v(4, 10, 6)), (1.1, v()), (1.6, v(2, -10, -6)), (2.2, v())])
    idle.rot("ArmR", [(0, v(-20, 0, 8)), (1.1, v(-28, 0, 12)), (2.2, v(-20, 0, 8))])
    idle.rot("ArmL", [(0, v(-20, 0, -8)), (1.1, v(-28, 0, -12)), (2.2, v(-20, 0, -8))])
    t = [0, 0.25, 0.5, 0.75, 1.0]
    walk = Clip("walk", 1.0, "loop", 2)
    # Quick, stiff strides: the arms swing short and the spikes on the back rock with the shoulders.
    walk.pos("Torso", [(0, v(0, 0.2)), (0.25, v(0, 0.7)), (0.5, v(0, 0.2)), (0.75, v(0, 0.7)), (1.0, v(0, 0.2))])
    walk.rot("Torso", [(0, v(4, 6, 0)), (0.25, v(3, 0, 2)), (0.5, v(4, -6, 0)), (0.75, v(3, 0, -2)), (1.0, v(4, 6, 0))])
    walk.rot("ArmR", cycle(t, [-26, -8, 14, -8, -26], lambda x: v(x, 0, 8)))
    walk.rot("ArmL", cycle(t, [14, -8, -26, -8, 14], lambda x: v(x, 0, -8)))
    walk.rot("LegR", cycle(t, [24, 3, -24, -3, 24], lambda x: v(x)))
    walk.rot("LegL", cycle(t, [-24, -3, 24, 3, -24], lambda x: v(x)))
    cast = Clip("throw", 0.7, "once", 3)
    # Raises the right shard hand high behind the head, the body arching back, then flings it forward at
    # the release (0.4 s) with the body snapping into the throw, and settles.
    cast.rot("Torso", [(0, v()), (0.22, v(-12, 18, 0)), (0.34, v(-14, 16, 0)), (0.4, v(14, -12, 0)), (0.55, v(8, -6, 0)), (0.7, v())])
    cast.rot("Head", [(0, v()), (0.22, v(-6, -14, 0)), (0.4, v(6, 10, 0)), (0.7, v())])
    cast.rot("ArmR", [(0, v(-20, 0, 8)), (0.22, v(-160, 0, 20)), (0.34, v(-172, 0, 24)), (0.4, v(-80, 0, 2)), (0.55, v(-60, 0, 4)), (0.7, v(-20, 0, 8))])
    cast.rot("ArmL", [(0, v(-20, 0, -8)), (0.22, v(-70, 0, -14)), (0.4, v(-40, 0, -18)), (0.7, v(-20, 0, -8))])
    cast.rot("LegR", [(0, v()), (0.22, v(8)), (0.4, v(-10)), (0.7, v())])
    cast.rot("LegL", [(0, v()), (0.22, v(-8)), (0.4, v(12)), (0.7, v())])
    cast.event(0.4, "release")
    return [idle, walk, cast] + hell.common_clips(heavy=False)


def sporeling_clips():
    idle = Clip("idle", 2.0, "loop", 1)
    # A sleepy bob: the whole body sinks and swells like breathing, the cap rocking a little behind it.
    idle.pos("Torso", [(0, v()), (1.0, v(0, -0.6)), (2.0, v())])
    idle.rot("Head", [(0, v()), (0.5, v(4, 0, 4)), (1.0, v()), (1.5, v(4, 0, -4)), (2.0, v())])
    idle.rot("ArmR", [(0, v(0, 0, 6)), (1.0, v(-6, 0, 10)), (2.0, v(0, 0, 6))])
    idle.rot("ArmL", [(0, v(0, 0, -6)), (1.0, v(-6, 0, -10)), (2.0, v(0, 0, -6))])
    t = [0, 0.3, 0.6, 0.9, 1.2]
    walk = Clip("walk", 1.2, "loop", 2)
    # A waddle: short legs, the body rolling side to side and the cap rocking against it.
    walk.pos("Torso", [(0, v(0, -0.3)), (0.3, v(0, 0.4)), (0.6, v(0, -0.3)), (0.9, v(0, 0.4)), (1.2, v(0, -0.3))])
    walk.rot("Torso", [(0, v(0, 0, 6)), (0.3, v(2)), (0.6, v(0, 0, -6)), (0.9, v(2)), (1.2, v(0, 0, 6))])
    walk.rot("Head", [(0, v(0, 0, -5)), (0.3, v()), (0.6, v(0, 0, 5)), (0.9, v()), (1.2, v(0, 0, -5))])
    walk.rot("ArmR", cycle(t, [-18, -4, 18, 4, -18], lambda x: v(x, 0, 10)))
    walk.rot("ArmL", cycle(t, [18, 4, -18, -4, 18], lambda x: v(x, 0, -10)))
    walk.rot("LegR", cycle(t, [26, 3, -26, -3, 26], lambda x: v(x)))
    walk.rot("LegL", cycle(t, [-26, -3, 26, 3, -26], lambda x: v(x)))
    burst = Clip("melee", 0.8, "once", 4)
    # Squats down and hugs itself, the cap pulled low, then springs up and throws the arms wide as the cap
    # puffs its spores at the impact (0.42 s), and settles.
    burst.pos("Torso", [(0, v()), (0.28, v(0, -2.2)), (0.38, v(0, -2.4)), (0.42, v(0, 1.6)), (0.55, v(0, 0.8)), (0.8, v())])
    burst.rot("Torso", [(0, v()), (0.28, v(14, 0, 0)), (0.38, v(16, 0, 0)), (0.42, v(-10, 0, 0)), (0.55, v(-6, 0, 0)), (0.8, v())])
    burst.rot("Head", [(0, v()), (0.28, v(16)), (0.42, v(-18)), (0.55, v(-10)), (0.8, v())])
    burst.rot("ArmR", [(0, v()), (0.28, v(-60, -30, -10)), (0.38, v(-64, -34, -12)), (0.42, v(-20, 0, 80)), (0.55, v(-14, 0, 60)), (0.8, v())])
    burst.rot("ArmL", [(0, v()), (0.28, v(-60, 30, 10)), (0.38, v(-64, 34, 12)), (0.42, v(-20, 0, -80)), (0.55, v(-14, 0, -60)), (0.8, v())])
    burst.rot("LegR", [(0, v()), (0.28, v(-20)), (0.42, v(6)), (0.8, v())])
    burst.rot("LegL", [(0, v()), (0.28, v(-20)), (0.42, v(6)), (0.8, v())])
    burst.event(0.42, "impact")
    return [idle, walk, burst] + hell.common_clips(heavy=False)


def magmaborn_clips():
    idle = Clip("idle", 3.0, "loop", 1)
    # A smoldering hulk: slow heaving breaths, the heavy fists swaying, the head swinging low.
    idle.pos("Torso", [(0, v()), (1.5, v(0, 0.8)), (3.0, v())])
    idle.rot("Torso", [(0, v(10, 0, 0)), (0.75, v(12, 0, 1.5)), (1.5, v(10, 0, 0)), (2.25, v(12, 0, -1.5)), (3.0, v(10, 0, 0))])
    idle.rot("Head", [(0, v(-10)), (1.0, v(-8, 14, 0)), (1.5, v(-10)), (2.2, v(-8, -12, 0)), (3.0, v(-10))])
    idle.rot("ArmR", [(0, v(-6, 0, 5)), (1.5, v(2, 0, 8)), (3.0, v(-6, 0, 5))])
    idle.rot("ArmL", [(0, v(-6, 0, -5)), (1.5, v(2, 0, -8)), (3.0, v(-6, 0, -5))])
    t = [0, 0.35, 0.7, 1.05, 1.4]
    walk = Clip("walk", 1.4, "loop", 2)
    # A grinding stomp: a deep drop on each footfall, the shoulders rolling and the fists dragging.
    walk.pos("Torso", [(0, v(0, -1.0)), (0.35, v(0, 0.6)), (0.7, v(0, -1.0)), (1.05, v(0, 0.6)), (1.4, v(0, -1.0))])
    walk.rot("Torso", [(0, v(12, 9, 0)), (0.35, v(10, 0, 3)), (0.7, v(12, -9, 0)), (1.05, v(10, 0, -3)), (1.4, v(12, 9, 0))])
    walk.rot("Head", [(0, v(-10, -7, 0)), (0.35, v(-8)), (0.7, v(-10, 7, 0)), (1.05, v(-8)), (1.4, v(-10, -7, 0))])
    walk.rot("ArmR", cycle(t, [-26, -3, 26, 3, -26], lambda x: v(x, 0, 7)))
    walk.rot("ArmL", cycle(t, [26, 3, -26, -3, 26], lambda x: v(x, 0, -7)))
    walk.rot("LegR", cycle(t, [24, 3, -24, -3, 24], lambda x: v(x)))
    walk.rot("LegL", cycle(t, [-24, -3, 24, 3, -24], lambda x: v(x)))
    walk.pos("LegR", [(0, v()), (0.7, v()), (1.05, v(0, 1.6)), (1.4, v())])
    walk.pos("LegL", [(0, v()), (0.35, v(0, 1.6)), (0.7, v()), (1.4, v())])
    punch = Clip("melee", 0.9, "once", 4)
    # A haymaker: the right fist wound far back with the shoulders twisted away, a held beat, then the fist
    # hurled across the front at the impact (0.48 s), the body turning through after it, then a slow recoil.
    punch.rot("Torso", [(0, v(10, 0, 0)), (0.3, v(4, 34, 0)), (0.42, v(4, 38, 0)), (0.48, v(18, -30, 0)), (0.62, v(16, -24, 0)), (0.9, v(10, 0, 0))])
    punch.rot("Head", [(0, v(-10)), (0.3, v(-8, -24, 0)), (0.48, v(-12, 20, 0)), (0.9, v(-10))])
    punch.rot("ArmR", [(0, v()), (0.3, v(-40, 0, 70)), (0.42, v(-44, 0, 76)), (0.48, v(-96, 30, 6)), (0.62, v(-84, 24, 4)), (0.9, v())])
    punch.rot("ArmL", [(0, v()), (0.3, v(-30, 0, -20)), (0.48, v(10, 0, -12)), (0.9, v())])
    punch.rot("LegR", [(0, v()), (0.3, v(10)), (0.48, v(-12)), (0.9, v())])
    punch.rot("LegL", [(0, v()), (0.3, v(-8)), (0.48, v(14)), (0.9, v())])
    punch.event(0.48, "impact")
    return [idle, walk, punch] + hell.common_clips(heavy=True)


# ---- outputs -------------------------------------------------------------------------------------

def outputs():
    files = []
    for rig, clips in ((SHARDLING, shardling_clips()), (SPORELING, sporeling_clips()), (MAGMABORN, magmaborn_clips())):
        text = hell.inline_number_arrays(json.dumps(hell.project(rig, clips), indent=4)) + "\n"
        files.append((os.path.join(hell.ENTITY_DIR, rig["name"] + ".bbmodel"), text.encode("utf-8"), True))
        files.append((os.path.join(hell.ENTITY_DIR, rig["name"] + ".png"), hell.skin(rig), False))
    return files


if __name__ == "__main__":
    hell.main(outputs)
