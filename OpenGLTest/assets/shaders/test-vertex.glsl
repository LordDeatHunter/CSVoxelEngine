#version 460 core
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec2 vTexCoord;
layout (location = 3) in float vSun;
layout (location = 4) in float vBlock;
layout (location = 5) in float vNormalIdx;
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
uniform mat4 uMVP;
uniform mat4 uModel;
uniform float uAlpha;
#define TO_WORLD(p) ((uModel * vec4((p), 1.0)).xyz)
#endif
uniform float uTime;
uniform float uSwayStrength;
uniform vec3 uCameraPos;

out vec4 fColor;
out vec2 fTexCoord;
flat out uint fLayer;
out float fSun;
out float fBlock;
out vec3 fNormal;
out vec3 fWorldPos;
out float fSubmerged;
flat out float fAlpha;

// Four directional sines: the sum stays under 0.09 blocks so a full cell's surface (8/9) never
// pokes out of its block. Duplicated verbatim in test-fragment.glsl for the analytic normal.
float WaveHeight(vec2 p, float t) {
    return 0.035 * sin(dot(p, vec2(0.9, 0.4)) * 0.9 + t * 1.6)
         + 0.025 * sin(dot(p, vec2(-0.5, 0.85)) * 1.4 + t * 2.1)
         + 0.015 * sin(dot(p, vec2(0.3, -0.95)) * 2.6 + t * 2.9)
         + 0.010 * sin(dot(p, vec2(-0.8, -0.6)) * 4.1 + t * 3.7);
}

void main() {
#ifdef CHUNK_DRAW
    vec4 chunkDraw = draws[uDrawBase + gl_DrawID].originAndAlpha;
    vec3 chunkOrigin = chunkDraw.xyz;
    fAlpha = chunkDraw.w;
#else
    fAlpha = uAlpha;
#endif
    // Canopy wind, shaderpack-style: broad sine waves rolling over world XZ breathe whole
    // crowns at once instead of jittering single blocks (per-block offsets would crack the
    // voxel seams). Wavelengths span many blocks, so neighbouring verts shear by ~a texel
    // while the canopy visibly sways; amplitude peaks under a tenth of a block.
    vec3 displaced = vPosition;
    // ponytail: water flag rides in vColor.a (254 = water, 255 = everything else); fColor.a is unused otherwise.
    bool isWater = vColor.a < 0.998;
    // Only true surface vertices move: skirt bottoms, the tops of stacked columns and the
    // integer-height tops of falling water all sit at whole numbers, and displacing those
    // would crack the skin against the floor and against the cell above.
    if (isWater && fract(vPosition.y) > 0.01) {
        // Y-only waves as a pure function of world XZ, so verts sharing an XZ (sloped-top
        // corners, side-face top edges) displace identically and the skin never cracks.
        // Faded out by ~160 blocks: the far LOD sheets sample different XZs, so any
        // displacement there would seam against the chunk water and between LOD levels.
        vec3 anchorW = TO_WORLD(vPosition);
        float wFade = 1.0 - smoothstep(64.0, 160.0, distance(anchorW, uCameraPos));
        displaced.y += WaveHeight(anchorW.xz, uTime) * wFade;
    }
    if (vSway > 0.001 && uSwayStrength > 0.001) {
        vec3 anchor = TO_WORLD(vPosition);
        float gust = 0.6 + 0.4 * sin(uTime * 0.5 + anchor.x * 0.08 + anchor.z * 0.06);
        float w1 = sin(uTime * 1.6 + anchor.x * 0.45 + anchor.z * 0.28);
        float w2 = sin(uTime * 2.7 + anchor.z * 0.62 - anchor.x * 0.19 + 1.3);
        // TO_WORLD is a pure translation for chunks, so a world-space offset applies to local coords as-is
        displaced += vec3(w1 * 0.055 + w2 * 0.028, (w1 + w2) * 0.018, w2 * 0.055 - w1 * 0.028)
            * (vSway * uSwayStrength * gust);
    }
    fWorldPos = TO_WORLD(displaced);
#ifdef CHUNK_DRAW
    gl_Position = uViewProj * vec4(fWorldPos, 1.0);
#else
    gl_Position = uMVP * vec4(displaced, 1.0);
#endif
    fColor = vColor;
    fTexCoord = vTexCoord;
    fLayer = vLayer;
    fSun = vSun;
    fBlock = vBlock;
    // uModel is a pure translation for chunks/LOD, so the normal is unchanged.
    // The mesher packs the face as a 0..5 index (Chunk.CubeFaces order) in bits 0-2 and
    // "this face looks into water" in bit 3; a nested ternary keeps the decode branchless
    // without relying on dynamic array indexing.
    int nIdx = int(vNormalIdx + 0.5);
    int face = nIdx & 7;
    fSubmerged = (nIdx & 8) != 0 ? 1.0 : 0.0;
    vec3 vNormal = face == 0 ? vec3(0.0, 0.0, -1.0)
        : face == 1 ? vec3(0.0, 0.0, 1.0)
        : face == 2 ? vec3(-1.0, 0.0, 0.0)
        : face == 3 ? vec3(1.0, 0.0, 0.0)
        : face == 4 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, -1.0, 0.0);
#ifdef CHUNK_DRAW
    fNormal = vNormal;
#else
    fNormal = mat3(uModel) * vNormal;
#endif
}
