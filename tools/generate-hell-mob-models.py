"""Deterministic generator of the hell mobs' models, skins and the ember shard sprite (placeholder art).

Writes, under OpenGLTest/assets:
  models/entity/hellbrute.bbmodel + hellbrute.png      the melee brute (plans/LAYERS.md L-10)
  models/entity/cinderwitch.bbmodel + cinderwitch.png  the fire caster
  textures/items/ember_shard.png                       the shard both drop, and the caster's bolt sprite

No randomness and no image library: every shade comes from a small integer hash of the texel, the PNGs
are encoded here with zlib at a fixed level and filter 0, and the JSON is dumped in a fixed key order, so
re-running reproduces every file byte for byte. Run from the repo root:
    python -I tools/generate-hell-mob-models.py           (writes the five files)
    python -I tools/generate-hell-mob-models.py --check   (exit 1 if any file differs)

Rig contract (EntityContract.RequireHumanoid, the goblin's): the groups Torso, Head, ArmL, ArmR, LegL and
LegR, with Head, ArmL and ArmR nested under Torso and the legs at the top level; the model faces -Z (the
"north" face is the front); units are pixels (16 px = 1 block) with the feet on y = 0. Group origins are
absolute pivots and nothing carries a rest rotation (BbmodelLoader rejects one).

Clip contract: both models have idle, walk, hurt, death and spawn; the brute's "melee" carries the
"impact" event and the caster's "throw" (its cast) carries "release" (Hellbrute.ValidateContent,
Cinderwitch.ValidateContent). The event times pace the attacks.

Sign convention (BbmodelLoader.ReadAnimations): a rotation (x, y, z) in degrees maps to engine Euler
(-x, -y, z). For limbs a NEGATIVE x raises a hanging limb forward and a positive x swings it back; for
the torso and head a positive x leans forward. Position keys are in pixels, y up.

UV layout: every face of every cube gets its own rectangle, packed in rows across the skin in cube
order (north, south, west, east, up, down, each its true size), and is painted by the cube's material.
"""
import json
import os
import re
import struct
import sys
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ENTITY_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "models", "entity")
ITEM_DIR = os.path.join(ROOT, "OpenGLTest", "assets", "textures", "items")
FACES = ("north", "south", "west", "east", "up", "down")
BONES = ("Torso", "Head", "ArmR", "ArmL", "LegR", "LegL")
NESTED = {"Head": "Torso", "ArmR": "Torso", "ArmL": "Torso"}


def hash2(a, b, seed=0):
    """A small integer hash of two ints, stable across Python versions."""
    v = (a * 374761393 + b * 668265263 + seed * 2246822519) & 0xFFFFFFFF
    v = ((v ^ (v >> 13)) * 1274126177) & 0xFFFFFFFF
    return v ^ (v >> 16)


def clamp(c):
    return tuple(max(0, min(255, int(v))) for v in c)


def shade(base, x, y, seed, spread=18):
    """The base color with a fine speckle and a coarser blotch, so no surface reads flat."""
    fine = hash2(x, y, seed) % (2 * spread + 1) - spread
    blotch = -14 if hash2(x // 2, y // 2, seed + 7) % 10 < 3 else 0
    return clamp(c + fine + blotch for c in base)


# ---- materials: (u, v, w, h, face, x, y) -> rgb, x and y the texel inside the face ------------------

def material(base, seed, spread=18, glow=None, glow_every=0):
    def paint(face, x, y, w, h, ax, ay):
        if glow is not None and glow_every and hash2(ax, ay, seed + 3) % glow_every == 0:
            return glow
        return shade(base, ax, ay, seed, spread)
    return paint


def face_with_eyes(base, seed, eye, eye_row, eye_cols, mouth=None):
    skin = material(base, seed)

    def paint(face, x, y, w, h, ax, ay):
        if face == "north":
            if y == eye_row and x in eye_cols:
                return eye
            if mouth is not None and y == eye_row + 3 and 2 <= x < w - 2:
                return mouth
        return skin(face, x, y, w, h, ax, ay)
    return paint


def banded(base, band, seed, band_rows):
    """A robe: the base cloth with a glowing trim along its hem (the bottom rows of every side)."""
    cloth = material(base, seed)

    def paint(face, x, y, w, h, ax, ay):
        if face not in ("up", "down") and y >= h - band_rows:
            return shade(band, ax, ay, seed + 11, 22)
        return cloth(face, x, y, w, h, ax, ay)
    return paint


# ---- the two rigs: cubes as (name, bone, from, to, material) --------------------------------------

BRUTE_HIDE = (112, 42, 32)
BRUTE_EMBER = (255, 132, 44)
BRUTE = {
    "name": "hellbrute",
    "uuid": 0xb0,
    "skin": 128,
    "origins": {"Torso": (0, 11, 0), "Head": (0, 23, -1), "ArmR": (9.5, 22, 0), "ArmL": (-9.5, 22, 0),
                "LegR": (3.5, 11, 0), "LegL": (-3.5, 11, 0)},
    "cubes": [
        ("torso", "Torso", (-7, 11, -3.5), (7, 24, 3.5), material(BRUTE_HIDE, 1, glow=BRUTE_EMBER, glow_every=29)),
        ("belt", "Torso", (-7.5, 10, -4), (7.5, 13, 4), material((58, 40, 34), 2)),
        ("hump", "Torso", (-5, 20, 2), (5, 25, 5), material((84, 34, 28), 3)),
        ("head", "Head", (-3.5, 23, -6), (3.5, 30, 1),
         face_with_eyes((128, 52, 40), 4, (255, 214, 72), 3, (1, 2, 4, 5), mouth=(40, 18, 14))),
        ("horn_right", "Head", (2.5, 28, -3), (4.5, 33, -1), material((214, 202, 172), 5, 10)),
        ("horn_left", "Head", (-4.5, 28, -3), (-2.5, 33, -1), material((214, 202, 172), 5, 10)),
        ("arm_right", "ArmR", (7, 10, -2.5), (12, 23, 2.5), material(BRUTE_HIDE, 6, glow=BRUTE_EMBER, glow_every=31)),
        ("fist_right", "ArmR", (6.5, 5, -3), (12.5, 10, 3), material((72, 30, 26), 7, glow=BRUTE_EMBER, glow_every=9)),
        ("arm_left", "ArmL", (-12, 10, -2.5), (-7, 23, 2.5), material(BRUTE_HIDE, 6, glow=BRUTE_EMBER, glow_every=31)),
        ("fist_left", "ArmL", (-12.5, 5, -3), (-6.5, 10, 3), material((72, 30, 26), 7, glow=BRUTE_EMBER, glow_every=9)),
        ("leg_right", "LegR", (1, 0, -2.5), (6, 11, 2.5), material((66, 46, 40), 8)),
        ("leg_left", "LegL", (-6, 0, -2.5), (-1, 11, 2.5), material((66, 46, 40), 8)),
    ],
}

WITCH_ROBE = (64, 30, 52)
WITCH_EMBER = (255, 148, 52)
WITCH = {
    "name": "cinderwitch",
    "uuid": 0xc0,
    "skin": 64,
    "origins": {"Torso": (0, 9, 0), "Head": (0, 18, 0), "ArmR": (5.5, 17, 0), "ArmL": (-5.5, 17, 0),
                "LegR": (1.75, 9, 0), "LegL": (-1.75, 9, 0)},
    "cubes": [
        ("torso", "Torso", (-4, 9, -2.5), (4, 18, 2.5), material(WITCH_ROBE, 21)),
        ("skirt", "Torso", (-4.5, 3, -3), (4.5, 10, 3), banded(WITCH_ROBE, WITCH_EMBER, 22, 2)),
        ("head", "Head", (-3, 18, -3), (3, 24, 3),
         face_with_eyes((164, 108, 98), 23, (255, 176, 60), 2, (1, 4))),
        ("hat_brim", "Head", (-4.5, 24, -4.5), (4.5, 25, 4.5), material((44, 20, 28), 24, 10)),
        ("hat", "Head", (-2.5, 25, -2.5), (2.5, 28, 2.5), banded((44, 20, 28), WITCH_EMBER, 25, 1)),
        ("hat_tip", "Head", (-1.5, 28, -0.5), (1.5, 30, 2.5), material((44, 20, 28), 24, 10)),
        ("arm_right", "ArmR", (4, 10, -1.5), (7, 18, 1.5), material(WITCH_ROBE, 26)),
        ("hand_right", "ArmR", (4.25, 8, -1.75), (6.75, 10, 1.75), material(WITCH_EMBER, 27, 26)),
        ("arm_left", "ArmL", (-7, 10, -1.5), (-4, 18, 1.5), material(WITCH_ROBE, 26)),
        ("hand_left", "ArmL", (-6.75, 8, -1.75), (-4.25, 10, 1.75), material(WITCH_EMBER, 27, 26)),
        ("leg_right", "LegR", (0.5, 0, -1.5), (3, 9, 1.5), material((38, 24, 30), 28)),
        ("leg_left", "LegL", (-3, 0, -1.5), (-0.5, 9, 1.5), material((38, 24, 30), 28)),
    ],
}


def face_size(frm, to, face):
    dx, dy, dz = (int(round(to[i] - frm[i])) for i in range(3))
    return {"north": (dx, dy), "south": (dx, dy), "west": (dz, dy), "east": (dz, dy),
            "up": (dx, dz), "down": (dx, dz)}[face]


def pack(rig):
    """Lays every cube face out in rows across the skin: {(cube index, face): (u, v, w, h)}."""
    places = {}
    u = v = row = 0
    for i, (_, _, frm, to, _) in enumerate(rig["cubes"]):
        for face in FACES:
            w, h = (max(1, s) for s in face_size(frm, to, face))
            if u + w > rig["skin"]:
                u, v, row = 0, v + row, 0
            places[(i, face)] = (u, v, w, h)
            u += w
            row = max(row, h)
    assert v + row <= rig["skin"], rig["name"] + " does not fit its skin"
    return places


def uuid(rig, n):
    return "4e110000-0000-4000-8000-%04x%08x" % (rig["uuid"], n)


def bone_uuid(rig, bone):
    return uuid(rig, 0x100 + BONES.index(bone))


def element(rig, i, cube, places):
    name, bone, frm, to, _ = cube
    faces = {}
    for face in FACES:
        u, v, w, h = places[(i, face)]
        faces[face] = {"uv": [u, v, u + w, v + h], "texture": 0}
    return {
        "name": name,
        "box_uv": False,
        "rescale": False,
        "locked": False,
        "from": list(frm),
        "to": list(to),
        "autouv": 0,
        "color": 0,
        "origin": list(rig["origins"][bone]),
        "rotation": [0, 0, 0],
        "type": "cube",
        "uuid": uuid(rig, i + 1),
        "faces": faces,
    }


def group(rig, bone, children):
    return {
        "name": bone,
        "origin": list(rig["origins"][bone]),
        "color": 0,
        "uuid": bone_uuid(rig, bone),
        "export": True,
        "mirror_uv": False,
        "isOpen": True,
        "locked": False,
        "visibility": True,
        "autouv": 0,
        "rotation": [0, 0, 0],
        "children": children,
    }


def outliner(rig):
    cubes_of = {bone: [uuid(rig, i + 1) for i, c in enumerate(rig["cubes"]) if c[1] == bone] for bone in BONES}
    torso = group(rig, "Torso", cubes_of["Torso"] + [group(rig, b, cubes_of[b]) for b in ("Head", "ArmL", "ArmR")])
    return [torso, group(rig, "LegL", cubes_of["LegL"]), group(rig, "LegR", cubes_of["LegR"])]


# ---- clips ---------------------------------------------------------------------------------------

def num(v):
    s = ("%.3f" % v).rstrip("0").rstrip(".")
    return "0" if s in ("", "-0") else s


class Clip:
    def __init__(self, name, length, loop, n):
        self.name, self.length, self.loop, self.n = name, length, loop, n
        self.tracks = {}
        self.events = []

    def rot(self, bone, keys):
        self.tracks.setdefault(bone, {})["rotation"] = keys
        return self

    def pos(self, bone, keys):
        self.tracks.setdefault(bone, {})["position"] = keys
        return self

    def event(self, time, script):
        self.events.append((time, script))
        return self

    def build(self, rig):
        counter = [0]

        def key_uuid():
            counter[0] += 1
            return uuid(rig, 0x10000 * self.n + counter[0])

        animators = {}
        for bone in BONES:
            if bone not in self.tracks:
                continue
            kfs = []
            for channel in ("rotation", "position"):
                for t, val in self.tracks[bone].get(channel, []):
                    kfs.append({
                        "channel": channel,
                        "data_points": [{"x": num(val[0]), "y": num(val[1]), "z": num(val[2])}],
                        "uuid": key_uuid(),
                        "time": t,
                        "color": -1,
                        "interpolation": "catmullrom",
                    })
            animators[bone_uuid(rig, bone)] = {"name": bone, "type": "bone", "keyframes": kfs}
        if self.events:
            animators["effects"] = {"name": "Effects", "type": "effect", "keyframes": [{
                "channel": "timeline",
                "data_points": [{"script": s}],
                "uuid": key_uuid(),
                "time": t,
                "color": -1,
                "interpolation": "linear",
            } for t, s in self.events]}
        return {
            "uuid": uuid(rig, 0x300 + self.n),
            "name": self.name,
            "loop": self.loop,
            "override": False,
            "length": self.length,
            "snapping": 24,
            "selected": False,
            "anim_time_update": "",
            "blend_weight": "",
            "start_delay": "",
            "loop_delay": "",
            "animators": animators,
        }


def v(x=0.0, y=0.0, z=0.0):
    return (x, y, z)


def cycle(times, xs, make):
    return [(t, make(x)) for t, x in zip(times, xs)]


def common_clips(heavy):
    """hurt, death and spawn: the same beats for both, the brute's slower and deeper."""
    s = 1.25 if heavy else 1.0
    hurt = Clip("hurt", 0.4, "once", 5)
    hurt.rot("Torso", [(0, v()), (0.06, v(-12 / s, 0, 4)), (0.18, v(-4, 0, 1)), (0.4, v())])
    hurt.rot("Head", [(0, v()), (0.06, v(-18, 8, 0)), (0.2, v(-5, 2, 0)), (0.4, v())])
    hurt.rot("ArmR", [(0, v()), (0.06, v(-18, 0, 24)), (0.2, v(-5, 0, 6)), (0.4, v())])
    hurt.rot("ArmL", [(0, v()), (0.06, v(-18, 0, -24)), (0.2, v(-5, 0, -6)), (0.4, v())])
    death = Clip("death", 0.8, "once", 6)
    death.rot("Head", [(0, v()), (0.2, v(-26, 12, 0)), (0.8, v(-32, 12, 0))])
    death.rot("ArmR", [(0, v()), (0.18, v(-90, 0, 22)), (0.5, v(-125, 0, 36)), (0.8, v(-115, 0, 32))])
    death.rot("ArmL", [(0, v()), (0.18, v(-78, 0, -22)), (0.5, v(-118, 0, -36)), (0.8, v(-108, 0, -32))])
    death.rot("LegR", [(0, v()), (0.25, v(22)), (0.8, v(28))])
    death.rot("LegL", [(0, v()), (0.25, v(-18)), (0.8, v(-24))])
    spawn = Clip("spawn", 0.45, "once", 7)
    spawn.pos("Torso", [(0, v(0, -2.5 * s)), (0.18, v(0, 1.0)), (0.32, v(0, -0.4)), (0.45, v())])
    spawn.rot("ArmR", [(0, v(-90, 0, 24)), (0.18, v(-105, 0, 30)), (0.45, v())])
    spawn.rot("ArmL", [(0, v(-90, 0, -24)), (0.18, v(-105, 0, -30)), (0.45, v())])
    spawn.rot("Head", [(0, v(-12)), (0.18, v(-8)), (0.45, v())])
    return [hurt, death, spawn]


def brute_clips():
    idle = Clip("idle", 2.8, "loop", 1)
    # Heavy breathing: the hunched chest heaves, the fists hang and sway, the head swings slowly.
    idle.pos("Torso", [(0, v()), (1.4, v(0, 0.7)), (2.8, v())])
    idle.rot("Torso", [(0, v(8, 0, 0)), (0.7, v(10, 0, 1.5)), (1.4, v(8, 0, 0)), (2.1, v(10, 0, -1.5)), (2.8, v(8, 0, 0))])
    idle.rot("Head", [(0, v(-8, 0, 0)), (0.9, v(-6, 16, 0)), (1.4, v(-8, 0, 0)), (2.0, v(-6, -12, 0)), (2.8, v(-8, 0, 0))])
    idle.rot("ArmR", [(0, v(-6, 0, 6)), (1.4, v(2, 0, 9)), (2.8, v(-6, 0, 6))])
    idle.rot("ArmL", [(0, v(-6, 0, -6)), (1.4, v(2, 0, -9)), (2.8, v(-6, 0, -6))])
    t = [0, 0.3, 0.6, 0.9, 1.2]
    walk = Clip("walk", 1.2, "loop", 2)
    # A lumbering stomp: short heavy strides, a deep bob at each footfall, the shoulders rolling.
    walk.pos("Torso", [(0, v(0, -0.8)), (0.3, v(0, 0.6)), (0.6, v(0, -0.8)), (0.9, v(0, 0.6)), (1.2, v(0, -0.8))])
    walk.rot("Torso", [(0, v(12, 8, 0)), (0.3, v(10, 0, 3)), (0.6, v(12, -8, 0)), (0.9, v(10, 0, -3)), (1.2, v(12, 8, 0))])
    walk.rot("Head", [(0, v(-10, -6, 0)), (0.3, v(-8, 0, -2)), (0.6, v(-10, 6, 0)), (0.9, v(-8, 0, 2)), (1.2, v(-10, -6, 0))])
    walk.rot("ArmR", cycle(t, [-30, -4, 30, 4, -30], lambda x: v(x, 0, 8)))
    walk.rot("ArmL", cycle(t, [30, 4, -30, -4, 30], lambda x: v(x, 0, -8)))
    walk.rot("LegR", cycle(t, [26, 3, -26, -3, 26], lambda x: v(x)))
    walk.rot("LegL", cycle(t, [-26, -3, 26, 3, -26], lambda x: v(x)))
    walk.pos("LegR", [(0, v()), (0.6, v()), (0.9, v(0, 1.4)), (1.2, v())])
    walk.pos("LegL", [(0, v()), (0.3, v(0, 1.4)), (0.6, v()), (1.2, v())])
    melee = Clip("melee", 0.85, "once", 4)
    # Both fists raised overhead, a held beat, then hammered down into the ground at the impact
    # (0.45 s) with the whole body following through, then a slow heave back up.
    melee.pos("Torso", [(0, v()), (0.22, v(0, 1.0)), (0.36, v(0, 1.2)), (0.45, v(0, -2.2)), (0.6, v(0, -1.6)), (0.85, v())])
    melee.rot("Torso", [(0, v(8, 0, 0)), (0.22, v(-14, 0, 0)), (0.36, v(-18, 0, 0)), (0.45, v(30, 0, 0)), (0.6, v(26, 0, 0)), (0.85, v(8, 0, 0))])
    melee.rot("Head", [(0, v(-8)), (0.22, v(-4)), (0.45, v(-20)), (0.6, v(-18)), (0.85, v(-8))])
    melee.rot("ArmR", [(0, v()), (0.22, v(-165, 0, -14)), (0.36, v(-172, 0, -16)), (0.45, v(-40, 0, -6)), (0.6, v(-30, 0, -4)), (0.85, v())])
    melee.rot("ArmL", [(0, v()), (0.22, v(-165, 0, 14)), (0.36, v(-172, 0, 16)), (0.45, v(-40, 0, 6)), (0.6, v(-30, 0, 4)), (0.85, v())])
    melee.rot("LegR", [(0, v()), (0.22, v(-6)), (0.45, v(16)), (0.6, v(12)), (0.85, v())])
    melee.rot("LegL", [(0, v()), (0.22, v(-6)), (0.45, v(16)), (0.6, v(12)), (0.85, v())])
    melee.event(0.45, "impact")
    return [idle, walk, melee] + common_clips(heavy=True)


def witch_clips():
    idle = Clip("idle", 2.4, "loop", 1)
    # A restless caster: the robe sways, the hands circle a little as if keeping a flame alive.
    idle.pos("Torso", [(0, v()), (1.2, v(0, 0.4)), (2.4, v())])
    idle.rot("Torso", [(0, v(0, 0, 0)), (0.6, v(0, 4, 2)), (1.2, v(0, 0, 0)), (1.8, v(0, -4, -2)), (2.4, v(0, 0, 0))])
    idle.rot("Head", [(0, v()), (0.8, v(4, 12, 0)), (1.2, v()), (1.7, v(2, -10, 0)), (2.4, v())])
    idle.rot("ArmR", [(0, v(-30, 0, 6)), (0.6, v(-38, 8, 10)), (1.2, v(-30, 0, 6)), (1.8, v(-24, -8, 4)), (2.4, v(-30, 0, 6))])
    idle.rot("ArmL", [(0, v(-24, 0, -6)), (0.6, v(-18, -8, -4)), (1.2, v(-24, 0, -6)), (1.8, v(-32, 8, -10)), (2.4, v(-24, 0, -6))])
    t = [0, 0.25, 0.5, 0.75, 1.0]
    walk = Clip("walk", 1.0, "loop", 2)
    # A gliding shuffle: short steps under the robe, the arms half raised and barely swinging.
    walk.pos("Torso", [(0, v(0, 0.2)), (0.25, v(0, 0.6)), (0.5, v(0, 0.2)), (0.75, v(0, 0.6)), (1.0, v(0, 0.2))])
    walk.rot("Torso", [(0, v(4, 5, 0)), (0.25, v(3, 0, 1.5)), (0.5, v(4, -5, 0)), (0.75, v(3, 0, -1.5)), (1.0, v(4, 5, 0))])
    walk.rot("ArmR", cycle(t, [-34, -24, -14, -24, -34], lambda x: v(x, 0, 8)))
    walk.rot("ArmL", cycle(t, [-14, -24, -34, -24, -14], lambda x: v(x, 0, -8)))
    walk.rot("LegR", cycle(t, [22, 3, -22, -3, 22], lambda x: v(x)))
    walk.rot("LegL", cycle(t, [-22, -3, 22, 3, -22], lambda x: v(x)))
    cast = Clip("throw", 0.65, "once", 3)
    # Draws the right hand back past the shoulder as the left reaches out to aim, then thrusts the right
    # palm forward at the release (0.38 s), the body leaning into it, and settles.
    cast.rot("Torso", [(0, v()), (0.2, v(-8, 22, 0)), (0.32, v(-6, 20, 0)), (0.38, v(12, -14, 0)), (0.5, v(8, -8, 0)), (0.65, v())])
    cast.rot("Head", [(0, v()), (0.2, v(-2, -18, 0)), (0.38, v(4, 12, 0)), (0.65, v())])
    cast.rot("ArmR", [(0, v(-30, 0, 6)), (0.2, v(-60, 0, 40)), (0.32, v(-70, 0, 44)), (0.38, v(-95, 0, 0)), (0.5, v(-80, 0, 0)), (0.65, v(-30, 0, 6))])
    cast.rot("ArmL", [(0, v(-24, 0, -6)), (0.2, v(-85, 0, -10)), (0.38, v(-60, 0, -14)), (0.65, v(-24, 0, -6))])
    cast.rot("LegR", [(0, v()), (0.2, v(8)), (0.38, v(-10)), (0.65, v())])
    cast.rot("LegL", [(0, v()), (0.2, v(-8)), (0.38, v(12)), (0.65, v())])
    cast.event(0.38, "release")
    return [idle, walk, cast] + common_clips(heavy=False)


# ---- outputs -------------------------------------------------------------------------------------

def project(rig, clips):
    places = pack(rig)
    return {
        "meta": {"format_version": "4.0", "model_format": "free", "box_uv": False},
        "name": rig["name"],
        "resolution": {"width": rig["skin"], "height": rig["skin"]},
        "textures": [{
            "id": "0",
            "name": rig["name"] + ".png",
            "source": rig["name"] + ".png",
            "width": rig["skin"],
            "height": rig["skin"],
            "uv_width": rig["skin"],
            "uv_height": rig["skin"],
            "particle": False,
            "use_as_default": False,
            "render_mode": "default",
        }],
        "elements": [element(rig, i, c, places) for i, c in enumerate(rig["cubes"])],
        "outliner": outliner(rig),
        "animations": [c.build(rig) for c in clips],
    }


def png_bytes(size, texel):
    """Minimal RGBA8 PNG: IHDR, one IDAT (filter 0 on every row, zlib level 9), IEND."""

    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    rows = []
    for y in range(size):
        row = bytearray()
        for x in range(size):
            row += bytes(texel(x, y))
        rows.append(b"\x00" + bytes(row))
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(b"".join(rows), 9))
    png += chunk(b"IEND", b"")
    return png


def skin(rig):
    places = pack(rig)
    owner = {}
    for (i, face), (u, v, w, h) in places.items():
        for y in range(v, v + h):
            for x in range(u, u + w):
                owner[(x, y)] = (i, face, x - u, y - v, w, h)

    def texel(x, y):
        if (x, y) not in owner:
            return (0, 0, 0, 0)
        i, face, fx, fy, w, h = owner[(x, y)]
        return clamp(rig["cubes"][i][4](face, fx, fy, w, h, x, y)) + (255,)
    return png_bytes(rig["skin"], texel)


SHARD_SIZE = 32
SHARD_OUTLINE = (58, 18, 10)
SHARD_DARK, SHARD_MID, SHARD_LIGHT, SHARD_CORE = (176, 52, 18), (232, 108, 30), (255, 172, 64), (255, 236, 160)
# The shard's silhouette at half resolution, one string per row ('#' outline, '.' body, '+' bright facet,
# '*' core); each character covers 2x2 texels of the 32x32 sprite, like the hand-made item sprites.
SHARD = [
    "                ",
    "          ##    ",
    "         #+.#   ",
    "        #++..#  ",
    "       #+*+..#  ",
    "      #+**+..#  ",
    "     #++*+...#  ",
    "    #++*+...#   ",
    "   #.+*+...#    ",
    "   #.++...#     ",
    "  #..+...#      ",
    "  #.....#       ",
    "  #....#        ",
    "   #..#         ",
    "    ##          ",
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
    return (SHARD_MID if hash2(x, y, 41) % 3 else SHARD_DARK) + (255,)


def inline_number_arrays(text):
    """Folds every array of plain numbers onto one line, the way Blockbench writes them."""
    return re.sub(r"\[\s*(-?[\d.]+(?:,\s*-?[\d.]+)*)\s*\]",
                  lambda m: "[" + ", ".join(p.strip() for p in m.group(1).split(",")) + "]", text)


def outputs():
    files = []
    for rig, clips in ((BRUTE, brute_clips()), (WITCH, witch_clips())):
        text = inline_number_arrays(json.dumps(project(rig, clips), indent=4)) + "\n"
        files.append((os.path.join(ENTITY_DIR, rig["name"] + ".bbmodel"), text.encode("utf-8"), True))
        files.append((os.path.join(ENTITY_DIR, rig["name"] + ".png"), skin(rig), False))
    files.append((os.path.join(ITEM_DIR, "ember_shard.png"), png_bytes(SHARD_SIZE, shard_texel), False))
    return files


def main(make=outputs):
    """Writes (or with --check compares) every file make() returns; the sky mobs' generator hands its
    own outputs to this same routine."""
    check = "--check" in sys.argv
    stale = []
    for path, data, is_text in make():
        old = None
        if os.path.exists(path):
            with open(path, "rb") as f:
                old = f.read()
        # A text file may come back from git with CRLF line endings; compare it by content.
        same = old is not None and (old.replace(b"\r\n", b"\n") == data if is_text else old == data)
        if check:
            if not same:
                stale.append(os.path.relpath(path, ROOT))
            continue
        if not same:
            with open(path, "wb") as f:
                f.write(data)
        print("wrote" if not same else "unchanged", os.path.relpath(path, ROOT))
    if check:
        for path in stale:
            print("differs:", path)
        sys.exit(1 if stale else 0)


if __name__ == "__main__":
    main()
