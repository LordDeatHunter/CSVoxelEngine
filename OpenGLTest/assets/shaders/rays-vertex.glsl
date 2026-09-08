#version 460 core
layout (location = 0) in vec2 vPos;
out vec2 fUv;
void main() {
    fUv = vPos * 0.5 + 0.5;
    gl_Position = vec4(vPos, 0.0, 1.0);
}
