"""Deterministic generator of the goblin's animation clips (assets/models/entity/goblin.bbmodel).

Rewrites only the top-level "animations" array, so the rig, cubes and UVs stay untouched; re-running
is byte-identical. Run from the repo root:  python tools/generate-goblin-clips.py

Sign convention (see BbmodelLoader.ReadAnimations): a rotation (x, y, z) in degrees maps to engine
Euler (-x, -y, z). For limbs a NEGATIVE x raises a hanging limb forward and a positive x swings it
back; for the torso and head a negative x leans back and a positive x leans forward.
Position keys are in pixels (1/16 block), y up.

Contract with Goblin.ValidateContent: clips idle, walk, throw and melee must exist, throw needs a
"release" event and melee an "impact" event; their TIMES are pinned by gameplay pacing, so keep them
(release at 0.42 s, impact at 0.30 s). hurt, death and spawn are optional extras: hurt plays as an
additive overlay, death and spawn as ordinary one-shot actions.
"""
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PATH = os.path.join(ROOT, "OpenGLTest", "assets", "models", "entity", "goblin.bbmodel")

TORSO = "00000000-0000-4000-8000-000000000001"
HEAD = "00000000-0000-4000-8000-000000000002"
ARM_R = "00000000-0000-4000-8000-000000000003"
ARM_L = "00000000-0000-4000-8000-000000000004"
LEG_R = "00000000-0000-4000-8000-000000000005"
LEG_L = "00000000-0000-4000-8000-000000000006"
NAMES = {TORSO: "Torso", HEAD: "Head", ARM_R: "ArmR", ARM_L: "ArmL", LEG_R: "LegR", LEG_L: "LegL"}

_next_uuid = [0x1000]


def uuid():
    _next_uuid[0] += 1
    return "00000000-0000-4000-8000-%012x" % _next_uuid[0]


def num(v):
    # Blockbench stores data as strings; keep them short and stable.
    s = ("%.3f" % v).rstrip("0").rstrip(".")
    return "0" if s in ("", "-0") else s


def keyframes(channel, keys):
    """keys: [(time, (x, y, z))] -> catmullrom keyframes."""
    return [{
        "channel": channel,
        "data_points": [{"x": num(v[0]), "y": num(v[1]), "z": num(v[2])}],
        "uuid": uuid(),
        "time": t,
        "color": -1,
        "interpolation": "catmullrom",
    } for t, v in keys]


class Clip:
    def __init__(self, name, length, loop, uid):
        self.name, self.length, self.loop, self.uid = name, length, loop, uid
        self.tracks = {}   # bone uuid -> {"rotation": [...], "position": [...]}
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

    def build(self):
        animators = {}
        for bone in (TORSO, HEAD, ARM_R, ARM_L, LEG_R, LEG_L):
            if bone not in self.tracks:
                continue
            kfs = []
            for channel in ("rotation", "position"):
                if channel in self.tracks[bone]:
                    kfs += keyframes(channel, self.tracks[bone][channel])
            animators[bone] = {"name": NAMES[bone], "type": "bone", "keyframes": kfs}
        if self.events:
            animators["effects"] = {"name": "Effects", "type": "effect", "keyframes": [{
                "channel": "timeline",
                "data_points": [{"script": s}],
                "uuid": uuid(),
                "time": t,
                "color": -1,
                "interpolation": "linear",
            } for t, s in self.events]}
        return {
            "uuid": self.uid,
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


def idle():
    c = Clip("idle", 2.4, "loop", "00000000-0000-4000-8000-000000000301")
    t = [0, 0.6, 1.2, 1.8, 2.4]
    # Breathing, a slow weight shift side to side, a look around and loose arms.
    c.pos(TORSO, [(0, v()), (1.2, v(0, 0.5)), (2.4, v())])
    c.rot(TORSO, [(0, v(0, 0, 0)), (0.6, v(-1.5, 0, 2.5)), (1.2, v(0, 0, 0)), (1.8, v(-1.5, 0, -2.5)), (2.4, v(0, 0, 0))])
    c.rot(HEAD, [(0, v(0, 0, 0)), (0.7, v(3, 14, 0)), (1.2, v(0, 0, 0)), (1.7, v(4, -10, 2)), (2.4, v(0, 0, 0))])
    c.rot(ARM_R, [(0, v(0, 0, 3)), (1.2, v(5, 0, 5)), (2.4, v(0, 0, 3))])
    c.rot(ARM_L, [(0, v(0, 0, -3)), (1.2, v(-4, 0, -5)), (2.4, v(0, 0, -3))])
    return c


def walk():
    c = Clip("walk", 0.8, "loop", "00000000-0000-4000-8000-000000000302")
    t = [0, 0.2, 0.4, 0.6, 0.8]
    # Contralateral gait: arms and legs swing opposite, the swinging leg lifts, the body bobs
    # at each mid-stance, twists against the hips, sways onto the stance leg and leans forward.
    c.pos(TORSO, [(0, v(0, 0.2)), (0.2, v(0, 1.0)), (0.4, v(0, 0.2)), (0.6, v(0, 1.0)), (0.8, v(0, 0.2))])
    c.rot(TORSO, [(0, v(5, 6, 0)), (0.2, v(4, 0, 2.5)), (0.4, v(5, -6, 0)), (0.6, v(4, 0, -2.5)), (0.8, v(5, 6, 0))])
    c.rot(HEAD, [(0, v(-4, -5, 0)), (0.2, v(-3, 0, -1.5)), (0.4, v(-4, 5, 0)), (0.6, v(-3, 0, 1.5)), (0.8, v(-4, -5, 0))])
    c.rot(ARM_R, cycle(t, [-44, -4, 44, 4, -44], lambda x: v(x, 0, 3)))
    c.rot(ARM_L, cycle(t, [44, 4, -44, -4, 44], lambda x: v(x, 0, -3)))
    c.rot(LEG_R, cycle(t, [34, 4, -34, -4, 34], lambda x: v(x)))
    c.rot(LEG_L, cycle(t, [-34, -4, 34, 4, -34], lambda x: v(x)))
    c.pos(LEG_R, [(0, v()), (0.4, v()), (0.6, v(0, 1.1)), (0.8, v())])
    c.pos(LEG_L, [(0, v()), (0.2, v(0, 1.1)), (0.4, v()), (0.8, v())])
    return c


def throw():
    c = Clip("throw", 0.7, "once", "00000000-0000-4000-8000-000000000303")
    # Crouch and swing back, wind up overhead, whip through at the release (0.42 s), follow through.
    c.pos(TORSO, [(0, v()), (0.12, v(0, -0.9)), (0.28, v(0, 0.2)), (0.42, v(0, 0.6)), (0.52, v(0, 0)), (0.7, v())])
    c.rot(TORSO, [(0, v()), (0.12, v(5, 8, 0)), (0.28, v(-13, -15, 0)), (0.42, v(15, 12, 0)), (0.52, v(8, 4, 0)), (0.7, v())])
    c.rot(HEAD, [(0, v()), (0.12, v(2, 0, 0)), (0.28, v(-7, 4, 0)), (0.42, v(5, 0, 0)), (0.7, v())])
    c.rot(ARM_R, [(0, v()), (0.12, v(30, 0, 8)), (0.28, v(-175, 0, 0)), (0.42, v(-42, 0, 0)), (0.52, v(-18, 0, 0)), (0.7, v())])
    c.rot(ARM_L, [(0, v()), (0.12, v(-12, 0, -8)), (0.28, v(-32, 0, -10)), (0.42, v(-10, 0, 0)), (0.7, v())])
    c.rot(LEG_L, [(0, v()), (0.12, v(6)), (0.28, v(-14)), (0.42, v(-22)), (0.52, v(-10)), (0.7, v())])
    c.rot(LEG_R, [(0, v()), (0.12, v(-6)), (0.28, v(14)), (0.42, v(12)), (0.52, v(5)), (0.7, v())])
    c.event(0.42, "release")
    return c


def melee():
    c = Clip("melee", 0.5, "once", "00000000-0000-4000-8000-000000000304")
    # Crouch and raise the club, slam down through the impact (0.30 s), recoil, recover.
    c.pos(TORSO, [(0, v()), (0.12, v(0, -0.5)), (0.3, v(0, -1.1)), (0.4, v(0, -0.4)), (0.5, v())])
    c.rot(TORSO, [(0, v()), (0.12, v(-15, 18, 0)), (0.3, v(22, -22, 0)), (0.4, v(10, -8, 0)), (0.5, v())])
    c.rot(HEAD, [(0, v()), (0.12, v(-9, 0, 0)), (0.3, v(12, 0, 0)), (0.4, v(6, 0, 0)), (0.5, v())])
    c.rot(ARM_R, [(0, v()), (0.12, v(-135, 0, -28)), (0.3, v(18, 0, 18)), (0.4, v(32, 0, 8)), (0.5, v())])
    c.rot(ARM_L, [(0, v()), (0.12, v(-65, 0, -12)), (0.3, v(-8, 0, -6)), (0.4, v(10, 0, 0)), (0.5, v())])
    c.rot(LEG_L, [(0, v()), (0.12, v(8)), (0.3, v(-26)), (0.4, v(-10)), (0.5, v())])
    c.rot(LEG_R, [(0, v()), (0.12, v(-8)), (0.3, v(20)), (0.4, v(8)), (0.5, v())])
    c.event(0.3, "impact")
    return c


def hurt():
    c = Clip("hurt", 0.4, "once", "00000000-0000-4000-8000-000000000305")
    # Additive flinch: snap back, arms flung out, then settle past rest once.
    c.rot(TORSO, [(0, v()), (0.06, v(-16, 0, 5)), (0.16, v(-5, 0, 2)), (0.28, v(2, 0, 0)), (0.4, v())])
    c.rot(HEAD, [(0, v()), (0.06, v(-20, 10, 0)), (0.18, v(-6, 3, 0)), (0.4, v())])
    c.rot(ARM_R, [(0, v()), (0.06, v(-20, 0, 28)), (0.2, v(-6, 0, 8)), (0.4, v())])
    c.rot(ARM_L, [(0, v()), (0.06, v(-20, 0, -28)), (0.2, v(-6, 0, -8)), (0.4, v())])
    return c


def death():
    c = Clip("death", 0.8, "once", "00000000-0000-4000-8000-000000000306")
    # Limbs flail up as the body topples (the fall itself is drawn by Goblin.BodyEffect).
    c.rot(HEAD, [(0, v()), (0.2, v(-28, 14, 0)), (0.8, v(-34, 14, 0))])
    c.rot(ARM_R, [(0, v()), (0.18, v(-95, 0, 24)), (0.5, v(-130, 0, 38)), (0.8, v(-120, 0, 34))])
    c.rot(ARM_L, [(0, v()), (0.18, v(-80, 0, -24)), (0.5, v(-120, 0, -38)), (0.8, v(-110, 0, -34))])
    c.rot(LEG_R, [(0, v()), (0.25, v(25)), (0.8, v(32))])
    c.rot(LEG_L, [(0, v()), (0.25, v(-20)), (0.8, v(-26))])
    return c


def spawn():
    c = Clip("spawn", 0.45, "once", "00000000-0000-4000-8000-000000000307")
    # Pops up from a crouch with the arms thrown up, then lands loose.
    c.pos(TORSO, [(0, v(0, -2.2)), (0.18, v(0, 1.2)), (0.32, v(0, -0.4)), (0.45, v())])
    c.rot(ARM_R, [(0, v(-100, 0, 20)), (0.18, v(-110, 0, 30)), (0.45, v())])
    c.rot(ARM_L, [(0, v(-100, 0, -20)), (0.18, v(-110, 0, -30)), (0.45, v())])
    c.rot(HEAD, [(0, v(-12)), (0.18, v(-10)), (0.45, v())])
    return c


def main():
    with open(PATH, "rb") as f:
        raw = f.read()
    # "animations" is the file's last top-level key: keep everything before it byte for byte and
    # replace the rest, so the rig and cubes never move in the diff.
    marker = b'    "animations": ['
    head = raw[:raw.index(marker)]
    anims = [c.build() for c in (idle(), walk(), throw(), melee(), hurt(), death(), spawn())]
    nl = chr(13) + chr(10)
    body = json.dumps({"animations": anims}, indent=4).split(chr(10))[1:-1]
    out = head + nl.join(body).encode("utf-8") + nl.encode() + b"}"
    if raw.endswith(nl.encode()):
        out += nl.encode()
    if "--check" in sys.argv:
        sys.exit(0 if out == raw else 1)
    with open(PATH, "wb") as f:
        f.write(out)
    print("wrote", PATH)


if __name__ == "__main__":
    main()
