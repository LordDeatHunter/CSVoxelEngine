#version 460 core
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec2 vTexCoord;
layout (location = 3) in float vSun;
layout (location = 4) in float vBlock;
// Vertex.Normal (low byte) + Vertex.LightTint (high byte) as one integer ushort: face in
// bits 0-2, submerged 3, biome tint 4, leaf sway 5, the 10-bit block-light tint code in 6-15.
layout (location = 5) in uint vNormalBits;
layout (location = 7) in uint vLayer; // array-texture slice (Vertex.Layer)
layout (location = 8) in uint vWaterSurface; // Vertex.WaterSurface

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
// Entity meshes are static meshes drawn at many positions, so their light cannot be baked into
// the vertices: RenderManager.SetDrawUniforms sets it per draw from EntityLight.Sample.
uniform float uEntitySun;
uniform float uEntityBlock;
// Palette slot of the block light at the entity's cell (EntityLightSample.Tint).
uniform int uEntityTint;
// Per-draw tint of a standalone copy of a biome-tinted block (mining pop, debris): one cached mesh
// is drawn at many cells, so the tint cannot ride its rgb. White for every other draw
// (RenderManager.InitWorldProgram sets the default; the chunk program never reads it).
uniform vec3 uBiomeTint;
#define TO_WORLD(p) ((uModel * vec4((p), 1.0)).xyz)
#endif
uniform float uTime;
uniform float uSwayStrength;
uniform vec2 uWind;       // downwind direction * WindLean (Wind.Direction)
uniform vec2 uWindOffset; // Wind.Offset(time, WindGustSpeed): how far the gusts have rolled
uniform vec3 uCameraPos;
// Atlas slice of the lava tile (RenderManager.InitWorldProgram): picks lava's own swell.
uniform float uLavaLayer;
// Linear block-light colours by palette slot (BlockLightPalette.Linear); slot 0 is the default
// warm torch colour.
uniform vec3 uBlockLightPalette[8];

out vec4 fColor;
out vec2 fTexCoord;
flat out uint fLayer;
out float fSun;
out float fBlock;
// Linear colour of the block light reaching this vertex (smooth: a corner blend of two slots).
out vec3 fBlockTint;
out vec3 fNormal;
// The tile's u and v directions in world space, for the reflective tiles' normal maps. Flat
// because they are constant per face, like the normal they complete.
flat out vec3 fTangent;
flat out vec3 fBitangent;
out vec3 fWorldPos;
out float fSubmerged;
flat out float fAlpha;
// BiomeTint.Gain on a biome-tinted face (Vertex.Normal bit 4), 1 everywhere else.
flat out float fTintGain;
// World Y of the water surface above a submerged face (0 elsewhere, decoding far below any
// face).
flat out float fWaterSurfaceY;
// Water flow on fluid quads (Vertex.EncodeWaterFlow): xy = flow direction * strength, z = falling
// flag. Deliberately NOT flat: the mesher bakes one code per corner and neighbouring cells share
// their corner codes (FluidFlow.Corner), so the interpolated flow is continuous across cells.
out vec3 fWaterFlow;

// Four directional sines: the sum stays under 0.09 blocks so a full cell's surface (8/9) never
// pokes out of its block. Duplicated verbatim in test-fragment.glsl's WaveSlope for the analytic normal.
float WaveHeight(vec2 p, float t) {
    return 0.035 * sin(dot(p, vec2(0.9, 0.4)) * 0.9 + t * 1.6)
         + 0.025 * sin(dot(p, vec2(-0.5, 0.85)) * 1.4 + t * 2.1)
         + 0.015 * sin(dot(p, vec2(0.3, -0.95)) * 2.6 + t * 2.9)
         + 0.010 * sin(dot(p, vec2(-0.8, -0.6)) * 4.1 + t * 3.7);
}

// Lava's swell: broader, lower-frequency and slower than water's so molten rock heaves in thick
// blobs instead of rippling. The amplitudes sum to 0.088, under the same 0.09 ceiling as
// WaveHeight. Its derivative is duplicated verbatim in test-fragment.glsl's LavaWaveSlope.
float LavaWaveHeight(vec2 p, float t) {
    return 0.040 * sin(dot(p, vec2(0.8, 0.6)) * 0.35 + t * 0.35)
         + 0.025 * sin(dot(p, vec2(-0.6, 0.8)) * 0.55 + t * 0.5)
         + 0.015 * sin(dot(p, vec2(0.2, -0.98)) * 0.9 + t * 0.7)
         + 0.008 * sin(dot(p, vec2(-0.9, -0.4)) * 1.5 + t * 0.9);
}

void main() {
    float vSway = (vNormalBits & 32u) != 0u ? 1.0 : 0.0;
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
    bool isLava = isWater && abs(float(vLayer) - uLavaLayer) < 0.5;
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
        displaced.y += (isLava ? LavaWaveHeight(anchorW.xz, uTime) : WaveHeight(anchorW.xz, uTime)) * wFade;
    }
    if (vSway > 0.001 && uSwayStrength > 0.001) {
        vec3 anchor = TO_WORLD(vPosition);
        // The gust wave is sampled at the anchor carried back by the wind's travelled offset
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
    fWorldPos = TO_WORLD(displaced);
#ifdef CHUNK_DRAW
    gl_Position = uViewProj * vec4(fWorldPos, 1.0);
#else
    gl_Position = uMVP * vec4(displaced, 1.0);
#endif
    fColor = vColor;
    // Vertex.Normal bit 4: biome-tinted face. Mirrors BiomeTint.Gain (4): the grey textureless
    // tiles need a multiplier above 1 to reach grass green, so the vertex rgb carries tint/4 and
    // the fragment scales it back.
    bool biomeTinted = (vNormalBits & 16u) != 0u;
    fTintGain = biomeTinted ? 4.0 : 1.0;
#ifndef CHUNK_DRAW
    if (biomeTinted) fColor.rgb *= uBiomeTint;
#endif
    fTexCoord = vTexCoord;
    fLayer = vLayer;
    // Mirrors Vertex.DecodeWaterSurface: must change with Vertex.WaterSurfaceStepsPerBlock and
    // Vertex.WaterSurfaceOffset. Only chunk faces are ever submerged, so the entity path's
    // value is never read.
    fWaterSurfaceY = float(vWaterSurface) / 4.0 - 4096.0;
    // On a fluid quad the same ushort carries the per-corner flow code instead. Mirrors
    // Vertex.EncodeWaterFlow / Vertex.WaterFlowSteps (63): 7-bit two's complement X in bits 0-6,
    // Z in bits 7-13, falling in bit 14; 0 is still water. The wave DISPLACEMENT above stays
    // flow-free on purpose (it must agree across every vertex sharing an XZ); only the fragment
    // normal reads the flow.
    if (isWater) {
        uint c = vWaterSurface;
        int fx = int(c & 127u);
        int fz = int((c >> 7) & 127u);
        if (fx >= 64) fx -= 128;
        if (fz >= 64) fz -= 128;
        fWaterFlow = vec3(float(fx) / 63.0, float(fz) / 63.0, float((c >> 14) & 1u));
    } else {
        fWaterFlow = vec3(0.0);
    }
#ifdef CHUNK_DRAW
    fSun = vSun;
    fBlock = vBlock;
    // Mirrors LightTint.Decode / LightTint.WeightDivisor (30): slot A in bits 0-2, slot B in
    // 3-5 and B's share q/30 in 6-9 of the tint code.
    uint tc = vNormalBits >> 6u;
    fBlockTint = mix(uBlockLightPalette[tc & 7u], uBlockLightPalette[(tc >> 3u) & 7u], float(tc >> 6u) / 30.0);
#else
    // Per-draw light sampled from the world at the entity's anchor (see uEntitySun above).
    fSun = uEntitySun;
    fBlock = uEntityBlock;
    fBlockTint = uBlockLightPalette[uEntityTint];
#endif
    // uModel is a pure translation for chunks/LOD, so the normal is unchanged.
    // The mesher packs the face as a 0..5 index (Chunk.CubeFaces order) in bits 0-2 and
    // "this face looks into water" in bit 3 (bit 4 is the biome tint and bit 5 sway, both read above); a
    // nested ternary keeps the decode branchless without relying on dynamic array indexing.
    int nIdx = int(vNormalBits & 63u);
    int face = nIdx & 7;
    fSubmerged = (nIdx & 8) != 0 ? 1.0 : 0.0;
    vec3 vNormal = face == 0 ? vec3(0.0, 0.0, -1.0)
        : face == 1 ? vec3(0.0, 0.0, 1.0)
        : face == 2 ? vec3(-1.0, 0.0, 0.0)
        : face == 3 ? vec3(1.0, 0.0, 0.0)
        : face == 4 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, -1.0, 0.0);
    // The tangent frame a normal map is read in has to agree with the UVs the mesher wrote, or
    // the relief comes out rotated or mirrored per face. Chunk.TiledUv is the one definition of
    // that orientation: u runs along X on -Z/+Z (mirrored on +Z) and along Z on -X/+X (mirrored
    // on -X), and along X on both caps; v runs bottom-to-top on every side, and along Z on the
    // caps. Same nested-ternary shape as the normal above, for the same reason.
    vec3 vTangent = face == 0 ? vec3(1.0, 0.0, 0.0)
        : face == 1 ? vec3(-1.0, 0.0, 0.0)
        : face == 2 ? vec3(0.0, 0.0, -1.0)
        : face == 3 ? vec3(0.0, 0.0, 1.0)
        : vec3(1.0, 0.0, 0.0);
    vec3 vBitangent = face == 4 ? vec3(0.0, 0.0, -1.0)
        : face == 5 ? vec3(0.0, 0.0, 1.0)
        : vec3(0.0, -1.0, 0.0);
#ifdef CHUNK_DRAW
    fNormal = vNormal;
    fTangent = vTangent;
    fBitangent = vBitangent;
#else
    fNormal = mat3(uModel) * vNormal;
    fTangent = mat3(uModel) * vTangent;
    fBitangent = mat3(uModel) * vBitangent;
#endif
}
