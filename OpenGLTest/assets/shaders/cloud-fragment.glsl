#version 460 core
// Near-flat cartoon shading: uniform skydome light plus a small sun term for form, then a
// normal band that keeps tops white, sides bright and bottoms cool grey. The faces are flat
// and axis-aligned, so the band reduces to three tones and the silhouette stays hard-edged.
// Alpha fades with fog on top of the colour blend, so distant clouds thin into the sky
// instead of ending in a visible band at the far plane.
in vec3 fWorldPos;
in vec3 fNormal;

uniform vec3 uSunDir;
uniform vec3 uSunColor;
uniform float uSunIntensity;
uniform vec3 uAmbient;
uniform vec3 uFogColor;
uniform float uFogNear;
uniform float uFogFar;
uniform vec3 uCameraPos;
uniform float uOpacity;

out vec4 FragColor;

// Cheap static value hash (vignette-fragment.glsl's Hash) for the output dither.
float Hash12(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

void main() {
    vec3 N = normalize(fNormal);
    float ndl = dot(N, uSunDir) * 0.5 + 0.5;
    vec3 light = uAmbient * 1.2 + uSunColor * (uSunIntensity * (0.26 + 0.04 * ndl));
    float up = smoothstep(-0.9, 0.15, N.y);
    vec3 col = light * mix(vec3(0.93, 0.94, 0.98), vec3(1.03), up);
    float dist = distance(fWorldPos, uCameraPos);
    float f = clamp((dist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    col = mix(col, uFogColor, f * f);
    // Half-LSB dither against banding in the soft fog blend; static per pixel, never crawls.
    col += (Hash12(gl_FragCoord.xy) - 0.5) / 255.0;
    FragColor = vec4(col, uOpacity * (1.0 - f * f * f));
}
