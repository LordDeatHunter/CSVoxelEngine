#version 460 core
// The frame's resolve pass: reads the linear RGBA16F scene target RenderManager renders the
// world into and writes the gamma default framebuffer, in this order: exposure, a per-channel
// clamp to 0..1, the exact sRGB encode, then the frame's one half-LSB dither (the per-shader
// dithers are gone; everything before this pass is float).
// The clamp is what the old 8-bit target did to every over-bright channel, so sunlit snow and
// white sand still clip to white and lava to yellow; a hue-preserving max-channel shoulder was
// tried and kept those saturated (cream snow, orange sand, red lava), which read worse.
// C# twin: Tonemap.cs (Tonemap.Apply); keep the two in step.
// FXAA (SH-9) runs unconditionally between the tonemap and the dither, on display values:
// every tap goes through ToDisplay first, so the edge search and the blend see the image the
// way it will be shown.
in vec2 fUv;

uniform sampler2D uScene;
uniform float uExposure;
uniform vec2 uResolution;      // scene texture size in pixels; taps clamp to it
uniform float uFxaaReduceMin;  // WorldGenerationConfig.FxaaReduceMin
uniform float uFxaaReduceMul;  // WorldGenerationConfig.FxaaReduceMul
uniform float uFxaaSpanMax;    // WorldGenerationConfig.FxaaSpanMax, in pixels
uniform float uFxaaEdgeThreshold;     // WorldGenerationConfig.FxaaEdgeThreshold
uniform float uFxaaEdgeThresholdMin;  // WorldGenerationConfig.FxaaEdgeThresholdMin
// The biome colour grade (BiomeGrade): a faint display-space multiplier over the finished image,
// warm over the desert and cool over the snow; (1, 1, 1) leaves the image alone.
uniform vec3 uGrade;
// The damage chromatic fringe (DamageFringe): for a moment after a hit the screen edges split into
// red and blue, read up to uFringeShare of the screen height apart at the corners. 0 draws none.
uniform float uDamageFringe;
uniform float uFringeShare;
// The underwater wobble (UnderwaterWobble): with the eye under water every pixel is read from a point
// swayed by two slow waves across the screen, and FXAA reads around that same point. 0 draws none.
uniform float uUnderwater;
uniform float uTime;          // seconds, wrapped on the CPU
uniform float uWobbleShare;   // most a pixel sways, as a share of the screen height
uniform float uWobbleSpeed;
uniform float uWobbleWaves;   // waves across the screen

out vec4 FragColor;

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
    return LinearToSrgb(clamp(hdr * uExposure, 0.0, 1.0)); // Tonemap.Apply's twin
}

// Cheap static value hash (vignette-fragment.glsl's Hash) for the output dither.
float Hash12(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

// One scene texel, tonemapped and encoded; out-of-range taps clamp to the edge texel.
vec3 DisplayAt(ivec2 p)
{
    return ToDisplay(texelFetch(uScene, clamp(p, ivec2(0), ivec2(uResolution) - 1), 0).rgb);
}

// Manual bilinear filter at pixelPos (pixel units, texel centres at .5). Each of the four texels
// is converted to display space before they are mixed, so a filtered tap is an average of
// tonemapped, encoded values. A hardware bilinear sample of the HDR buffer would instead average
// a highlight far above 1 with its dark neighbours before the tonemap, and the clamp would
// turn that mix into a bright halo around the sun disc, lava and ore highlights.
vec3 DisplayBilinear(vec2 pixelPos)
{
    vec2 p = pixelPos - 0.5;
    vec2 i = floor(p);
    vec2 f = p - i;
    ivec2 b = ivec2(i);
    vec3 c00 = DisplayAt(b);
    vec3 c10 = DisplayAt(b + ivec2(1, 0));
    vec3 c01 = DisplayAt(b + ivec2(0, 1));
    vec3 c11 = DisplayAt(b + ivec2(1, 1));
    return mix(mix(c00, c10, f.x), mix(c01, c11, f.x), f.y);
}

float Luma(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }

// Classic single-pass FXAA (Lottes, PC/console variant) in pixel units, with FXAA 3.11's
// contrast early-out: the four half-pixel
// corner taps are 2x2 averages that find the local edge direction, then the pixel is blended
// along it from two (rgbA) or four (rgbB) filtered taps, falling back to the narrower blend
// when the wider one overshoots the neighbourhood's luma range (it crossed another edge).
vec3 Fxaa(vec2 pos)
{
    // The four corner taps DisplayBilinear(pos +- 0.5) land exactly between texel centres, so
    // each is the plain mean of a 2x2 block, and together they cover just the 3x3 neighbourhood:
    // fetch those nine once instead of sixteen times (same result, fewer ToDisplay calls).
    ivec2 c = ivec2(pos);
    vec3 t00 = DisplayAt(c + ivec2(-1, -1)), t10 = DisplayAt(c + ivec2(0, -1)), t20 = DisplayAt(c + ivec2(1, -1));
    vec3 t01 = DisplayAt(c + ivec2(-1,  0)), rgbM = DisplayAt(c),              t21 = DisplayAt(c + ivec2(1,  0));
    vec3 t02 = DisplayAt(c + ivec2(-1,  1)), t12 = DisplayAt(c + ivec2(0,  1)), t22 = DisplayAt(c + ivec2(1,  1));
    vec3 rgbNW = 0.25 * (t00 + t10 + t01 + rgbM);
    vec3 rgbNE = 0.25 * (t10 + t20 + rgbM + t21);
    vec3 rgbSW = 0.25 * (t01 + rgbM + t02 + t12);
    vec3 rgbSE = 0.25 * (rgbM + t21 + t12 + t22);

    float lumaNW = Luma(rgbNW);
    float lumaNE = Luma(rgbNE);
    float lumaSW = Luma(rgbSW);
    float lumaSE = Luma(rgbSE);
    float lumaM  = Luma(rgbM);
    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    // FXAA 3.11's local-contrast early-out: a neighbourhood whose luma range is below the
    // threshold holds no edge worth smoothing, so it keeps its own pixel and skips the sixteen
    // direction-tap fetches. Most of a frame (sky, flat faces, fog) takes this branch, which is
    // what keeps the pass near a millisecond at 1080p; it also stops low-contrast texture
    // detail from being blurred along a false edge.
    // A swayed point (the underwater wobble) sits between texel centres, so it keeps its exact
    // filtered colour instead of the nearest texel's, or the sway would step a whole pixel at a time
    if (lumaMax - lumaMin < max(uFxaaEdgeThresholdMin, lumaMax * uFxaaEdgeThreshold))
        return uUnderwater > 0.0 ? DisplayBilinear(pos) : rgbM;

    vec2 dir;
    dir.x = -((lumaNW + lumaNE) - (lumaSW + lumaSE));
    dir.y =  ((lumaNW + lumaSW) - (lumaNE + lumaSE));

    float dirReduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * (0.25 * uFxaaReduceMul),
                          uFxaaReduceMin);
    float rcpDirMin = 1.0 / (min(abs(dir.x), abs(dir.y)) + dirReduce);
    dir = clamp(dir * rcpDirMin, vec2(-uFxaaSpanMax), vec2(uFxaaSpanMax));

    vec3 rgbA = 0.5 * (DisplayBilinear(pos + dir * (1.0 / 3.0 - 0.5)) +
                       DisplayBilinear(pos + dir * (2.0 / 3.0 - 0.5)));
    vec3 rgbB = rgbA * 0.5 + 0.25 * (DisplayBilinear(pos + dir * -0.5) +
                                     DisplayBilinear(pos + dir * 0.5));
    float lumaB = Luma(rgbB);
    return (lumaB < lumaMin || lumaB > lumaMax) ? rgbA : rgbB;
}

void main()
{
    // The underwater wobble moves the point this pixel shows (UnderwaterWobble.Offset); everything
    // below, FXAA included, reads around that point
    vec2 pos = gl_FragCoord.xy;
    if (uUnderwater > 0.0) {
        vec2 uv = gl_FragCoord.xy / uResolution;
        pos += vec2(sin(uv.y * uWobbleWaves * 6.2831853 + uTime * uWobbleSpeed),
                    cos(uv.x * uWobbleWaves * 6.2831853 + uTime * uWobbleSpeed * 0.83))  // VerticalSpeedShare
             * (uUnderwater * uWobbleShare * uResolution.y);
    }
    vec3 c = Fxaa(pos);
    // The damage fringe: red and blue read from either side of the pixel along the line from the
    // screen centre, nothing in the middle and the most at the corners (DamageFringe.EdgeWeight)
    if (uDamageFringe > 0.0) {
        vec2 fromCentre = gl_FragCoord.xy / uResolution - 0.5;
        float d = length(fromCentre);
        vec2 shift = fromCentre / max(d, 1e-4)
                   * (uDamageFringe * smoothstep(0.2, 0.7, d) * uFringeShare * uResolution.y);
        c.r = DisplayBilinear(pos + shift).r;
        c.b = DisplayBilinear(pos - shift).b;
    }
    // The grade after the anti-aliasing, so FXAA keeps judging edges on the image itself
    c = min(c * uGrade, vec3(1.0));
    // Half-LSB dither before the 8-bit write, so long fog, sky and lighting gradients break up
    // instead of banding. Static per pixel (no time term), so it never crawls. It runs after
    // FXAA so the blur never smooths the dither away.
    c += (Hash12(gl_FragCoord.xy) - 0.5) / 255.0;
    FragColor = vec4(c, 1.0);
}
