#version 460 core
in vec2 fUv;
in vec4 fTint;
in float fEmissive;
in float fDist;

uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;

out vec4 FragColor;

// Twin of ColorSpace.SrgbToLinear
vec3 SrgbToLinear(vec3 c) {
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void main() {
    if (fEmissive > 1.5) {
        // A flame tongue: u across, v up from the base; a teardrop that is widest low and tapers to a tip
        float x = (fUv.x - 0.5) * 2.0;
        float y = fUv.y;
        float w = max(pow(1.0 - y, 0.75) * (0.35 + 0.65 * smoothstep(0.0, 0.25, y)), 0.02);
        float side = abs(x) / w;
        float shape = smoothstep(1.0, 0.2, side) * (1.0 - y * y);
        float alpha = fTint.a * shape;
        if (alpha < 0.01) discard;
        float core = (1.0 - smoothstep(0.0, 0.8, side)) * (1.0 - y);
        vec3 col = SrgbToLinear(fTint.rgb) * (0.8 + 0.5 * core);
        float f = clamp((fDist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
        FragColor = vec4(col, alpha * (1.0 - f * f));
        return;
    }
    float d = length(fUv - vec2(0.5)) * 2.0;
    float shape = smoothstep(1.0, 0.35, d);
    float alpha = fTint.a * shape;
    if (alpha < 0.01) discard;

    float core = 1.0 - smoothstep(0.0, 1.0, d);
    // The ember and smoke colors are authored as sRGB bytes; decode them for the linear target
    vec3 col = SrgbToLinear(fTint.rgb) * mix(0.6, 1.0 + 1.6 * core, fEmissive);

    float f = clamp((fDist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    if (fEmissive > 0.5) {
        // Embers are drawn additively: added light cannot blend toward the fog color, so
        // fade the contribution out with distance instead.
        alpha *= 1.0 - f * f;
    } else {
        col = mix(col, uFogColor, f * f);
    }

    FragColor = vec4(col, alpha);
}
