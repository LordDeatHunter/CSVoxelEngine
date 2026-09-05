#version 330 core
in vec4 fColor;
in vec2 fTexCoord;

uniform sampler2D uTextureAtlas;
uniform float uAlpha;
uniform float uFlash;

out vec4 FragColor;

void main() {
    vec4 texColor = texture(uTextureAtlas, fTexCoord);
    vec3 lit = texColor.rgb * fColor.rgb;
    FragColor = vec4(mix(lit, vec3(1.0), uFlash), uAlpha);
}
