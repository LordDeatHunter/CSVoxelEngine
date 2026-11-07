#!/usr/bin/env python3
"""Deterministic placeholder art for the tin and bronze metals, the copper sword and the iron pickaxe.

Recolors the existing copper tool sprites and the iron sword (only the metal pixels: the wooden
handles and outlines are kept) onto a per-metal luminance ramp, and the malachite ore tile and
chunk onto tin. Iron only gets the pickaxe, recolored from the copper one (its sword is the
source). Also writes the 32x32 mold_sword.png silhouette the mold model generator reads
(tools/generate-mold-models.ps1). Re-runs are byte-identical.

Usage: python tools/generate-metal-textures.py [tin|copper|bronze|iron ...]   (default: all)
"""
import os
import sys
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
ITEMS = os.path.join(ROOT, 'OpenGLTest', 'assets', 'textures', 'items')
BLOCKS = os.path.join(ROOT, 'OpenGLTest', 'assets', 'textures', 'blocks')

RAMPS = {  # dark -> light, sRGB
    'tin': ((62, 72, 88), (150, 164, 180), (232, 238, 244)),
    'bronze': ((84, 52, 18), (176, 120, 44), (240, 196, 104)),
    'copper': ((115, 25, 0), (209, 101, 11), (255, 168, 72)),
    'iron': ((48, 52, 62), (128, 136, 150), (214, 222, 232)),
}


def ramp(metal, t):
    t = max(0.0, min(1.0, t))
    a, b, c = RAMPS[metal]
    lo, hi, u = (a, b, t * 2) if t < 0.5 else (b, c, (t - 0.5) * 2)
    return tuple(int(round(lo[i] + (hi[i] - lo[i]) * u)) for i in range(3))


def lum(p):
    return 0.3 * p[0] + 0.59 * p[1] + 0.11 * p[2]


def recolor(src, dst, metal, is_metal, lo, hi):
    im = Image.open(src).convert('RGBA')
    px = im.load()
    for y in range(im.height):
        for x in range(im.width):
            p = px[x, y]
            if p[3] == 0 or not is_metal(p):
                continue
            px[x, y] = ramp(metal, (lum(p) - lo) / (hi - lo)) + (p[3],)
    im.save(dst)


def copper_metal(p):
    return p[0] >= 115 and p[0] - p[2] >= 100


def grey_metal(p):
    return max(p[:3]) - min(p[:3]) < 40 or (p[2] >= p[0] and lum(p) > 90)


def green_ore(p):
    return p[1] > p[0] + 6 and p[1] >= p[2] - 20


def main():
    metals = sys.argv[1:] or ['tin', 'copper', 'bronze', 'iron']
    for m in metals:
        if m == 'iron':
            for part in ('pickaxe', 'axe', 'axe_head', 'pickaxe_head'):
                recolor(os.path.join(ITEMS, 'copper_%s.png' % part), os.path.join(ITEMS, 'iron_%s.png' % part),
                        m, copper_metal, 40, 200)
            continue
        if m != 'copper':
            for part in ('axe', 'axe_head', 'pickaxe', 'pickaxe_head', 'ingot'):
                recolor(os.path.join(ITEMS, 'copper_%s.png' % part), os.path.join(ITEMS, '%s_%s.png' % (m, part)),
                        m, copper_metal, 40, 200)
        for part in ('sword', 'sword_head'):
            recolor(os.path.join(ITEMS, 'iron_%s.png' % part), os.path.join(ITEMS, '%s_%s.png' % (m, part)),
                    m, grey_metal, 50, 255)
        if m == 'tin':
            recolor(os.path.join(ITEMS, 'malachite_ore.png'), os.path.join(ITEMS, 'tin_ore.png'), m, green_ore, 20, 130)
            recolor(os.path.join(BLOCKS, 'malachite_ore.png'), os.path.join(BLOCKS, 'tin_ore.png'), m, green_ore, 20, 130)
    if 'copper' in metals:
        Image.open(os.path.join(ITEMS, 'iron_sword.png')).convert('RGBA').resize((32, 32), Image.NEAREST).save(
            os.path.join(ITEMS, 'mold_sword.png'))


main()
