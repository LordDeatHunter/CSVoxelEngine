#version 460 core
// Screen-space contact AO (SH-28). GL-free twin: ContactAo.cs - keep the two in step.
//
// Drawn full screen into the HDR scene colour with blend (Zero, SrcColor), so the output is a
// multiplier: 1 leaves the pixel alone, 1 - occ darkens it. It reads the HDR scene depth (never
// attached to the framebuffer this pass draws into, so there is no feedback loop).
//
// The measure is a crease: for each opposite tap pair (left/right, down/up) the midpoint of the
// two raw window depths is reconstructed on the centre pixel's own ray and compared with the
// centre. Raw depth is affine in screen space over a plane (a + b/w, and 1/w is affine over a
// plane), so on any flat surface that midpoint is exactly the centre depth and planes never
// darken at any viewing angle. A concave corner puts the centre farther than the midpoint
// (positive crease, in blocks); convex edges go negative and silhouettes against far terrain jump
// past uRangeBlocks, and both are ignored.
//
// A thin object standing in front of the ground (a torch, a drop, a mob) puts one tap of a ground
// pixel a tap radius beside it on the object, which creases exactly like a corner and would draw a
// displaced copy of its silhouette. So the crease is split between the two taps along the centre
// surface's own slope, read from its 1-pixel neighbours: a tap in front of that surface is an
// occluder, and it only counts while its surface lies within uReachNear..uReachFar tap radii of the
// centre point. A wall or pebble rising beside the centre is that close; the part of a post well
// above the ground behind it is not. With every occluder in reach the crease is exactly the plain
// midpoint crease.
in vec2 fUv;
out vec4 FragColor;

uniform sampler2D uDepth;
uniform mat4 uInvViewProj;
uniform vec3 uCameraPos;
uniform vec2 uResolution;
// Viewport height / (2 tan(fovY / 2)): a block at distance 1 spans this many pixels.
uniform float uPxPerBlock;
uniform float uStrength;
uniform float uMinBlocks;
uniform float uPeakBlocks;
uniform float uRangeBlocks;
uniform float uFadeNear;
uniform float uFadeFar;
uniform float uRadiusBlocks;
uniform float uMaxPx;
// Occluder reach, in tap radii: full up to uReachNear, gone past uReachFar.
uniform float uReachNear;
uniform float uReachFar;

// The water pass's reconstruction, verbatim (test-fragment.glsl).
vec3 Reconstruct(vec2 uv, float depth) {
    vec4 p = uInvViewProj * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    return p.xyz / p.w;
}

// How fully an occluding tap counts: 1 while the surface it sees lies within uReachNear tap radii
// of the centre point, 0 past uReachFar.
float Reach(vec2 tapUv, float dTap, vec3 centre, float tapBlocks) {
    float d = distance(Reconstruct(tapUv, dTap), centre);
    return 1.0 - smoothstep(uReachNear * tapBlocks, uReachFar * tapBlocks, d);
}

// The raw-depth crease 2 dC - dA - dB with each occluding tap's share scaled by its reach, the
// shares split along the centre surface's raw-depth change over one tap offset: a tap in front of
// that surface is an occluder, one behind it is not. With both reaches 1 it is the plain crease.
float Held(float dC, float dA, float dB, float slope, float reachA, float reachB) {
    float devA = dC - slope - dA;
    float devB = dC + slope - dB;
    return (devA > 0.0 ? devA * reachA : devA) + (devB > 0.0 ? devB * reachB : devB);
}

// How much farther, in blocks, the centre lies than the midpoint of taps A (uv - tapUv) and
// B (uv + tapUv) along the centre ray; dMinus1/dPlus1 are the centre's 1-pixel neighbours on the
// same axis and tapPx the tap offset in pixels.
float Crease(vec2 uv, float dC, vec3 centre, float centreDist, vec2 tapUv, float dA, float dB,
             float dMinus1, float dPlus1, float tapPx, float tapBlocks) {
    if (dA >= 1.0 || dB >= 1.0 || tapPx <= 0.0) return 0.0;
    float reachA = Reach(uv - tapUv, dA, centre, tapBlocks);
    float reachB = Reach(uv + tapUv, dB, centre, tapBlocks);
    // The centre surface's slope, read from one neighbour or the other. A slope read across a
    // silhouette right beside the centre misses its own side's tap by the whole jump, so the one
    // that carries the surface out to its tap wins; at the very pixel of a crease both do (each
    // neighbour on its own face), and the stronger crease is kept.
    float minusSlope = (dC - dMinus1) * tapPx;
    float plusSlope = (dPlus1 - dC) * tapPx;
    float minusMiss = abs(dC - minusSlope - dA);
    float plusMiss = abs(dC + plusSlope - dB);
    float tolerance = min(minusMiss, plusMiss) + 0.25 * abs(2.0 * dC - dA - dB);
    float held = -3.4e38;
    if (minusMiss <= tolerance) held = Held(dC, dA, dB, minusSlope, reachA, reachB);
    if (plusMiss <= tolerance) held = max(held, Held(dC, dA, dB, plusSlope, reachA, reachB));
    return centreDist - distance(Reconstruct(uv, dC - 0.5 * held), uCameraPos);
}

float Weight(float c) {
    return smoothstep(uMinBlocks, uPeakBlocks, c) * (1.0 - smoothstep(uPeakBlocks, uRangeBlocks, c));
}

void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    float dC = texelFetch(uDepth, p, 0).r;
    if (dC >= 1.0) { FragColor = vec4(1.0); return; } // sky

    vec2 uv = gl_FragCoord.xy / uResolution;
    vec3 centre = Reconstruct(uv, dC);
    float dist = distance(centre, uCameraPos);
    float fade = 1.0 - smoothstep(uFadeNear, uFadeFar, dist);
    if (fade <= 0.0) { FragColor = vec4(1.0); return; }

    int r = int(round(clamp(uRadiusBlocks * uPxPerBlock / max(dist, 1e-4), 1.0, uMaxPx)));
    // Shrink the radius symmetrically at the screen edge: a lopsided pair would break the
    // midpoint's plane invariance.
    ivec2 size = ivec2(uResolution);
    int rx = min(r, min(p.x, size.x - 1 - p.x));
    int ry = min(r, min(p.y, size.y - 1 - p.y));

    float dL = texelFetch(uDepth, p - ivec2(rx, 0), 0).r;
    float dR = texelFetch(uDepth, p + ivec2(rx, 0), 0).r;
    float dD = texelFetch(uDepth, p - ivec2(0, ry), 0).r;
    float dU = texelFetch(uDepth, p + ivec2(0, ry), 0).r;

    // The 1-pixel neighbours, clamped to the screen (a clamped one reads the centre: slope 0).
    ivec2 lo = max(p - 1, ivec2(0));
    ivec2 hi = min(p + 1, size - 1);
    float dL1 = texelFetch(uDepth, ivec2(lo.x, p.y), 0).r;
    float dR1 = texelFetch(uDepth, ivec2(hi.x, p.y), 0).r;
    float dD1 = texelFetch(uDepth, ivec2(p.x, lo.y), 0).r;
    float dU1 = texelFetch(uDepth, ivec2(p.x, hi.y), 0).r;

    vec2 tapPx = vec2(rx, ry);
    vec2 tapUv = tapPx / uResolution;
    vec2 tapBlocks = tapPx * dist / uPxPerBlock;
    float wH = Weight(Crease(uv, dC, centre, dist, vec2(tapUv.x, 0.0), dL, dR, dL1, dR1, tapPx.x, tapBlocks.x));
    float wV = Weight(Crease(uv, dC, centre, dist, vec2(0.0, tapUv.y), dD, dU, dD1, dU1, tapPx.y, tapBlocks.y));
    float occ = uStrength * max(wH, wV) * fade;
    FragColor = vec4(vec3(1.0 - occ), 1.0);
}
