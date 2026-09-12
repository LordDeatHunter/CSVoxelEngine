#version 460 core
// The cloud field is one static mesh in field-local coordinates. The CPU culls the 3x3 ring around
// the camera and hands the survivors in as one instance stream, one rigid offset each (field
// multiples plus drift), so the whole sweep is a single draw and a copy never tears.
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec3 vNormal;
// Per-instance: one copy's world-space (x, z) origin, advanced once per instance by the divisor the
// vertex array sets, so every copy of the sweep rides the same static mesh.
layout (location = 2) in vec2 vOffset;

uniform mat4 uMVP;

out vec3 fWorldPos;
out vec3 fNormal;

void main() {
    vec3 wp = vec3(vPosition.x + vOffset.x, vPosition.y, vPosition.z + vOffset.y);
    fWorldPos = wp;
    fNormal = vNormal;
    gl_Position = uMVP * vec4(wp, 1.0);
}
