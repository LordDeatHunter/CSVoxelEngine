#version 330 core
in vec4 fColor;
in vec2 fTexCoord;

uniform sampler2D uTextureAtlas;
uniform float uAlpha;

out vec4 FragColor;

void main() {
    vec4 texColor = texture(uTextureAtlas, fTexCoord);
    FragColor = vec4(texColor.rgb * fColor.rgb, uAlpha);
}
