#version 460 core
layout (location = 0) in vec3 vPosition;
// Only the alpha is read: a swaying vertex's sway weight (Vertex.SwayWeight, see test-vertex.glsl).
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec2 vTexCoord;
layout (location = 5) in uint vNormalBits; // Vertex.Normal + LightTint; sway is bit 5
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
uniform vec2 uWind;       // downwind direction * WindLean (Wind.Direction)
uniform vec2 uWindOffset; // Wind.Offset(time, WindGustSpeed): how far the gusts have rolled

out vec2 fTexCoord;
flat out uint fLayer;

#ifdef TREE_PROXY
layout (std430, binding = 3) readonly buffer TreePayload { uint treeWords[]; };
flat out uint fTreeBase;
out vec2 fTreeXZ;
bool treePresent;
uint treeBase;
vec3 treePosition;
uint treeLayer;
uint treeNormalBits;
void LoadTree() {
    treeBase = uint(gl_BaseInstance) * 4u;
    uint i = treeBase + treeWords[treeBase] + uint(gl_InstanceID) * 16u;
    int rootX = int(treeWords[i]), rootZ = int(treeWords[i+1u]);
    int baseY = int(treeWords[i+2u]), bottom = int(treeWords[i+3u]), top = int(treeWords[i+4u]);
    int minX = int(treeWords[i+5u]), maxX = int(treeWords[i+6u]);
    int minZ = int(treeWords[i+7u]), maxZ = int(treeWords[i+8u]);
    int width = int(treeWords[i+9u]);
    int part = int(uint(vLayer) / 12u);
    int face = int((uint(vLayer) / 2u) % 6u);
    int layer = int(uint(vLayer) % 2u);
    int material = int(treeWords[i + (part == 0 ? 10u : 11u)]);
    int profile = int(treeWords[i+12u]), variant = int(treeWords[i+13u]);
    int middle = bottom + max(1, (top - bottom) / 2);
    vec3 lo, hi;
    if (part == 0) {
        float halfWidth = float(width) * 0.5;
        lo = vec3(float(rootX) + 0.5 - halfWidth, float(baseY), float(rootZ) + 0.5 - halfWidth);
        hi = vec3(float(rootX) + 0.5 + halfWidth, float(max(baseY + 1, bottom)),
            float(rootZ) + 0.5 + halfWidth);
    } else if (part == 1) {
        lo = vec3(float(minX), float(bottom), float(minZ));
        hi = vec3(float(maxX), float(profile == 2 ? top : middle), float(maxZ));
    } else {
        float inset = (profile == 1 ? 0.32 : 0.18) + (variant != 0 ? 0.04 : 0.0);
        lo = vec3(float(minX) + float(maxX - minX) * inset, float(middle),
            float(minZ) + float(maxZ - minZ) * inset);
        hi = vec3(float(maxX) - float(maxX - minX) * inset, float(top),
            float(maxZ) - float(maxZ - minZ) * inset);
    }
    treePosition = mix(lo, hi, vPosition);
    uint m = treeBase + treeWords[treeBase + 1u] + uint(max(material, 0)) * 8u;
    uint pair = treeWords[m + uint(face)];
    treeLayer = layer == 0 ? pair & 65535u : pair >> 16u;
    treePresent = material >= 0 && treeLayer != 65535u &&
        !(part == 2 && profile == 2) && all(greaterThan(hi, lo));
    if (!treePresent) return;
    treeNormalBits = uint(face);
}
#define vPosition treePosition
#define vLayer treeLayer
#define vNormalBits treeNormalBits
#endif

void main() {
#ifdef TREE_PROXY
    LoadTree();
    if (!treePresent) {
        gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
        return;
    }
#endif
    // Bit 5 flags a swaying vertex, whose alpha is then its sway weight: uniform on canopy
    // leaves, eased from nothing at a rooted plant's base to full at its tip (Vertex.SwayWeight).
    bool swaying = (vNormalBits & 32u) != 0u;
    float vSway = swaying ? vColor.a : 0.0;
#ifdef CHUNK_DRAW
    vec3 chunkOrigin = draws[uDrawBase + gl_DrawID].originAndAlpha.xyz;
#endif
    // Canopy wind, shaderpack-style: broad sine waves rolling over world XZ breathe whole
    // crowns at once instead of jittering single blocks (per-block offsets would crack the
    // voxel seams). Wavelengths span many blocks, so neighboring verts shear by ~a texel
    // while the canopy visibly sways; amplitude peaks under a tenth of a block.
    // Kept byte for byte in step with test-vertex.glsl, or leaf shadows would drift away
    // from the leaves that cast them.
    vec3 displaced = vPosition;
    if (vSway > 0.001 && uSwayStrength > 0.001) {
        vec3 anchor = TO_WORLD(vPosition);
        // The gust wave is sampled at the anchor carried back by the wind's traveled offset
        // (Wind.Offset, bounded), so gusts roll downwind while the phase rate stays the same
        // anywhere in the world; Wind.GustPhase is the C# twin.
        vec2 ga = anchor.xz - uWindOffset;
        float gust = 0.6 + 0.4 * sin(uTime * 0.5 + ga.x * 0.08 + ga.y * 0.06);
        float w1 = sin(uTime * 1.6 + anchor.x * 0.45 + anchor.z * 0.28);
        float w2 = sin(uTime * 2.7 + anchor.z * 0.62 - anchor.x * 0.19 + 1.3);
        // TO_WORLD is a pure translation for chunks, so a world-space offset applies to local coords as-is.
        // uWind is the downwind direction scaled by WindLean, so its length is the lean at full gust.
        displaced += (vec3(w1 * 0.055 + w2 * 0.028, (w1 + w2) * 0.018, w2 * 0.055 - w1 * 0.028)
            + vec3(uWind.x, 0.0, uWind.y)) * (vSway * uSwayStrength * gust);
    }
#ifdef CHUNK_DRAW
    gl_Position = uViewProj * vec4(TO_WORLD(displaced), 1.0);
#ifdef TREE_PROXY
    fTreeBase = treeBase;
    fTreeXZ = TO_WORLD(displaced).xz - chunkOrigin.xz;
#endif
#else
    gl_Position = uLightMVP * vec4(displaced, 1.0);
#endif
    fTexCoord = vTexCoord;
    fLayer = vLayer;
}
