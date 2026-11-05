#version 460 core
// One water ripple: a short wave train trailing a leading edge, over a flat quad whose UV spans 0..1
// (remapped to -1..1 below). The vertex stage is bubble-particle-vertex.glsl; the quad's tint carries
// the ripple: r = leading edge radius, g = wavelength (both as shares of the quad's half-size),
// b = a per-ripple seed, a = opacity.
in vec2 fUv;
flat in uint fLayer;
in vec4 fTint;
in float fDist;

uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform float uTrainWaves;

out vec4 FragColor;

const float TAU = 6.2831853;

void main() {
    vec2 p = fUv * 2.0 - 1.0;
    float front = fTint.r;
    float wl = max(fTint.g, 0.004);
    float seed = fTint.b;

    // The edge is not a perfect circle: a couple of slow lobes, different for every ripple
    float ang = atan(p.y, p.x);
    float wob = 1.0 + 0.045 * sin(ang * 3.0 + seed * TAU) + 0.03 * sin(ang * 5.0 + seed * 17.0);
    float d = length(p) / wob;

    // Distance behind the leading edge: the train lives in [0, trainLen]
    float behind = front - d;
    float trainLen = wl * uTrainWaves;
    if (behind < -wl * 0.3 || behind > trainLen) discard;

    // Crests and troughs along that distance, strongest at the edge and dying away behind it; the
    // swell is also thinner the farther it has spread (energy over a longer circle)
    float wave = sin(behind / wl * TAU);
    float env = smoothstep(-wl * 0.3, wl * 0.15, behind) * pow(1.0 - behind / trainLen, 1.6);
    env *= inversesqrt(max(front, 0.18)) * 0.55;
    float w = wave * env;

    // Crest: pale sky-lit foam; trough: a darker, bluer water. Linear light (decoded sRGB constants)
    vec3 crest = vec3(0.62, 0.82, 1.0);
    vec3 trough = vec3(0.02, 0.09, 0.22);
    vec3 col = w > 0.0 ? crest : trough;
    float alpha = clamp(abs(w), 0.0, 1.0) * (w > 0.0 ? 0.85 : 0.55) * fTint.a;
    if (alpha < 0.01) discard;

    float f = clamp((fDist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    col = mix(col, uFogColor, f * f);

    FragColor = vec4(col, alpha);
}
