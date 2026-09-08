#version 330 core
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec2 vTexCoord;
layout (location = 3) in float vSun;
layout (location = 4) in float vBlock;
layout (location = 5) in float vNormalIdx;
layout (location = 6) in float vSway;

uniform mat4 uMVP;
uniform mat4 uModel;
uniform float uTime;
uniform float uSwayStrength;
uniform vec3 uCameraPos;

out vec4 fColor;
out vec2 fTexCoord;
out float fSun;
out float fBlock;
out vec3 fNormal;
out vec3 fWorldPos;

void main() {
    // Canopy wind, shaderpack-style: broad sine waves rolling over world XZ breathe whole
    // crowns at once instead of jittering single blocks (per-block offsets would crack the
    // voxel seams). Wavelengths span many blocks, so neighbouring verts shear by ~a texel
    // while the canopy visibly sways; amplitude peaks under a tenth of a block.
    vec3 displaced = vPosition;
    // ponytail: water flag rides in vColor.a (254 = water, 255 = everything else); fColor.a is unused otherwise.
    bool isWater = vColor.a < 0.998;
    if (isWater) {
        // Y-only waves as a pure function of world XZ, so verts sharing an XZ (sloped-top
        // corners, side-face top edges) displace identically and the skin never cracks.
        // Faded out by ~160 blocks: the far LOD sheets sample different XZs, so any
        // displacement there would seam against the chunk water and between LOD levels.
        vec4 anchorW = uModel * vec4(vPosition, 1.0);
        float wFade = 1.0 - smoothstep(64.0, 160.0, distance(anchorW.xyz, uCameraPos));
        displaced.y += (sin(uTime * 1.8 + anchorW.x * 1.2 + anchorW.z * 0.9) * 0.03
                      + sin(uTime * 3.1 + anchorW.z * 2.2 - anchorW.x * 0.7) * 0.015) * wFade;
    }
    if (vSway > 0.001 && uSwayStrength > 0.001) {
        vec4 anchor = uModel * vec4(vPosition, 1.0);
        float gust = 0.6 + 0.4 * sin(uTime * 0.5 + anchor.x * 0.08 + anchor.z * 0.06);
        float w1 = sin(uTime * 1.6 + anchor.x * 0.45 + anchor.z * 0.28);
        float w2 = sin(uTime * 2.7 + anchor.z * 0.62 - anchor.x * 0.19 + 1.3);
        // uModel is a pure translation, so a world-space offset applies to local coords as-is
        displaced += vec3(w1 * 0.055 + w2 * 0.028, (w1 + w2) * 0.018, w2 * 0.055 - w1 * 0.028)
            * (vSway * uSwayStrength * gust);
    }
    vec4 wp = uModel * vec4(displaced, 1.0);
    fWorldPos = wp.xyz;
    gl_Position = uMVP * vec4(displaced, 1.0);
    fColor = vColor;
    fTexCoord = vTexCoord;
    fSun = vSun;
    fBlock = vBlock;
    // uModel is a pure translation for chunks/LOD, so the normal is unchanged.
    // The mesher packs the face as a 0..5 index (Chunk.CubeFaces order); a nested ternary
    // keeps it branchless without relying on dynamic array indexing.
    vec3 vNormal = vNormalIdx < 0.5 ? vec3(0.0, 0.0, -1.0)
        : vNormalIdx < 1.5 ? vec3(0.0, 0.0, 1.0)
        : vNormalIdx < 2.5 ? vec3(-1.0, 0.0, 0.0)
        : vNormalIdx < 3.5 ? vec3(1.0, 0.0, 0.0)
        : vNormalIdx < 4.5 ? vec3(0.0, 1.0, 0.0) : vec3(0.0, -1.0, 0.0);
    fNormal = mat3(uModel) * vNormal;
}
