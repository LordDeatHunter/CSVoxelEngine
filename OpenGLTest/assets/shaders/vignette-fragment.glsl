#version 460 core

uniform vec2 uResolution;
uniform float uStrength;
// Low-health pulse, 0..1: the heartbeat and its envelope are computed on the CPU
// (LowHealthVignette), so this pass only shapes it into a broad edge glow.
uniform float uLowHealth;
// Damage direction arc, 0..1 (DamageDirection): a red glow on the screen edge toward where the
// last blow came from. uHitArcDir is its unit screen direction from the centre, x right and y up.
// 0 draws nothing.
uniform float uHitArc;
uniform vec2 uHitArcDir;
// Surfacing droplets, 0..1 (SurfacingDroplets): drops streaking down the screen after the eye came
// up out of water. uDropletTime is the seconds since surfacing (it slides them), uDropletSeed picks a
// fresh pattern per surfacing. 0 draws nothing.
uniform float uDroplets;
uniform float uDropletTime;
uniform float uDropletSeed;
uniform float uTime;

out vec4 FragColor;

// Cheap value hash, driving slow film-grain so the reddening is never a flat fill.
float Hash(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

// Grid cell of the droplet scatter in screen heights, the share of cells holding a drop, the
// slide of the smallest and the biggest drop (screen heights after a second of sliding; the slide
// grows with time^1.5), and how many cells above a pixel a drop that slid down onto it may have
// started from: it must cover the biggest drop's slide over the droplets' 1.5 s.
const float DropCell = 0.13;
const float DropDensity = 0.45;
const float DropSlideMin = 0.04;
const float DropSlideMax = 0.40;
const int DropReachCells = 6;

// The surfacing drops over this pixel as (color, alpha): one hashed drop per occupied grid cell,
// a little taller than wide, with a dark refracting rim, a pale body and a bright glint, sliding
// down (bigger ones faster, each after its own short delay) and leaving a thin fading trail.
// p is in screen heights from the bottom-left corner, so drops stay round on any window.
vec4 Droplets(vec2 p)
{
    vec2 salt = fract(vec2(uDropletSeed * 0.1031, uDropletSeed * 0.1173)) * 64.0;
    vec2 cell = floor(p / DropCell);
    vec3 color = vec3(0.0);
    float alpha = 0.0;
    for (int dy = 0; dy <= DropReachCells; dy++)
    for (int dx = -1; dx <= 1; dx++)
    {
        vec2 id = cell + vec2(float(dx), float(dy));
        vec2 key = id + salt;
        if (Hash(key) > DropDensity)
            continue;

        float size = Hash(key * 3.1 + 1.3);
        float r = DropCell * mix(0.10, 0.26, size);
        vec2 origin = (id + 0.15 + 0.7 * vec2(Hash(key * 1.7 + 3.1), Hash(key * 2.3 + 7.7))) * DropCell;
        float delay = 0.25 * Hash(key * 4.7 + 2.9);
        float slide = max(uDropletTime - delay, 0.0);
        float fall = mix(DropSlideMin, DropSlideMax, size * size) * slide * sqrt(slide);
        vec2 centre = origin - vec2(0.0, fall);

        vec2 q = (p - centre) / vec2(r, r * 1.2);
        float len = length(q);
        float body = 1.0 - smoothstep(0.85, 1.0, len);
        float rim = smoothstep(0.55, 0.95, len) * body;
        float glint = 1.0 - smoothstep(0.0, 0.32, length(q - vec2(-0.32, 0.38)));
        float above = p.y - centre.y;
        float trail = (1.0 - smoothstep(r * 0.22, r * 0.42, abs(p.x - centre.x)))
            * step(0.0, above) * step(above, fall) * (1.0 - above / max(fall, 1e-4)) * (1.0 - body);

        float bodyA = body * 0.10;
        float rimA = rim * 0.24;
        float glintA = glint * body * 0.55;
        float trailA = trail * 0.30;
        float a = bodyA + rimA + glintA + trailA;
        if (a <= alpha)
            continue;
        // Drops rarely overlap, so the strongest one at a pixel stands for all of them
        color = (vec3(0.70, 0.80, 0.90) * (bodyA + trailA) + vec3(0.10, 0.14, 0.18) * rimA + vec3(1.0) * glintA) / a;
        alpha = a;
    }
    return vec4(color, min(alpha, 1.0));
}

void main()
{
    // Screen coords normalised to the viewport, deliberately NOT aspect-corrected: all four
    // edges then redden by the same amount and the corners, being farthest, are strongest.
    vec2 uv = gl_FragCoord.xy / uResolution;
    float radius = length((uv - 0.5) * 2.0);

    // Smooth radial falloff, squared so the clear centre opens up and the red hugs the
    // corners instead of washing the whole frame.
    float vignette = smoothstep(0.55, 1.70, radius);
    vignette *= vignette;

    // Animated grain and a slow pulse keep the glow alive rather than flat.
    float grain = 0.86 + 0.14 * Hash(floor(gl_FragCoord.xy * 0.66) + floor(uTime * 22.0));
    float pulse = 0.93 + 0.07 * sin(uTime * 4.2);

    float hit = uStrength * vignette * grain * pulse * 2.50;

    // The low-health edge starts further in than the hit flash and carries no flicker, so it
    // reads as a slow throb hugging the frame rather than another hit; the two terms add.
    float edge = smoothstep(0.35, 1.55, radius);
    edge *= edge;
    float low = uLowHealth * edge * (0.9 + 0.1 * grain) * 1.6;

    // The direction arc is measured in aspect-corrected units (the half height is 1), so its size
    // and thickness read the same on any window: a soft spot centred where the arc's direction
    // leaves the frame, cut down to a band hugging the edge, so it wraps round a corner when the
    // blow came from a diagonal.
    float arc = 0.0;
    if (uHitArc > 0.0)
    {
        vec2 halfExtent = vec2(uResolution.x / uResolution.y, 1.0);
        vec2 p = (uv - 0.5) * 2.0 * halfExtent;
        vec2 dir = uHitArcDir;
        float reach = min(halfExtent.x / max(abs(dir.x), 1e-4), halfExtent.y / max(abs(dir.y), 1e-4));
        float spot = 1.0 - smoothstep(0.15, 0.80, length(p - dir * reach));
        float inset = min(halfExtent.x - abs(p.x), halfExtent.y - abs(p.y));
        float band = 1.0 - smoothstep(0.0, 0.32, inset);
        arc = uHitArc * spot * band * band * (0.92 + 0.08 * grain) * 0.85;
    }

    vec4 drops = vec4(0.0);
    if (uDroplets > 0.0)
    {
        drops = Droplets(gl_FragCoord.xy / uResolution.y);
        drops.a *= uDroplets;
    }

    float red = hit + low + arc;
    float alpha = clamp(red + drops.a, 0.0, 1.0);
    vec3 color = mix(vec3(0.85, 0.06, 0.04), vec3(0.50, 0.0, 0.0), max(vignette, edge));
    // The arc keeps a brighter red than the dark edge glow, by its share of the coverage
    color = mix(color, vec3(0.95, 0.10, 0.06), arc / max(red, 1e-4));
    // ...and the drops their own pale colors, by theirs
    color = mix(color, drops.rgb, drops.a / max(red + drops.a, 1e-4));
    FragColor = vec4(color, alpha);
}

