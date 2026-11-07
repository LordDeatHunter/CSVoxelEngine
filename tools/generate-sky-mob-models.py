"""Deterministic generator of the sky mobs' models, skins and the cloud shard sprite (placeholder art).

Writes, under OpenGLTest/assets:
  models/entity/galeraptor.bbmodel + galeraptor.png    the diving flyer (plans/LAYERS.md L-15)
  models/entity/cloudgolem.bbmodel + cloudgolem.png    the slamming ground bruiser
  textures/items/cloud_shard.png                       the shard both drop

The rig, UV, PNG, JSON and clip machinery is the hell mobs' generator's (tools/generate-hell-mob-models.py),
loaded from that file, so both scripts write their projects the same way: no randomness and no image
library, every shade from a small integer hash of the texel, so re-running reproduces every file byte for
byte. Run from the repo root:
    python -I tools/generate-sky-mob-models.py           (writes the five files)
    python -I tools/generate-sky-mob-models.py --check   (exit 1 if any file differs)

Rig contract (EntityContract): both models carry the humanoid groups Torso, Head, ArmL, ArmR, LegL and LegR
(Head and the arms nested under Torso). The galeraptor's arms are its wings and its legs its talons; it is a
flyer, so its moving clip is "fly" (EntityAnimator.FlyClip) and its attack "dive" carries the "impact" event
(Galeraptor.ValidateContent). The golem walks ("walk") and its "slam" carries "impact"
(Cloudgolem.ValidateContent). Both have idle, hurt, death and spawn.

Sign convention (BbmodelLoader.ReadAnimations): a rotation (x, y, z) in degrees maps to engine Euler
(-x, -y, z). For limbs a NEGATIVE x raises a hanging limb forward; for the torso and head a positive x leans
forward. About z a positive angle lifts the right wing (+X) and lowers the left one.
"""
import importlib.util
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("hell_mobs", os.path.join(HERE, "generate-hell-mob-models.py"))
hell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hell)

Clip, v, cycle, material, face_with_eyes = hell.Clip, hell.v, hell.cycle, hell.material, hell.face_with_eyes


def puffy(base, seed):
    """A cloud: a soft, pale material whose speckle is gentle, so it reads as vapor over the stone."""
    return material(base, seed, spread=8)


# ---- the two rigs: cubes as (name, bone, from, to, material) --------------------------------------

RAPTOR_FEATHER = (92, 132, 176)
RAPTOR_BELLY = (208, 222, 236)
RAPTOR = {
    "name": "galeraptor",
    "uuid": 0xd0,
    "skin": 128,
    "origins": {"Torso": (0, 6, 0), "Head": (0, 9, -4), "ArmR": (3, 8, -1), "ArmL": (-3, 8, -1),
                "LegR": (1.5, 4, 0), "LegL": (-1.5, 4, 0)},
    "cubes": [
        ("body", "Torso", (-3, 4, -5), (3, 9, 4), material(RAPTOR_FEATHER, 31)),
        ("belly", "Torso", (-2.5, 3.5, -4), (2.5, 5, 3), material(RAPTOR_BELLY, 32, 10)),
        ("tail", "Torso", (-2, 6, 4), (2, 8, 10), material((70, 104, 148), 33)),
        ("head", "Head", (-2.5, 8, -9), (2.5, 12, -4),
         face_with_eyes(RAPTOR_BELLY, 34, (255, 210, 64), 1, (0, 4))),
        ("beak", "Head", (-1, 8.5, -12), (1, 10.5, -9), material((236, 184, 72), 35, 12)),
        ("crest", "Head", (-0.5, 12, -8), (0.5, 15, -5), material((176, 216, 248), 36, 10)),
        ("wing_right", "ArmR", (3, 7.5, -4), (15, 8.5, 2), material(RAPTOR_FEATHER, 37)),
        ("tip_right", "ArmR", (15, 7.5, -3), (21, 8.5, 1), material((176, 216, 248), 38, 12)),
        ("wing_left", "ArmL", (-15, 7.5, -4), (-3, 8.5, 2), material(RAPTOR_FEATHER, 37)),
        ("tip_left", "ArmL", (-21, 7.5, -3), (-15, 8.5, 1), material((176, 216, 248), 38, 12)),
        ("leg_right", "LegR", (1, 1, -1), (2, 4, 1), material((236, 184, 72), 39, 10)),
        ("talon_right", "LegR", (0.5, 0, -2), (2.5, 1, 1), material((60, 52, 46), 40, 8)),
        ("leg_left", "LegL", (-2, 1, -1), (-1, 4, 1), material((236, 184, 72), 39, 10)),
        ("talon_left", "LegL", (-2.5, 0, -2), (-0.5, 1, 1), material((60, 52, 46), 40, 8)),
    ],
}

GOLEM_STONE = (148, 156, 168)
GOLEM_CLOUD = (238, 242, 248)
GOLEM_GLOW = (150, 228, 255)
GOLEM = {
    "name": "cloudgolem",
    "uuid": 0xe0,
    "skin": 256,
    "origins": {"Torso": (0, 16, 0), "Head": (0, 33, -1), "ArmR": (13, 31, 0), "ArmL": (-13, 31, 0),
                "LegR": (5, 16, 0), "LegL": (-5, 16, 0)},
    "cubes": [
        ("torso", "Torso", (-10, 15, -5), (10, 33, 5), material(GOLEM_STONE, 51, glow=GOLEM_GLOW, glow_every=41)),
        ("core", "Torso", (-2, 23, -6), (2, 28, -5), material(GOLEM_GLOW, 52, 14)),
        ("mantle", "Torso", (-9, 30, -6), (9, 35, 6), puffy(GOLEM_CLOUD, 53)),
        ("head", "Head", (-4, 32, -7), (4, 40, 1),
         face_with_eyes((128, 136, 150), 54, GOLEM_GLOW, 3, (1, 2, 5, 6))),
        ("arm_right", "ArmR", (10, 13, -4), (16, 31, 4), material(GOLEM_STONE, 55)),
        ("cloud_right", "ArmR", (9, 28, -5), (17, 35, 5), puffy(GOLEM_CLOUD, 56)),
        ("fist_right", "ArmR", (9, 5, -5), (17, 13, 5), material((112, 120, 134), 57, glow=GOLEM_GLOW, glow_every=17)),
        ("arm_left", "ArmL", (-16, 13, -4), (-10, 31, 4), material(GOLEM_STONE, 55)),
        ("cloud_left", "ArmL", (-17, 28, -5), (-9, 35, 5), puffy(GOLEM_CLOUD, 56)),
        ("fist_left", "ArmL", (-17, 5, -5), (-9, 13, 5), material((112, 120, 134), 57, glow=GOLEM_GLOW, glow_every=17)),
        ("leg_right", "LegR", (1, 0, -4), (9, 16, 4), material((122, 130, 142), 58)),
        ("leg_left", "LegL", (-9, 0, -4), (-1, 16, 4), material((122, 130, 142), 58)),
    ],
}


# ---- clips ---------------------------------------------------------------------------------------

def flap(clip, times, angles, droop=0):
    """Both wings beat together: the right wing lifts at a positive z, the left mirrors it."""
    clip.rot("ArmR", cycle(times, angles, lambda a: v(0, droop, a)))
    clip.rot("ArmL", cycle(times, angles, lambda a: v(0, -droop, -a)))
    return clip


def raptor_clips():
    idle = Clip("idle", 0.8, "loop", 1)
    # Hovering in place: slow, deep beats, the body rising on each downstroke, the talons hanging.
    flap(idle, [0, 0.4, 0.8], [34, -26, 34])
    idle.pos("Torso", [(0, v(0, -0.4)), (0.4, v(0, 0.6)), (0.8, v(0, -0.4))])
    idle.rot("Head", [(0, v(-4, 0, 0)), (0.4, v(2, 0, 0)), (0.8, v(-4, 0, 0))])
    idle.rot("LegR", [(0, v(12)), (0.4, v(6)), (0.8, v(12))])
    idle.rot("LegL", [(0, v(12)), (0.4, v(6)), (0.8, v(12))])
    fly = Clip("fly", 0.45, "loop", 2)
    # Cruising: quick, shallower beats swept a little back, the talons tucked under the tail.
    flap(fly, [0, 0.225, 0.45], [26, -20, 26], droop=8)
    fly.pos("Torso", [(0, v(0, -0.3)), (0.225, v(0, 0.3)), (0.45, v(0, -0.3))])
    fly.rot("Head", [(0, v(-10)), (0.45, v(-10))])
    fly.rot("LegR", [(0, v(70)), (0.45, v(70))])
    fly.rot("LegL", [(0, v(70)), (0.45, v(70))])
    dive = Clip("dive", 1.0, "once", 3)
    # A stoop: the wings fold back along the body as it tips nose down, then flare wide while the talons
    # swing forward to rake at the impact (0.55 s), then a hard beat back up.
    dive.rot("Torso", [(0, v()), (0.3, v(28, 0, 0)), (0.5, v(18, 0, 0)), (0.55, v(-16, 0, 0)), (0.75, v(-10, 0, 0)), (1.0, v())])
    dive.rot("Head", [(0, v()), (0.3, v(-18)), (0.55, v(10)), (1.0, v())])
    dive.rot("ArmR", [(0, v(0, 0, 20)), (0.3, v(0, 55, -10)), (0.5, v(0, 50, -6)), (0.55, v(0, -10, 48)),
                      (0.75, v(0, 0, -30)), (1.0, v(0, 0, 20))])
    dive.rot("ArmL", [(0, v(0, 0, -20)), (0.3, v(0, -55, 10)), (0.5, v(0, -50, 6)), (0.55, v(0, 10, -48)),
                      (0.75, v(0, 0, 30)), (1.0, v(0, 0, -20))])
    dive.rot("LegR", [(0, v(40)), (0.3, v(70)), (0.5, v(20)), (0.55, v(-75)), (0.75, v(-40)), (1.0, v(12))])
    dive.rot("LegL", [(0, v(40)), (0.3, v(70)), (0.5, v(20)), (0.55, v(-75)), (0.75, v(-40)), (1.0, v(12))])
    dive.event(0.55, "impact")
    hurt = Clip("hurt", 0.4, "once", 5)
    hurt.rot("Torso", [(0, v()), (0.06, v(-14, 0, 6)), (0.2, v(-4, 0, 2)), (0.4, v())])
    hurt.rot("Head", [(0, v()), (0.06, v(-20, 10, 0)), (0.2, v(-5, 2, 0)), (0.4, v())])
    flap(hurt, [0, 0.06, 0.2, 0.4], [0, 50, 12, 0])
    death = Clip("death", 0.8, "once", 6)
    # The wings crumple and hang as it drops out of the sky.
    death.rot("Head", [(0, v()), (0.2, v(-30, 14, 0)), (0.8, v(-40, 14, 0))])
    flap(death, [0, 0.2, 0.8], [0, -60, -80], droop=-20)
    death.rot("LegR", [(0, v()), (0.3, v(-40)), (0.8, v(-30))])
    death.rot("LegL", [(0, v()), (0.3, v(-30)), (0.8, v(-20))])
    spawn = Clip("spawn", 0.45, "once", 7)
    # Unfurls from a ball of folded wings with one strong beat.
    spawn.pos("Torso", [(0, v(0, -1.5)), (0.18, v(0, 1.0)), (0.45, v())])
    flap(spawn, [0, 0.18, 0.32, 0.45], [-70, 60, -10, 0])
    spawn.rot("Head", [(0, v(20)), (0.18, v(-10)), (0.45, v())])
    return [idle, fly, dive, hurt, death, spawn]


def golem_clips():
    idle = Clip("idle", 3.2, "loop", 1)
    # Slow, grinding breaths: the shoulders heave, the clouds on them drift with the arms, the head turns.
    idle.pos("Torso", [(0, v()), (1.6, v(0, 0.8)), (3.2, v())])
    idle.rot("Torso", [(0, v(6, 0, 0)), (0.8, v(8, 0, 1)), (1.6, v(6, 0, 0)), (2.4, v(8, 0, -1)), (3.2, v(6, 0, 0))])
    idle.rot("Head", [(0, v(-6)), (1.1, v(-4, 14, 0)), (1.6, v(-6)), (2.4, v(-4, -10, 0)), (3.2, v(-6))])
    idle.rot("ArmR", [(0, v(-4, 0, 4)), (1.6, v(3, 0, 7)), (3.2, v(-4, 0, 4))])
    idle.rot("ArmL", [(0, v(-4, 0, -4)), (1.6, v(3, 0, -7)), (3.2, v(-4, 0, -4))])
    t = [0, 0.4, 0.8, 1.2, 1.6]
    walk = Clip("walk", 1.6, "loop", 2)
    # Earth-shaking strides: a deep drop on each footfall, the fists dragging low and swinging slowly.
    walk.pos("Torso", [(0, v(0, -1.2)), (0.4, v(0, 0.6)), (0.8, v(0, -1.2)), (1.2, v(0, 0.6)), (1.6, v(0, -1.2))])
    walk.rot("Torso", [(0, v(10, 10, 0)), (0.4, v(8, 0, 3)), (0.8, v(10, -10, 0)), (1.2, v(8, 0, -3)), (1.6, v(10, 10, 0))])
    walk.rot("Head", [(0, v(-8, -8, 0)), (0.4, v(-6)), (0.8, v(-8, 8, 0)), (1.2, v(-6)), (1.6, v(-8, -8, 0))])
    walk.rot("ArmR", cycle(t, [-22, -2, 22, 2, -22], lambda x: v(x, 0, 6)))
    walk.rot("ArmL", cycle(t, [22, 2, -22, -2, 22], lambda x: v(x, 0, -6)))
    walk.rot("LegR", cycle(t, [22, 2, -22, -2, 22], lambda x: v(x)))
    walk.rot("LegL", cycle(t, [-22, -2, 22, 2, -22], lambda x: v(x)))
    walk.pos("LegR", [(0, v()), (0.8, v()), (1.2, v(0, 1.8)), (1.6, v())])
    walk.pos("LegL", [(0, v()), (0.4, v(0, 1.8)), (0.8, v()), (1.6, v())])
    slam = Clip("slam", 1.2, "once", 4)
    # Both fists heaved high overhead, a long held beat, then driven into the ground at the impact
    # (0.65 s) with the whole body crashing after them, the shockwave's moment, then a slow rise.
    slam.pos("Torso", [(0, v()), (0.35, v(0, 1.6)), (0.55, v(0, 2.0)), (0.65, v(0, -3.4)), (0.85, v(0, -2.6)), (1.2, v())])
    slam.rot("Torso", [(0, v(6, 0, 0)), (0.35, v(-18, 0, 0)), (0.55, v(-22, 0, 0)), (0.65, v(34, 0, 0)), (0.85, v(30, 0, 0)), (1.2, v(6, 0, 0))])
    slam.rot("Head", [(0, v(-6)), (0.35, v(-2)), (0.65, v(-24)), (0.85, v(-20)), (1.2, v(-6))])
    slam.rot("ArmR", [(0, v()), (0.35, v(-170, 0, -12)), (0.55, v(-176, 0, -14)), (0.65, v(-38, 0, -4)), (0.85, v(-30, 0, -4)), (1.2, v())])
    slam.rot("ArmL", [(0, v()), (0.35, v(-170, 0, 12)), (0.55, v(-176, 0, 14)), (0.65, v(-38, 0, 4)), (0.85, v(-30, 0, 4)), (1.2, v())])
    slam.rot("LegR", [(0, v()), (0.35, v(-8)), (0.65, v(18)), (0.85, v(14)), (1.2, v())])
    slam.rot("LegL", [(0, v()), (0.35, v(-8)), (0.65, v(18)), (0.85, v(14)), (1.2, v())])
    slam.event(0.65, "impact")
    return [idle, walk, slam] + hell.common_clips(heavy=True)


# ---- the cloud shard sprite ----------------------------------------------------------------------

SHARD_SIZE = 32
SHARD_OUTLINE = (52, 78, 112)
SHARD_DARK, SHARD_MID, SHARD_LIGHT, SHARD_CORE = (128, 170, 214), (182, 214, 240), (226, 240, 252), (255, 255, 255)
# A wisp of hardened cloud at half resolution ('#' outline, '.' body, '+' bright facet, '*' core), like the
# ember shard: each character covers 2x2 texels of the 32x32 sprite.
SHARD = [
    "                ",
    "     ###        ",
    "    #++.#  ##   ",
    "   #+**+.##+.#  ",
    "   #+*++....+#  ",
    "  #.++.......#  ",
    "  #+*+..+*+..#  ",
    " #.++..+**+...# ",
    " #.....++....#  ",
    "  #..........#  ",
    "   #...##...#   ",
    "    ###  ###    ",
    "                ",
    "                ",
    "                ",
    "                ",
]


def shard_texel(x, y):
    c = SHARD[y // 2][x // 2]
    if c == " ":
        return (0, 0, 0, 0)
    if c == "#":
        return SHARD_OUTLINE + (255,)
    if c == "*":
        return SHARD_CORE + (255,)
    if c == "+":
        return SHARD_LIGHT + (255,)
    return (SHARD_MID if hell.hash2(x, y, 61) % 3 else SHARD_DARK) + (255,)


# ---- outputs -------------------------------------------------------------------------------------

def outputs():
    files = []
    for rig, clips in ((RAPTOR, raptor_clips()), (GOLEM, golem_clips())):
        text = hell.inline_number_arrays(json.dumps(hell.project(rig, clips), indent=4)) + "\n"
        files.append((os.path.join(hell.ENTITY_DIR, rig["name"] + ".bbmodel"), text.encode("utf-8"), True))
        files.append((os.path.join(hell.ENTITY_DIR, rig["name"] + ".png"), hell.skin(rig), False))
    files.append((os.path.join(hell.ITEM_DIR, "cloud_shard.png"), hell.png_bytes(SHARD_SIZE, shard_texel), False))
    return files


if __name__ == "__main__":
    hell.main(outputs)
