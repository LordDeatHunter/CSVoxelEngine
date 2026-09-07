#version 330 core
in vec4 fColor;
in vec2 fTexCoord;
in float fSun;
in float fBlock;
in vec3 fNormal;
in vec3 fWorldPos;

uniform sampler2D uTextureAtlas;
uniform float uAlpha;
uniform float uFlash;
uniform float uTime;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform vec3 uAmbient;
uniform vec3 uTorchColor;
uniform float uTorchIntensity;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform vec3 uCameraPos;
uniform int uNumPointLights;
uniform vec3 uPointPos[8];
uniform vec3 uPointColor[8];

out vec4 FragColor;

// Directional shadow mapping is parked for now (it shaded inverted/wonky) — everything
// stays fully lit until it comes back. The depth-pass shaders are still on disk at
// assets/shaders/shadow-vertex.glsl + shadow-fragment.glsl; re-enabling means restoring
// the depth FBO + light-space matrix in RenderManager and the real lookup here.
float SampleShadow(vec3 wp) {
    return 1.0;
}

void main() {
    vec4 texColor = texture(uTextureAtlas, fTexCoord);
    // Leaf tiles have transparent holes; cut them out so the canopy sees through instead of
    // blending ghosts. Every other tile is fully opaque, so this changes nothing for them.
    if (texColor.a < 0.5) discard;
    vec3 albedo = texColor.rgb * fColor.rgb;
    vec3 N = normalize(fNormal);
    float ndl = max(dot(N, uSunDir), 0.0);
    // Blend between flat and directional so sides stay readable while the sun orbits
    float shadow = SampleShadow(fWorldPos);
    vec3 sun = uSunColor * (fSun * uSunIntensity * (0.35 + 0.65 * ndl) * shadow);
    float flick = 0.9 + 0.1 * sin(uTime * 9.0) * sin(uTime * 7.3 + 1.7);
    vec3 torch = uTorchColor * (fBlock * uTorchIntensity * flick);
    vec3 pl = vec3(0.0);
    for (int i = 0; i < 8; i++) {
        if (i >= uNumPointLights) break;
        vec3 L = uPointPos[i] - fWorldPos;
        float dist = length(L);
        float att = 1.0 / (1.0 + 0.09 * dist + 0.032 * dist * dist);
        pl += uPointColor[i] * att;
    }
    // Ambient is skylight bounce, so it only reaches fragments the sky reaches (fSun).
    // Without this every sealed cave renders at full ambient brightness in daytime.
    vec3 light = uAmbient * (0.12 + 0.88 * fSun) + sun + torch + pl;
    vec3 col = albedo * light;
    float dist = distance(fWorldPos, uCameraPos);
    float f = clamp((dist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    col = mix(col, uFogColor, f * f);
    // Tile alpha carries the fluid's translucency; uAlpha is the chunk fade-in. The
    // discard above still removes the leaf tiles' fully transparent holes.
    FragColor = vec4(mix(col, vec3(1.0), uFlash), texColor.a * uAlpha);
}
