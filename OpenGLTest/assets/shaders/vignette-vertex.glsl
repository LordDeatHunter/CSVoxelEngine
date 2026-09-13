#version 460 core
layout (location = 0) in vec2 vPos;
void main() {
    // One oversized triangle covering NDC, so the visible area has no diagonal seam.
    gl_Position = vec4(vPos, 0.0, 1.0);
}
