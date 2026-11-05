#version 460 core
// The slice-trail ribbon (SliceTrailRenderer): world-space vertices built by SliceTrailRibbon.
// Only locations 0, 1 and 2 are read; the ribbon's sun and block light bytes are 0.
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec2 vTexCoord;

uniform mat4 uVP;
uniform vec3 uCameraPos;

out vec2 fUv;
out vec4 fTint;
out float fDist;

void main() {
    gl_Position = uVP * vec4(vPosition, 1.0);
    fUv = vTexCoord;
    fTint = vColor;
    fDist = distance(vPosition, uCameraPos);
}
