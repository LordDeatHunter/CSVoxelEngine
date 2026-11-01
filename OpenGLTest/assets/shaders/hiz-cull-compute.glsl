#version 460 core
// The occlusion cull of the opaque chunk pass (ChunkOcclusionCuller): for every not yet culled
// command of a range, projects the chunk's box (grown by the reach of its drawn vertices) with the
// very matrix the chunk vertex stage draws with, and zeroes the command's InstanceCount in place
// when the box's nearest window depth lies strictly behind the max-depth pyramid over its
// outward-rounded screen rect. Nothing else is written, so the draw order is untouched.
// C# twin: HizOcclusion.TryScreenBounds / IsOccluded and HizPyramid.LevelFor / MaxOver; keep them
// in step (shader-sync).
layout(local_size_x = 64) in;

struct ChunkDraw { vec4 originAndAlpha; };
layout(std430, binding = 0) readonly buffer ChunkInfo { ChunkDraw draws[]; };
// The chunk indirect ring, five uints per DrawElementsIndirectCommand, InstanceCount at +1.
layout(std430, binding = 2) buffer Commands { uint cmds[]; };

uniform int uFirst;
uniform int uCount;
uniform mat4 uViewProj;
uniform float uReach;
uniform float uChunkSize;
uniform ivec2 uViewport;
uniform int uLevels;
uniform float uDepthMargin;
uniform float uMinClipW;
uniform float uScreenClamp;
uniform int uSafety;
uniform sampler2D uHiz;

void main() {
    int i = int(gl_GlobalInvocationID.x);
    if (i >= uCount) return;
    int idx = uFirst + i;
    if (cmds[idx * 5 + 1] == 0u) return;

    vec3 origin = draws[idx].originAndAlpha.xyz;
    vec3 mn = origin - vec3(uReach);
    vec3 mx = origin + vec3(uChunkSize + uReach);

    vec2 lo = vec2(3.0e38);
    vec2 hi = vec2(-3.0e38);
    float near = 3.0e38;
    for (int k = 0; k < 8; k++) {
        vec3 p = vec3((k & 1) != 0 ? mx.x : mn.x, (k & 2) != 0 ? mx.y : mn.y, (k & 4) != 0 ? mx.z : mn.z);
        vec4 c = uViewProj * vec4(p, 1.0);
        // A corner at or behind the eye plane: the box must be drawn.
        if (!(c.w > uMinClipW)) return;
        vec2 s = clamp((c.xy / c.w * 0.5 + 0.5) * vec2(uViewport), vec2(-uScreenClamp), vec2(uScreenClamp));
        lo = min(lo, s);
        hi = max(hi, s);
        near = min(near, 0.5 * c.z / c.w + 0.5);
    }

    ivec2 r0 = ivec2(floor(lo)) - uSafety;
    ivec2 r1 = ivec2(floor(hi)) + uSafety;
    if (r1.x < 0 || r1.y < 0 || r0.x > uViewport.x - 1 || r0.y > uViewport.y - 1) return;
    r0 = max(r0, ivec2(0));
    r1 = min(r1, uViewport - 1);

    int level = 0;
    while (level < uLevels - 1
           && ((r1.x >> (level + 1)) - (r0.x >> (level + 1)) > 1
               || (r1.y >> (level + 1)) - (r0.y >> (level + 1)) > 1))
        level++;

    ivec2 size = textureSize(uHiz, level);
    ivec2 t0 = min(r0 >> (level + 1), size - 1);
    ivec2 t1 = min(r1 >> (level + 1), size - 1);
    float m = max(max(texelFetch(uHiz, t0, level).r, texelFetch(uHiz, ivec2(t1.x, t0.y), level).r),
                  max(texelFetch(uHiz, ivec2(t0.x, t1.y), level).r, texelFetch(uHiz, t1, level).r));

    // Strictly behind, past the margin: a tie is never culled.
    if (near > m + uDepthMargin) cmds[idx * 5 + 1] = 0u;
}
