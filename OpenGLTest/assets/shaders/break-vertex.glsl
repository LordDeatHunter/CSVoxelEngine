#version 330 core
layout (location = 0) in vec3 vPosition;

uniform mat4 uMVP;

out vec3 vLocal;

void main() {
    vLocal = vPosition;
    gl_Position = uMVP * vec4(vPosition, 1.0);
}
