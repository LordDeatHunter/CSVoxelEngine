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

// The water pass's reconstruction, verbatim (test-fragment.glsl).
vec3 Reconstruct(vec2 uv, float depth) {
    vec4 p = uInvViewProj * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    return p.xyz / p.w;
}

float Crease(vec2 uv, float dC, float dA, float dB) {
    if (dA >= 1.0 || dB >= 1.0) return 0.0;
    return distance(Reconstruct(uv, dC), uCameraPos)
         - distance(Reconstruct(uv, 0.5 * (dA + dB)), uCameraPos);
}

float Weight(float c) {
    return smoothstep(uMinBlocks, uPeakBlocks, c) * (1.0 - smoothstep(uPeakBlocks, uRangeBlocks, c));
}

void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    float dC = texelFetch(uDepth, p, 0).r;
    if (dC >= 1.0) { FragColor = vec4(1.0); return; } // sky

    vec2 uv = gl_FragCoord.xy / uResolution;
    float dist = distance(Reconstruct(uv, dC), uCameraPos);
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

    float wH = Weight(Crease(uv, dC, dL, dR));
    float wV = Weight(Crease(uv, dC, dD, dU));
    float occ = uStrength * max(wH, wV) * fade;
    FragColor = vec4(vec3(1.0 - occ), 1.0);
}
