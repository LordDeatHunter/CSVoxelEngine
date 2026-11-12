"""Deterministic generator of three creature models: rig, clips and skin (placeholder art, no gameplay yet).

Writes, under OpenGLTest/assets/models/entity:
  dragonfly.bbmodel + dragonfly.png    a man-sized dragonfly: a flying hunter whose flat thorax can seat a rider
  crow.bbmodel + crow.png              a crow: a pest that struts, flies, pecks and swoops
  deer.bbmodel + deer.png              a spotted stag: the common grazer

The UV, PNG, JSON and clip machinery is the hell mobs' generator's (tools/generate-hell-mob-models.py),
loaded from that file: no randomness and no image library, so re-running reproduces every file byte for
byte. Run from the repo root:
    python -I tools/generate-critter-models.py           (writes the six files)
    python -I tools/generate-critter-models.py --check   (exit 1 if any file differs)

Rigs. Every model faces -Z, stands with its feet on y = 0 (16 px = 1 block) and carries no rest rotation
(BbmodelLoader rejects one). Each keeps the six groups EntityContract.HumanoidParts asks for, so a later
mob class can use RequireFlyer or RequireHumanoid as it is, and adds its own bones around them. Everything
nests under Torso, so pitching or lifting the body carries the whole animal:
  dragonfly  ArmR/ArmL are the fore wings and WingBackR/L the hind wings; LegR/LegL are the middle legs,
             with LegFrontR/L and LegBackR/L; Abdomen1 > Abdomen2 > Abdomen3 chain backward; MandibleR/L
             hang under Head. The rest pose is the perched one (legs down, wings spread flat); the top of
             the thorax (y = 13, around z = 0) is flat and clear of the wings, where a rider sits.
  crow       ArmR/ArmL are the inner wings with WingTipR/L nested; LegR/LegL, Tail, and Jaw (the lower
             beak) under Head. The rest pose has the wings spread; idle and walk fold them over the back.
             Torso pivots on the hip line the legs hang from, so the body can tip while the feet stay put,
             and each wing pivots on the root of its leading edge, the edge it folds about.
  deer       ArmR/ArmL are the forelegs and LegR/LegL the hind legs, each with a lower segment
             (ArmLowerR/L, LegLowerR/L); Neck carries Head, which carries EarR/EarL and the antlers; Tail.

Clips. All three have idle, hurt, death and spawn. Attacks carry the "impact" timeline event.
  dragonfly  idle (hover), fly, bite (impact), land, perch, takeoff
  crow       idle, walk, fly, glide, peck (impact), swoop (impact), caw, takeoff, land
  deer       idle, walk, run, graze, alert, butt (impact)
An action clip overrides only the bones it keys (EntityAnimator), so the dragonfly's wings keep beating
through its bite and a crow's hurt leaves its wings wherever the locomotion clip has them.

Sign convention (BbmodelLoader.ReadAnimations): a rotation (x, y, z) in degrees maps to engine Euler
(-x, -y, z). A positive x tips a body, head or neck nose down and so lifts whatever trails behind its
pivot (a tail tip, an abdomen); on a hanging limb a negative x swings it forward. A positive z lifts a
part on the right (+X) side and a positive y sweeps it back; the left side mirrors both (see `both`).

Skins use small hand-picked palettes, one texel per pixel. A material may answer RGBA: alpha 0 cuts the
texel out (the dragonfly's wing cells, the crow's feather tips) and keeps a sensible color underneath.
"""
import importlib.util
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location("hell_mobs", os.path.join(HERE, "generate-hell-mob-models.py"))
hell = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hell)

Clip, v, hash2 = hell.Clip, hell.v, hell.hash2


# ---- painting: (face, x, y, w, h, ax, ay) -> rgb or rgba, x and y the texel inside the face ---------

def front_dist(face, x, y, w, h):
    """Texels from the cube's front (-Z) edge on the four faces that run along Z, else None."""
    return {"east": w - 1 - x, "west": x, "up": y, "down": h - 1 - y}.get(face)


def out_dist(face, x, w, right):
    """Texels from the body-side end of a cube that sticks out along X (a wing), on a face running along X."""
    return x if (face != "north") == right else w - 1 - x


def mottle(colors, seed, weights=(6, 2, 1)):
    """Every texel is one of `colors`, picked by a hash of its place on the skin (the first most often)."""
    total = sum(weights[:len(colors)])

    def paint(face, x, y, w, h, ax, ay):
        r = hash2(ax, ay, seed) % total
        for color, weight in zip(colors, weights):
            if r < weight:
                return color
            r -= weight
        return colors[0]
    return paint


def over(base, rule):
    """`base` with a pattern on top: rule(face, x, y, w, h) answers a color, or None to leave the base."""
    def paint(face, x, y, w, h, ax, ay):
        color = rule(face, x, y, w, h)
        return color if color is not None else base(face, x, y, w, h, ax, ay)
    return paint


def cut(color):
    """A cutout texel that keeps `color` under its zero alpha, for a renderer that ignores the alpha."""
    return color + (0,)


# ---- rig helpers ---------------------------------------------------------------------------------

def pair(name, bone, frm, to, right_material, left_material=None, shared=False):
    """A cube on the right (+X) side and its mirror on the left: `name`_right on `bone`R and `name`_left on
    `bone`L, or both on the one `bone` when it is `shared` (the eyes on a head)."""
    left_from, left_to = (-to[0], frm[1], frm[2]), (-frm[0], to[1], to[2])
    return [(name + "_right", bone if shared else bone + "R", frm, to, right_material),
            (name + "_left", bone if shared else bone + "L", left_from, left_to, left_material or right_material)]


def sides(origins):
    """Pivots for both sides from the right one: {"Arm": p} becomes ArmR at p and ArmL at its mirror."""
    out = {}
    for bone, (x, y, z) in origins.items():
        out[bone + "R"] = (x, y, z)
        out[bone + "L"] = (-x, y, z)
    return out


def both_parents(bone, parent, paired_parent=False):
    """Both sides of `bone` under `parent` (under the parent of the same side when it is paired too)."""
    return {bone + s: parent + (s if paired_parent else "") for s in "RL"}


# ---- the dragonfly -------------------------------------------------------------------------------

FLY_GREEN = [(70, 150, 58), (50, 118, 50), (98, 180, 70)]
FLY_BELLY = (176, 198, 96)
FLY_FACE = [(164, 192, 74), (138, 168, 62), (190, 212, 96)]
FLY_BLUE = [(48, 122, 224), (36, 92, 190), (92, 164, 246)]
FLY_BLACK = (24, 26, 30)
FLY_EYE = [(170, 46, 30), (124, 28, 26), (214, 84, 40)]
FLY_GLINT = (255, 206, 150)
FLY_LEG = [(40, 34, 32), (26, 24, 26), (70, 58, 46)]
FLY_JAW = [(168, 104, 42), (132, 78, 34)]
FLY_VEIN = (44, 58, 66)
FLY_MEMBRANE = (206, 232, 240)
FLY_MEMBRANE_TINT = (170, 214, 232)
FLY_STIGMA = (226, 150, 48)


def thorax_rule(face, x, y, w, h):
    if face == "down":
        return FLY_BELLY
    if face == "up" and x in (w // 2 - 1, w // 2):
        return FLY_BLACK
    if face in ("east", "west") and (front_dist(face, x, y, w, h) + y) % 5 == 0:
        return FLY_BLACK
    return None


def fly_head_rule(face, x, y, w, h):
    return FLY_BLACK if face == "north" and y >= h - 2 else None


def fly_eye_rule(face, x, y, w, h):
    return FLY_GLINT if face in ("north", "up") and (x, y) == (1, 1) else None


def abdomen_rule(face, x, y, w, h):
    along = front_dist(face, x, y, w, h)
    if along is None:
        return None
    if along % 4 == 3:
        return FLY_BLACK
    return FLY_BLACK if face == "up" and w % 2 == 1 and x == w // 2 else None


def mandible_rule(face, x, y, w, h):
    return FLY_BLACK if front_dist(face, x, y, w, h) == 0 or face == "north" else None


def fly_wing(right, seed):
    """A glassy wing: a dark leading edge and vein lattice, an amber stigma near the tip, open cells."""
    def paint(face, x, y, w, h, ax, ay):
        if face not in ("up", "down"):
            return FLY_VEIN
        out = out_dist(face, x, w, right)
        chord = front_dist(face, x, y, w, h)
        if out == w - 1 and chord in (0, h - 1):
            return cut(FLY_MEMBRANE)
        if chord <= 1 and w - 6 <= out <= w - 4:
            return FLY_STIGMA
        if chord == 0 or out < 2 or out % 4 == 1 or (chord == h // 2 and out < w - 2):
            return FLY_VEIN
        # The cells: most are open, the rest a pale film, the same on both faces of the wing.
        film = hash2(out, chord, seed) % 5
        if film >= 2:
            return cut(FLY_MEMBRANE)
        return FLY_MEMBRANE if film else FLY_MEMBRANE_TINT
    return paint


def fly_leg(name, bone, z, shin_z, seed):
    leg = mottle(FLY_LEG, seed)
    return (pair(name + "_femur", bone, (3, 5.5, z - 0.5), (8, 6.5, z + 0.5), leg)
            + pair(name + "_tibia", bone, (7, 0, shin_z - 0.5), (8, 5.5, shin_z + 0.5), leg))


DRAGONFLY = {
    "name": "dragonfly",
    "uuid": 0x80,
    "skin": 128,
    "bones": ("Torso", "Head", "MandibleR", "MandibleL", "ArmR", "ArmL", "WingBackR", "WingBackL",
              "LegFrontR", "LegFrontL", "LegR", "LegL", "LegBackR", "LegBackL",
              "Abdomen1", "Abdomen2", "Abdomen3"),
    "parents": {"Head": "Torso", "Abdomen1": "Torso", "Abdomen2": "Abdomen1", "Abdomen3": "Abdomen2",
                **both_parents("Mandible", "Head"), **both_parents("Arm", "Torso"),
                **both_parents("WingBack", "Torso"), **both_parents("LegFront", "Torso"),
                **both_parents("Leg", "Torso"), **both_parents("LegBack", "Torso")},
    "origins": {"Torso": (0, 9, 0), "Head": (0, 10, -6),
                "Abdomen1": (0, 10, 5), "Abdomen2": (0, 10, 13), "Abdomen3": (0, 10, 21),
                **sides({"Mandible": (1.5, 6.5, -10), "Arm": (4, 12.25, -3.5), "WingBack": (4, 12.25, 3),
                         "LegFront": (3, 6, -4), "Leg": (3, 6, -1), "LegBack": (3, 6, 3)})},
    "cubes": [
        ("thorax", "Torso", (-4, 5, -6), (4, 13, 5), over(mottle(FLY_GREEN, 1), thorax_rule)),
        ("neck", "Head", (-1.5, 8, -7), (1.5, 11, -6), mottle([FLY_BLACK, (40, 44, 48)], 2)),
        ("head", "Head", (-3, 6.5, -11), (3, 12.5, -7), over(mottle(FLY_FACE, 3), fly_head_rule)),
        *pair("eye", "Head", (2, 9, -12), (5, 13, -8), over(mottle(FLY_EYE, 4), fly_eye_rule), shared=True),
        *pair("mandible", "Mandible", (0.5, 5, -12), (2.5, 7, -10), over(mottle(FLY_JAW, 5), mandible_rule)),
        *pair("fore_wing", "Arm", (4, 12, -6), (28, 12.5, -1), fly_wing(True, 6), fly_wing(False, 6)),
        *pair("hind_wing", "WingBack", (4, 12, 0), (26, 12.5, 6), fly_wing(True, 7), fly_wing(False, 7)),
        *fly_leg("fore_leg", "LegFront", -4, -4.5, 8),
        *fly_leg("mid_leg", "Leg", -1, -1, 9),
        *fly_leg("hind_leg", "LegBack", 3, 3.5, 10),
        ("abdomen_base", "Abdomen1", (-2.5, 7.5, 5), (2.5, 12.5, 13), over(mottle(FLY_BLUE, 11), abdomen_rule)),
        ("abdomen_middle", "Abdomen2", (-2, 8, 13), (2, 12, 21), over(mottle(FLY_BLUE, 12), abdomen_rule)),
        ("abdomen_tip", "Abdomen3", (-1.5, 8.5, 21), (1.5, 11.5, 29), over(mottle(FLY_BLUE, 13), abdomen_rule)),
        *pair("clasper", "Abdomen3", (0.5, 9.5, 29), (1.5, 10.5, 32), mottle([FLY_BLACK, (40, 44, 48)], 14), shared=True),
    ],
}


# ---- the crow ------------------------------------------------------------------------------------

CROW_FEATHER = [(26, 26, 34), (36, 40, 58), (58, 66, 104)]
CROW_BLACK = (20, 20, 26)
CROW_SHEEN = (44, 50, 78)
CROW_EYE = (160, 170, 192)
CROW_BEAK = [(42, 42, 50), (30, 30, 36)]
CROW_BEAK_RIDGE = (70, 70, 82)
CROW_LEG = [(58, 54, 56), (42, 40, 44)]


def crow_head_rule(face, x, y, w, h):
    return CROW_EYE if face in ("east", "west") and (front_dist(face, x, y, w, h), y) == (1, 1) else None


def crow_beak_rule(face, x, y, w, h):
    return CROW_BEAK_RIDGE if face == "up" else None


def crow_wing(right, fingered):
    """Rows of flight feathers behind a dark leading edge; the outer wing ends in spread feather tips."""
    feathers = mottle(CROW_FEATHER, 21)

    def paint(face, x, y, w, h, ax, ay):
        if face not in ("up", "down"):
            return CROW_BLACK
        out = out_dist(face, x, w, right)
        chord = front_dist(face, x, y, w, h)
        if fingered and ((chord == h - 1 and out % 2 == 1) or (out == w - 1 and chord % 2 == 1)):
            return cut(CROW_BLACK)
        if chord == 0:
            return CROW_BLACK
        if chord >= 2:
            return CROW_SHEEN if out % 2 == 0 else CROW_BLACK
        return feathers(face, x, y, w, h, ax, ay)
    return paint


def crow_tail_rule(face, x, y, w, h):
    if face not in ("up", "down"):
        return None
    along = front_dist(face, x, y, w, h)
    if along == h - 1 and x % 2 == 1:
        return cut(CROW_BLACK)
    return CROW_SHEEN if x % 2 == 0 and along >= 1 else None


CROW = {
    "name": "crow",
    "uuid": 0x84,
    "skin": 64,
    "bones": ("Torso", "Head", "Jaw", "ArmR", "ArmL", "WingTipR", "WingTipL", "LegR", "LegL", "Tail"),
    "parents": {"Head": "Torso", "Jaw": "Head", "Tail": "Torso", **both_parents("Arm", "Torso"),
                **both_parents("WingTip", "Arm", paired_parent=True), **both_parents("Leg", "Torso")},
    "origins": {"Torso": (0, 3, 0), "Head": (0, 8, -3), "Jaw": (0, 8.5, -7), "Tail": (0, 6.5, 3),
                **sides({"Arm": (2.5, 8.25, -3.5), "WingTip": (7.5, 8.25, -3.5), "Leg": (1.25, 3, 0)})},
    "cubes": [
        ("body", "Torso", (-2.5, 3, -4), (2.5, 8, 3), mottle(CROW_FEATHER, 22)),
        ("head", "Head", (-2, 7.5, -7), (2, 11.5, -3), over(mottle(CROW_FEATHER, 23), crow_head_rule)),
        ("beak", "Head", (-1, 8.5, -10), (1, 10, -7), over(mottle(CROW_BEAK, 24), crow_beak_rule)),
        ("beak_tip", "Head", (-0.5, 8.5, -11), (0.5, 9.5, -10), mottle(CROW_BEAK, 25)),
        ("beak_lower", "Jaw", (-1, 8, -9.5), (1, 8.5, -7), mottle(CROW_BEAK, 26)),
        *pair("wing", "Arm", (2.5, 8, -3.5), (7.5, 8.5, 1.5), crow_wing(True, False), crow_wing(False, False)),
        *pair("wing_tip", "WingTip", (7.5, 8, -3), (13.5, 8.5, 1), crow_wing(True, True), crow_wing(False, True)),
        *pair("leg", "Leg", (0.75, 0.5, -0.5), (1.75, 3, 0.5), mottle(CROW_LEG, 27)),
        *pair("foot", "Leg", (0.25, 0, -2), (2.25, 0.5, 1), mottle(CROW_LEG, 28)),
        ("tail", "Tail", (-2, 6, 3), (2, 7, 8), over(mottle(CROW_FEATHER, 29), crow_tail_rule)),
    ],
}


# ---- the deer ------------------------------------------------------------------------------------

DEER_COAT = [(156, 108, 64), (134, 90, 52), (176, 126, 78)]
DEER_DARK = (108, 72, 44)
DEER_CREAM = (232, 214, 182)
DEER_WHITE = (246, 240, 228)
DEER_SPOT = (226, 200, 160)
DEER_EYE = (22, 18, 18)
DEER_NOSE = (44, 34, 34)
DEER_EAR = (214, 160, 150)
DEER_HOOF = (48, 40, 38)
DEER_ANTLER = [(214, 196, 160), (184, 164, 128), (236, 222, 192)]


def deer_body_rule(face, x, y, w, h):
    if face == "down":
        return DEER_CREAM
    if face in ("east", "west"):
        if y >= h - 2:
            return DEER_CREAM
        # Fawn spots along the upper flank, the same on both sides.
        return DEER_SPOT if 1 <= y <= 3 and hash2(front_dist(face, x, y, w, h), y, 31) % 6 == 0 else None
    if face == "up":
        if x == w // 2:
            return DEER_DARK
        return DEER_SPOT if hash2(y, min(x, w - 1 - x), 32) % 6 == 0 else None
    if face == "south":
        return DEER_WHITE if 1 <= x < w - 1 and 1 <= y < h - 2 else None
    return DEER_CREAM if y >= h - 3 else None


def deer_neck_rule(face, x, y, w, h):
    return DEER_CREAM if face == "north" and 1 <= x < w - 1 and y >= 2 else None


def deer_head_rule(face, x, y, w, h):
    if face == "down":
        return DEER_CREAM
    if face == "up" and 1 <= x < w - 1:
        return DEER_DARK
    return DEER_EYE if face in ("east", "west") and (front_dist(face, x, y, w, h), y) == (3, 1) else None


def deer_snout_rule(face, x, y, w, h):
    if face == "down":
        return DEER_CREAM
    if face == "north":
        return DEER_NOSE if y == 0 else DEER_CREAM
    along = front_dist(face, x, y, w, h)
    if along == 0 and (face == "up" or y == 0):
        return DEER_NOSE
    return DEER_CREAM if face in ("east", "west") and y == h - 1 else None


def deer_ear_rule(face, x, y, w, h):
    return DEER_EAR if face == "north" else None


def deer_shin_rule(face, x, y, w, h):
    return DEER_HOOF if face == "down" or (face != "up" and y >= h - 2) else None


def deer_tail_rule(face, x, y, w, h):
    return DEER_WHITE if face in ("south", "down") or (face in ("east", "west") and y == h - 1) else None


def antlers():
    """Both antlers on the Head: a beam, a branch out to the side with its upright tine, and a brow tine."""
    bone = mottle(DEER_ANTLER, 33)
    return (pair("antler_beam", "Head", (1, 26, -10), (2, 31, -9), bone, shared=True)
            + pair("antler_branch", "Head", (2, 28, -10), (5, 29, -9), bone, shared=True)
            + pair("antler_tine", "Head", (4, 29, -10), (5, 32, -9), bone, shared=True)
            + pair("antler_brow", "Head", (1, 27, -12), (2, 28, -10), bone, shared=True))


DEER = {
    "name": "deer",
    "uuid": 0x88,
    "skin": 128,
    "bones": ("Torso", "Neck", "Head", "EarR", "EarL", "Tail", "ArmR", "ArmL", "ArmLowerR", "ArmLowerL",
              "LegR", "LegL", "LegLowerR", "LegLowerL"),
    "parents": {"Neck": "Torso", "Head": "Neck", "Tail": "Torso", **both_parents("Ear", "Head"),
                **both_parents("Arm", "Torso"), **both_parents("ArmLower", "Arm", paired_parent=True),
                **both_parents("Leg", "Torso"), **both_parents("LegLower", "Leg", paired_parent=True)},
    "origins": {"Torso": (0, 15, 0), "Neck": (0, 16, -7), "Head": (0, 23, -8), "Tail": (0, 18, 8),
                **sides({"Ear": (2.5, 25.5, -9.5), "Arm": (2.5, 12, -6), "ArmLower": (2.5, 6, -6),
                         "Leg": (2.5, 12, 6), "LegLower": (2.5, 6, 6.25)})},
    "cubes": [
        ("body", "Torso", (-3.5, 11, -8), (3.5, 18, 8), over(mottle(DEER_COAT, 34), deer_body_rule)),
        ("neck", "Neck", (-2, 15, -10), (2, 24, -6), over(mottle(DEER_COAT, 35), deer_neck_rule)),
        ("head", "Head", (-2.5, 21, -14), (2.5, 26, -8), over(mottle(DEER_COAT, 36), deer_head_rule)),
        ("snout", "Head", (-1.5, 21, -18), (1.5, 24, -14), over(mottle(DEER_COAT, 37), deer_snout_rule)),
        *antlers(),
        *pair("ear", "Ear", (2.5, 24.5, -10), (5.5, 26.5, -9), over(mottle([DEER_DARK, DEER_COAT[1]], 38), deer_ear_rule)),
        ("tail", "Tail", (-1, 14, 8), (1, 18, 9), over(mottle([DEER_DARK, DEER_COAT[1]], 39), deer_tail_rule)),
        *pair("foreleg", "Arm", (1.5, 6, -7), (3.5, 12, -5), mottle(DEER_COAT, 40)),
        *pair("foreleg_lower", "ArmLower", (1.75, 0, -6.75), (3.25, 6, -5.25),
              over(mottle([DEER_COAT[1], DEER_DARK], 41), deer_shin_rule)),
        *pair("hind_leg", "Leg", (1.25, 6, 4.5), (3.75, 13, 7.5), mottle(DEER_COAT, 42)),
        *pair("hind_leg_lower", "LegLower", (1.75, 0, 5.5), (3.25, 6, 7),
              over(mottle([DEER_COAT[1], DEER_DARK], 43), deer_shin_rule)),
    ],
}


# ---- clip helpers --------------------------------------------------------------------------------

def mirrored(value):
    """The left side's rotation for a right side one: the sweep (y) and the lift (z) flip."""
    return (value[0], -value[1], -value[2])


def both(clip, bone, keys):
    """Keys the right bone and mirrors them onto the left one (`bone` without its side letter)."""
    clip.rot(bone + "R", keys)
    clip.rot(bone + "L", [(t, mirrored(value)) for t, value in keys])
    return clip


def hold(clip, value):
    """The same value at both ends of the clip: a pose held for its whole length."""
    return [(0, value), (clip.length, value)]


def added(a, b):
    return tuple(p + q for p, q in zip(a, b))


# ---- dragonfly clips -----------------------------------------------------------------------------

WING_BEAT = 0.1  # seconds per wing beat; every looping clip is a whole number of beats long
FLY_TUCK = {"LegFront": v(0, -25, -50), "Leg": v(0, 0, -55), "LegBack": v(0, 25, -55)}
FLY_LEGS = ("LegFront", "Leg", "LegBack")
FLY_ABDOMEN = ("Abdomen1", "Abdomen2", "Abdomen3")


def buzz(clip, amp, sweep=0.0, twist=10.0):
    """All four wings beating for the whole clip, the hind pair half a beat behind the fore pair."""
    half = WING_BEAT / 2
    beats = int(round(clip.length / half))
    fore, hind = [], []
    for i in range(beats + 1):
        s = 1 if i % 2 == 0 else -1
        fore.append((i * half, v(twist * s, sweep, amp * s)))
        hind.append((i * half, v(-twist * s, sweep * 1.4, -amp * s)))
    both(clip, "Arm", fore)
    both(clip, "WingBack", hind)
    return clip


def tucked(clip, extra=v()):
    """The legs folded into a basket under the thorax for the whole clip."""
    for leg in FLY_LEGS:
        both(clip, leg, hold(clip, added(FLY_TUCK[leg], extra)))
    return clip


def curl(clip, keys, spread=(1.0, 1.4, 1.7)):
    """The abdomen bending as a chain: each (time, x) key is the base segment's, growing toward the tip."""
    for bone, scale in zip(FLY_ABDOMEN, spread):
        clip.rot(bone, [(t, v(x * scale)) for t, x in keys])
    return clip


def dragonfly_clips():
    idle = Clip("idle", 1.6, "loop", 1)
    # Hovering: the wings a blur, the body riding up and down, the abdomen trailing a slow wave, the
    # head scanning from side to side, the legs held in a basket.
    buzz(idle, 26)
    tucked(idle)
    idle.pos("Torso", [(0, v()), (0.8, v(0, 1.0)), (1.6, v())])
    idle.rot("Torso", [(0, v(-4)), (0.8, v(-2)), (1.6, v(-4))])
    idle.rot("Head", [(0, v()), (0.4, v(0, 14, 0)), (0.8, v()), (1.2, v(0, -14, 0)), (1.6, v())])
    curl(idle, [(0, -4), (0.8, -8), (1.6, -4)])
    both(idle, "Mandible", [(0, v()), (0.5, v()), (0.6, v(0, 14, 0)), (0.7, v()), (1.6, v())])
    fly = Clip("fly", 0.4, "loop", 2)
    # Darting forward: nose down, deeper beats swept back, the legs trailing, the abdomen a straight rudder.
    buzz(fly, 34, sweep=12)
    tucked(fly, v(0, 20, -12))
    fly.pos("Torso", [(0, v(0, -0.3)), (0.2, v(0, 0.3)), (0.4, v(0, -0.3))])
    fly.rot("Torso", hold(fly, v(14)))
    fly.rot("Head", hold(fly, v(-12)))
    curl(fly, [(0, -3), (0.2, -5), (0.4, -3)], spread=(1.0, 0.8, 0.6))
    bite = Clip("bite", 0.7, "once", 3)
    # Rears back with the jaws spreading, then lunges: the legs snatch forward and the jaws snap shut at
    # the impact (0.3 s) while the abdomen whips under for balance.
    bite.pos("Torso", [(0, v()), (0.18, v(0, 0.5, 3)), (0.3, v(0, -0.5, -7)), (0.45, v(0, 0, -4)), (0.7, v())])
    bite.rot("Torso", [(0, v(-4)), (0.18, v(-14)), (0.3, v(16)), (0.45, v(8)), (0.7, v(-4))])
    bite.rot("Head", [(0, v()), (0.18, v(-14)), (0.3, v(12)), (0.45, v(4)), (0.7, v())])
    both(bite, "Mandible", [(0, v()), (0.18, v(0, 42, 0)), (0.27, v(0, 44, 0)), (0.3, v(0, -4, 0)), (0.7, v())])
    both(bite, "LegFront", [(0, FLY_TUCK["LegFront"]), (0.18, v(0, -10, -30)), (0.3, v(0, -62, -34)),
                            (0.45, v(0, -50, -40)), (0.7, FLY_TUCK["LegFront"])])
    both(bite, "Leg", [(0, FLY_TUCK["Leg"]), (0.18, v(0, 4, -34)), (0.3, v(0, -44, -38)),
                       (0.45, v(0, -30, -44)), (0.7, FLY_TUCK["Leg"])])
    curl(bite, [(0, -4), (0.18, 8), (0.3, -16), (0.45, -10), (0.7, -4)])
    bite.event(0.3, "impact")
    land = Clip("land", 0.6, "once", 4)
    # Settling: the beats slow and die away as the legs reach down, a squat at the touchdown (0.35 s),
    # then the wings come to rest spread flat.
    beats = [(0, 26), (0.05, -26), (0.1, 24), (0.15, -22), (0.2, 18), (0.28, -14), (0.38, 8), (0.5, -6), (0.6, -4)]
    both(land, "Arm", [(t, v(0, 0, a)) for t, a in beats])
    both(land, "WingBack", [(t, v(0, 0, -a if t < 0.5 else a)) for t, a in beats])
    for leg in FLY_LEGS:
        both(land, leg, [(0, FLY_TUCK[leg]), (0.3, v(0, 0, -8)), (0.38, v()), (0.6, v())])
    land.pos("Torso", [(0, v(0, 2.5)), (0.3, v(0, 0.2)), (0.38, v(0, -1.0)), (0.6, v())])
    land.rot("Torso", [(0, v(-10)), (0.3, v(-6)), (0.6, v())])
    curl(land, [(0, -6), (0.38, -14), (0.6, -3)])
    perch = Clip("perch", 2.4, "loop", 5)
    # At rest on its six legs, where a rider mounts: the wings spread and a little drooped with a quick
    # flick now and then, the abdomen pumping slowly, the head tilting, the jaws working.
    for leg in FLY_LEGS:
        both(perch, leg, hold(perch, v()))
    flick = [(0, -4), (1.1, -4), (1.18, 10), (1.26, -6), (1.36, -4), (2.4, -4)]
    both(perch, "Arm", [(t, v(0, 0, a)) for t, a in flick])
    both(perch, "WingBack", [(t + (0.04 if 1.0 < t < 2.0 else 0), v(0, 0, a)) for t, a in flick])
    perch.pos("Torso", [(0, v()), (1.2, v(0, 0.3)), (2.4, v())])
    perch.rot("Torso", hold(perch, v()))
    perch.rot("Head", [(0, v()), (0.5, v()), (0.6, v(0, 18, 8)), (1.5, v(0, 18, 8)), (1.6, v(0, -10, 0)),
                       (2.2, v(0, -10, 0)), (2.4, v())])
    curl(perch, [(0, -3), (0.6, -6), (1.2, -3), (1.8, -6), (2.4, -3)], spread=(1.0, 1.0, 1.0))
    both(perch, "Mandible", [(0, v()), (0.8, v()), (0.9, v(0, 16, 0)), (1.0, v()), (1.1, v(0, 16, 0)),
                             (1.2, v()), (2.4, v())])
    takeoff = Clip("takeoff", 0.5, "once", 6)
    # A crouch, the wings whirring up to speed, then the legs let go and fold as it lifts.
    climb = [(0, -4), (0.1, 18), (0.17, -18), (0.24, 26), (0.3, -26), (0.35, 30), (0.4, -30), (0.45, 28), (0.5, -26)]
    both(takeoff, "Arm", [(t, v(0, 0, a)) for t, a in climb])
    both(takeoff, "WingBack", [(t, v(0, 0, a if t == 0 else -a)) for t, a in climb])
    for leg in FLY_LEGS:
        both(takeoff, leg, [(0, v()), (0.15, v(0, 0, 6)), (0.24, v(0, 0, -6)), (0.45, FLY_TUCK[leg]), (0.5, FLY_TUCK[leg])])
    takeoff.pos("Torso", [(0, v()), (0.15, v(0, -1.5)), (0.3, v(0, 1.0)), (0.5, v(0, 3.0))])
    takeoff.rot("Torso", [(0, v()), (0.15, v(6)), (0.3, v(-10)), (0.5, v(-4))])
    curl(takeoff, [(0, -3), (0.15, 6), (0.3, -12), (0.5, -4)])
    hurt = Clip("hurt", 0.4, "once", 7)
    # A flinch laid over whatever else is playing (EntityAnimator.PlayOverlay adds it), so it starts and
    # ends at the identity: the body jolts and a whip runs down the abdomen, segment after segment.
    hurt.rot("Torso", [(0, v()), (0.06, v(-10, 0, 8)), (0.2, v(-2, 0, 2)), (0.4, v())])
    hurt.rot("Head", [(0, v()), (0.06, v(-18, 10, 0)), (0.2, v(-5, 2, 0)), (0.4, v())])
    both(hurt, "Mandible", [(0, v()), (0.06, v(0, 36, 0)), (0.2, v(0, 10, 0)), (0.4, v())])
    hurt.rot("Abdomen1", [(0, v()), (0.06, v(14)), (0.2, v(-4)), (0.4, v())])
    hurt.rot("Abdomen2", [(0, v()), (0.1, v(20)), (0.24, v(-4)), (0.4, v())])
    hurt.rot("Abdomen3", [(0, v()), (0.14, v(25)), (0.28, v(-5)), (0.4, v())])
    death = Clip("death", 0.8, "once", 8)
    # The wings stall and sag, the legs clench under it, the abdomen curls and the head lolls.
    both(death, "Arm", [(0, v(0, 0, 20)), (0.2, v(10, 10, -34)), (0.8, v(14, 16, -52))])
    both(death, "WingBack", [(0, v(0, 0, -20)), (0.25, v(-8, 14, -30)), (0.8, v(-12, 24, -46))])
    for leg, sweep in zip(FLY_LEGS, (-30, 0, 30)):
        both(death, leg, [(0, FLY_TUCK[leg]), (0.3, v(0, sweep, -70)), (0.8, v(0, sweep, -84))])
    death.rot("Head", [(0, v()), (0.2, v(-24, 14, 0)), (0.8, v(-32, 16, 6))])
    both(death, "Mandible", [(0, v()), (0.2, v(0, 30, 0)), (0.8, v(0, 22, 0))])
    curl(death, [(0, -4), (0.3, -22), (0.8, -30)], spread=(1.0, 1.2, 1.3))
    spawn = Clip("spawn", 0.45, "once", 9)
    # Uncurls with a hop: the abdomen snaps straight from a tight coil as the head comes up.
    spawn.pos("Torso", [(0, v(0, -2.0)), (0.18, v(0, 1.0)), (0.32, v(0, -0.3)), (0.45, v())])
    spawn.rot("Head", [(0, v(22)), (0.18, v(-8)), (0.45, v())])
    curl(spawn, [(0, -34), (0.18, 6), (0.32, -8), (0.45, -4)])
    return [idle, fly, bite, land, perch, takeoff, hurt, death, spawn]


# ---- crow clips ----------------------------------------------------------------------------------

# A folded wing: turned on edge about its leading edge (x), then swept back along the flank (y), its lower
# edge flared a little off the body (z). The outer wing carries straight on to the tail tip.
CROW_FOLD = v(-84, 88, 6)
CROW_TIP_FOLD = v(0, 4, 0)
CROW_LEGS_TUCKED = v(62)


def folded(clip, shrug=v()):
    """The wings folded over the back for the whole clip."""
    both(clip, "Arm", hold(clip, added(CROW_FOLD, shrug)))
    both(clip, "WingTip", hold(clip, CROW_TIP_FOLD))
    return clip


def planted(clip, pitch_keys):
    """Tips the body about the hips while the legs stay upright: each key is (time, torso pitch)."""
    clip.rot("Torso", [(t, v(x)) for t, x in pitch_keys])
    for leg in ("LegR", "LegL"):
        clip.rot(leg, [(t, v(-x)) for t, x in pitch_keys])
    return clip


def crow_clips():
    idle = Clip("idle", 3.2, "loop", 1)
    # Standing watch: wings folded, the head snapping from one cocked look to the next, a tail flick.
    folded(idle)
    idle.pos("Torso", [(0, v()), (1.6, v(0, 0.25)), (3.2, v())])
    idle.rot("Head", [(0, v()), (0.5, v()), (0.6, v(0, 32, 0)), (1.3, v(0, 32, 0)), (1.4, v(6, 32, 16)),
                      (2.0, v(6, 32, 16)), (2.1, v(0, -24, 0)), (2.8, v(0, -24, 0)), (2.9, v()), (3.2, v())])
    idle.rot("Tail", [(0, v()), (1.5, v()), (1.58, v(-16)), (1.68, v(6)), (1.8, v()), (3.2, v())])
    walk = Clip("walk", 0.6, "loop", 2)
    # A strut: the legs scissor, the body bobs on each step and the head pumps forward and back.
    folded(walk)
    t = [0, 0.15, 0.3, 0.45, 0.6]
    walk.rot("Torso", hold(walk, v(6)))
    walk.pos("Torso", [(0, v()), (0.15, v(0, 0.5)), (0.3, v()), (0.45, v(0, 0.5)), (0.6, v())])
    walk.rot("LegR", [(s, v(x - 6)) for s, x in zip(t, [-28, 0, 28, 0, -28])])
    walk.rot("LegL", [(s, v(x - 6)) for s, x in zip(t, [28, 0, -28, 0, 28])])
    walk.pos("Head", [(0, v(0, 0, 0.5)), (0.15, v(0, 0, -0.9)), (0.3, v(0, 0, 0.5)), (0.45, v(0, 0, -0.9)),
                      (0.6, v(0, 0, 0.5))])
    walk.rot("Head", hold(walk, v(-6)))
    walk.rot("Tail", [(0, v(0, 7, 0)), (0.3, v(0, -7, 0)), (0.6, v(0, 7, 0))])
    fly = Clip("fly", 0.4, "loop", 3)
    # Rowing flight: a full upstroke and downstroke, the outer wing trailing behind the inner one, the
    # body lifting on the downstroke, the legs tucked under the tail.
    both(fly, "Arm", [(0, v(0, 0, 42)), (0.2, v(0, 0, -32)), (0.4, v(0, 0, 42))])
    both(fly, "WingTip", [(0, v()), (0.1, v(0, 0, 22)), (0.2, v()), (0.3, v(0, 0, -26)), (0.4, v())])
    fly.pos("Torso", [(0, v(0, -0.5)), (0.2, v(0, 0.5)), (0.4, v(0, -0.5))])
    fly.rot("Torso", hold(fly, v(5)))
    fly.rot("Head", hold(fly, v(-5)))
    both(fly, "Leg", hold(fly, CROW_LEGS_TUCKED))
    fly.rot("Tail", [(0, v(-5)), (0.2, v(5)), (0.4, v(-5))])
    glide = Clip("glide", 2.0, "loop", 4)
    # Soaring on set wings: a shallow V that rocks from side to side, the tail steering against it.
    both(glide, "Arm", [(0, v(0, 0, 9)), (1.0, v(0, 0, 6)), (2.0, v(0, 0, 9))])
    both(glide, "WingTip", [(0, v(0, 0, 7)), (1.0, v(0, 0, 10)), (2.0, v(0, 0, 7))])
    glide.rot("Torso", [(0, v(2, 0, 5)), (1.0, v(2, 0, -5)), (2.0, v(2, 0, 5))])
    glide.rot("Head", [(0, v(-2, 0, -5)), (1.0, v(-2, 0, 5)), (2.0, v(-2, 0, -5))])
    both(glide, "Leg", hold(glide, CROW_LEGS_TUCKED))
    glide.rot("Tail", [(0, v(0, -9, 0)), (1.0, v(0, 9, 0)), (2.0, v(0, -9, 0))])
    peck = Clip("peck", 0.5, "once", 5)
    # A stab at the ground or an ankle: rears, drives the open beak down, shuts it on the impact (0.2 s).
    planted(peck, [(0, 0), (0.1, -10), (0.2, 42), (0.3, 34), (0.5, 0)])
    peck.rot("Head", [(0, v()), (0.1, v(-22)), (0.2, v(30)), (0.3, v(22)), (0.5, v())])
    peck.pos("Head", [(0, v()), (0.1, v(0, 0, 0.6)), (0.2, v(0, 0, -1.2)), (0.5, v())])
    peck.rot("Jaw", [(0, v()), (0.1, v(26)), (0.17, v(28)), (0.2, v()), (0.5, v())])
    peck.rot("Tail", [(0, v()), (0.1, v(8)), (0.2, v(-14)), (0.5, v())])
    peck.event(0.2, "impact")
    swoop = Clip("swoop", 0.9, "once", 6)
    # A stoop from the air: the wings fold back as it tips over, then flare wide with the feet thrown
    # forward and the beak striking at the impact (0.5 s), then a hard beat away.
    swoop.rot("Torso", [(0, v(5)), (0.2, v(36)), (0.42, v(30)), (0.5, v(-22)), (0.7, v(-8)), (0.9, v(5))])
    swoop.rot("Head", [(0, v(-5)), (0.2, v(-22)), (0.42, v(-16)), (0.5, v(20)), (0.9, v(-5))])
    both(swoop, "Arm", [(0, v(0, 0, 20)), (0.2, v(0, 52, -8)), (0.42, v(0, 48, -6)), (0.5, v(0, -8, 50)),
                        (0.7, v(0, 0, -30)), (0.9, v(0, 0, 20))])
    both(swoop, "WingTip", [(0, v()), (0.2, v(0, 30, 0)), (0.42, v(0, 28, 0)), (0.5, v(0, 0, 14)),
                            (0.7, v(0, 0, 12)), (0.9, v())])
    both(swoop, "Leg", [(0, CROW_LEGS_TUCKED), (0.42, v(40)), (0.5, v(-70)), (0.7, v(-30)), (0.9, CROW_LEGS_TUCKED)])
    swoop.rot("Jaw", [(0, v()), (0.36, v()), (0.44, v(30)), (0.5, v()), (0.9, v())])
    swoop.rot("Tail", [(0, v()), (0.2, v(-6)), (0.5, v(22)), (0.9, v())])
    swoop.event(0.5, "impact")
    caw = Clip("caw", 1.0, "once", 7)
    # Leans out with the neck stretched and calls twice: the beak gapes, the tail dips and the folded
    # wings hitch on each caw.
    planted(caw, [(0, 0), (0.15, 16), (0.75, 16), (1.0, 0)])
    caw.pos("Head", [(0, v()), (0.15, v(0, 0, -0.8)), (0.75, v(0, 0, -0.8)), (1.0, v())])
    caw.rot("Head", [(0, v()), (0.15, v(-8)), (0.3, v(4)), (0.42, v(-6)), (0.55, v(4)), (0.7, v(-6)), (1.0, v())])
    caw.rot("Jaw", [(0, v()), (0.2, v()), (0.3, v(34)), (0.42, v(4)), (0.55, v(34)), (0.7, v()), (1.0, v())])
    caw.rot("Tail", [(0, v()), (0.2, v()), (0.3, v(-14)), (0.42, v(-2)), (0.55, v(-14)), (0.7, v()), (1.0, v())])
    hitch = [(0, 0), (0.2, 0), (0.3, 9), (0.42, 2), (0.55, 9), (0.7, 0), (1.0, 0)]
    both(caw, "Arm", [(s, added(CROW_FOLD, v(0, 0, a))) for s, a in hitch])
    both(caw, "WingTip", hold(caw, CROW_TIP_FOLD))
    takeoff = Clip("takeoff", 0.45, "once", 8)
    # Crouches, throws the wings open overhead and springs off the legs into the first downstroke.
    takeoff.pos("Torso", [(0, v()), (0.12, v(0, -1.0)), (0.3, v(0, 2.0)), (0.45, v(0, 3.5))])
    takeoff.rot("Torso", [(0, v()), (0.12, v(14)), (0.3, v(-16)), (0.45, v(-4))])
    both(takeoff, "Arm", [(0, CROW_FOLD), (0.12, v(0, 34, 40)), (0.22, v(0, 0, 62)), (0.32, v(0, 0, -34)),
                          (0.45, v(0, 0, 30))])
    both(takeoff, "WingTip", [(0, CROW_TIP_FOLD), (0.12, v(0, 20, 0)), (0.22, v(0, 0, 10)), (0.32, v(0, 0, 20)),
                              (0.45, v())])
    both(takeoff, "Leg", [(0, v()), (0.12, v(-24)), (0.3, v(26)), (0.45, CROW_LEGS_TUCKED)])
    takeoff.rot("Tail", [(0, v()), (0.12, v(10)), (0.3, v(-10)), (0.45, v())])
    land = Clip("land", 0.5, "once", 9)
    # Brakes with the wings cupped high and the feet reaching, touches down (0.3 s), then folds up.
    land.pos("Torso", [(0, v(0, 2.5)), (0.3, v(0, -0.8)), (0.5, v())])
    land.rot("Torso", [(0, v(-26)), (0.3, v(-8)), (0.4, v(8)), (0.5, v())])
    both(land, "Arm", [(0, v(0, -6, 46)), (0.15, v(0, 0, 20)), (0.3, v(0, 10, 44)), (0.5, CROW_FOLD)])
    both(land, "WingTip", [(0, v(0, 0, 12)), (0.3, v(0, 0, 8)), (0.5, CROW_TIP_FOLD)])
    both(land, "Leg", [(0, v(-14)), (0.3, v(8)), (0.4, v(-8)), (0.5, v())])
    land.rot("Tail", [(0, v(24)), (0.3, v(10)), (0.5, v())])
    hurt = Clip("hurt", 0.35, "once", 10)
    hurt.rot("Torso", [(0, v()), (0.05, v(-10, 0, 7)), (0.18, v(-3, 0, 2)), (0.35, v())])
    hurt.rot("Head", [(0, v()), (0.05, v(-20, 12, 0)), (0.18, v(-5, 3, 0)), (0.35, v())])
    hurt.rot("Jaw", [(0, v()), (0.05, v(28)), (0.2, v(8)), (0.35, v())])
    hurt.rot("Tail", [(0, v()), (0.05, v(16)), (0.2, v(4)), (0.35, v())])
    death = Clip("death", 0.8, "once", 11)
    # The wings fall half open and limp, the head drops back, the feet curl.
    both(death, "Arm", [(0, v(0, 30, -30)), (0.25, v(0, 22, -40)), (0.8, v(0, 30, -58))])
    both(death, "WingTip", [(0, v(0, 6, 0)), (0.25, v(0, 8, -12)), (0.8, v(0, 10, -22))])
    death.rot("Head", [(0, v()), (0.2, v(-28, 16, 0)), (0.8, v(-36, 18, 8))])
    death.rot("Jaw", [(0, v()), (0.2, v(26)), (0.8, v(18))])
    both(death, "Leg", [(0, v()), (0.3, v(-34)), (0.8, v(-50))])
    death.rot("Tail", [(0, v()), (0.3, v(-18)), (0.8, v(-24))])
    spawn = Clip("spawn", 0.45, "once", 12)
    spawn.pos("Torso", [(0, v(0, -1.2)), (0.18, v(0, 0.8)), (0.32, v(0, -0.2)), (0.45, v())])
    spawn.rot("Head", [(0, v(18)), (0.18, v(-8)), (0.45, v())])
    spawn.rot("Tail", [(0, v(-20)), (0.18, v(10)), (0.45, v())])
    return [idle, walk, fly, glide, peck, swoop, caw, takeoff, land, hurt, death, spawn]


# ---- deer clips ----------------------------------------------------------------------------------

def deer_clips():
    idle = Clip("idle", 4.0, "loop", 1)
    # Standing at ease: slow breaths, a long look to one side, an ear flick on each side, a tail swish.
    idle.pos("Torso", [(0, v()), (2.0, v(0, 0.25)), (4.0, v())])
    idle.rot("Neck", [(0, v()), (2.0, v(3)), (4.0, v())])
    idle.rot("Head", [(0, v()), (0.8, v()), (1.1, v(0, 26, 0)), (1.9, v(0, 26, 0)), (2.3, v()), (4.0, v())])
    idle.rot("EarR", [(0, v()), (0.5, v()), (0.58, v(0, 34, 0)), (0.68, v()), (4.0, v())])
    idle.rot("EarL", [(0, v()), (2.8, v()), (2.88, v(0, -34, 0)), (2.98, v()), (4.0, v())])
    idle.rot("Tail", [(0, v()), (3.2, v()), (3.3, v(10, 0, 26)), (3.4, v(10, 0, -22)), (3.52, v()), (4.0, v())])
    walk = Clip("walk", 1.2, "loop", 2)
    # An unhurried walk on diagonal pairs: each leg swings through with its lower half folded, the neck
    # nodding once a step.
    t = [0, 0.3, 0.6, 0.9, 1.2]
    lead, trail = [-20, 0, 20, 4, -20], [20, 4, -20, 0, 20]
    lead_fold, trail_fold = [0, 0, 6, 46, 0], [6, 46, 0, 0, 6]
    for upper, lower, swing, fold in (("ArmR", "ArmLowerR", lead, lead_fold), ("LegL", "LegLowerL", lead, lead_fold),
                                      ("ArmL", "ArmLowerL", trail, trail_fold), ("LegR", "LegLowerR", trail, trail_fold)):
        walk.rot(upper, [(s, v(x)) for s, x in zip(t, swing)])
        walk.rot(lower, [(s, v(x)) for s, x in zip(t, fold)])
    walk.pos("Torso", [(0, v(0, -0.3)), (0.3, v(0, 0.3)), (0.6, v(0, -0.3)), (0.9, v(0, 0.3)), (1.2, v(0, -0.3))])
    walk.rot("Neck", [(0, v(8)), (0.3, v(3)), (0.6, v(8)), (0.9, v(3)), (1.2, v(8))])
    walk.rot("Head", [(0, v(-6)), (0.3, v(-2)), (0.6, v(-6)), (0.9, v(-2)), (1.2, v(-6))])
    walk.rot("Tail", [(0, v(0, 0, 8)), (0.6, v(0, 0, -8)), (1.2, v(0, 0, 8))])
    run = Clip("run", 0.5, "loop", 3)
    # A bounding flight: stretched out in the air (0 s), then gathered with all four legs under the body
    # (0.25 s); the neck reaches forward, the ears lie back and the white tail stands up like a flag.
    run.pos("Torso", [(0, v(0, 2.2)), (0.25, v(0, -0.6)), (0.5, v(0, 2.2))])
    run.rot("Torso", [(0, v(-6)), (0.25, v(6)), (0.5, v(-6))])
    run.rot("ArmR", [(0, v(-52)), (0.25, v(34)), (0.5, v(-52))])
    run.rot("ArmL", [(0, v(-42)), (0.25, v(40)), (0.5, v(-42))])
    run.rot("ArmLowerR", [(0, v(8)), (0.12, v(40)), (0.25, v(58)), (0.38, v(70)), (0.5, v(8))])
    run.rot("ArmLowerL", [(0, v(14)), (0.12, v(30)), (0.25, v(52)), (0.38, v(76)), (0.5, v(14))])
    run.rot("LegR", [(0, v(46)), (0.25, v(-42)), (0.5, v(46))])
    run.rot("LegL", [(0, v(40)), (0.25, v(-48)), (0.5, v(40))])
    run.rot("LegLowerR", [(0, v(10)), (0.12, v(52)), (0.25, v(30)), (0.38, v(6)), (0.5, v(10))])
    run.rot("LegLowerL", [(0, v(16)), (0.12, v(58)), (0.25, v(36)), (0.38, v(6)), (0.5, v(16))])
    run.rot("Neck", [(0, v(26)), (0.25, v(12)), (0.5, v(26))])
    run.rot("Head", [(0, v(-18)), (0.25, v(-10)), (0.5, v(-18))])
    both(run, "Ear", hold(run, v(0, 42, 0)))
    run.rot("Tail", [(0, v(118)), (0.25, v(96)), (0.5, v(118))])
    graze = Clip("graze", 3.0, "once", 4)
    # Lowers its head to the grass, crops a few mouthfuls with small nods, and lifts it again.
    graze.rot("Neck", [(0, v()), (0.6, v(116)), (1.0, v(110)), (1.4, v(117)), (1.8, v(110)), (2.2, v(117)), (3.0, v())])
    graze.rot("Head", [(0, v()), (0.6, v(-28)), (1.0, v(-22)), (1.4, v(-30)), (1.8, v(-22)), (2.2, v(-30)), (3.0, v())])
    graze.rot("ArmR", [(0, v()), (0.6, v(-9)), (2.2, v(-9)), (3.0, v())])
    graze.rot("EarL", [(0, v()), (1.5, v()), (1.58, v(0, -34, 0)), (1.68, v()), (3.0, v())])
    graze.rot("Tail", [(0, v()), (0.9, v()), (1.0, v(10, 0, 24)), (1.1, v(10, 0, -20)), (1.22, v()), (3.0, v())])
    alert = Clip("alert", 1.4, "once", 5)
    # Something moved: the head jerks up tall, the ears swing forward, a hard stare to one side, the
    # tail half raised, the whole animal held still.
    alert.pos("Torso", [(0, v()), (0.1, v(0, 0.4)), (1.1, v(0, 0.4)), (1.4, v())])
    alert.rot("Neck", [(0, v()), (0.1, v(-12)), (1.1, v(-12)), (1.4, v())])
    alert.rot("Head", [(0, v()), (0.1, v(6)), (0.45, v(6)), (0.55, v(6, 36, 0)), (1.1, v(6, 36, 0)), (1.4, v())])
    both(alert, "Ear", [(0, v()), (0.1, v(0, -30, 22)), (1.1, v(0, -30, 22)), (1.4, v())])
    alert.rot("Tail", [(0, v()), (0.1, v(56)), (1.1, v(56)), (1.4, v())])
    butt = Clip("butt", 0.8, "once", 6)
    # A stag's shove: drops the antlers, sets back on its haunches, then drives forward and tosses the
    # head up through the impact (0.4 s).
    butt.pos("Torso", [(0, v()), (0.25, v(0, -0.6, 2.5)), (0.4, v(0, 0, -5)), (0.55, v(0, 0, -3)), (0.8, v())])
    butt.rot("Neck", [(0, v()), (0.25, v(66)), (0.4, v(36)), (0.55, v(20)), (0.8, v())])
    butt.rot("Head", [(0, v()), (0.25, v(44)), (0.4, v(4)), (0.55, v(-8)), (0.8, v())])
    both(butt, "Leg", [(0, v()), (0.25, v(-12)), (0.4, v(26)), (0.55, v(16)), (0.8, v())])
    both(butt, "Arm", [(0, v()), (0.25, v(10)), (0.4, v(-18)), (0.55, v(-10)), (0.8, v())])
    both(butt, "Ear", [(0, v()), (0.25, v(0, 40, 0)), (0.55, v(0, 40, 0)), (0.8, v())])
    butt.event(0.4, "impact")
    hurt = Clip("hurt", 0.4, "once", 7)
    hurt.rot("Torso", [(0, v()), (0.06, v(-5, 0, 5)), (0.2, v(-2, 0, 1)), (0.4, v())])
    hurt.rot("Neck", [(0, v()), (0.06, v(-16)), (0.2, v(-5)), (0.4, v())])
    hurt.rot("Head", [(0, v()), (0.06, v(-10, 14, 0)), (0.2, v(-3, 4, 0)), (0.4, v())])
    both(hurt, "Ear", [(0, v()), (0.06, v(0, 44, 0)), (0.25, v(0, 20, 0)), (0.4, v())])
    hurt.rot("Tail", [(0, v()), (0.06, v(60)), (0.25, v(20)), (0.4, v())])
    death = Clip("death", 0.9, "once", 8)
    # The legs give way front first and the body sinks onto them as the neck and head go slack.
    death.pos("Torso", [(0, v()), (0.3, v(0, -3)), (0.9, v(0, -8))])
    death.rot("Torso", [(0, v()), (0.3, v(6)), (0.9, v(2))])
    both(death, "Arm", [(0, v()), (0.3, v(30)), (0.9, v(44))])
    both(death, "ArmLower", [(0, v()), (0.3, v(40)), (0.9, v(84))])
    both(death, "Leg", [(0, v()), (0.4, v(-14)), (0.9, v(-40))])
    both(death, "LegLower", [(0, v()), (0.4, v(30)), (0.9, v(88))])
    death.rot("Neck", [(0, v()), (0.3, v(40)), (0.9, v(84))])
    death.rot("Head", [(0, v()), (0.3, v(10, 0, 8)), (0.9, v(6, 0, 20))])
    both(death, "Ear", [(0, v()), (0.3, v(0, 30, -10)), (0.9, v(0, 40, -24))])
    spawn = Clip("spawn", 0.45, "once", 9)
    spawn.pos("Torso", [(0, v(0, -2.0)), (0.18, v(0, 1.0)), (0.32, v(0, -0.3)), (0.45, v())])
    spawn.rot("Neck", [(0, v(30)), (0.18, v(-6)), (0.45, v())])
    both(spawn, "Ear", [(0, v(0, 40, 0)), (0.2, v(0, -10, 0)), (0.45, v())])
    return [idle, walk, run, graze, alert, butt, hurt, death, spawn]


# ---- outputs -------------------------------------------------------------------------------------

def outliner(rig):
    """The group tree from the rig's own bone list and parents: a bone's cubes, then its child bones."""
    def node(bone):
        cubes = [hell.uuid(rig, i + 1) for i, c in enumerate(rig["cubes"]) if c[1] == bone]
        return hell.group(rig, bone, cubes + [node(b) for b in rig["bones"] if rig["parents"].get(b) == bone])
    return [node(bone) for bone in rig["bones"] if bone not in rig["parents"]]


def checked(rig, clips):
    """The clips with their key times rounded for the file, after checking every key names a bone of the
    rig and sits inside its clip."""
    for clip in clips:
        for bone, channels in clip.tracks.items():
            assert bone in rig["bones"], "%s: clip '%s' keys the unknown bone '%s'" % (rig["name"], clip.name, bone)
            for channel, keys in channels.items():
                channels[channel] = keys = [(round(t, 4), value) for t, value in keys]
                assert all(0 <= t <= clip.length for t, _ in keys), "%s: '%s' keys %s outside the clip" % (
                    rig["name"], clip.name, bone)
    return clips


def project_text(rig, clips):
    # The hell generator's bone list and outliner drive its uuid, clip and group code; each rig here
    # brings its own.
    hell.BONES, hell.outliner = rig["bones"], outliner
    assert all(cube[1] in rig["bones"] for cube in rig["cubes"]), rig["name"] + " has a cube on an unknown bone"
    return hell.inline_number_arrays(json.dumps(hell.project(rig, checked(rig, clips)), indent=4)) + "\n"


def outputs():
    files = []
    for rig, clips in ((DRAGONFLY, dragonfly_clips()), (CROW, crow_clips()), (DEER, deer_clips())):
        files.append((os.path.join(hell.ENTITY_DIR, rig["name"] + ".bbmodel"), project_text(rig, clips).encode("utf-8"), True))
        files.append((os.path.join(hell.ENTITY_DIR, rig["name"] + ".png"), hell.skin(rig), False))
    return files


if __name__ == "__main__":
    hell.main(outputs)
