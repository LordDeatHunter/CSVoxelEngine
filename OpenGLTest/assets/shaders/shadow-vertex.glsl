#version 460 core
layout (location = 0) in vec3 vPosition;
layout (location = 2) in vec2 vTexCoord;
layout (location = 6) in float vSway;
layout (location = 7) in uint vLayer; // array-texture slice (Vertex.Layer)

#ifdef CHUNK_DRAW
// Pooled chunk draws: one glMultiDrawElementsIndirect per pass, per-draw data in the ChunkInfo
// SSBO picked by gl_DrawID (ChunkDrawBatch). Chunk model matrices are pure translations.
struct ChunkDraw { vec4 originAndAlpha; };
layout (std430, binding = 0) readonly buffer ChunkInfo { ChunkDraw draws[]; };
uniform mat4 uViewProj;
uniform int uDrawBase;
#define TO_WORLD(p) ((p) + chunkOrigin)
#else
uniform mat4 uLightMVP;
uniform mat4 uModel;
#define TO_WORLD(p) ((uModel * vec4((p), 1.0)).xyz)
#endif
uniform float uTime;
uniform float uSwayStrength;

out vec2 fTexCoord;
flat out uint fLayer;

void main() {
#ifdef CHUNK_DRAW
    vec3 chunkOrigin = draws[uDrawBase + gl_DrawID].originAndAlpha.xyz;
#endif
    // Canopy wind, shaderpack-style: broad sine waves rolling over world XZ breathe whole
    // crowns at once instead of jittering single blocks (per-block offsets would crack the
    // voxel seams). Wavelengths span many blocks, so neighbouring verts shear by ~a texel
    // while the canopy visibly sways; amplitude peaks under a tenth of a block.
    // Kept byte for byte in step with test-vertex.glsl, or leaf shadows would drift away
    // from the leaves that cast them.
    vec3 displaced = vPosition;
    if (vSway > 0.001 && uSwayStrength > 0.001) {
        vec3 anchor = TO_WORLD(vPosition);
        float gust = 0.6 + 0.4 * sin(uTime * 0.5 + anchor.x * 0.08 + anchor.z * 0.06);
        float w1 = sin(uTime * 1.6 + anchor.x * 0.45 + anchor.z * 0.28);
        float w2 = sin(uTime * 2.7 + anchor.z * 0.62 - anchor.x * 0.19 + 1.3);
        // TO_WORLD is a pure translation for chunks, so a world-space offset applies to local coords as-is
        displaced += vec3(w1 * 0.055 + w2 * 0.028, (w1 + w2) * 0.018, w2 * 0.055 - w1 * 0.028)
            * (vSway * uSwayStrength * gust);
    }
#ifdef CHUNK_DRAW
    gl_Position = uViewProj * vec4(TO_WORLD(displaced), 1.0);
#else
    gl_Position = uLightMVP * vec4(displaced, 1.0);
#endif
    fTexCoord = vTexCoord;
    fLayer = vLayer;
}
