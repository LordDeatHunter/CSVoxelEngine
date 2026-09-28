#version 460 core

uniform vec3 uColor;
uniform float uOpacity;

// Face highlight only (uDisplayMix 1): uColor is sRGB and the mix happens in display space, the
// way it looked before the scene went linear. Blending it into the linear HDR target instead
// lifts a dark face far more (15% of a pale grey over coal read as a flat grey slab). uScene is
// the HDR target this pass is drawing into, read texel-for-texel after a glTextureBarrier, so
// blending is off for this draw. The outline (uDisplayMix 0) stays an ordinary linear blend.
uniform int uDisplayMix;
uniform sampler2D uScene;

out vec4 FragColor;

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

void main() {
    if (uDisplayMix == 0) {
        FragColor = vec4(uColor, uOpacity);
        return;
    }
    vec3 dst = texelFetch(uScene, ivec2(gl_FragCoord.xy), 0).rgb;
    // The tonemap is the identity below its knee at exposure 1, so the encode alone is the
    // display value there; anything over 1 keeps its overflow, faded like the rest.
    vec3 body = min(dst, vec3(1.0));
    vec3 mixed = SrgbToLinear(mix(LinearToSrgb(body), uColor, uOpacity));
    FragColor = vec4(mixed + (dst - body) * (1.0 - uOpacity), 1.0);
}
