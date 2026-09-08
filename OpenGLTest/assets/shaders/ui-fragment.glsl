#version 460 core
in vec4 fColor;
in vec2 fTexCoord;
flat in uint fLayer;

// Every UI texture is an array: the block atlas (icons pick their tile through fLayer),
// and single-slice sprites, font and crosshair on layer 0.
uniform sampler2DArray uTex;
uniform vec4 uColor;
uniform bool uUseTexture;
// Alpha discard threshold for cutout draws (extruded item sprites). 0 disables: without it
// the sprite's transparent front texels would still write depth and swallow the extrusion
// walls behind them whenever the slab turns.
uniform float uAlphaCutoff;

out vec4 FragColor;

void main()
{
    // Vertex colour bakes in per-face shading, uColor is the per-draw tint. Solid shapes skip
    // the sampler entirely so they don't need a texture bound.
    vec4 texel = vec4(1.0);

    if (uUseTexture)
    {
        texel = texture(uTex, vec3(fTexCoord, float(fLayer)));
        if (uAlphaCutoff > 0.0 && texel.a < uAlphaCutoff)
            discard;
    }

    FragColor = fColor * uColor * texel;
}
