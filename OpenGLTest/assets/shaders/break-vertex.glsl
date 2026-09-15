#version 460 core
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec2 vTexCoord;

uniform mat4 uMVP;
uniform vec2 uUvOffset;
uniform vec2 uUvScale;

out vec2 vUv;

void main() {
    vUv = uUvOffset + vTexCoord * uUvScale;
    gl_Position = uMVP * vec4(vPosition, 1.0);
}
