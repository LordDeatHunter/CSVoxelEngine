"""Deterministic generator of the spell sound effects (OpenGLTest/assets/sfx/spell_*.wav).

Three short placeholder clips, each 16-bit PCM, stereo (identical channels), 44100 Hz, the only
format SfxManager loads; peaks sit near half of full scale like the other clips:
    spell_cast.wav    ~0.35 s  a rising whoosh: a sine sweep from ~300 to ~1100 Hz under low-passed
                               noise, a quick attack and a smooth decay (the caster's own cast)
    spell_impact.wav  ~0.30 s  a low thump (a sine falling from ~110 to ~45 Hz, fast decay) with a
                               crackle of sparse short clicks over its first 150 ms (a bolt landing)
    spell_fizzle.wav  ~0.35 s  a soft low-passed noise puff with a few sputtering dips (a fizzle)

The file names are the SfxLibrary groups spell_cast, spell_impact and spell_fizzle, played by code
(SpellFeedback, SpellEffects), never named by a block.

The noise comes from random.Random with a fixed seed and the wav bytes are written by the wave
module, so re-running reproduces the files byte for byte. Run from anywhere:
    python -I tools/generate-spell-sfx.py           write the clips
    python -I tools/generate-spell-sfx.py --check   exit 1 if a file on disk differs
"""
import io
import math
import os
import random
import struct
import sys
import wave

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SFX = os.path.join(ROOT, "OpenGLTest", "assets", "sfx")

RATE = 44100
PEAK = 0.5


def lowpass(samples, alpha):
    """One-pole low-pass: each output moves alpha of the way toward its input."""
    out = []
    state = 0.0
    for s in samples:
        state += alpha * (s - state)
        out.append(state)
    return out


def normalize(samples, peak=PEAK):
    top = max(abs(s) for s in samples) or 1.0
    return [s * peak / top for s in samples]


def smoothstep(x):
    x = min(1.0, max(0.0, x))
    return x * x * (3.0 - 2.0 * x)


def cast_clip():
    rng = random.Random(0x5CA57)
    count = int(RATE * 0.35)
    noise = lowpass([rng.uniform(-1.0, 1.0) for _ in range(count)], 0.08)
    out = []
    phase = 0.0
    for i in range(count):
        t = i / RATE
        u = i / count
        freq = 300.0 + 800.0 * u * u
        phase += 2.0 * math.pi * freq / RATE
        attack = min(1.0, t / 0.02)
        decay = 1.0 - smoothstep((u - 0.25) / 0.75)
        out.append((0.55 * math.sin(phase) + 0.9 * noise[i]) * attack * decay)
    return normalize(out)


def impact_clip():
    rng = random.Random(0x1A9AC7)
    count = int(RATE * 0.30)
    out = [0.0] * count
    phase = 0.0
    for i in range(count):
        t = i / RATE
        freq = 45.0 + 65.0 * math.exp(-t / 0.05)
        phase += 2.0 * math.pi * freq / RATE
        attack = min(1.0, t / 0.003)
        out[i] = math.sin(phase) * attack * math.exp(-t / 0.07)
    # The crackle: short decaying clicks scattered over the first 150 ms
    crackle_end = int(RATE * 0.15)
    for _ in range(40):
        start = rng.randrange(crackle_end)
        length = rng.randrange(20, 90)
        height = rng.uniform(0.3, 0.8) * (1.0 - start / crackle_end)
        sign = 1.0 if rng.random() < 0.5 else -1.0
        for k in range(length):
            if start + k >= count:
                break
            out[start + k] += sign * height * math.exp(-k / 12.0) * rng.uniform(0.6, 1.0)
    return normalize(out)


def fizzle_clip():
    rng = random.Random(0xF1221E)
    count = int(RATE * 0.35)
    noise = lowpass([rng.uniform(-1.0, 1.0) for _ in range(count)], 0.12)
    # A few sputtering dips: the puff drops near silence for a moment and comes back
    dips = sorted(rng.uniform(0.05, 0.28) for _ in range(4))
    out = []
    for i in range(count):
        t = i / RATE
        attack = min(1.0, t / 0.01)
        gate = 1.0
        for d in dips:
            distance = abs(t - d)
            if distance < 0.012:
                gate = min(gate, 0.15 + 0.85 * distance / 0.012)
        out.append(noise[i] * attack * math.exp(-t / 0.11) * gate)
    return normalize(out)


CLIPS = [
    ("spell_cast.wav", cast_clip),
    ("spell_impact.wav", impact_clip),
    ("spell_fizzle.wav", fizzle_clip),
]


def encode(samples):
    frames = bytearray()
    for s in samples:
        value = int(round(max(-1.0, min(1.0, s)) * 32767.0))
        frames += struct.pack("<hh", value, value)
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as out:
        out.setnchannels(2)
        out.setsampwidth(2)
        out.setframerate(RATE)
        out.writeframes(bytes(frames))
    return buffer.getvalue()


def main():
    check = "--check" in sys.argv[1:]
    stale = False
    for name, build in CLIPS:
        path = os.path.join(SFX, name)
        data = encode(build())
        if check:
            try:
                with open(path, "rb") as f:
                    same = f.read() == data
            except OSError:
                same = False
            if not same:
                print("stale", os.path.relpath(path, ROOT))
                stale = True
            continue
        with open(path, "wb") as f:
            f.write(data)
        print("wrote", os.path.relpath(path, ROOT))
    if stale:
        sys.exit(1)


if __name__ == "__main__":
    main()
