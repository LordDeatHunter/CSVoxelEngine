#version 460 core
// Falling-leaf shading: the atlas crop with its transparent holes cut out, lit like a
// horizontal leaf (billboards have no meaningful normal), faded per particle and fogged
// exactly like the world so leaves melt into the distance with the terrain.
in vec2 fUv;
flat in uint fLayer;
in vec4 fTint;
in float fSun;
in float fDist;

uniform sampler2DArray uAtlas;
uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform vec3 uAmbient;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;

out vec4 FragColor;

void main() {
    vec4 tex = texture(uAtlas, vec3(fUv, float(fLayer)));
    if (tex.a < 0.5) discard;

    float ndl = clamp(uSunDir.y, 0.0, 1.0);
    vec3 light = uAmbient * (0.12 + 0.88 * fSun) + uSunColor * (fSun * uSunIntensity * (0.35 + 0.65 * ndl));
    vec3 col = tex.rgb * fTint.rgb * light;

    float f = clamp((fDist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    col = mix(col, uFogColor, f * f);

    FragColor = vec4(col, fTint.a);
}
