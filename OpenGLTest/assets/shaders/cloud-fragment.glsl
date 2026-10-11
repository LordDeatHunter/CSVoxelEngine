#version 460 core
// Near-flat cartoon shading: uniform skydome light plus a small sun term for form, then a
// normal band that keeps tops white, sides bright and bottoms cool grey. The faces are flat
// and axis-aligned, so the band reduces to three tones and the silhouette stays hard-edged.
// Alpha fades with fog on top of the colour blend, so distant clouds thin into the sky
// instead of ending in a visible band at the far plane.
// Everything is done in display space, the way it looked before the scene went linear: the
// light is summed from the display-space sun and ambient, fogged and then mixed over the scene
// behind it by hand. uScene is the HDR target this pass draws into, read texel-for-texel after a
// glTextureBarrier with blending off; the depth pre-pass in CloudRenderer lets exactly one cloud
// surface through per pixel, which is what makes that read legal. Blending the same colour in
// linear light left the clouds opaque slabs that hid the ground and sky texture behind them.
in vec3 fWorldPos;
in vec3 fNormal;

uniform vec3 uSunDir;
// Display-space (sRGB) light, the pre-SH-10 values; unlike the world's uniforms, not linear.
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
uniform sampler2D uScene;     // the target being drawn into (HDR scene, or the mirror)

// The silver lining (CloudSilverLining is the C# twin): thin cloud edges near the sun light up.
// uSilverStrength is the frame's strength (0 with the sun at or below the horizon, which skips it
// all); the thinness is read along the view ray from the cloud-shadow coverage map on unit 8, cell
// by cell like the stars' cloud cover in sky-fragment.glsl.
uniform float uSilverStrength;
uniform float uSilverExponent;
uniform float uSilverStep;    // blocks between the thinness samples
uniform sampler2D uCloudShadowMap;
uniform int uCloudCells;      // texels per side of the map
uniform int uCloudMaxLevels;
uniform float uCloudHeight;
uniform float uCloudLayer;    // one layer's thickness
uniform float uCloudField;
uniform vec2 uCloudDrift;

const int SilverSamples = 6;  // CloudSilverLining.Samples

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

// Exact piecewise sRGB encode, the twin of ColorSpace.LinearToSrgb (and post-fragment.glsl's).
vec3 LinearToSrgb(vec3 c)
{
    c = max(c, 0.0);
    vec3 lo = c * 12.92;
    vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
    return mix(hi, lo, lessThanEqual(c, vec3(0.0031308)));
}

// Twin of ColorSpace.SrgbToLinear
vec3 SrgbToLinear(vec3 c)
{
    vec3 lo = c / 12.92;
    vec3 hi = pow((c + 0.055) / 1.055, vec3(2.4));
    return mix(hi, lo, lessThanEqual(c, vec3(0.04045)));
}

// How many layers the field cell under world column p stacks; twin of CloudCover.LevelAt (and of
// sky-fragment.glsl's CloudLevelAt). Wrapped by hand like the copies (texelFetch ignores REPEAT).
int CloudLevelAt(vec2 p)
{
    ivec2 cell = ivec2(floor((p - uCloudDrift) / uCloudField * float(uCloudCells)));
    cell = ((cell % uCloudCells) + uCloudCells) % uCloudCells;
    return int(round(texelFetch(uCloudShadowMap, cell, 0).r * float(uCloudMaxLevels)));
}

// How thin the cloud is behind surface point p along the view ray v, 0..1: the share of the
// samples past p that are out of the cloud volume. Twin of CloudSilverLining.Thinness.
float CloudThinness(vec3 p, vec3 v)
{
    int inside = 0;
    for (int i = 1; i <= SilverSamples; i++) {
        vec3 q = p + v * (float(i) * uSilverStep);
        float top = uCloudHeight + float(CloudLevelAt(q.xz)) * uCloudLayer;
        if (q.y > uCloudHeight && q.y < top) inside++;
    }
    return 1.0 - float(inside) / float(SilverSamples);
}

out vec4 FragColor;

void main() {
    vec3 N = normalize(fNormal);
    float ndl = dot(N, uSunDir) * 0.5 + 0.5;
    float dist = distance(fWorldPos, uCameraPos);
    vec3 V = (fWorldPos - uCameraPos) / max(dist, 1e-4);
    vec3 light = uAmbient * 1.2 + uSunColor * (uSunIntensity * (0.26 + 0.04 * ndl));
    float up = smoothstep(-0.9, 0.15, N.y);
    vec3 col = light * mix(vec3(0.93, 0.94, 0.98), vec3(1.03), up);
    // The silver lining (CloudSilverLining.Weight): only computed near the sun, where the
    // forward-scatter lobe is non-zero, so most cloud pixels skip the thinness reads.
    if (uSilverStrength > 0.0) {
        float lobe = pow(max(dot(V, uSunDir), 0.0), uSilverExponent);
        if (lobe > 1e-3)
            col += uSunColor * (uSunIntensity * uSilverStrength * lobe * CloudThinness(fWorldPos, V));
    }
    col = min(col, vec3(1.0));
    float f = clamp((dist - uFogNear) / (uFogFar - uFogNear), 0.0, 1.0);
    col = mix(col, LinearToSrgb(min(mix(SkySample(V), uFogColor, uFogWet), vec3(1.0))), f * f);
    float a = uOpacity * (1.0 - f * f * f);
    // The tonemap is a plain clamp at exposure 1, so the encode alone is the display value of
    // anything up to 1. The background is clamped to that range before the mix: the sun disc is
    // additively drawn well above 1 in the target behind us, and preserving that overflow here
    // would add a fraction ((1 - a) of 2.5x, never zero while the cloud is translucent) of pure
    // sun on top of the cloud body, saturating it and making the sun read as if it stood in front
    // of the cloud. The cloud owns its own sun term (uSunColor/uAmbient below), so it must occlude
    // the disc rather than leak it; the post pass clamps the rest of the scene anyway.
    vec3 dst = texelFetch(uScene, ivec2(gl_FragCoord.xy), 0).rgb;
    vec3 body = min(dst, vec3(1.0));
    FragColor = vec4(SrgbToLinear(mix(LinearToSrgb(body), col, a)), 1.0);
}
