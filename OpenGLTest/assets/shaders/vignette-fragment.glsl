#version 460 core

uniform vec2 uResolution;
uniform float uStrength;
// Low-health pulse, 0..1: the heartbeat and its envelope are computed on the CPU
// (LowHealthVignette), so this pass only shapes it into a broad edge glow.
uniform float uLowHealth;
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

    float alpha = clamp(hit + low, 0.0, 1.0);
    vec3 color = mix(vec3(0.85, 0.06, 0.04), vec3(0.50, 0.0, 0.0), max(vignette, edge));
    FragColor = vec4(color, alpha);
}

