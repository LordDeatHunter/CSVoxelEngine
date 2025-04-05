#version 330 core
layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec2 vTexCoord;

uniform mat4 uMVP;

out vec4 fColor;
out vec2 fTexCoord;

void main() {
    gl_Position = uMVP * vec4(vPosition, 1.0);
    fColor = vColor;
    fTexCoord = vTexCoord;
}
