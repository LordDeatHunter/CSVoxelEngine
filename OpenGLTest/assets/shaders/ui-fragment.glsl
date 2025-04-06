#version 330 core
in vec4 fColor;
in vec2 fTexCoord;

uniform sampler2D uTex;
out vec4 FragColor;

void main()
{
    FragColor = texture(uTex, fTexCoord) * fColor;
}
