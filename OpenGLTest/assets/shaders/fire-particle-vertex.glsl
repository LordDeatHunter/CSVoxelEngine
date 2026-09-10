#version 460 core
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec2 vTexCoord;
layout (location = 3) in float vSun;
layout (location = 7) in uint vLayer;

uniform mat4 uVP;
uniform vec3 uCameraPos;

out vec2 fUv;
out vec4 fTint;
out float fEmissive;
out float fDist;

void main() {
    gl_Position = uVP * vec4(vPosition, 1.0);
    fUv = vTexCoord;
    fTint = vColor;
    fEmissive = vSun;
    fDist = distance(vPosition, uCameraPos);
}
