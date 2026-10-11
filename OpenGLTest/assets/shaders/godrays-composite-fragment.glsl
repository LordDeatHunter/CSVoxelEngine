#version 460 core
// Godrays composite: the half-resolution march (godrays-fragment.glsl) blurred radially toward the
// sun's place on screen and added in display space over the scene. The march already holds the
// light scattered along every pixel's ray, bright where the eye looks through long sunlit air (open
// sky past the trees, a gap in the clouds) and dark where a near trunk, crown or cloud cuts the ray
// short or its shadow darkens it; averaging it along the line from each pixel toward the sun
// carries that contrast outward from the sun as streaks, so the shafts read through trees, clouds
// and gaps. The weights fall off along the line (uDecay), so a pixel is lit mostly by what lies just
// sunward of it. Like the underwater shafts the result is display-space light, added to the ENCODED
// scene and clipped at 1, then decoded; uScene is the HDR target this pass draws into, read
// texel-for-texel after a glTextureBarrier with blending off.
in vec2 fUv;
uniform sampler2D uRays;      // unit 5: the half-res march (rgb = scattered light)
uniform sampler2D uScene;     // the HDR target being drawn into
uniform vec2 uSunUv;          // the sun's screen position; may lie off screen
uniform float uBlurLength;    // how much of the way to the sun the blur reaches
uniform float uDecay;         // each sample's weight relative to the one before it
uniform int uSamples;
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

void main()
{
    vec2 step = (uSunUv - fUv) * (uBlurLength / float(uSamples));
    vec2 uv = fUv;
    vec3 sum = vec3(0.0);
    float weight = 1.0;
    float total = 0.0;
    for (int i = 0; i < uSamples; i++) {
        sum += textureLod(uRays, clamp(uv, vec2(0.0), vec2(1.0)), 0.0).rgb * weight;
        total += weight;
        weight *= uDecay;
        uv += step;
    }
    vec3 rays = sum / total;
    vec3 dst = texelFetch(uScene, ivec2(gl_FragCoord.xy), 0).rgb;
    vec3 body = min(dst, vec3(1.0));
    FragColor = vec4(SrgbToLinear(min(LinearToSrgb(body) + rays, vec3(1.0))) + (dst - body), 1.0);
}
