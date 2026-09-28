#version 460 core
// The frame's resolve pass: reads the linear RGBA16F scene target RenderManager renders the
// world into and writes the gamma default framebuffer, in this order: exposure, a hue-preserving
// Reinhard shoulder on the max channel, the exact sRGB encode, then the frame's one half-LSB
// dither (the per-shader dithers are gone; everything before this pass is float).
// The shoulder is the identity below uKnee, so darks and mid-tones pass through unchanged and
// only highlights roll off toward 1 (C1 at the knee). At the default knee 0.8 it maps
// 0.05 -> 0.05, 0.2 -> 0.2, 0.5 -> 0.5 and 1.0 -> 0.9, and never reaches 1.
// C# twin: Tonemap.cs (Tonemap.Apply / Tonemap.Shoulder); keep the two in step.
in vec2 fUv;

uniform sampler2D uScene;
uniform float uExposure;
uniform float uKnee;

out vec4 FragColor;

// Tonemap.Apply's twin: scale rgb by Shoulder(max)/max, so the hue survives the roll-off.
vec3 Shoulder(vec3 hdr)
{
    float m = max(hdr.r, max(hdr.g, hdr.b));
    if (m <= uKnee || m <= 1e-6) return hdr;
    float x = (m - uKnee) / (1.0 - uKnee);
    float f = uKnee + (1.0 - uKnee) * x / (1.0 + x);
    return hdr * (f / m);
}

// Exact piecewise sRGB encode, the twin of ColorSpace.LinearToSrgb.
vec3 LinearToSrgb(vec3 c)
{
    c = max(c, 0.0);
    vec3 lo = c * 12.92;
    vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
    return mix(hi, lo, lessThanEqual(c, vec3(0.0031308)));
}

vec3 ToDisplay(vec3 hdr)
{
    // RGBA16F can hold NaN and Inf, which the old 8-bit write clamped away; never let one
    // reach the tonemap (a NaN max channel would poison the whole pixel).
    hdr = any(isnan(hdr)) ? vec3(0.0) : clamp(hdr, 0.0, 64.0);
    return LinearToSrgb(Shoulder(hdr * uExposure));
}

// Cheap static value hash (vignette-fragment.glsl's Hash) for the output dither.
float Hash12(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

void main()
{
    vec3 c = ToDisplay(texelFetch(uScene, ivec2(gl_FragCoord.xy), 0).rgb);
    // Half-LSB dither before the 8-bit write, so long fog, sky and lighting gradients break up
    // instead of banding. Static per pixel (no time term), so it never crawls.
    c += (Hash12(gl_FragCoord.xy) - 0.5) / 255.0;
    FragColor = vec4(c, 1.0);
}
