#version 460 core
// One water ripple: a soft ring and a fainter trailing one, drawn over a flat quad whose UV spans
// 0..1 (remapped to -1..1 below). The vertex stage is bubble-particle-vertex.glsl; the quad's tint
// carries the ring's place: r = leading ring radius, g = ring width (both as shares of the quad's
// half-size, 0..1), a = opacity.
in vec2 fUv;
flat in uint fLayer;
in vec4 fTint;
in float fDist;

uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform float uTrailShare;

out vec4 FragColor;

float Ring(float d, float radius, float width) {
    return smoothstep(radius - width, radius, d) * (1.0 - smoothstep(radius, radius + width, d));
}

void main() {
    float d = length(fUv * 2.0 - 1.0);
    float radius = fTint.r;
    float width = max(fTint.g, 0.005);
    float lead = Ring(d, radius, width);
    float trail = Ring(d, radius * uTrailShare, width) * 0.45;
    float alpha = clamp(lead + trail, 0.0, 1.0) * fTint.a;
    if (alpha < 0.01) discard;

    // Pale blue-white: the splash droplets' (0.72, 0.86, 1.0) sRGB colour, decoded to linear light
    vec3 col = vec3(0.48, 0.71, 1.0);

    float f = clamp((fDist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    col = mix(col, uFogColor, f * f);

    FragColor = vec4(col, alpha);
}
