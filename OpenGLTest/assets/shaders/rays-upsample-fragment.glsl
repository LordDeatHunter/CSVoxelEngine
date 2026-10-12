#version 460 core
// Depth-aware upsample for the underwater sun shafts. The march in rays-fragment.glsl now
// runs into a half-resolution RGBA16F target (rgb = additive shaft color, a = the world-space
// distance in blocks that half-res pixel marched, capped at uMaxDistance); this pass draws one
// full-resolution additive full-screen triangle that reconstructs the shafts at native
// resolution. The half-res target covers exactly the same NDC region as the screen, so screen UV
// maps onto it as the identity - there is no UV scale factor and no partial-texel edge case.
//
// A plain bilinear fetch would be wrong here: the shaft term is ADDITIVE and its magnitude jumps
// at a silhouette (a near seabed marches a few blocks, the open water behind it marches the full
// uMaxDistance), so bilinear would smear the far term over the near geometry as a halo. Instead
// each of the four half-res taps is weighted by how closely the distance IT marched matches the
// distance THIS full-res pixel marches, which stops the bleed across depth discontinuities.
//
// The shafts are display-space light, added to the ENCODED scene and clipped at 1 the way the
// pre-SH-10 8-bit target did, then decoded again: added straight onto the linear HDR target the
// same march read far weaker toward the sun and grayed the water elsewhere. uScene is the HDR
// target this pass draws into, read texel-for-texel after a glTextureBarrier with blending off
// (one full-screen triangle writes each pixel once).
in vec2 fUv;
uniform sampler2D uRays;        // unit 5: the half-res march output (rgb = shafts, a = marched distance)
uniform sampler2D uSceneDepth;  // unit 4: the same full-res scene depth copy the march read
uniform mat4 uInvViewProj;
uniform vec3 uCameraPos;
uniform float uMaxDistance;
uniform float uTolerance;       // blocks of marched-distance mismatch before a tap stops contributing
uniform sampler2D uScene;       // the HDR target being drawn into
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
    // Reconstruct this full-res pixel's own marched distance exactly the way the march does
    // (rays-fragment.glsl:37-40): unproject through the inverse of the very matrix that wrote the
    // depth, so the projection's clip-depth convention cancels out. The result is a distance in
    // BLOCKS, not a linearized depth - the C# projection is Direct3D-style 0..1 and no near/far
    // linearization would be correct. Comparing marched distances (rather than raw window depth)
    // is what makes the bilateral weight below physically meaningful and resolution-independent.
    float depth = texture(uSceneDepth, fUv).r;
    vec4 h = uInvViewProj * vec4(fUv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    float len = min(length(h.xyz / h.w - uCameraPos), uMaxDistance);

    // The 2x2 half-res neighborhood a bilinear tap at fUv would blend, fetched explicitly so the
    // bilinear weights can be reweighted per tap. textureSize means no texel-size uniform.
    ivec2 size = textureSize(uRays, 0);
    vec2 t = fUv * vec2(size) - 0.5;
    ivec2 b = ivec2(floor(t));
    vec2 f = t - vec2(b);
    ivec2 hi = size - 1;
    vec4 taps[4];
    taps[0] = texelFetch(uRays, clamp(b + ivec2(0, 0), ivec2(0), hi), 0);
    taps[1] = texelFetch(uRays, clamp(b + ivec2(1, 0), ivec2(0), hi), 0);
    taps[2] = texelFetch(uRays, clamp(b + ivec2(0, 1), ivec2(0), hi), 0);
    taps[3] = texelFetch(uRays, clamp(b + ivec2(1, 1), ivec2(0), hi), 0);
    float bw[4];
    bw[0] = (1.0 - f.x) * (1.0 - f.y);
    bw[1] = f.x * (1.0 - f.y);
    bw[2] = (1.0 - f.x) * f.y;
    bw[3] = f.x * f.y;

    vec3 weighted = vec3(0.0);
    float wsum = 0.0;
    vec3 best = vec3(0.0);
    float bestD = 1e30;
    for (int i = 0; i < 4; i++) {
        vec4 s = taps[i];
        // A tap whose alpha says it marched to a very different distance saw the other side of a
        // silhouette; its shaft color belongs to different geometry and must not bleed across.
        float d = abs(s.a - len);
        float w = bw[i] * exp(-d / uTolerance);
        weighted += s.rgb * w;
        wsum += w;
        if (d < bestD) { bestD = d; best = s.rgb; }
    }

    // The weights are normalized, so the common 0-to-a-few-blocks mismatch is just a soft
    // bilateral blend. Since the bilinear weights sum to 1, wsum only collapses below 1e-4 when
    // EVERY one of the four taps mismatches by more than roughly uTolerance * ln(1e4) (about 18
    // blocks at the default tolerance) - i.e. a thin full-res feature that no half-res sample
    // landed on. There the geometrically right answer is the single closest-matching tap (a
    // nearest-depth upsample), not a bilinear blend of four taps that are all wrong.
    vec3 rays = wsum > 1e-4 ? weighted / wsum : best;
    // The tonemap is a plain clamp at exposure 1, so the encode alone is the display value of
    // anything up to 1; whatever lies above keeps its overflow untouched.
    vec3 dst = texelFetch(uScene, ivec2(gl_FragCoord.xy), 0).rgb;
    vec3 body = min(dst, vec3(1.0));
    FragColor = vec4(SrgbToLinear(min(LinearToSrgb(body) + rays, vec3(1.0))) + (dst - body), 1.0);
}
