#version 330 core
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec2 vTexCoord;
layout (location = 3) in float vSun;
layout (location = 4) in float vBlock;
layout (location = 5) in vec3 vNormal;
layout (location = 6) in float vSway;

uniform mat4 uMVP;
uniform mat4 uModel;
uniform float uTime;
uniform float uSwayStrength;

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
    // uModel is a pure translation for chunks/LOD, so the normal is unchanged
    fNormal = mat3(uModel) * vNormal;
}
