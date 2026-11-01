#version 460 core
// One level of the max-depth pyramid the opaque chunk pass culls against (ChunkOcclusionCuller):
// each texel is the max of its 2x2 source texels, every tap clamped to the source's last row and
// column. Compiled twice: with FROM_DEPTH the source is the HDR scene's depth texture (level 0,
// half the frame padded to a power of two), without it the previous level of the pyramid.
// C# twin: HizPyramid.Build; keep them in step (shader-sync).
layout(local_size_x = 8, local_size_y = 8) in;

#ifdef FROM_DEPTH
uniform sampler2D uDepth;
#else
layout(r32f, binding = 0) readonly uniform image2D uSrc;
#endif
layout(r32f, binding = 1) writeonly uniform image2D uDst;

uniform ivec2 uSrcSize;
uniform ivec2 uDstSize;

float Tap(ivec2 p) {
    p = min(p, uSrcSize - 1);
#ifdef FROM_DEPTH
    return texelFetch(uDepth, p, 0).r;
#else
    return imageLoad(uSrc, p).r;
#endif
}

void main() {
    ivec2 t = ivec2(gl_GlobalInvocationID.xy);
    if (t.x >= uDstSize.x || t.y >= uDstSize.y) return;
    ivec2 s = 2 * t;
    float m = max(max(Tap(s), Tap(s + ivec2(1, 0))), max(Tap(s + ivec2(0, 1)), Tap(s + ivec2(1, 1))));
    imageStore(uDst, t, vec4(m, 0.0, 0.0, 0.0));
}
