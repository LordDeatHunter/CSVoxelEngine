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
uniform vec3 uSkyZenith;
uniform vec3 uSkyHorizon;
uniform vec3 uSkyGlow;        // dusk-scaled on the CPU: zero at noon and at night
uniform vec4 uSkyShape;       // x horizon band, y haze width, z haze strength, w glow exponent
uniform float uFogWet;        // 1 underwater/in lava: fog falls back to the flat tinted uFogColor
uniform vec3 uCloudLight;     // ambient weight, sun base, sun ndl (SkyPalette.CloudLightWeights)

// Twin copies: sky-fragment.glsl, test-fragment.glsl, cloud-fragment.glsl; C# reference SkyPalette.Sample - change all four together
vec3 SkySample(vec3 dir)
{
    float e = max(dir.y, 0.0);
    float az = (dir.x * uSunDir.x + dir.z * uSunDir.z)
             / sqrt(max((dir.x * dir.x + dir.z * dir.z) * (uSunDir.x * uSunDir.x + uSunDir.z * uSunDir.z), 1e-6));
    float band = smoothstep(0.0, uSkyShape.x, e);
    float lobe = pow(max(az, 0.0), uSkyShape.w) * (1.0 - band);
    float haze = uSkyShape.z * (1.0 - smoothstep(0.0, uSkyShape.y, e));
    return mix(uSkyHorizon * (1.0 + haze), uSkyZenith, band) + uSkyGlow * lobe;
}

out vec4 FragColor;

void main() {
    vec3 N = normalize(fNormal);
    float ndl = dot(N, uSunDir) * 0.5 + 0.5;
    float dist = distance(fWorldPos, uCameraPos);
    vec3 V = (fWorldPos - uCameraPos) / max(dist, 1e-4);
    vec3 light = uAmbient * uCloudLight.x + uSunColor * (uSunIntensity * (uCloudLight.y + uCloudLight.z * ndl));
    // Sunset rim: the dome's glow lobe toward the sun's azimuth. uSkyGlow is already dusk-scaled,
    // so the rim fades exactly like the dome's lobe and needs no dusk factor of its own.
    float az = dot(V.xz, uSunDir.xz) * inversesqrt(max(dot(V.xz, V.xz) * dot(uSunDir.xz, uSunDir.xz), 1e-6));
    light += uSkyGlow * pow(max(az, 0.0), uSkyShape.w) * ndl;
    float up = smoothstep(-0.9, 0.15, N.y);
    // The old sRGB albedo mix(vec3(0.93, 0.94, 0.98), vec3(1.03), up), decoded to linear light
    vec3 col = light * mix(vec3(0.855, 0.872, 0.955), vec3(1.069), up);
    float f = clamp((dist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    col = mix(col, mix(SkySample(V), uFogColor, uFogWet), f * f);
    FragColor = vec4(col, uOpacity * (1.0 - f * f * f));
}
