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
uniform float uTime;

out vec4 FragColor;

// Cheap value hash, driving slow film-grain so the reddening is never a flat fill.
float Hash(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
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

    float alpha = clamp(hit + low + arc, 0.0, 1.0);
    vec3 color = mix(vec3(0.85, 0.06, 0.04), vec3(0.50, 0.0, 0.0), max(vignette, edge));
    // The arc keeps a brighter red than the dark edge glow, by its share of the coverage
    color = mix(color, vec3(0.95, 0.10, 0.06), arc / max(hit + low + arc, 1e-4));
    FragColor = vec4(color, alpha);
}

