#version 460 core
// Falling-leaf billboards. Reuses the shared Vertex layout (positions are world-space
// quad corners baked on the CPU), but fades per vertex through the color alpha —
// the world shader's water flag rides there, so leaves get their own program.
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec2 vTexCoord;
layout (location = 3) in float vSun;
layout (location = 7) in uint vLayer;

uniform mat4 uVP;
uniform vec3 uCameraPos;

out vec2 fUv;
flat out uint fLayer;
out vec4 fTint;
out float fSun;
out float fDist;

void main() {
    gl_Position = uVP * vec4(vPosition, 1.0);
    fUv = vTexCoord;
    fLayer = vLayer;
    fTint = vColor;
    fSun = vSun;
    fDist = distance(vPosition, uCameraPos);
}
