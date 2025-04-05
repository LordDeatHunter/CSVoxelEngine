#version 330 core
in vec4 fColor;
in vec2 fTexCoord;

uniform sampler2D uTextureAtlas;

out vec4 FragColor;

void main() {
    vec4 texColor = texture(uTextureAtlas, fTexCoord);
    FragColor = texColor * fColor;
}
