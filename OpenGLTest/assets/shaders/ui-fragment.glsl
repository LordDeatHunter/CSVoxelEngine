#version 330 core
in vec4 fColor;
in vec2 fTexCoord;

uniform sampler2D uTex;
uniform vec4 uColor;
uniform bool uUseTexture;

out vec4 FragColor;

void main()
{
    // Vertex colour bakes in per-face shading, uColor is the per-draw tint. Solid shapes skip
    // the sampler entirely so they don't need a texture bound.
    vec4 color = fColor * uColor;

    if (uUseTexture)
        color *= texture(uTex, fTexCoord);

    FragColor = color;
}
