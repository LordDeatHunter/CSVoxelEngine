#version 460 core
// The cloud field is one static mesh in field-local coordinates. The CPU draws it 3x3 around
// the camera with a rigid per-copy offset (field multiples plus drift), so a copy never tears.
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec3 vNormal;

uniform mat4 uMVP;
uniform vec2 uOffset;

out vec3 fWorldPos;
out vec3 fNormal;

void main() {
    vec3 wp = vec3(vPosition.x + uOffset.x, vPosition.y, vPosition.z + uOffset.y);
    fWorldPos = wp;
    fNormal = vNormal;
    gl_Position = uMVP * vec4(wp, 1.0);
}
