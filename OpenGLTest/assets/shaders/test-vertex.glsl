#version 330 core
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec2 vTexCoord;
layout (location = 3) in float vSun;
layout (location = 4) in float vBlock;
layout (location = 5) in vec3 vNormal;

uniform mat4 uMVP;
uniform mat4 uModel;

out vec4 fColor;
out vec2 fTexCoord;
out float fSun;
out float fBlock;
out vec3 fNormal;
out vec3 fWorldPos;

void main() {
    vec4 wp = uModel * vec4(vPosition, 1.0);
    fWorldPos = wp.xyz;
    gl_Position = uMVP * vec4(vPosition, 1.0);
    fColor = vColor;
    fTexCoord = vTexCoord;
    fSun = vSun;
    fBlock = vBlock;
    // uModel is a pure translation for chunks/LOD, so the normal is unchanged
    fNormal = mat3(uModel) * vNormal;
}
