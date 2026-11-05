#version 460 core
// The slice-trail ribbon, drawn additively (SrcAlpha, One). fUv.x is the stable arc length in
// blocks along the swing, fUv.y runs 0 at the inner edge to 1 at the tip edge; fTint is the
// trail's sRGB color with the age fade in its alpha.
in vec2 fUv;
in vec4 fTint;
in float fDist;

uniform float uCoreCenter;
uniform float uCoreWidth;
uniform float uCoreBoost;
uniform float uGlowPower;
uniform float uStreakDepth;
uniform float uStreakSpan;
uniform float uFadeNear;
uniform float uFadeFar;
uniform int uStreakLanes;
// 1 decodes the sRGB tint for the game's linear HDR target, 0 keeps it (the item debugger draws in display space)
uniform int uLinearOutput;

out vec4 FragColor;

// Twin of ColorSpace.SrgbToLinear
vec3 SrgbToLinear(vec3 c) {
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

// Twin of SliceTrailGlow (formula, not bits)
float Hash(float x) {
    return fract(sin(x * 12.9898) * 43758.5453);
}

float Core(float v) {
    float d = (v - uCoreCenter) / uCoreWidth;
    return exp(-d * d);
}

float Streak(float along, float v) {
    float lane = floor(v * float(uStreakLanes));
    float h = Hash(lane + 1.0);
    return smoothstep(h * 0.8 * uStreakSpan, (h * 0.8 + 0.2) * uStreakSpan, along);
}

float Glow(float along, float v) {
    float body = pow(clamp(v, 0.0, 1.0), uGlowPower);
    return body * mix(1.0, Streak(along, v), uStreakDepth * (1.0 - Core(v)));
}

void main() {
    float along = fUv.x;
    float v = fUv.y;
    float glow = Glow(along, v);
    float core = Core(v);

    vec3 col = uLinearOutput != 0 ? SrgbToLinear(fTint.rgb) : fTint.rgb;
    col = mix(col * glow, vec3(1.0), core) * (1.0 + uCoreBoost * core);

    float alpha = fTint.a * max(glow, core);
    // Added light cannot blend toward the fog color, so fade the contribution out with distance instead.
    float f = clamp((fDist - uFadeNear) / (uFadeFar - uFadeNear), 0.0, 1.0);
    alpha *= 1.0 - f * f;

    if (alpha < 0.004) discard;
    FragColor = vec4(col, alpha);
}
