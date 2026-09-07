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
uniform sampler2DShadow uShadowMap;
uniform mat4 uLightViewProj;
uniform float uShadowStrength;
uniform float uShadowTexel;
uniform float uShadowWorldTexel;
uniform float uShadowSoftness;
uniform float uShadowNormalOffset;

out vec4 FragColor;

// Directional sun shadows. RenderManager renders the casters depth-only through
// uLightViewProj into uShadowMap, so a fragment's own light-space depth compared against
// what is stored there says whether something else got the sunlight first. The ortho
// projection puts clip z in 0..1 exactly like GL's depth write, so all three components
// map with *0.5+0.5 and the comparison is apples to apples.
float SampleShadow(vec3 wp, vec3 N, float ndl) {
    if (uShadowStrength <= 0.0) return 1.0;
    // Normal offset: axis-aligned voxel faces at grazing angles otherwise show acne
    vec3 p = wp + N * (uShadowWorldTexel * uShadowNormalOffset * (1.0 - ndl));
    vec4 lp = uLightViewProj * vec4(p, 1.0);
    vec3 ndc = lp.xyz / lp.w;
    vec3 uvz = ndc * 0.5 + 0.5;                 // all three components -> [0,1]
    float edge = max(abs(ndc.x), abs(ndc.y));
    float fade = 1.0 - smoothstep(0.85, 1.0, edge);   // soften the ortho-box boundary
    if (fade <= 0.0 || uvz.z > 1.0) return 1.0;
    uvz.z -= 0.0008;
    if (uShadowSoftness <= 0.0) return mix(1.0, texture(uShadowMap, uvz), fade * uShadowStrength);
    // Four taps on the diagonals, half a texel out. The sampler filters linearly, so each
    // fetch is already a hardware 2x2 comparison and the four overlap into a smooth ~3x3
    // tent — the same softness as the old (2s+1)^2 loop for 4 fetches instead of 9.
    float o = 0.5 * uShadowTexel * uShadowSoftness;
    float lit = texture(uShadowMap, vec3(uvz.x - o, uvz.y - o, uvz.z))
              + texture(uShadowMap, vec3(uvz.x + o, uvz.y - o, uvz.z))
              + texture(uShadowMap, vec3(uvz.x - o, uvz.y + o, uvz.z))
              + texture(uShadowMap, vec3(uvz.x + o, uvz.y + o, uvz.z));
    return mix(1.0, lit * 0.25, fade * uShadowStrength);
}

void main() {
    vec4 texColor = texture(uTextureAtlas, fTexCoord);
    // Leaf tiles have transparent holes; cut them out so the canopy sees through instead of
    // blending ghosts. Every other tile is fully opaque, so this changes nothing for them.
    if (texColor.a < 0.5) discard;
    vec3 albedo = texColor.rgb * fColor.rgb;
    vec3 N = normalize(fNormal);
    float ndl = max(dot(N, uSunDir), 0.0);
    float shadow = SampleShadow(fWorldPos, N, ndl);
    // Blend between flat and directional so sides stay readable while the sun orbits;
    // the shadow term only ever dims the direct sun, never the ambient below
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
